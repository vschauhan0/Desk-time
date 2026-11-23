using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NISS.Application.Abstractions;
using NISS.Application.Options;
using NISS.Application.Settings;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Infrastructure.Security;

/// <summary>PBKDF2 password hashing through the ASP.NET Core Identity hasher (versioned format, automatic rehash detection).</summary>
internal sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<AdminUser> _hasher = new();
    private static readonly AdminUser Anonymous = new();

    public string Hash(string password) => _hasher.HashPassword(Anonymous, password);

    public bool Verify(string hash, string password, out bool needsRehash)
    {
        var result = _hasher.VerifyHashedPassword(Anonymous, hash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }
}

/// <summary>32 random bytes as the secret; SHA-256 of it as the stored value; constant-time verification.</summary>
internal sealed class SecretHasher : ISecretHasher
{
    public string GenerateSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public bool Verify(string secret, byte[] expectedHash) => CryptographicOperations.FixedTimeEquals(Hash(secret), expectedHash);
}

/// <summary>JWT signing key from configuration, or a random key generated once and persisted in <c>server_settings</c>.</summary>
internal sealed class SigningKeyProvider(IServiceScopeFactory scopes, IOptions<SecurityOptions> options) : ISigningKeyProvider
{
    private byte[]? _key;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_key is not null) return;
        var configured = options.Value.JwtSigningKey;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length < 32) throw new InvalidOperationException("Security:JwtSigningKey must be at least 32 characters long.");
            _key = Encoding.UTF8.GetBytes(configured);
            return;
        }

        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var stored = await settings.GetRawAsync(SettingKeys.JwtSigningKey, ct);
        if (stored is null)
        {
            try { await settings.SetRawAsync(SettingKeys.JwtSigningKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)), ct); }
            catch (DbUpdateException) { /* another instance won the race: read its key below */ }
            stored = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().ServerSettings.AsNoTracking()
                .Where(s => s.Key == SettingKeys.JwtSigningKey).Select(s => s.Value).FirstAsync(ct);
        }
        _key = Convert.FromBase64String(stored);
    }

    public byte[] GetKey() => _key ?? throw new InvalidOperationException("The signing key has not been initialised.");
}

/// <summary>Issues HS256 access tokens.</summary>
public sealed class AccessTokenIssuer(ISigningKeyProvider keys, IOptions<SecurityOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    public const string Issuer = "niss-monitor";
    public const string Audience = "niss-dashboard";

    public (string Token, DateTimeOffset ExpiresAtUtc) Issue(UserInfo user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(Math.Max(1, options.Value.AccessTokenMinutes));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                ["name"] = user.Username,
                ["role"] = user.Role,
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(keys.GetKey()), SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

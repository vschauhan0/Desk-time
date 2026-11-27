using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Updates;

public interface IAgentPackageService
{
    /// <summary>Verifies (hashes, newer version, Authenticode publisher pin), stores and indexes an uploaded package zip.</summary>
    Task<AgentPackageDto> UploadAsync(byte[] zip, CancellationToken ct);
}

internal sealed class AgentPackageService(
    IAppDbContext db,
    IPackageStore store,
    ISignatureChecker signatures,
    IEnumerable<ISignerSubjectReader> subjectReaders,
    IOptions<AgentUpdateOptions> options,
    IAuditService audit,
    ICurrentUser user,
    TimeProvider clock) : IAgentPackageService
{
    private const string ManifestName = "manifest.json";
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private static readonly string[] ExecutableExtensions = [".exe", ".dll"];

    public async Task<AgentPackageDto> UploadAsync(byte[] zip, CancellationToken ct)
    {
        ZipArchive archive;
        try { archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw Bad("The upload is not a valid zip file."); }

        using (archive)
        {
            var manifestBytes = ReadEntry(archive, ManifestName) ?? throw Bad("The package has no manifest.json.");
            ReleaseManifest manifest;
            try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, ProtocolJson.Options) ?? throw new JsonException(); }
            catch (JsonException) { throw Bad("manifest.json is not a valid release manifest."); }

            if (string.IsNullOrWhiteSpace(manifest.Version) || !Version.TryParse(manifest.Version, out _))
                throw Bad("The manifest version is not a valid dotted version.");
            // An empty or executable-free manifest would "verify" vacuously: refuse it.
            if (manifest.Files is null || manifest.Files.Count == 0 || !manifest.Files.Any(f => IsExecutable(f.Path)))
                throw Bad("The manifest lists no executable (.exe/.dll) files.");

            if (await db.AgentPackages.AnyAsync(p => p.Version == manifest.Version, ct))
                throw new AppException(409, "Version exists", $"Version {manifest.Version} has already been uploaded.");

            var current = (await db.AgentPackages.Select(p => p.Version).ToListAsync(ct))
                .Select(v => Version.TryParse(v, out var parsed) ? parsed : null)
                .Where(v => v is not null).Max()?.ToString() ?? "0.0.0";

            var pin = new PublisherPin(options.Value.AcceptedPublisherSubjects);
            var result = PackageVerification.Verify(manifest,
                path => ReadEntry(archive, path) ?? throw new FileNotFoundException("Not in the package."),
                signatures, pin, current);
            if (!result.Ok) throw Bad(result.Error ?? "Package verification failed.");

            var firstExe = manifest.Files.First(f => IsExecutable(f.Path));
            var signer = TryReadSigner(ReadEntry(archive, firstExe.Path));

            var rel = await store.SaveAsync(manifest.Version, zip, manifestBytes, ct);
            var package = new AgentPackage
            {
                Id = Guid.NewGuid(),
                Version = manifest.Version,
                RelPath = rel,
                SizeBytes = zip.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
                SignerSubject = signer,
                UploadedBy = user.Username,
                UploadedAt = clock.GetUtcNow(),
                Notes = Text.Trunc(manifest.Notes, 2000),
            };
            db.AgentPackages.Add(package);
            audit.Record(AuditActions.AgentPackageUploaded, manifest.Version, $"sha256={package.Sha256}; signer={signer}");
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                store.Delete(rel);
                throw new AppException(409, "Version exists", $"Version {manifest.Version} has already been uploaded.");
            }
            return new AgentPackageDto(package.Version, package.SizeBytes, package.Sha256, package.SignerSubject,
                package.UploadedBy, package.UploadedAt, package.Notes);
        }
    }

    private static bool IsExecutable(string path) =>
        ExecutableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private string? TryReadSigner(byte[]? bytes)
    {
        if (bytes is null) return null;
        foreach (var reader in subjectReaders)
        {
            try { var s = reader.GetSignerSubject(bytes); if (s is not null) return s; }
            catch { /* display-only */ }
        }
        return null;
    }

    private static byte[]? ReadEntry(ZipArchive archive, string path)
    {
        var name = path.Replace('\\', '/');
        var entry = archive.GetEntry(name)
            ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > MaxEntryBytes) return null;
        using var ms = new MemoryStream((int)entry.Length);
        using var s = entry.Open();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static AppException Bad(string detail) => new(400, "Invalid package", detail);
}

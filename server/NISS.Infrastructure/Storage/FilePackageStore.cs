using Microsoft.Extensions.Options;
using NISS.Application.Options;
using NISS.Application.Updates;

namespace NISS.Infrastructure.Storage;

/// <summary>Stores agent packages as <c>{root}/{version}/package.zip</c> plus <c>manifest.json</c>.</summary>
internal sealed class FilePackageStore(IOptions<AgentUpdateOptions> options) : IPackageStore
{
    private string Root => string.IsNullOrWhiteSpace(options.Value.Root)
        ? Path.Combine(Path.GetTempPath(), "niss-packages")
        : options.Value.Root;

    public async Task<string> SaveAsync(string version, byte[] zip, byte[] manifestJson, CancellationToken ct)
    {
        // The version is a parsed dotted number (digits and dots only), but never trust it as a path segment.
        var dir = string.Concat(version.Where(c => char.IsAsciiDigit(c) || c == '.'));
        if (dir.Length == 0 || dir != version) throw new ArgumentException("Invalid version.", nameof(version));
        var full = Path.Combine(Root, dir);
        Directory.CreateDirectory(full);
        await File.WriteAllBytesAsync(Path.Combine(full, "manifest.json"), manifestJson, ct);
        await File.WriteAllBytesAsync(Path.Combine(full, "package.zip"), zip, ct);
        return Path.Combine(dir, "package.zip");
    }

    public Stream OpenRead(string relPath) => File.OpenRead(Path.Combine(Root, relPath));

    public void Delete(string relPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.Combine(Root, relPath));
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Text.Json;

namespace NISS.Monitor.Infrastructure.Updates;

/// <summary>
/// Pure path/state logic for the side-by-side layout: <c>&lt;installDir&gt;\app\&lt;version&gt;\</c>. The initial
/// (pre-update) install lives directly in <c>&lt;installDir&gt;</c> and is treated as the "legacy" version folder
/// until an update moves the service into <c>app\</c>.
/// </summary>
public static class UpdateLayout
{
    public const string AppFolder = "app";

    public static string AppRoot(string installDir) => Path.Combine(installDir, AppFolder);

    /// <summary>Folder for a version. Throws unless <paramref name="version"/> is a plain dotted version (no separators, no "..").</summary>
    public static string VersionDir(string installDir, string version)
    {
        if (!Version.TryParse(version, out _) || version.Any(c => !(char.IsAsciiDigit(c) || c == '.')))
            throw new ArgumentException($"'{version}' is not a plain dotted version.", nameof(version));
        return Path.Combine(AppRoot(installDir), version);
    }

    /// <summary>
    /// Folder holding <paramref name="version"/>: <c>app\version</c> when it exists, else the install dir when
    /// <paramref name="version"/> is the legacy (pre-update) version, else null.
    /// </summary>
    public static string? ResolveVersionDir(string installDir, string version, string? legacyVersion, Func<string, bool> dirExists)
    {
        var side = VersionDir(installDir, version);
        if (dirExists(side)) return side;
        return string.Equals(version, legacyVersion, StringComparison.Ordinal) ? installDir : null;
    }

    /// <summary>Version folders to delete: everything except the current and previous versions.</summary>
    public static IReadOnlyList<string> VersionsToPrune(IEnumerable<string> onDisk, string current, string? previous) =>
        onDisk.Where(v => !string.Equals(v, current, StringComparison.Ordinal) && !string.Equals(v, previous, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Zip-slip guard: resolves <paramref name="relative"/> under <paramref name="root"/> and returns the full path,
    /// or null when it is rooted, drive-qualified, contains a ".." segment or otherwise escapes the root.
    /// </summary>
    public static string? SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var rel = relative.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || Path.IsPathRooted(relative)) return null;
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is ".." or ".")) return null;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar, segments)));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>Persisted update bookkeeping (survives watchdog restarts and reboots).</summary>
/// <param name="Current">Version the service is configured to run, or null while still on the legacy layout.</param>
/// <param name="Previous">Retained previous version (rollback target).</param>
/// <param name="Pending">Version switched to but not yet health-confirmed; resumed after a restart.</param>
public sealed record UpdateState(string? Current, string? Previous, string? Pending);

/// <summary>JSON file store for <see cref="UpdateState"/>; writes are atomic (temp file + replace).</summary>
public sealed class UpdateStateStore(string path)
{
    public UpdateState Load()
    {
        try { return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)) ?? new UpdateState(null, null, null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new UpdateState(null, null, null); }
    }

    public void Save(UpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, overwrite: true);
    }
}

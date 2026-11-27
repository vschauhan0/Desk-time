namespace NISS.Application.Updates;

/// <summary>Stores uploaded agent packages (zip + manifest.json) as files on disk, like screenshots.</summary>
public interface IPackageStore
{
    /// <summary>Writes the zip and manifest under the package root. Returns the zip's path relative to the root.</summary>
    Task<string> SaveAsync(string version, byte[] zip, byte[] manifestJson, CancellationToken ct);

    /// <summary>Opens a stored zip for reading (streamed to agents).</summary>
    Stream OpenRead(string relPath);

    /// <summary>Removes the files for a stored package (used to roll back a failed upload).</summary>
    void Delete(string relPath);
}

/// <summary>Reads the Authenticode signer subject of a file, for display only (trust decisions use ISignatureChecker).</summary>
public interface ISignerSubjectReader
{
    string? GetSignerSubject(byte[] fileBytes);
}

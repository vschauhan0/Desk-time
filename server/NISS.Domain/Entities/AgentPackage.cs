namespace NISS.Domain.Entities;

/// <summary>
/// One uploaded, server-verified agent update package. The zip (and its manifest.json) live on disk under the
/// configured package root at <see cref="RelPath"/>; this row is the index. Versions are unique.
/// </summary>
public sealed class AgentPackage
{
    public Guid Id { get; set; }
    /// <summary>Dotted version from the manifest (e.g. 1.4.0.0); unique.</summary>
    public string Version { get; set; } = "";
    /// <summary>Path of the package zip relative to the package storage root.</summary>
    public string RelPath { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Lowercase hex SHA-256 of the whole zip.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>Authenticode signer subject of the first executable, when it could be read.</summary>
    public string? SignerSubject { get; set; }
    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public string? Notes { get; set; }
}

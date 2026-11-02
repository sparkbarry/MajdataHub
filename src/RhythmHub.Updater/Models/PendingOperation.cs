namespace RhythmHub.Updater.Models;

public enum PendingStage
{
    /// <summary><c>downloads/{v}.zip.partial</c> may hold a prefix of the package.</summary>
    Downloading,

    /// <summary><c>downloads/{v}.zip</c> exists and matched the manifest hash.</summary>
    Downloaded,

    /// <summary>Extraction / move in progress; current.json may or may not have been committed.</summary>
    Installing,
}

/// <summary>
/// Contents of <c>games/{gameId}/pending.json</c>: what an interrupted update was doing.
/// Never evidence that a version is installed; only current.json is.
/// </summary>
public sealed record PendingOperation
{
    public int SchemaVersion { get; init; } = 1;
    public required string Version { get; init; }
    public required string Sha256 { get; init; }
    public required string PackageUrl { get; init; }
    public long SizeBytes { get; init; }
    public PendingStage Stage { get; init; }

    /// <summary>Validator for If-Range so a resumed download never splices two different files.</summary>
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsFor(GameRelease release) =>
        Version == release.Version && Sha256 == release.Sha256 && PackageUrl == release.PackageUrl.AbsoluteUri;
}

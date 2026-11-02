using System.Text.Json.Serialization;

namespace RhythmHub.Updater.Models;

/// <summary>
/// Signed channel pointer at <c>{gameId}/{platform}/{channel}.json</c>. Replacing it is the publish (or rollback) switch.
/// It binds the exact bytes of the release manifest through <see cref="ManifestSha256"/>.
/// </summary>
public sealed record ChannelPointer
{
    public int SchemaVersion { get; init; }
    public required string GameId { get; init; }
    public required string Platform { get; init; }
    public required string Channel { get; init; }
    public required string Version { get; init; }
    public required string ManifestSha256 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManifestSignature? Signature { get; init; }
}

/// <summary>
/// Signed per-version file list at <c>{gameId}/{platform}/{version}.json</c>. The files themselves live, unpacked,
/// under <c>{gameId}/{platform}/{version}/</c>. Symlinks, permissions and empty directories cannot be stored as
/// S3 objects, so they are carried here and recreated by the launcher.
/// </summary>
public sealed record ReleaseManifest
{
    public int SchemaVersion { get; init; }
    public required string GameId { get; init; }
    public required string Platform { get; init; }
    public required string Version { get; init; }

    /// <summary>Top-level executable (Windows) or .app bundle (macOS), relative to the version root.</summary>
    public required string Entrypoint { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumLauncherVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseNotesUrl { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }
    public IReadOnlyList<ManifestSymlink> Symlinks { get; init; } = [];

    /// <summary>Directories with no entries below them; all others are implied by file paths.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManifestSignature? Signature { get; init; }

    [JsonIgnore]
    public long InstalledSizeBytes => Files.Sum(f => f.Size);
}

/// <param name="Path">Forward-slash path relative to the version root; also the object key suffix.</param>
/// <param name="Mode">Unix permission bits (0..0o777). Omitted for Windows builds.</param>
public sealed record ManifestFile(
    string Path,
    long Size,
    string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Mode = null);

public sealed record ManifestSymlink(string Path, string Target);

public sealed record ManifestSignature
{
    public required string Algorithm { get; init; }
    public required string KeyId { get; init; }
    public required string Value { get; init; }
}

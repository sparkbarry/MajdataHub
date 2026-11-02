using System.Text.Json.Serialization;

namespace RhythmHub.Updater.Models;

/// <summary>
/// Channel manifest served at <c>game/{gameId}/{platform}/{channel}.json</c>.
/// See docs/update-protocol.md.
/// </summary>
public sealed record GameManifest
{
    public int SchemaVersion { get; init; }
    public required string GameId { get; init; }
    public required string Channel { get; init; }
    public required string Platform { get; init; }
    public required string Version { get; init; }
    public string? MinimumLauncherVersion { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public required ManifestPackage Package { get; init; }
    public required string Entrypoint { get; init; }

    /// <summary>Ed25519 signature over the canonicalized manifest. Verified from phase B on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManifestSignature? Signature { get; init; }
}

public sealed record ManifestPackage
{
    public required string Url { get; init; }
    public long SizeBytes { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Unpacked size, used for the disk space check. Optional.</summary>
    public long? InstalledSizeBytes { get; init; }
}

public sealed record ManifestSignature
{
    public required string Algorithm { get; init; }
    public required string KeyId { get; init; }
    public required string Value { get; init; }
}

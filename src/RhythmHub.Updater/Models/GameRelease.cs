namespace RhythmHub.Updater.Models;

public sealed record GameRelease(
    string GameId,
    string Version,
    string Platform,
    Uri PackageUrl,
    long SizeBytes,
    long? InstalledSizeBytes,
    string Sha256,
    string Entrypoint,
    Uri? ReleaseNotesUrl)
{
    public static GameRelease FromManifest(GameManifest m) => new(
        m.GameId,
        m.Version,
        m.Platform,
        new Uri(m.Package.Url),
        m.Package.SizeBytes,
        m.Package.InstalledSizeBytes,
        m.Package.Sha256.ToLowerInvariant(),
        m.Entrypoint,
        m.ReleaseNotesUrl is null ? null : new Uri(m.ReleaseNotesUrl));
}

public sealed record DownloadProgress(long CompletedBytes, long TotalBytes);

/// <summary>A version that is present under <c>versions/</c> and referenced by current.json.</summary>
public sealed record InstalledGame(string Version, string VersionDirectory, string Entrypoint);

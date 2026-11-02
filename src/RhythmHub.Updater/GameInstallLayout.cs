namespace RhythmHub.Updater;

/// <summary>Per-game directory layout under the install root. All paths share one volume so renames stay atomic.</summary>
public sealed class GameInstallLayout(string installRoot, string gameId)
{
    public string GameId { get; } = gameId;
    public string GameRoot { get; } = Path.Combine(installRoot, "games", gameId);
    public string VersionsDirectory => Path.Combine(GameRoot, "versions");
    public string DownloadsDirectory => Path.Combine(GameRoot, "downloads");
    public string StagingDirectory => Path.Combine(GameRoot, "staging");
    public string CurrentStatePath => Path.Combine(GameRoot, "current.json");
    public string PendingStatePath => Path.Combine(GameRoot, "pending.json");
    public string HealthFilePath => Path.Combine(GameRoot, "health", "health.json");

    public string VersionDirectory(string version) => Path.Combine(VersionsDirectory, SafeName(version));
    public string ReleaseInfoPath(string version) => Path.Combine(VersionDirectory(version), ManifestPaths.ReservedDirectory, "release.json");
    public string ManifestCopyPath(string version) => Path.Combine(VersionDirectory(version), ManifestPaths.ReservedDirectory, "manifest.json");

    /// <summary>Content-addressed store for one version's download: <c>downloads/{v}/{sha256}</c>.</summary>
    public string ObjectsDirectory(string version) => Path.Combine(DownloadsDirectory, SafeName(version));
    public string ObjectPath(string version, string sha256) => Path.Combine(ObjectsDirectory(version), sha256);
    public string PartialObjectPath(string version, string sha256) => ObjectPath(version, sha256) + ".partial";

    public void EnsureCreated()
    {
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
    }

    /// <summary>Versions and ids come from the network; never let them become path separators.</summary>
    internal static string SafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')))
        {
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Illegal identifier '{value}'.");
        }
        return value;
    }
}

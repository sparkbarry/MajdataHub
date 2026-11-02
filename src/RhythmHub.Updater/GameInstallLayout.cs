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

    public string VersionDirectory(string version) => Path.Combine(VersionsDirectory, SafeName(version));
    public string ReleaseInfoPath(string version) => Path.Combine(VersionDirectory(version), ".rhythmhub", "release.json");
    public string PartialDownloadPath(string version) => Path.Combine(DownloadsDirectory, $"{SafeName(version)}.zip.partial");
    public string CompletedDownloadPath(string version) => Path.Combine(DownloadsDirectory, $"{SafeName(version)}.zip");

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

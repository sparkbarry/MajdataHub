namespace RhythmHub.Updater;

public sealed record UpdaterOptions
{
    /// <summary>Root for all launcher-managed data, e.g. <c>~/Library/Application Support/RhythmHub</c>.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>Runtime identifier style platform id: win-x64, osx-arm64, osx-x64.</summary>
    public required string PlatformId { get; init; }

    /// <summary>Base URL of the static feed; manifests live at <c>{FeedBaseUri}game/{gameId}/{platform}/{channel}.json</c>.</summary>
    public required Uri FeedBaseUri { get; init; }

    public string Channel { get; init; } = "stable";

    public required Version LauncherVersion { get; init; }

    /// <summary>Extra free space required on top of package + installed size.</summary>
    public long DiskSafetyMarginBytes { get; init; } = 256L * 1024 * 1024;
}

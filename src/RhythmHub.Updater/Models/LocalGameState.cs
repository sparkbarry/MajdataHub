namespace RhythmHub.Updater.Models;

/// <summary>Contents of <c>games/{gameId}/current.json</c>. Switching this file is the install commit point.</summary>
public sealed record LocalGameState
{
    public int SchemaVersion { get; init; } = 1;
    public string? ActiveVersion { get; init; }
    public string? PreviousGoodVersion { get; init; }
    public string Channel { get; init; } = "stable";
    public bool LastLaunchSuccessful { get; init; }
}

/// <summary>Written next to each installed version as <c>versions/{v}/.rhythmhub/release.json</c>.</summary>
public sealed record InstalledReleaseInfo
{
    public required string Version { get; init; }
    public required string Entrypoint { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
}

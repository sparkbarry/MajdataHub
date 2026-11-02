using System.Text.Json;

namespace RhythmHub.Launcher.Services;

/// <param name="HealthCheck">True once the game writes the launch health marker (docs/update-protocol.md §7).</param>
public sealed record GameEntry(string Id, string DisplayName, string? Description, bool HealthCheck = false);

/// <summary>Built-in game catalog shipped as games.json next to the executable.</summary>
public sealed record LauncherConfig(string FeedBaseUrl, string Channel, IReadOnlyList<GameEntry> Games)
{
    /// <summary>Overrides the feed for local testing, e.g. http://127.0.0.1:8787/.</summary>
    public const string FeedOverrideVariable = "RHYTHMHUB_FEED_URL";

    public static LauncherConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "games.json");
        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("games.json is empty.");

        return Environment.GetEnvironmentVariable(FeedOverrideVariable) is { Length: > 0 } feed
            ? config with { FeedBaseUrl = feed }
            : config;
    }
}

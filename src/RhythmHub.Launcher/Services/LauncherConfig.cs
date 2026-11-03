using System.Globalization;
using System.Text.Json;

namespace RhythmHub.Launcher.Services;

/// <param name="HealthCheck">True once the game writes the launch health marker (docs/update-protocol.md §7).</param>
/// <param name="Color">The game's tile color in the launcher, as a hex string; shown behind the initial when there is no icon.</param>
/// <param name="Icon">Path of the game's icon inside the launcher's Assets, e.g. "Games/OurTaikoPlay.png".</param>
/// <param name="Descriptions">Translations of <paramref name="Description"/> keyed by language ("zh", "ja").</param>
public sealed record GameEntry(
    string Id, string DisplayName, string? Description, bool HealthCheck = false, string? Color = null, string? Icon = null,
    IReadOnlyDictionary<string, string>? Descriptions = null)
{
    /// <summary>The description in the current UI language, falling back to the English one.</summary>
    public string LocalizedDescription =>
        Descriptions?.GetValueOrDefault(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? Description ?? "";
}

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

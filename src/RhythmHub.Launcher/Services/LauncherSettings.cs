using System.Globalization;
using System.Text.Json;
using RhythmHub.Launcher.Resources;

namespace RhythmHub.Launcher.Services;

/// <summary>A release channel the player can pick for a game.</summary>
/// <param name="ShortLabel">The label in the segmented channel switch.</param>
public sealed record ChannelOption(string Id, string Label, string ShortLabel)
{
    public static readonly IReadOnlyList<ChannelOption> All =
    [
        new("stable", Strings.Channel_Stable, Strings.Channel_Stable),
        new("nightly", Strings.Channel_Nightly, Strings.Channel_NightlyShort),
    ];

    public static ChannelOption Find(string id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
}

/// <summary>A UI language the player can pick; an empty id follows the system.</summary>
public sealed record LanguageOption(string Id, string Label)
{
    public static readonly IReadOnlyList<LanguageOption> All =
    [
        new("", Strings.Language_System),
        new("zh", "中文"),
        new("en", "English"),
        new("ja", "日本語"),
    ];

    public static LanguageOption Find(string? id) => All.FirstOrDefault(l => l.Id == id) ?? All[0];

    /// <summary>Switches resource lookups to <paramref name="id"/>; must run before any window is built.</summary>
    public static void Apply(string? id)
    {
        if (string.IsNullOrEmpty(id) || Find(id).Id != id) return;
        var culture = CultureInfo.GetCultureInfo(id);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

/// <summary>Per-user launcher preferences in {installRoot}/settings.json; survives launcher updates.</summary>
public sealed class LauncherSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();
    private Data _data;

    private LauncherSettings(string path, Data data)
    {
        _path = path;
        _data = data;
    }

    public static LauncherSettings Load(string installRoot)
    {
        var path = Path.Combine(installRoot, "settings.json");
        Data? data = null;
        try
        {
            if (File.Exists(path))
                data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // A damaged settings file only loses preferences; start over.
        }
        return new LauncherSettings(path, data ?? new Data());
    }

    /// <summary>The channel picked for <paramref name="gameId"/>, or <paramref name="fallback"/> when none was.</summary>
    public string GetChannel(string gameId, string fallback)
    {
        lock (_lock)
            return _data.Channels.TryGetValue(gameId, out var channel) ? channel : fallback;
    }

    public void SetChannel(string gameId, string channel)
    {
        lock (_lock)
            Save(_data with { Channels = new Dictionary<string, string>(_data.Channels) { [gameId] = channel } });
    }

    /// <summary>The picked UI language ("zh", "en", "ja"), or null to follow the system.</summary>
    public string? Language
    {
        get
        {
            lock (_lock)
                return _data.Language;
        }
    }

    public void SetLanguage(string? language)
    {
        lock (_lock)
            Save(_data with { Language = string.IsNullOrEmpty(language) ? null : language });
    }

    private void Save(Data data)
    {
        _data = data;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Data
    {
        public Dictionary<string, string> Channels { get; init; } = [];
        public string? Language { get; init; }
    }
}

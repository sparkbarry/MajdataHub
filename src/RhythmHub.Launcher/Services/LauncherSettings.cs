using System.Text.Json;

namespace RhythmHub.Launcher.Services;

/// <summary>A release channel the player can pick for a game.</summary>
public sealed record ChannelOption(string Id, string Label)
{
    public static readonly IReadOnlyList<ChannelOption> All =
    [
        new("stable", "稳定版"),
        new("nightly", "每日构建（Nightly）"),
    ];

    public static ChannelOption Find(string id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
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
        {
            _data = _data with { Channels = new Dictionary<string, string>(_data.Channels) { [gameId] = channel } };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private sealed record Data
    {
        public Dictionary<string, string> Channels { get; init; } = [];
    }
}

using System.Reflection;
using RhythmHub.Platform;
using RhythmHub.Updater;

namespace RhythmHub.Launcher.Services;

/// <summary>Composition root: one shared HttpClient, logger and platform for all games.</summary>
public sealed class LauncherContext : IDisposable
{
    private LauncherContext(LauncherConfig config, IGamePlatform platform, UpdaterOptions options,
        HttpClient http, FileLoggerFactory loggers)
    {
        Config = config;
        Platform = platform;
        Options = options;
        Http = http;
        Loggers = loggers;
    }

    /// <summary>Redirects all game data for local testing so the real install stays untouched.</summary>
    public const string InstallRootOverrideVariable = "RHYTHMHUB_INSTALL_ROOT";

    public LauncherConfig Config { get; }
    public IGamePlatform Platform { get; }
    public UpdaterOptions Options { get; }
    public HttpClient Http { get; }
    public FileLoggerFactory Loggers { get; }

    public static LauncherContext Create()
    {
        var config = LauncherConfig.Load();
        var platform = GamePlatform.Current();
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        var options = new UpdaterOptions
        {
            InstallRoot = Environment.GetEnvironmentVariable(InstallRootOverrideVariable) is { Length: > 0 } root
                ? Path.GetFullPath(root)
                : platform.DefaultInstallRoot,
            PlatformId = platform.PlatformId,
            FeedBaseUri = new Uri(config.FeedBaseUrl),
            Channel = config.Channel,
            LauncherVersion = new Version(version.Major, version.Minor, Math.Max(version.Build, 0)),
        };
        var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            // Per-request timeouts are handled by cancellation; a whole-download timeout would break slow links.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RhythmHub/{options.LauncherVersion} ({options.PlatformId})");
        var loggers = new FileLoggerFactory(Path.Combine(options.InstallRoot, "logs"));
        return new LauncherContext(config, platform, options, http, loggers);
    }

    public void Dispose()
    {
        Http.Dispose();
        Loggers.Dispose();
    }
}

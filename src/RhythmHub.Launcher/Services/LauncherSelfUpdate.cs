using Microsoft.Extensions.Logging;
using Velopack;

namespace RhythmHub.Launcher.Services;

/// <summary>
/// Updates the launcher itself through Velopack. The feed is {feedBaseUrl}RhythmHub/{platform}/ on the
/// same CDN as the games (docs/operations.md §8). Does nothing when the launcher runs from a build
/// output instead of a Velopack install.
/// </summary>
public sealed class LauncherSelfUpdate
{
    private readonly UpdateManager _manager;
    private readonly ILogger _log;
    private VelopackAsset? _ready;

    public LauncherSelfUpdate(Uri feedBaseUri, string platformId, ILogger log)
    {
        _manager = new UpdateManager(new Uri(feedBaseUri, $"RhythmHub/{platformId}/").AbsoluteUri);
        _log = log;
    }

    /// <summary>Downloads the newest launcher release, if any; returns its version once it is ready to apply.</summary>
    public async Task<string?> PrepareAsync()
    {
        if (!_manager.IsInstalled)
            return null;
        if (_manager.UpdatePendingRestart is { } pending)
        {
            _ready = pending;
            return pending.Version.ToString();
        }
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
            return null;
        _log.LogInformation("Launcher update {Version} found, downloading", info.TargetFullRelease.Version);
        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _ready = info.TargetFullRelease;
        return _ready.Version.ToString();
    }

    /// <summary>Exits, installs the downloaded release and starts the new launcher.</summary>
    public void ApplyAndRestart()
    {
        if (_ready is not null)
            _manager.ApplyUpdatesAndRestart(_ready);
    }
}

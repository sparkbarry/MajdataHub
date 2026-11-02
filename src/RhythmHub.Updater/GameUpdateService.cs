using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>Platform-specific hooks the updater needs but must not implement itself.</summary>
public interface IPackageVerifier
{
    Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct);
    Task<bool> IsGameRunningAsync(string versionDirectory, string entrypoint, CancellationToken ct);
}

public sealed class GameUpdateService : IGameUpdateService
{
    private readonly UpdaterOptions _options;
    private readonly GameInstallLayout _layout;
    private readonly VersionRepository _versions;
    private readonly InstallationManager _installer;
    private readonly ManifestClient _manifests;
    private readonly DownloadService _downloads;
    private readonly IPackageVerifier _platform;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameUpdateService(string gameId, UpdaterOptions options, HttpClient http, IPackageVerifier platform, ILoggerFactory loggers)
    {
        GameId = GameInstallLayout.SafeName(gameId);
        _options = options;
        _layout = new GameInstallLayout(options.InstallRoot, gameId);
        _versions = new VersionRepository(_layout);
        _installer = new InstallationManager(_layout, _versions, loggers.CreateLogger<InstallationManager>());
        _manifests = new ManifestClient(http, loggers.CreateLogger<ManifestClient>());
        _downloads = new DownloadService(http, loggers.CreateLogger<DownloadService>());
        _platform = platform;
        _logger = loggers.CreateLogger<GameUpdateService>();
    }

    public string GameId { get; }
    public GameInstallLayout Layout => _layout;

    public Task<InstalledGame?> GetInstalledAsync(CancellationToken ct) => _versions.GetActiveAsync(ct);

    public async Task<GameRelease?> CheckAsync(CancellationToken ct)
    {
        var manifest = await _manifests.FetchAsync(_options, GameId, ct);
        var installed = await GetInstalledAsync(ct);
        _logger.LogInformation("{Game}: installed {Installed}, channel {Channel} has {Remote}",
            GameId, installed?.Version ?? "none", _options.Channel, manifest.Version);
        // Any difference counts: the channel manifest is authoritative, so an older version there is a deliberate rollback.
        return installed?.Version == manifest.Version ? null : GameRelease.FromManifest(manifest);
    }

    public async Task DownloadAsync(GameRelease release, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _layout.EnsureCreated();
            EnsureDiskSpace(release);

            var partial = _layout.PartialDownloadPath(release.Version);
            var completed = _layout.CompletedDownloadPath(release.Version);
            if (File.Exists(completed) && await FileHasher.ComputeSha256Async(completed, ct) == release.Sha256)
            {
                _logger.LogInformation("Reusing verified download {Path}", completed);
                progress?.Report(new DownloadProgress(release.SizeBytes, release.SizeBytes));
                return;
            }

            await _downloads.DownloadAsync(release.PackageUrl, partial, release.SizeBytes, progress, ct);

            var actual = await FileHasher.ComputeSha256Async(partial, ct);
            if (actual != release.Sha256)
            {
                File.Delete(partial);
                throw new UpdateException(UpdateErrorCode.HashMismatch,
                    $"Package hash mismatch (expected {release.Sha256}, got {actual}).");
            }
            File.Move(partial, completed, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InstallAsync(GameRelease release, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var completed = _layout.CompletedDownloadPath(release.Version);
            if (!File.Exists(completed))
                throw new UpdateException(UpdateErrorCode.DownloadIncomplete, "Package has not been downloaded.");
            await EnsureNotRunningAsync(ct);
            await _installer.InstallAsync(release, completed, _platform.VerifyPlatformPackageAsync, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task VerifyInstalledAsync(CancellationToken ct)
    {
        var installed = await GetInstalledAsync(ct)
            ?? throw new UpdateException(UpdateErrorCode.NotInstalled, $"{GameId} is not installed.");
        await _platform.VerifyPlatformPackageAsync(installed.VersionDirectory, installed.Entrypoint, ct);
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureNotRunningAsync(ct);
            await _installer.RollbackAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records that the active version started. Phase B replaces this with the in-game health marker.</summary>
    public async Task MarkLaunchedAsync(CancellationToken ct)
    {
        var state = await _versions.LoadAsync(ct);
        if (!state.LastLaunchSuccessful)
            await _versions.SaveAsync(state with { LastLaunchSuccessful = true }, ct);
    }

    private async Task EnsureNotRunningAsync(CancellationToken ct)
    {
        if (await GetInstalledAsync(ct) is { } installed &&
            await _platform.IsGameRunningAsync(installed.VersionDirectory, installed.Entrypoint, ct))
            throw new UpdateException(UpdateErrorCode.GameRunning, "Close the game before updating.");
    }

    private void EnsureDiskSpace(GameRelease release)
    {
        var installed = release.InstalledSizeBytes ?? release.SizeBytes * 2;
        var required = release.SizeBytes + installed + _options.DiskSafetyMarginBytes;
        var root = Path.GetPathRoot(Path.GetFullPath(_layout.GameRoot));
        if (root is null) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < required)
            throw new UpdateException(UpdateErrorCode.InsufficientDiskSpace,
                $"Need {required / 1048576} MB free, only {free / 1048576} MB available.");
    }
}

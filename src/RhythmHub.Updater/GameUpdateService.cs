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

    public GameUpdateService(string gameId, UpdaterOptions options, HttpClient http, IPackageVerifier platform,
        ILoggerFactory loggers, DownloadService? downloads = null)
    {
        GameId = GameInstallLayout.SafeName(gameId);
        _options = options;
        _layout = new GameInstallLayout(options.InstallRoot, gameId);
        _versions = new VersionRepository(_layout);
        _installer = new InstallationManager(_layout, _versions, loggers.CreateLogger<InstallationManager>());
        _manifests = new ManifestClient(http, loggers.CreateLogger<ManifestClient>());
        _downloads = downloads ?? new DownloadService(http, loggers.CreateLogger<DownloadService>());
        _platform = platform;
        _logger = loggers.CreateLogger<GameUpdateService>();
        Health = new LaunchHealth(_layout);
    }

    public string GameId { get; }
    public GameInstallLayout Layout => _layout;
    public LaunchHealth Health { get; }

    public Task<InstalledGame?> GetInstalledAsync(CancellationToken ct) => _versions.GetActiveAsync(ct);

    public Task<LocalGameState> GetStateAsync(CancellationToken ct) => _versions.LoadAsync(ct);

    public Task<PendingOperation?> GetPendingAsync(CancellationToken ct) =>
        AtomicFile.ReadJsonAsync<PendingOperation>(_layout.PendingStatePath, ct);

    /// <summary>
    /// Brings the game directory back to a consistent state after a crash or forced quit.
    /// Must run before any other operation, while no other launcher instance is active.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var pending = await GetPendingAsync(ct);
            var state = await _versions.LoadAsync(ct);
            if (pending is not null)
            {
                var completed = _layout.CompletedDownloadPath(pending.Version);
                var partial = _layout.PartialDownloadPath(pending.Version);
                if (pending.Stage == PendingStage.Installing && state.ActiveVersion == pending.Version)
                {
                    _logger.LogInformation("Recovery: {Version} was committed before the interruption", pending.Version);
                    File.Delete(completed);
                    await ClearPendingAsync();
                }
                else if (pending.Stage is PendingStage.Installing or PendingStage.Downloaded && File.Exists(completed))
                {
                    _logger.LogInformation("Recovery: {Version} is downloaded but not installed", pending.Version);
                    await SavePendingAsync(pending with { Stage = PendingStage.Downloaded }, ct);
                }
                else if (File.Exists(partial))
                {
                    _logger.LogInformation("Recovery: {Version} download can resume from {Bytes} bytes",
                        pending.Version, new FileInfo(partial).Length);
                    await SavePendingAsync(pending with { Stage = PendingStage.Downloading }, ct);
                }
                else
                {
                    _logger.LogInformation("Recovery: nothing left of pending {Version}", pending.Version);
                    await ClearPendingAsync();
                }
            }
            // Drops half-built staging dirs and any version directory current.json does not reference.
            await _installer.PruneAsync(ct);
            CleanStrayDownloads(await GetPendingAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GameRelease?> CheckAsync(CancellationToken ct)
    {
        var manifest = await _manifests.FetchAsync(_options, GameId, ct);
        var installed = await GetInstalledAsync(ct);
        _logger.LogInformation("{Game}: installed {Installed}, channel {Channel} has {Remote}",
            GameId, installed?.Version ?? "none", _options.Channel, manifest.Version);
        // Any difference counts: the channel manifest is authoritative, so an older version there is a deliberate rollback.
        return installed?.Version == manifest.Version ? null : GameRelease.FromManifest(manifest);
    }

    /// <summary>Bytes already on disk for <paramref name="release"/> from an earlier, interrupted attempt.</summary>
    public async Task<long> GetResumableBytesAsync(GameRelease release, CancellationToken ct)
    {
        if (await GetPendingAsync(ct) is not { } pending || !pending.IsFor(release)) return 0;
        if (pending.Stage != PendingStage.Downloading) return release.SizeBytes;
        var partial = _layout.PartialDownloadPath(release.Version);
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    public async Task DownloadAsync(GameRelease release, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _layout.EnsureCreated();
            var completed = _layout.CompletedDownloadPath(release.Version);
            var partial = _layout.PartialDownloadPath(release.Version);

            var pending = await GetPendingAsync(ct);
            if (pending is not null && !pending.IsFor(release))
            {
                _logger.LogInformation("Abandoning pending {Old} in favour of {New}", pending.Version, release.Version);
                DeleteDownloads(pending.Version);
                pending = null;
            }

            if (pending?.Stage is PendingStage.Downloaded or PendingStage.Installing && File.Exists(completed))
            {
                _logger.LogInformation("Reusing verified download {Path}", completed);
                progress?.Report(new DownloadProgress(release.SizeBytes, release.SizeBytes));
                return;
            }

            if (pending is null) DeleteDownloads(release.Version);
            EnsureDiskSpace(release, File.Exists(partial) ? new FileInfo(partial).Length : 0);

            pending ??= new PendingOperation
            {
                Version = release.Version,
                Sha256 = release.Sha256,
                PackageUrl = release.PackageUrl.AbsoluteUri,
                SizeBytes = release.SizeBytes,
                Stage = PendingStage.Downloading,
            };
            await SavePendingAsync(pending, ct);

            var checkpoint = pending.ETag is null && pending.LastModified is null
                ? null
                : new DownloadCheckpoint(pending.ETag, pending.LastModified);
            await _downloads.DownloadAsync(release.PackageUrl, partial, release.SizeBytes, checkpoint,
                async cp =>
                {
                    pending = pending with { ETag = cp.ETag, LastModified = cp.LastModified };
                    await SavePendingAsync(pending, CancellationToken.None);
                },
                progress, ct);

            var actual = await FileHasher.ComputeSha256Async(partial, ct);
            if (actual != release.Sha256)
            {
                DeleteDownloads(release.Version);
                await ClearPendingAsync();
                throw new UpdateException(UpdateErrorCode.HashMismatch,
                    $"Package hash mismatch (expected {release.Sha256}, got {actual}).");
            }
            File.Move(partial, completed, overwrite: true);
            await SavePendingAsync(pending with { Stage = PendingStage.Downloaded }, ct);
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
            if (await GetPendingAsync(ct) is not { Stage: PendingStage.Downloaded } pending || !pending.IsFor(release) ||
                !File.Exists(completed))
                throw new UpdateException(UpdateErrorCode.DownloadIncomplete, "Package has not been downloaded.");
            await EnsureNotRunningAsync(ct);

            await SavePendingAsync(pending with { Stage = PendingStage.Installing }, ct);
            try
            {
                await _installer.InstallAsync(release, completed, _platform.VerifyPlatformPackageAsync, ct);
            }
            catch
            {
                // Not committed: the verified zip is still there, so the next attempt skips the download.
                if ((await _versions.LoadAsync(CancellationToken.None)).ActiveVersion != release.Version)
                    await SavePendingAsync(pending with { Stage = PendingStage.Downloaded }, CancellationToken.None);
                throw;
            }
            await ClearPendingAsync();
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

    /// <summary>Records the result of a launch handshake for the active version.</summary>
    public async Task RecordLaunchAsync(string version, LaunchOutcome outcome, CancellationToken ct)
    {
        var state = await _versions.LoadAsync(ct);
        if (state.ActiveVersion != version) return;
        // A version that launched well before stays known-good after a later timeout; a crash revokes it.
        var next = outcome switch
        {
            LaunchOutcome.Healthy => true,
            LaunchOutcome.TimedOut => state.LastLaunchSuccessful,
            _ => false,
        };
        if (next != state.LastLaunchSuccessful)
            await _versions.SaveAsync(state with { LastLaunchSuccessful = next }, ct);
        _logger.LogInformation("{Game} {Version} launch outcome {Outcome}", GameId, version, outcome);
    }

    private async Task EnsureNotRunningAsync(CancellationToken ct)
    {
        if (await GetInstalledAsync(ct) is { } installed &&
            await _platform.IsGameRunningAsync(installed.VersionDirectory, installed.Entrypoint, ct))
            throw new UpdateException(UpdateErrorCode.GameRunning, "Close the game before updating.");
    }

    private void EnsureDiskSpace(GameRelease release, long alreadyDownloaded)
    {
        var installed = release.InstalledSizeBytes ?? release.SizeBytes * 2;
        var required = release.SizeBytes - alreadyDownloaded + installed + _options.DiskSafetyMarginBytes;
        var root = Path.GetPathRoot(Path.GetFullPath(_layout.GameRoot));
        if (root is null) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < required)
            throw new UpdateException(UpdateErrorCode.InsufficientDiskSpace,
                $"Need {required / 1048576} MB free, only {free / 1048576} MB available.");
    }

    private Task SavePendingAsync(PendingOperation pending, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(_layout.PendingStatePath, pending with { UpdatedAt = DateTimeOffset.UtcNow }, ct);

    private Task ClearPendingAsync()
    {
        File.Delete(_layout.PendingStatePath);
        return Task.CompletedTask;
    }

    private void DeleteDownloads(string version)
    {
        File.Delete(_layout.PartialDownloadPath(version));
        File.Delete(_layout.CompletedDownloadPath(version));
    }

    /// <summary>Anything in downloads/ that pending.json does not account for is garbage.</summary>
    private void CleanStrayDownloads(PendingOperation? pending)
    {
        if (!Directory.Exists(_layout.DownloadsDirectory)) return;
        var keep = pending is null
            ? new HashSet<string>()
            : [_layout.PartialDownloadPath(pending.Version), _layout.CompletedDownloadPath(pending.Version)];
        foreach (var file in Directory.GetFiles(_layout.DownloadsDirectory))
        {
            if (keep.Contains(file)) continue;
            _logger.LogInformation("Removing stray download {File}", file);
            File.Delete(file);
        }
    }
}

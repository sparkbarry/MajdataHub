using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

public interface IGameUpdateService
{
    string GameId { get; }
    Task<InstalledGame?> GetInstalledAsync(CancellationToken ct);

    /// <summary>Returns the channel release if it differs from the active version, otherwise null.</summary>
    Task RecoverAsync(CancellationToken ct);
    Task<GameRelease?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(GameRelease release, IProgress<DownloadProgress>? progress, CancellationToken ct);
    Task InstallAsync(GameRelease release, CancellationToken ct);
    Task VerifyInstalledAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

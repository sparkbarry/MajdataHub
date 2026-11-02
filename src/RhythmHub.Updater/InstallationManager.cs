using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>
/// Turns a verified zip into <c>versions/{v}</c> and commits it via current.json.
/// The previously active version is never touched before the commit.
/// </summary>
public sealed class InstallationManager(GameInstallLayout layout, VersionRepository versions, ILogger<InstallationManager> logger)
{
    /// <summary>Hard cap against zip bombs when the manifest does not declare an installed size.</summary>
    private const long DefaultMaxUnpackedBytes = 8L * 1024 * 1024 * 1024;

    /// <param name="verifyPackage">Platform check run on the staged directory before it is moved into place.</param>
    public async Task InstallAsync(GameRelease release, string zipPath,
        Func<string, string, CancellationToken, Task> verifyPackage, CancellationToken ct)
    {
        layout.EnsureCreated();
        var staging = Path.Combine(layout.StagingDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var limit = release.InstalledSizeBytes is { } declared ? declared + declared / 10 : DefaultMaxUnpackedBytes;
            logger.LogInformation("Extracting {Zip} to {Staging}", zipPath, staging);
            await ArchiveExtractor.ExtractAsync(zipPath, staging, limit, ct);

            var entry = Path.Combine(staging, release.Entrypoint);
            if (!File.Exists(entry) && !Directory.Exists(entry))
                throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Entrypoint '{release.Entrypoint}' missing from package.");
            await verifyPackage(staging, release.Entrypoint, ct);

            await AtomicFile.WriteJsonAsync(Path.Combine(staging, ".rhythmhub", "release.json"), new InstalledReleaseInfo
            {
                Version = release.Version,
                Entrypoint = release.Entrypoint,
                Sha256 = release.Sha256,
                InstalledAt = DateTimeOffset.UtcNow,
            }, ct);

            var state = await versions.LoadAsync(ct);
            var target = layout.VersionDirectory(release.Version);
            if (Directory.Exists(target))
            {
                if (state.ActiveVersion == release.Version)
                    throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Version {release.Version} is already active.");
                // A leftover from an earlier, uncommitted attempt: safe to replace.
                logger.LogWarning("Replacing stale version directory {Dir}", target);
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);

            // Commit point.
            var previous = state.ActiveVersion;
            await versions.SaveAsync(state with
            {
                ActiveVersion = release.Version,
                PreviousGoodVersion = previous ?? state.PreviousGoodVersion,
                LastLaunchSuccessful = false,
            }, ct);
            logger.LogInformation("Activated {Game} {Version} (previous {Previous})", layout.GameId, release.Version, previous);

            TryDelete(zipPath);
            await PruneAsync(ct);
        }
        finally
        {
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
        }
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        var state = await versions.LoadAsync(ct);
        if (state.PreviousGoodVersion is not { } previous || previous == state.ActiveVersion ||
            await versions.LoadReleaseInfoAsync(previous, ct) is null)
            throw new UpdateException(UpdateErrorCode.NoRollbackTarget, "No previous version is available.");

        await versions.SaveAsync(state with
        {
            ActiveVersion = previous,
            PreviousGoodVersion = state.ActiveVersion,
            LastLaunchSuccessful = false,
        }, ct);
        logger.LogInformation("Rolled back {Game} from {From} to {To}", layout.GameId, state.ActiveVersion, previous);
    }

    /// <summary>Keep the active and previous good versions; drop everything else, plus stale staging/download leftovers.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var state = await versions.LoadAsync(ct);
        var keep = new HashSet<string?>([state.ActiveVersion, state.PreviousGoodVersion]);
        if (Directory.Exists(layout.VersionsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(layout.VersionsDirectory))
                if (!keep.Contains(Path.GetFileName(dir))) TryDeleteDirectory(dir);
        }
        if (Directory.Exists(layout.StagingDirectory))
        {
            foreach (var dir in Directory.GetDirectories(layout.StagingDirectory)) TryDeleteDirectory(dir);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { logger.LogWarning(ex, "Could not delete {Path}", path); }
    }

    private void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}

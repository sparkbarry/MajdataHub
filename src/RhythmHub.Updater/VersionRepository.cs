using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

public sealed class VersionRepository(GameInstallLayout layout)
{
    public async Task<LocalGameState> LoadAsync(CancellationToken ct) =>
        await AtomicFile.ReadJsonAsync<LocalGameState>(layout.CurrentStatePath, ct) ?? new LocalGameState();

    public Task SaveAsync(LocalGameState state, CancellationToken ct) =>
        AtomicFile.WriteJsonAsync(layout.CurrentStatePath, state, ct);

    public Task<InstalledReleaseInfo?> LoadReleaseInfoAsync(string version, CancellationToken ct) =>
        AtomicFile.ReadJsonAsync<InstalledReleaseInfo>(layout.ReleaseInfoPath(version), ct);

    /// <summary>Returns the active version only if its directory and release info are present.</summary>
    public async Task<InstalledGame?> GetActiveAsync(CancellationToken ct)
    {
        var state = await LoadAsync(ct);
        if (state.ActiveVersion is null) return null;
        var info = await LoadReleaseInfoAsync(state.ActiveVersion, ct);
        if (info is null) return null;
        return new InstalledGame(info.Version, layout.VersionDirectory(info.Version), info.Entrypoint);
    }
}

using RhythmHub.Updater;

namespace RhythmHub.Platform;

public interface IGamePlatform : IPackageVerifier
{
    /// <summary>win-x64, osx-arm64 or osx-x64; matches the feed directory name.</summary>
    string PlatformId { get; }

    /// <summary>Per-user, writable root for games, downloads and logs. No admin rights needed.</summary>
    string DefaultInstallRoot { get; }

    /// <param name="environment">Extra variables for the game process (the health handshake uses these).</param>
    Task LaunchGameAsync(string versionDirectory, string entrypoint, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct);

    /// <summary>Opens a folder in Explorer / Finder.</summary>
    void RevealDirectory(string path);
}

public static class GamePlatform
{
    public const string AppFolderName = "RhythmHub";

    public static IGamePlatform Current()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new MacOSPlatform();
        throw new PlatformNotSupportedException("RhythmHub supports Windows and macOS.");
    }
}

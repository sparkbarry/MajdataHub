using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RhythmHub.Updater;

namespace RhythmHub.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IGamePlatform
{
    public string PlatformId => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "win-x64",
        var other => throw new PlatformNotSupportedException($"Windows on {other} is not supported yet."),
    };

    public string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), GamePlatform.AppFolderName);

    public Task LaunchGameAsync(string versionDirectory, string entrypoint, CancellationToken ct)
    {
        var exe = Path.Combine(versionDirectory, entrypoint);
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = versionDirectory,
            UseShellExecute = false,
        })?.Dispose();
        return Task.CompletedTask;
    }

    public Task<bool> IsGameRunningAsync(string versionDirectory, string entrypoint, CancellationToken ct)
    {
        var root = Path.GetFullPath(versionDirectory);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(entrypoint)))
        {
            using (p)
            {
                try
                {
                    // Only count processes started from this game's versions/ tree.
                    if (p.MainModule?.FileName is { } file &&
                        file.StartsWith(Path.GetDirectoryName(root)!, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(true);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Access denied or exited; it cannot be ours in a way that matters.
                }
            }
        }
        return Task.FromResult(false);
    }

    public Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct)
    {
        if (!entrypoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"Windows entrypoint must be an .exe, got '{entrypoint}'.");
        var exe = Path.Combine(unpackedDirectory, entrypoint);
        if (!File.Exists(exe))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, $"'{entrypoint}' not found.");
        if (!File.Exists(Path.Combine(unpackedDirectory, "UnityPlayer.dll")))
            throw new UpdateException(UpdateErrorCode.InvalidPackage, "UnityPlayer.dll not found; not a Unity Windows player.");
        // TODO(phase C): Authenticode check once release builds are signed.
        return Task.CompletedTask;
    }

    public void RevealDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}

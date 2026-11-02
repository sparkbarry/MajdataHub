using System.Security.Cryptography;
using System.Text.Json;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

public enum LaunchOutcome
{
    /// <summary>The game wrote a health marker with this launch's nonce (or, without health support, kept running).</summary>
    Healthy,

    /// <summary>The game process went away before reporting healthy.</summary>
    Exited,

    /// <summary>Still running, but no health marker within the deadline.</summary>
    TimedOut,
}

/// <summary>What the launcher passes to one game start; the game echoes Nonce back in the health file.</summary>
public sealed record LaunchTicket(string Version, string Nonce, string HealthFilePath, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Health handshake (docs/update-protocol.md §7): the launcher sets RHYTHMHUB_HEALTH_FILE / RHYTHMHUB_LAUNCH_NONCE,
/// the game writes {"nonce","version"} to that file once it has initialized.
/// </summary>
public sealed class LaunchHealth(GameInstallLayout layout)
{
    public const string HealthFileVariable = "RHYTHMHUB_HEALTH_FILE";
    public const string NonceVariable = "RHYTHMHUB_LAUNCH_NONCE";
    public const string VersionVariable = "RHYTHMHUB_GAME_VERSION";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>LaunchServices can take a moment before the process shows up; don't call it "exited" too early.</summary>
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromSeconds(5);

    public LaunchTicket Prepare(InstalledGame game)
    {
        var path = layout.HealthFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return new LaunchTicket(game.Version, nonce, path, new Dictionary<string, string>
        {
            [HealthFileVariable] = path,
            [NonceVariable] = nonce,
            [VersionVariable] = game.Version,
        });
    }

    /// <param name="healthSupported">False for games that don't implement the marker yet: surviving the grace period counts as healthy.</param>
    public async Task<LaunchOutcome> WaitAsync(LaunchTicket ticket, bool healthSupported,
        Func<CancellationToken, Task<bool>> isRunning, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var seenRunning = false;
        while (true)
        {
            if (healthSupported && await ReadMarkerAsync(ticket, ct)) return LaunchOutcome.Healthy;

            var elapsed = DateTimeOffset.UtcNow - started;
            var running = await isRunning(ct);
            seenRunning |= running;
            if (!running && (seenRunning || elapsed > StartupGrace))
                return healthSupported && await ReadMarkerAsync(ticket, ct) ? LaunchOutcome.Healthy : LaunchOutcome.Exited;
            if (!healthSupported && running && elapsed > StartupGrace) return LaunchOutcome.Healthy;
            if (elapsed > timeout) return LaunchOutcome.TimedOut;

            await Task.Delay(PollInterval, ct);
        }
    }

    private static async Task<bool> ReadMarkerAsync(LaunchTicket ticket, CancellationToken ct)
    {
        if (!File.Exists(ticket.HealthFilePath)) return false;
        try
        {
            await using var fs = new FileStream(ticket.HealthFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var marker = await JsonSerializer.DeserializeAsync<HealthMarker>(fs, UpdaterJson.Options, ct);
            return marker?.Nonce == ticket.Nonce && marker.Version == ticket.Version;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false; // Half-written; look again on the next poll.
        }
    }

    private sealed record HealthMarker(string? Nonce, string? Version);
}

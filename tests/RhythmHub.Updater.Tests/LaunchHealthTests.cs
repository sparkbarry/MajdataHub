using System.Text.Json;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

public class LaunchHealthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LaunchHealth Health, LaunchTicket Ticket, TempDir Dir) Setup()
    {
        var dir = new TempDir();
        var health = new LaunchHealth(new GameInstallLayout(dir.Path, "demo"))
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            StartupGrace = TimeSpan.FromMilliseconds(200),
        };
        var ticket = health.Prepare(new InstalledGame("1.0.0", "/nowhere", "Game.app"));
        return (health, ticket, dir);
    }

    private static void WriteMarker(LaunchTicket t, string nonce, string version) =>
        File.WriteAllText(t.HealthFilePath, JsonSerializer.Serialize(new { nonce, version }));

    [Fact]
    public void Ticket_exposes_handshake_environment()
    {
        var (_, t, dir) = Setup();
        using var cleanup = dir;
        Assert.Equal(t.HealthFilePath, t.Environment[LaunchHealth.HealthFileVariable]);
        Assert.Equal(t.Nonce, t.Environment[LaunchHealth.NonceVariable]);
        Assert.Equal("1.0.0", t.Environment[LaunchHealth.VersionVariable]);
        Assert.Equal(32, t.Nonce.Length);
    }

    [Fact]
    public async Task Matching_marker_is_healthy()
    {
        var (h, t, dir) = Setup();
        using var cleanup = dir;
        _ = Task.Delay(100, Ct).ContinueWith(_ => WriteMarker(t, t.Nonce, "1.0.0"), Ct);
        Assert.Equal(LaunchOutcome.Healthy, await h.WaitAsync(t, true, _ => Task.FromResult(true), TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Stale_marker_from_previous_launch_is_ignored()
    {
        var (h, t, dir) = Setup();
        using var cleanup = dir;
        WriteMarker(t, "old-nonce", "1.0.0");
        var calls = 0;
        var outcome = await h.WaitAsync(t, true, _ => Task.FromResult(++calls < 5), TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(LaunchOutcome.Exited, outcome);
    }

    [Fact]
    public async Task Process_that_never_appears_counts_as_exited_after_grace()
    {
        var (h, t, dir) = Setup();
        using var cleanup = dir;
        Assert.Equal(LaunchOutcome.Exited, await h.WaitAsync(t, true, _ => Task.FromResult(false), TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Running_without_marker_times_out()
    {
        var (h, t, dir) = Setup();
        using var cleanup = dir;
        Assert.Equal(LaunchOutcome.TimedOut,
            await h.WaitAsync(t, true, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(300), Ct));
    }

    [Fact]
    public async Task Without_health_support_surviving_grace_is_healthy()
    {
        var (h, t, dir) = Setup();
        using var cleanup = dir;
        Assert.Equal(LaunchOutcome.Healthy, await h.WaitAsync(t, false, _ => Task.FromResult(true), TimeSpan.FromSeconds(5), Ct));
    }
}

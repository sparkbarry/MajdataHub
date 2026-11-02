using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

public class ResumeAndRecoveryTests
{
    private const string G = Factory.Game;
    private const string P = Factory.Platform;
    private const string Big = "Game.app/Contents/Resources/big.bin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dropped_connection_is_retried_with_range_request()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").WithBigFile("a"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        feed.DropConnectionAfter = 100_000;

        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);

        var bigRequests = feed.FileRequests.Where(r => r.Uri.AbsolutePath.EndsWith("big.bin")).ToList();
        Assert.Equal(2, bigRequests.Count);
        Assert.Null(bigRequests[0].RangeFrom);
        Assert.Equal(100_000, bigRequests[1].RangeFrom);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Interrupted_download_resumes_in_a_new_session()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").WithBigFile("a"));

        // Session 1 gives up after one attempt, as if the launcher were closed mid-download.
        var first = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        var release = (await first.CheckAsync(Ct))!;
        feed.DropConnectionAfter = 60_000;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => first.DownloadAsync(release, null, Ct));
        Assert.Equal(UpdateErrorCode.Network, ex.Code);
        Assert.Equal(PendingStage.Downloading, (await first.GetPendingAsync(Ct))!.Stage);

        var second = Factory.Service(tmp.Path, feed);
        await second.RecoverAsync(Ct);
        Assert.True(await second.GetResumableBytesAsync(release, Ct) >= 60_000);
        await second.DownloadAsync(release, null, Ct);
        await second.InstallAsync(release, Ct);

        Assert.Equal(60_000, feed.FileRequests.Last(r => r.Uri.AbsolutePath.EndsWith("big.bin")).RangeFrom);
        Assert.Null(await second.GetPendingAsync(Ct));
        Assert.Equal("1.0.0", (await second.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Corrupt_partial_is_discarded_and_fetched_cleanly()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        var manifest = feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").WithBigFile("a"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        var big = manifest.Files.Single(f => f.Path == Big);
        Directory.CreateDirectory(svc.Layout.ObjectsDirectory("1.0.0"));
        File.WriteAllText(svc.Layout.PartialObjectPath("1.0.0", big.Sha256), new string('?', 50_000));

        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);

        var bigRequests = feed.FileRequests.Where(r => r.Uri.AbsolutePath.EndsWith("big.bin")).Select(r => r.RangeFrom).ToList();
        Assert.Equal([50_000L, null], bigRequests);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Pending_for_older_release_is_discarded()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1").WithBigFile("a"));
        var svc = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        feed.DropConnectionAfter = 50_000;
        await Assert.ThrowsAsync<UpdateException>(async () => await svc.DownloadAsync((await svc.CheckAsync(Ct))!, null, Ct));

        feed.Publish(G, P, "1.1.0", Factory.GameBuild("v2"));
        var newer = (await svc.CheckAsync(Ct))!;
        Assert.Equal(0, await svc.GetResumableBytesAsync(newer, Ct));
        await svc.DownloadAsync(newer, null, Ct);

        Assert.False(Directory.Exists(svc.Layout.ObjectsDirectory("1.0.0")));
        Assert.Equal("1.1.0", (await svc.GetPendingAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Recovery_after_crash_mid_install_refills_and_installs()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);

        // Simulate dying mid-assembly: stage Installing, one object already moved into a half-built staging dir.
        var pending = (await svc.GetPendingAsync(Ct))!;
        await AtomicFile.WriteJsonAsync(svc.Layout.PendingStatePath, pending with { Stage = PendingStage.Installing }, Ct);
        var moved = release.Manifest.Files[0];
        var staged = Path.Combine(svc.Layout.StagingDirectory, "abc", moved.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Move(svc.Layout.ObjectPath("1.0.0", moved.Sha256), staged);
        Directory.CreateDirectory(Path.Combine(svc.Layout.VersionDirectory("1.0.0"), "Game.app"));
        File.WriteAllText(Path.Combine(svc.Layout.DownloadsDirectory, "junk.partial"), "junk");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Equal(PendingStage.Downloading, (await restarted.GetPendingAsync(Ct))!.Stage);
        Assert.Empty(Directory.GetDirectories(restarted.Layout.StagingDirectory));
        Assert.False(Directory.Exists(restarted.Layout.VersionDirectory("1.0.0")));
        Assert.False(File.Exists(Path.Combine(restarted.Layout.DownloadsDirectory, "junk.partial")));

        feed.Log.Clear();
        await restarted.DownloadAsync(release, null, Ct);
        Assert.Single(feed.FileRequests); // only the consumed object is fetched again
        await restarted.InstallAsync(release, Ct);
        Assert.Equal("1.0.0", (await restarted.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Recovery_after_crash_right_after_commit_finishes_cleanup()
    {
        using var tmp = new TempDir();
        using var feed = new FakeFeed();
        feed.Publish(G, P, "1.0.0", Factory.GameBuild("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        var pending = (await svc.GetPendingAsync(Ct))!;
        await svc.InstallAsync(release, Ct);

        // Pretend the process died after current.json was written but before pending.json and objects were removed.
        await AtomicFile.WriteJsonAsync(svc.Layout.PendingStatePath, pending with { Stage = PendingStage.Installing }, Ct);
        Directory.CreateDirectory(svc.Layout.ObjectsDirectory("1.0.0"));
        File.WriteAllText(Path.Combine(svc.Layout.ObjectsDirectory("1.0.0"), "leftover"), "x");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Null(await restarted.GetPendingAsync(Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(restarted.Layout.DownloadsDirectory));
        Assert.Equal("1.0.0", (await restarted.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public void Second_instance_cannot_take_the_lock()
    {
        using var tmp = new TempDir();
        using (var first = InstanceLock.TryAcquire(tmp.Path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(tmp.Path));
        }
        using var again = InstanceLock.TryAcquire(tmp.Path);
        Assert.NotNull(again);
    }
}

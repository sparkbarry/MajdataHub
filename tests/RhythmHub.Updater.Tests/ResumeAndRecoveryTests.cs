using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

public class ResumeAndRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dropped_connection_is_retried_with_range_request()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var zip = Factory.BigGameZip("v1");
        feed.Publish("demo", Factory.Platform, "1.0.0", zip);
        var svc = Factory.Service(tmp.Path, feed);
        feed.DropConnectionAfter = zip.Length / 2;

        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);

        var packageRequests = feed.Log.Where(r => r.Uri.AbsolutePath.EndsWith(".zip")).ToList();
        Assert.Equal(2, packageRequests.Count);
        Assert.Null(packageRequests[0].RangeFrom);
        Assert.Equal(zip.Length / 2, packageRequests[1].RangeFrom);
        Assert.NotNull(packageRequests[1].IfRange);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Interrupted_download_resumes_in_a_new_session()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var zip = Factory.BigGameZip("v1");
        feed.Publish("demo", Factory.Platform, "1.0.0", zip);

        // Session 1: give up after one attempt, as if the launcher were closed mid-download.
        var first = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        feed.DropConnectionAfter = 60_000;
        var release = (await first.CheckAsync(Ct))!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => first.DownloadAsync(release, null, Ct));
        Assert.Equal(UpdateErrorCode.Network, ex.Code);

        var pending = await first.GetPendingAsync(Ct);
        Assert.Equal(PendingStage.Downloading, pending!.Stage);
        Assert.NotNull(pending.ETag);

        // Session 2.
        var second = Factory.Service(tmp.Path, feed);
        await second.RecoverAsync(Ct);
        Assert.Equal(60_000, await second.GetResumableBytesAsync(release, Ct));
        await second.DownloadAsync(release, null, Ct);
        await second.InstallAsync(release, Ct);

        Assert.Equal(60_000, feed.Log.Last(r => r.Uri.AbsolutePath.EndsWith(".zip")).RangeFrom);
        Assert.Null(await second.GetPendingAsync(Ct));
        Assert.Equal("1.0.0", (await second.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Changed_remote_file_is_never_spliced_onto_old_bytes()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var zip = Factory.BigGameZip("v1");
        var manifest = feed.Publish("demo", Factory.Platform, "1.0.0", zip);
        var svc = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        feed.DropConnectionAfter = 50_000;
        var release = (await svc.CheckAsync(Ct))!;
        await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release, null, Ct));

        // Someone overwrote the "immutable" object: new bytes, new ETag, same URL and manifest.
        var tampered = (byte[])zip.Clone();
        tampered[^100] ^= 0xFF;
        feed.Put(new Uri(manifest.Package.Url).AbsolutePath.TrimStart('/'), tampered);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Factory.Service(tmp.Path, feed).DownloadAsync(release, null, Ct));

        // If-Range made the server send the whole new file; the hash check then refused it.
        Assert.Equal(UpdateErrorCode.HashMismatch, ex.Code);
        var zipRequests = feed.Log.Where(r => r.Uri.AbsolutePath.EndsWith(".zip")).ToList();
        Assert.Equal(2, zipRequests.Count);
        Assert.Equal(50_000, zipRequests[1].RangeFrom);
        Assert.Null(await svc.GetPendingAsync(Ct));
        Assert.Empty(Directory.GetFiles(svc.Layout.DownloadsDirectory));
    }

    [Fact]
    public async Task Size_changed_remote_file_drops_the_stale_partial()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var zip = Factory.BigGameZip("v1");
        var manifest = feed.Publish("demo", Factory.Platform, "1.0.0", zip);
        var svc = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        feed.DropConnectionAfter = 50_000;
        var release = (await svc.CheckAsync(Ct))!;
        await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release, null, Ct));

        feed.Put(new Uri(manifest.Package.Url).AbsolutePath.TrimStart('/'), [.. zip, 0, 0, 0]);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release, null, Ct));

        Assert.Equal(UpdateErrorCode.DownloadIncomplete, ex.Code);
        Assert.False(File.Exists(svc.Layout.PartialDownloadPath("1.0.0")));
    }

    [Fact]
    public async Task Pending_for_older_release_is_discarded()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.BigGameZip("v1"));
        var svc = Factory.Service(tmp.Path, feed, maxAttempts: 1);
        feed.DropConnectionAfter = 50_000;
        await Assert.ThrowsAsync<UpdateException>(async () => await svc.DownloadAsync((await svc.CheckAsync(Ct))!, null, Ct));

        feed.Publish("demo", Factory.Platform, "1.1.0", Factory.GameZip("v2"));
        var newer = (await svc.CheckAsync(Ct))!;
        Assert.Equal(0, await svc.GetResumableBytesAsync(newer, Ct));
        await svc.DownloadAsync(newer, null, Ct);

        Assert.False(File.Exists(svc.Layout.PartialDownloadPath("1.0.0")));
        Assert.Equal("1.1.0", (await svc.GetPendingAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Recovery_after_crash_before_commit_keeps_verified_zip_and_cleans_staging()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);

        // Simulate dying mid-install: stage marked Installing, half-built staging and version dirs left behind.
        var pending = (await svc.GetPendingAsync(Ct))!;
        await AtomicFile.WriteJsonAsync(svc.Layout.PendingStatePath, pending with { Stage = PendingStage.Installing }, Ct);
        Directory.CreateDirectory(Path.Combine(svc.Layout.StagingDirectory, "abc", "Game.app"));
        Directory.CreateDirectory(Path.Combine(svc.Layout.VersionDirectory("1.0.0"), "Game.app"));
        File.WriteAllText(Path.Combine(svc.Layout.DownloadsDirectory, "0.9.0.zip.partial"), "junk");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Equal(PendingStage.Downloaded, (await restarted.GetPendingAsync(Ct))!.Stage);
        Assert.Empty(Directory.GetDirectories(restarted.Layout.StagingDirectory));
        Assert.False(Directory.Exists(restarted.Layout.VersionDirectory("1.0.0")));
        Assert.Equal([restarted.Layout.CompletedDownloadPath("1.0.0")], Directory.GetFiles(restarted.Layout.DownloadsDirectory));
        Assert.Null(await restarted.GetInstalledAsync(Ct));

        var before = feed.Log.Count;
        await restarted.DownloadAsync(release, null, Ct);
        Assert.Equal(before, feed.Log.Count); // reused, not downloaded again
        await restarted.InstallAsync(release, Ct);
        Assert.Equal("1.0.0", (await restarted.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Recovery_after_crash_right_after_commit_finishes_cleanup()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        var svc = Factory.Service(tmp.Path, feed);
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        var pending = (await svc.GetPendingAsync(Ct))!;
        await svc.InstallAsync(release, Ct);

        // Pretend the process died after current.json was written but before pending.json was removed.
        await AtomicFile.WriteJsonAsync(svc.Layout.PendingStatePath, pending with { Stage = PendingStage.Installing }, Ct);
        File.WriteAllText(svc.Layout.CompletedDownloadPath("1.0.0"), "leftover");

        var restarted = Factory.Service(tmp.Path, feed);
        await restarted.RecoverAsync(Ct);

        Assert.Null(await restarted.GetPendingAsync(Ct));
        Assert.Empty(Directory.GetFiles(restarted.Layout.DownloadsDirectory));
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

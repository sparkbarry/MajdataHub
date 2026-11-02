namespace RhythmHub.Updater.Tests;

public class GameUpdateServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task InstallLatestAsync(GameUpdateService svc)
    {
        var release = await svc.CheckAsync(Ct);
        Assert.NotNull(release);
        await svc.DownloadAsync(release, null, Ct);
        await svc.InstallAsync(release, Ct);
    }

    [Fact]
    public async Task First_install_then_up_to_date()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        var svc = Factory.Service(tmp.Path, feed);

        Assert.Null(await svc.GetInstalledAsync(Ct));
        await InstallLatestAsync(svc);

        var installed = await svc.GetInstalledAsync(Ct);
        Assert.NotNull(installed);
        Assert.Equal("1.0.0", installed.Version);
        Assert.True(File.Exists(Path.Combine(installed.VersionDirectory, "Game.app/Contents/MacOS/Game")));
        Assert.Null(await svc.CheckAsync(Ct));
        Assert.Empty(Directory.GetFiles(svc.Layout.DownloadsDirectory));
        Assert.Empty(Directory.GetDirectories(svc.Layout.StagingDirectory));
    }

    [Fact]
    public async Task Update_keeps_previous_version_and_can_roll_back()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);
        feed.Publish("demo", Factory.Platform, "1.1.0", Factory.GameZip("v2"));
        await InstallLatestAsync(svc);

        var state = await new VersionRepository(svc.Layout).LoadAsync(Ct);
        Assert.Equal("1.1.0", state.ActiveVersion);
        Assert.Equal("1.0.0", state.PreviousGoodVersion);
        Assert.True(Directory.Exists(svc.Layout.VersionDirectory("1.0.0")));

        await svc.RollbackAsync(Ct);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Prunes_versions_older_than_previous_good()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            feed.Publish("demo", Factory.Platform, v, Factory.GameZip(v));
            await InstallLatestAsync(svc);
        }

        // 1.1.0 never launched successfully, so 1.0.0 stays the rollback target and 1.1.0 is dropped.
        var dirs = Directory.GetDirectories(svc.Layout.VersionsDirectory).Select(Path.GetFileName).Order();
        Assert.Equal(["1.0.0", "1.2.0"], dirs);
    }

    [Fact]
    public async Task Healthy_version_becomes_rollback_target()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            feed.Publish("demo", Factory.Platform, v, Factory.GameZip(v));
            await InstallLatestAsync(svc);
            await svc.RecordLaunchAsync(v, LaunchOutcome.Healthy, Ct);
        }

        var state = await svc.GetStateAsync(Ct);
        Assert.Equal("1.1.0", state.PreviousGoodVersion);
        Assert.Equal(["1.1.0", "1.2.0"], Directory.GetDirectories(svc.Layout.VersionsDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Rollback_does_not_offer_the_abandoned_version_again()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);
        await svc.RecordLaunchAsync("1.0.0", LaunchOutcome.Healthy, Ct);
        feed.Publish("demo", Factory.Platform, "1.1.0", Factory.GameZip("v2"));
        await InstallLatestAsync(svc);
        await svc.RecordLaunchAsync("1.1.0", LaunchOutcome.Exited, Ct);

        await svc.RollbackAsync(Ct);

        var state = await svc.GetStateAsync(Ct);
        Assert.Equal("1.0.0", state.ActiveVersion);
        Assert.Null(state.PreviousGoodVersion);
        Assert.True(state.LastLaunchSuccessful);
    }

    [Fact]
    public async Task Installed_size_mismatch_is_rejected()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"), installedSize: 999_999);

        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.InstallAsync(release, Ct));
        Assert.Equal(UpdateErrorCode.InvalidPackage, ex.Code);
        Assert.Null(await svc.GetInstalledAsync(Ct));
    }

    [Fact]
    public async Task Unsigned_manifest_is_rejected_before_download()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"), sign: false);
        var svc = Factory.Service(tmp.Path, feed);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.SignatureInvalid, ex.Code);
        Assert.DoesNotContain(feed.Log, r => r.Uri.AbsolutePath.EndsWith(".zip"));
    }

    [Fact]
    public async Task Hash_mismatch_leaves_current_version_untouched()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);

        feed.Publish("demo", Factory.Platform, "1.1.0", Factory.GameZip("v2"), sha: new string('0', 64));
        var release = await svc.CheckAsync(Ct);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(release!, null, Ct));

        Assert.Equal(UpdateErrorCode.HashMismatch, ex.Code);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
        Assert.Empty(Directory.GetFiles(svc.Layout.DownloadsDirectory));
    }

    [Fact]
    public async Task Package_without_entrypoint_is_not_activated()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);

        feed.Publish("demo", Factory.Platform, "1.1.0", Zips.Build(new Zips.Entry("Other.app/x", "x")));
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.InstallAsync(release, Ct));

        Assert.Equal(UpdateErrorCode.InvalidPackage, ex.Code);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
        Assert.False(Directory.Exists(svc.Layout.VersionDirectory("1.1.0")));
        Assert.Empty(Directory.GetDirectories(svc.Layout.StagingDirectory));
    }

    [Fact]
    public async Task Refuses_to_install_while_game_runs()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var platform = new FakePlatform();
        var svc = Factory.Service(tmp.Path, feed, platform);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);

        feed.Publish("demo", Factory.Platform, "1.1.0", Factory.GameZip("v2"));
        var release = (await svc.CheckAsync(Ct))!;
        await svc.DownloadAsync(release, null, Ct);
        platform.Running = true;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.InstallAsync(release, Ct));
        Assert.Equal(UpdateErrorCode.GameRunning, ex.Code);
        Assert.Equal("1.0.0", (await svc.GetInstalledAsync(Ct))!.Version);
    }

    [Fact]
    public async Task Missing_release_reports_not_found()
    {
        using var tmp = new TempDir();
        var svc = Factory.Service(tmp.Path, new FakeFeed());

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.CheckAsync(Ct));
        Assert.Equal(UpdateErrorCode.ManifestNotFound, ex.Code);
    }

    [Fact]
    public async Task Rollback_without_previous_version_fails_cleanly()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        var svc = Factory.Service(tmp.Path, feed);
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(svc);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => svc.RollbackAsync(Ct));
        Assert.Equal(UpdateErrorCode.NoRollbackTarget, ex.Code);
    }

    [Fact]
    public async Task Games_are_isolated_by_id()
    {
        using var tmp = new TempDir();
        var feed = new FakeFeed();
        feed.Publish("demo", Factory.Platform, "1.0.0", Factory.GameZip("v1"));
        await InstallLatestAsync(Factory.Service(tmp.Path, feed));

        Assert.True(File.Exists(Path.Combine(tmp.Path, "games", "demo", "current.json")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "games", "other")));
    }
}

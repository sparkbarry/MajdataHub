using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

public class ManifestValidationTests
{
    private static readonly string Sha = new('a', 64);

    private static ReleaseManifest Valid() => new()
    {
        SchemaVersion = 2,
        GameId = "demo",
        Platform = "MacOS",
        Version = "1.2.0",
        Entrypoint = "Game.app",
        Files = [new("Game.app/Contents/MacOS/Game", 10, Sha, 0b111_101_101), new("Game.app/Contents/Info.plist", 5, Sha)],
    };

    private static UpdateErrorCode Fail(ReleaseManifest m) =>
        Assert.Throws<UpdateException>(() => ManifestClient.ValidateRelease(m, "demo", "MacOS")).Code;

    private static ReleaseManifest WithFile(string path) => Valid() with { Files = [.. Valid().Files, new(path, 1, Sha)] };

    [Fact]
    public void Accepts_valid_manifest() => ManifestClient.ValidateRelease(Valid(), "demo", "MacOS");

    [Fact]
    public void Rejects_other_game() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { GameId = "other" }));

    [Fact]
    public void Rejects_other_platform() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Platform = "Windows" }));

    [Fact]
    public void Rejects_unknown_schema() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { SchemaVersion = 1 }));

    [Theory]
    [InlineData("../1.0")]
    [InlineData("1.0/x")]
    [InlineData("stable")]
    [InlineData("")]
    public void Rejects_unsafe_versions(string version) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Version = version }));

    [Theory]
    [InlineData("../evil")]
    [InlineData("Game.app/../../evil")]
    [InlineData("/etc/evil")]
    [InlineData("C:/evil")]
    [InlineData("Game.app\\evil")]
    [InlineData("Game.app//evil")]
    [InlineData("Game.app/./evil")]
    [InlineData("Game.app/evil.")]
    [InlineData("Game.app/evil ")]
    [InlineData("Game.app/CON")]
    [InlineData("Game.app/aux.txt")]
    [InlineData("Game.app/a?b")]
    [InlineData(".rhythmhub/release.json")]
    [InlineData("Game.app/\u0001")]
    public void Rejects_unsafe_paths(string path) => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(WithFile(path)));

    [Fact]
    public void Rejects_paths_differing_only_by_case() =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(WithFile("game.app/contents/info.plist")));

    [Theory]
    [InlineData("../../../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows")]
    public void Rejects_symlinks_escaping_release(string target) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Symlinks = [new("Game.app/Contents/link", target)] }));

    [Fact]
    public void Accepts_symlink_within_release() =>
        ManifestClient.ValidateRelease(Valid() with { Symlinks = [new("Game.app/Contents/link", "../Contents/Info.plist")] }, "demo", "MacOS");

    [Fact]
    public void Rejects_entries_below_a_symlink() =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(WithFile("Game.app/Contents/link/x") with
        {
            Symlinks = [new("Game.app/Contents/link", "MacOS")],
        }));

    [Theory]
    [InlineData("Other.app")]
    [InlineData("Game.app/Contents")]
    [InlineData("")]
    public void Rejects_bad_entrypoint(string entrypoint) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Entrypoint = entrypoint }));

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zz")]
    public void Rejects_bad_hash(string sha) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Files = [new("Game.app/x", 1, sha)] }));

    [Fact]
    public void Rejects_setuid_mode() =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Files = [new("Game.app/x", 1, Sha, 0b100_111_101_101)] }));

    [Fact]
    public void Rejects_insecure_release_notes() =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { ReleaseNotesUrl = "http://example.com/notes" }));

    [Fact]
    public void Rejects_when_launcher_too_old() =>
        Assert.Equal(UpdateErrorCode.LauncherTooOld, Assert.Throws<UpdateException>(() =>
            ManifestClient.CheckLauncherVersion(Valid() with { MinimumLauncherVersion = "2.0.0" }, new Version(1, 9, 9))).Code);

    [Fact]
    public void Pointer_must_match_request() =>
        Assert.Throws<UpdateException>(() => ManifestClient.ValidatePointer(new ChannelPointer
        {
            SchemaVersion = 2, GameId = "demo", Platform = "Windows", Channel = "stable", Version = "1.0.0", ManifestSha256 = Sha,
        }, "demo", "MacOS", "stable"));

    [Fact]
    public void Feed_urls_escape_each_segment() =>
        Assert.Equal("https://cdn.test/base/MajdataPlay/MacOS/1.2.0%2Bb/Majdata%20Play.app/Contents/%E9%9F%B3.ogg",
            FeedLayout.FileUri(new Uri("https://cdn.test/base"), "MajdataPlay", "MacOS", "1.2.0+b",
                "Majdata Play.app/Contents/音.ogg").AbsoluteUri);

    [Fact]
    public void Channel_and_manifest_urls_follow_layout()
    {
        Assert.Equal("https://distrib.sparkbarry.com/MajdataPlay/Windows/stable.json",
            FeedLayout.ChannelUri(new Uri("https://distrib.sparkbarry.com/"), "MajdataPlay", "Windows", "stable").AbsoluteUri);
        Assert.Equal("https://distrib.sparkbarry.com/MajdataPlay/Windows/1.2.0.json",
            FeedLayout.ManifestUri(new Uri("https://distrib.sparkbarry.com/"), "MajdataPlay", "Windows", "1.2.0").AbsoluteUri);
    }
}

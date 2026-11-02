using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

public class ManifestValidationTests
{
    private static GameManifest Valid() => new()
    {
        SchemaVersion = 1,
        GameId = "demo",
        Channel = "stable",
        Platform = Factory.Platform,
        Version = "1.2.0",
        Package = new ManifestPackage
        {
            Url = "https://cdn.test/game/demo/osx-arm64/releases/1.2.0/game.zip",
            SizeBytes = 10,
            Sha256 = new string('a', 64),
        },
        Entrypoint = "Game.app",
    };

    private static UpdateErrorCode Fail(GameManifest m, string launcher = "1.0.0") =>
        Assert.Throws<UpdateException>(() => ManifestClient.Validate(m, Factory.Options("/tmp", launcher), "demo")).Code;

    [Fact]
    public void Accepts_valid_manifest() =>
        ManifestClient.Validate(Valid(), Factory.Options("/tmp"), "demo");

    [Fact]
    public void Rejects_other_game() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { GameId = "other" }));

    [Fact]
    public void Rejects_other_platform() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Platform = "win-x64" }));

    [Fact]
    public void Rejects_unknown_schema() => Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { SchemaVersion = 2 }));

    [Theory]
    [InlineData("../1.0")]
    [InlineData("1.0/x")]
    [InlineData("")]
    public void Rejects_unsafe_versions(string version) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Version = version }));

    [Theory]
    [InlineData("http://cdn.test/game.zip")]
    [InlineData("ftp://cdn.test/game.zip")]
    public void Rejects_insecure_package_urls(string url) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Package = Valid().Package with { Url = url } }));

    [Fact]
    public void Allows_http_on_loopback() =>
        ManifestClient.Validate(Valid() with { Package = Valid().Package with { Url = "http://127.0.0.1:8787/game.zip" } },
            Factory.Options("/tmp"), "demo");

    [Theory]
    [InlineData("zz")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Rejects_bad_hash(string sha) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Package = Valid().Package with { Sha256 = sha } }));

    [Theory]
    [InlineData("../Game.app")]
    [InlineData("sub/Game.app")]
    [InlineData("/Applications/Game.app")]
    public void Rejects_unsafe_entrypoints(string entrypoint) =>
        Assert.Equal(UpdateErrorCode.ManifestInvalid, Fail(Valid() with { Entrypoint = entrypoint }));

    [Fact]
    public void Rejects_when_launcher_too_old() =>
        Assert.Equal(UpdateErrorCode.LauncherTooOld, Fail(Valid() with { MinimumLauncherVersion = "2.0.0" }, launcher: "1.9.9"));

    [Fact]
    public void Builds_per_game_manifest_url() =>
        Assert.Equal("https://cdn.test/base/game/demo/osx-arm64/stable.json",
            ManifestClient.BuildManifestUri(new Uri("https://cdn.test/base"), "demo", "osx-arm64", "stable").AbsoluteUri);
}

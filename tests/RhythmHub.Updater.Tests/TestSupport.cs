using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("rhythmhub-tests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Zips
{
    public sealed record Entry(string Name, string Content = "", int UnixType = 0x8000, int Mode = 0b110_100_100);

    public static byte[] Build(params Entry[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var entry = zip.CreateEntry(e.Name);
                entry.ExternalAttributes = (e.UnixType | e.Mode) << 16;
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(e.Content));
            }
        }
        return ms.ToArray();
    }

    public static string Write(string dir, params Entry[] entries)
    {
        var path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, Build(entries));
        return path;
    }
}

/// <summary>In-memory static feed keyed by absolute URL.</summary>
internal sealed class FakeFeed : HttpMessageHandler
{
    public static readonly Uri Base = new("https://cdn.test/");
    private readonly Dictionary<string, byte[]> _files = new();
    public int Requests { get; private set; }

    public void Put(string relative, byte[] content) => _files[new Uri(Base, relative).AbsoluteUri] = content;

    public GameManifest Publish(string gameId, string platform, string version, byte[] zip,
        string entrypoint = "Game.app", string? sha = null, string? minLauncher = null)
    {
        var rel = $"game/{gameId}/{platform}/releases/{version}/game.zip";
        Put(rel, zip);
        var manifest = new GameManifest
        {
            SchemaVersion = 1,
            GameId = gameId,
            Channel = "stable",
            Platform = platform,
            Version = version,
            MinimumLauncherVersion = minLauncher,
            Package = new ManifestPackage
            {
                Url = new Uri(Base, rel).AbsoluteUri,
                SizeBytes = zip.Length,
                Sha256 = sha ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)),
            },
            Entrypoint = entrypoint,
        };
        Put($"game/{gameId}/{platform}/stable.json",
            JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return manifest;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        if (!_files.TryGetValue(request.RequestUri!.AbsoluteUri, out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}

internal sealed class FakePlatform : IPackageVerifier
{
    public bool Running { get; set; }
    public Task VerifyPlatformPackageAsync(string unpackedDirectory, string entrypoint, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> IsGameRunningAsync(string versionDirectory, string entrypoint, CancellationToken ct) => Task.FromResult(Running);
}

internal static class Factory
{
    public const string Platform = "osx-arm64";

    public static UpdaterOptions Options(string root, string launcher = "1.0.0") => new()
    {
        InstallRoot = root,
        PlatformId = Platform,
        FeedBaseUri = FakeFeed.Base,
        LauncherVersion = Version.Parse(launcher),
        DiskSafetyMarginBytes = 0,
    };

    public static GameUpdateService Service(string root, FakeFeed feed, FakePlatform? platform = null) =>
        new("demo", Options(root), new HttpClient(feed), platform ?? new FakePlatform(), NullLoggerFactory.Instance);

    public static byte[] GameZip(string label) => Zips.Build(
        new Zips.Entry("Game.app/Contents/Info.plist", "<plist/>"),
        new Zips.Entry("Game.app/Contents/MacOS/Game", label, Mode: 0b111_101_101));
}

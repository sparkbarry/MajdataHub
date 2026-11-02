using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

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

internal static class TestKeys
{
    public const string KeyId = "test-key";
    public static readonly (string Private, string Public) Pair = ManifestSigning.GenerateKeyPair();
    public static IReadOnlyDictionary<string, string> Trusted => new Dictionary<string, string> { [KeyId] = Pair.Public };
}

/// <summary>In-memory static feed keyed by absolute URL, with strong ETags and Range / If-Range support.</summary>
internal sealed class FakeFeed : HttpMessageHandler
{
    public static readonly Uri Base = new("https://cdn.test/");
    private readonly Dictionary<string, (byte[] Body, string ETag)> _files = new();

    public List<(Uri Uri, long? RangeFrom, string? IfRange)> Log { get; } = [];

    /// <summary>One-shot fault: the next package response breaks after this many body bytes.</summary>
    public long? DropConnectionAfter { get; set; }

    public void Put(string relative, byte[] content) =>
        _files[new Uri(Base, relative).AbsoluteUri] = (content, $"\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))[..16]}\"");

    public GameManifest Publish(string gameId, string platform, string version, byte[] zip,
        string entrypoint = "Game.app", string? sha = null, string? minLauncher = null, long? installedSize = null,
        bool sign = true)
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
                InstalledSizeBytes = installedSize,
            },
            Entrypoint = entrypoint,
        };
        var node = JsonSerializer.SerializeToNode(manifest, Json)!.AsObject();
        if (sign) ManifestSigning.Sign(node, TestKeys.Pair.Private, TestKeys.KeyId);
        Put($"game/{gameId}/{platform}/stable.json", Encoding.UTF8.GetBytes(node.ToJsonString()));
        return manifest;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        var ifRange = request.Headers.IfRange?.EntityTag?.Tag;
        Log.Add((request.RequestUri!, range, ifRange));
        if (!_files.TryGetValue(request.RequestUri!.AbsoluteUri, out var file))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var partial = range is { } from && (ifRange is null || ifRange == file.ETag);
        var offset = partial ? range!.Value : 0;
        if (offset > file.Body.Length)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

        Stream body = new MemoryStream(file.Body, (int)offset, file.Body.Length - (int)offset);
        if (DropConnectionAfter is { } cut && request.RequestUri.AbsolutePath.EndsWith(".zip"))
        {
            DropConnectionAfter = null;
            body = new BrokenStream(body, cut);
        }
        var content = new StreamContent(body);
        content.Headers.ContentLength = file.Body.Length - offset;
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(file.ETag);
        if (partial)
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, file.Body.Length - 1, file.Body.Length);
        return Task.FromResult(response);
    }

    /// <summary>Yields <paramref name="limit"/> bytes, then fails like a dropped TCP connection.</summary>
    private sealed class BrokenStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new IOException("Connection reset by peer");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
        TrustedKeys = TestKeys.Trusted,
    };

    public static GameUpdateService Service(string root, FakeFeed feed, FakePlatform? platform = null, int maxAttempts = 4) =>
        new("demo", Options(root), new HttpClient(feed), platform ?? new FakePlatform(), NullLoggerFactory.Instance,
            new DownloadService(new HttpClient(feed), NullLogger<DownloadService>.Instance)
            {
                MaxAttempts = maxAttempts,
                InitialBackoff = TimeSpan.Zero,
            });

    /// <summary>A zip big enough that a mid-body cut leaves a meaningful partial file.</summary>
    public static byte[] BigGameZip(string label) => Zips.Build(
        new Zips.Entry("Game.app/Contents/Info.plist", "<plist/>"),
        new Zips.Entry("Game.app/Contents/MacOS/Game", label, Mode: 0b111_101_101),
        new Zips.Entry("Game.app/Contents/Resources/data.bin", RandomText(200_000)));

    private static string RandomText(int length) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(length))[..length];

    public static byte[] GameZip(string label) => Zips.Build(
        new Zips.Entry("Game.app/Contents/Info.plist", "<plist/>"),
        new Zips.Entry("Game.app/Contents/MacOS/Game", label, Mode: 0b111_101_101));
}

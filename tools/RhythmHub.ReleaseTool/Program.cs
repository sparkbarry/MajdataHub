using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

// rhythmhub-release: packages a Unity player build into the feed layout described in docs/update-protocol.md.
return await ReleaseTool.RunAsync(args);

internal static class ReleaseTool
{
    private const string Usage = """
        Usage:
          pack --game <id> --platform <win-x64|osx-arm64|osx-x64> --version <x.y.z>
               --input <build dir> --entrypoint <Game.exe|Game.app>
               --base-url <https://cdn/> --out <feed root> --key-id <id>
               [--channel stable] [--min-launcher 0.1.0] [--release-notes-url <url>]
              Zips <input>, writes game/<id>/<platform>/releases/<version>/game.zip and the signed channel manifest.
              The release zip is written before the manifest, matching the production publish order.
              The Ed25519 private key is read from $RHYTHMHUB_SIGNING_KEY (base64), never from the command line.

          keygen --out <private key file>
              Creates an Ed25519 key pair; prints the public key to paste into ReleaseKeys.cs.

          fake-game --platform <id> --name <Name> --out <dir> [--label <text>] [--crash true]
              Writes a tiny stand-in player (Windows layout or a macOS .app) for local end-to-end testing.
              The macOS one writes the health marker; with --crash it exits immediately instead.
        """;

    private const string SigningKeyVariable = "RHYTHMHUB_SIGNING_KEY";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        var opts = ParseOptions(args[1..]);
        try
        {
            switch (args[0])
            {
                case "pack": await PackAsync(opts); return 0;
                case "fake-game": FakeGame(opts); return 0;
                case "keygen": KeyGen(opts); return 0;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private static async Task PackAsync(Dictionary<string, string> o)
    {
        var game = Req(o, "game");
        var platform = Req(o, "platform");
        var version = Req(o, "version");
        var channel = o.GetValueOrDefault("channel", "stable");
        var input = Path.GetFullPath(Req(o, "input"));
        var entrypoint = Req(o, "entrypoint");
        var baseUrl = Req(o, "base-url").TrimEnd('/') + "/";
        var outRoot = Path.GetFullPath(Req(o, "out"));
        var keyId = Req(o, "key-id");
        var privateKey = Environment.GetEnvironmentVariable(SigningKeyVariable)
            ?? throw new InvalidOperationException($"Set {SigningKeyVariable} to the base64 Ed25519 private key.");

        if (!File.Exists(Path.Combine(input, entrypoint)) && !Directory.Exists(Path.Combine(input, entrypoint)))
            throw new InvalidOperationException($"{entrypoint} not found in {input}");

        var platformDir = Path.Combine(outRoot, "game", game, platform);
        var releaseRel = $"game/{game}/{platform}/releases/{version}/game.zip";
        var zipPath = Path.Combine(outRoot, releaseRel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(zipPath))
            throw new InvalidOperationException($"{releaseRel} already exists; published releases are immutable.");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var installedSize = ZipDirectory(input, zipPath);
        var sha = await FileHasher.ComputeSha256Async(zipPath, CancellationToken.None);
        var manifest = new GameManifest
        {
            SchemaVersion = 1,
            GameId = game,
            Channel = channel,
            Platform = platform,
            Version = version,
            MinimumLauncherVersion = o.GetValueOrDefault("min-launcher"),
            ReleaseNotesUrl = o.GetValueOrDefault("release-notes-url"),
            Package = new ManifestPackage
            {
                Url = baseUrl + releaseRel,
                SizeBytes = new FileInfo(zipPath).Length,
                Sha256 = sha,
                InstalledSizeBytes = installedSize,
            },
            Entrypoint = entrypoint,
        };
        var node = JsonSerializer.SerializeToNode(manifest, Json)!.AsObject();
        ManifestSigning.Sign(node, privateKey, keyId);
        ManifestSigning.Verify(node, new Dictionary<string, string> { [keyId] = ManifestSigning.PublicKeyFor(privateKey) });

        var manifestPath = Path.Combine(platformDir, $"{channel}.json");
        var tmp = manifestPath + ".tmp";
        await File.WriteAllTextAsync(tmp, node.ToJsonString(Json));
        File.Move(tmp, manifestPath, overwrite: true);

        Console.WriteLine($"zip       {zipPath}");
        Console.WriteLine($"sha256    {sha}");
        Console.WriteLine($"manifest  {manifestPath}");
    }

    /// <summary>Like `ditto -c -k --keepParent`-less: stores unix modes and symlinks as links, not their targets.</summary>
    private static long ZipDirectory(string input, string zipPath)
    {
        long total = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var path in Walk(input).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(input, path).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(path);
            var isLink = info.LinkTarget is not null;
            var isDir = !isLink && Directory.Exists(path);
            if (isDir && Directory.EnumerateFileSystemEntries(path).Any()) continue;

            var entry = zip.CreateEntry(isDir ? rel + "/" : rel, CompressionLevel.Optimal);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (int)File.GetUnixFileMode(path);
                var type = isLink ? 0xA000 : isDir ? 0x4000 : 0x8000;
                entry.ExternalAttributes = (type | mode) << 16;
            }
            if (isDir) continue;
            using var dest = entry.Open();
            if (isLink)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(info.LinkTarget!);
                dest.Write(bytes);
                continue;
            }
            using var src = File.OpenRead(path);
            src.CopyTo(dest);
            total += info.Length;
        }
        return total;
    }

    /// <summary>Recursive listing that records symlinked directories as entries instead of descending into them.</summary>
    private static IEnumerable<string> Walk(string dir)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            yield return path;
            if (new FileInfo(path).LinkTarget is null && Directory.Exists(path))
                foreach (var child in Walk(path)) yield return child;
        }
    }

    private static void KeyGen(Dictionary<string, string> o)
    {
        var path = Path.GetFullPath(Req(o, "out"));
        if (File.Exists(path)) throw new InvalidOperationException($"{path} already exists.");
        var (priv, pub) = ManifestSigning.GenerateKeyPair();
        File.WriteAllText(path, priv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"private key written to {path} (keep it secret; store it as a CI secret)");
        Console.WriteLine($"public key: {pub}");
    }

    private static void FakeGame(Dictionary<string, string> o)
    {
        var platform = Req(o, "platform");
        var name = Req(o, "name");
        var label = o.GetValueOrDefault("label", name);
        var outDir = Path.GetFullPath(Req(o, "out"));
        Directory.CreateDirectory(outDir);

        if (platform.StartsWith("win", StringComparison.Ordinal))
        {
            // Not runnable; enough to satisfy the Windows package check.
            File.WriteAllText(Path.Combine(outDir, $"{name}.exe"), "fake");
            File.WriteAllText(Path.Combine(outDir, "UnityPlayer.dll"), "fake");
            Directory.CreateDirectory(Path.Combine(outDir, $"{name}_Data"));
            File.WriteAllText(Path.Combine(outDir, $"{name}_Data", "version.txt"), label);
            Console.WriteLine(Path.Combine(outDir, $"{name}.exe"));
            return;
        }

        var contents = Path.Combine(outDir, $"{name}.app", "Contents");
        var macos = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>CFBundleExecutable</key><string>{name}</string>
              <key>CFBundleIdentifier</key><string>dev.rhythmhub.fake.{name.ToLowerInvariant()}</string>
              <key>CFBundleName</key><string>{name}</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleShortVersionString</key><string>1.0</string>
            </dict></plist>
            """);
        File.WriteAllText(Path.Combine(resources, "label.txt"), label);
        if (o.GetValueOrDefault("crash") == "true") File.WriteAllText(Path.Combine(resources, "crash"), "");
        File.CreateSymbolicLink(Path.Combine(resources, "label-link.txt"), "label.txt");
        // Framework-style directory symlink, the shape that breaks naive zip tools.
        var framework = Path.Combine(contents, "Frameworks", "Fake.framework");
        Directory.CreateDirectory(Path.Combine(framework, "Versions", "A"));
        File.WriteAllText(Path.Combine(framework, "Versions", "A", "Fake"), "fake");
        Directory.CreateSymbolicLink(Path.Combine(framework, "Versions", "Current"), "A");
        var exe = Path.Combine(macos, name);
        File.WriteAllText(exe, $"""
            #!/bin/sh
            echo "{label} started $(date)" >> "$HOME/Library/Logs/rhythmhub-fake-game.log"
            # Simulate a game that reports healthy after initializing (docs/update-protocol.md §7).
            [ -f "$(dirname "$0")/../Resources/crash" ] && exit 1
            if [ -n "$RHYTHMHUB_HEALTH_FILE" ]; then
              sleep 1
              printf '{"{"}"nonce":"%s","version":"%s"{"}"}' "$RHYTHMHUB_LAUNCH_NONCE" "$RHYTHMHUB_GAME_VERSION" > "$RHYTHMHUB_HEALTH_FILE.tmp"
              mv "$RHYTHMHUB_HEALTH_FILE.tmp" "$RHYTHMHUB_HEALTH_FILE"
            fi
            sleep 30
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        Console.WriteLine(Path.Combine(outDir, $"{name}.app"));
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            d[args[i][2..]] = args[++i];
        }
        return d;
    }

    private static string Req(Dictionary<string, string> o, string key) =>
        o.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException($"Missing --{key}.");
}

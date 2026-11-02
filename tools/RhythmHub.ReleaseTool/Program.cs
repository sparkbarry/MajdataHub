using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RhythmHub.Updater;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

// rhythmhub-release: describes Unity builds as signed release manifests (docs/update-protocol.md).
return await ReleaseTool.RunAsync(args);

internal static class ReleaseTool
{
    private const string Usage = """
        Usage:
          stage --game <MajdataPlay> --platform <Windows|MacOS> --version <x.y.z>
                --input <build dir> --entrypoint <Game.exe|Game.app> --out <manifest file>
                [--min-launcher 0.1.0] [--release-notes-url <https://…>] [--copy-to <local feed root>]
              Run in the game's CI, on the finished (for macOS: signed and notarized) build. Writes the unsigned
              release manifest, which records hashes, permission bits, symlinks and empty directories.
              Upload <input> to <game>/<platform>/<version>/ (aws s3 sync --no-follow-symlinks) and the manifest to
              <game>/<platform>/<version>.unsigned.json. --copy-to does both into a local folder instead.

          publish --feed <local feed root> --game <id> --platform <id> --version <x.y.z> --key-id <id> [--channel stable]
              Run in this repository's release workflow on a local copy of the feed. Checks every file under
              <version>/ against <version>.unsigned.json, writes the signed <version>.json, then the signed
              <channel>.json pointer. If <version>.json already exists (rollback), only re-points the channel.
              The Ed25519 private key is read from $RHYTHMHUB_SIGNING_KEY (base64), never from the command line.

          keygen --out <private key file>
              Creates an Ed25519 key pair; prints the public key to paste into ReleaseKeys.cs.

          fake-game --platform <Windows|MacOS> --name <Name> --out <dir> [--label <text>] [--crash true]
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
        try
        {
            var opts = ParseOptions(args[1..]);
            switch (args[0])
            {
                case "stage": await StageAsync(opts); return 0;
                case "publish": await PublishAsync(opts); return 0;
                case "fake-game": FakeGame(opts); return 0;
                case "keygen": KeyGen(opts); return 0;
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UpdateException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private static async Task StageAsync(Dictionary<string, string> o)
    {
        var game = Req(o, "game");
        var platform = Req(o, "platform");
        var version = Req(o, "version");
        var input = Path.GetFullPath(Req(o, "input"));
        var manifest = await ReleaseManifestBuilder.FromDirectoryAsync(input, game, platform, version, Req(o, "entrypoint"),
            CancellationToken.None);
        manifest = manifest with
        {
            MinimumLauncherVersion = o.GetValueOrDefault("min-launcher"),
            ReleaseNotesUrl = o.GetValueOrDefault("release-notes-url"),
        };
        ManifestClient.ValidateRelease(manifest, game, platform);
        var json = JsonSerializer.Serialize(manifest, Json);

        var outPath = Path.GetFullPath(Req(o, "out"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine($"manifest  {outPath}");
        Console.WriteLine($"files     {manifest.Files.Count} ({manifest.InstalledSizeBytes} bytes), " +
                          $"{manifest.Symlinks.Count} symlinks, {manifest.Directories.Count} empty dirs");

        if (o.GetValueOrDefault("copy-to") is { } feedRoot)
        {
            var platformDir = Path.Combine(Path.GetFullPath(feedRoot), game, platform);
            var versionDir = Path.Combine(platformDir, version);
            if (Directory.Exists(versionDir) || File.Exists(Path.Combine(platformDir, version + ".json")))
                throw new InvalidOperationException($"{game}/{platform}/{version} already exists; published versions are immutable.");
            foreach (var f in manifest.Files)
            {
                var target = Path.Combine(versionDir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(input, f.Path), target);
            }
            await File.WriteAllTextAsync(Path.Combine(platformDir, version + ".unsigned.json"), json);
            Console.WriteLine($"copied    {versionDir}");
        }
    }

    private static async Task PublishAsync(Dictionary<string, string> o)
    {
        var game = Req(o, "game");
        var platform = Req(o, "platform");
        var version = Req(o, "version");
        var channel = o.GetValueOrDefault("channel", "stable");
        var keyId = Req(o, "key-id");
        var privateKey = Environment.GetEnvironmentVariable(SigningKeyVariable)
            ?? throw new InvalidOperationException($"Set {SigningKeyVariable} to the base64 Ed25519 private key.");
        var trusted = new Dictionary<string, string> { [keyId] = ManifestSigning.PublicKeyFor(privateKey) };

        var platformDir = Path.Combine(Path.GetFullPath(Req(o, "feed")), game, platform);
        var signedPath = Path.Combine(platformDir, version + ".json");
        byte[] manifestBytes;
        if (File.Exists(signedPath))
        {
            manifestBytes = await File.ReadAllBytesAsync(signedPath);
            var existing = ManifestClient.Parse<ReleaseManifest>(manifestBytes, trusted);
            ManifestClient.ValidateRelease(existing, game, platform);
            Console.WriteLine($"{version}.json is already signed; re-pointing {channel} only");
        }
        else
        {
            var unsignedPath = Path.Combine(platformDir, version + ".unsigned.json");
            var unsignedBytes = await File.ReadAllBytesAsync(unsignedPath);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(unsignedBytes, Json)
                ?? throw new InvalidOperationException($"{unsignedPath} is empty.");
            ManifestClient.ValidateRelease(manifest, game, platform);
            if (manifest.Version != version)
                throw new InvalidOperationException($"{unsignedPath} describes {manifest.Version}, not {version}.");
            await VerifyFilesAsync(manifest, Path.Combine(platformDir, version));

            var node = JsonNode.Parse(unsignedBytes)!.AsObject();
            ManifestSigning.Sign(node, privateKey, keyId);
            manifestBytes = Encoding.UTF8.GetBytes(node.ToJsonString(Json));
            ManifestClient.Parse<ReleaseManifest>(manifestBytes, trusted);
            await WriteAtomicAsync(signedPath, manifestBytes);
        }

        var pointer = new ChannelPointer
        {
            SchemaVersion = ManifestClient.SchemaVersion,
            GameId = game,
            Platform = platform,
            Channel = channel,
            Version = version,
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
        };
        var pointerNode = JsonSerializer.SerializeToNode(pointer, Json)!.AsObject();
        ManifestSigning.Sign(pointerNode, privateKey, keyId);
        var pointerBytes = Encoding.UTF8.GetBytes(pointerNode.ToJsonString(Json));
        ManifestClient.ValidatePointer(ManifestClient.Parse<ChannelPointer>(pointerBytes, trusted), game, platform, channel);
        var pointerPath = Path.Combine(platformDir, channel + ".json");
        await WriteAtomicAsync(pointerPath, pointerBytes);

        Console.WriteLine("Upload in this order (each only after the previous is reachable):");
        Console.WriteLine($"  1. {signedPath}   (immutable cache)");
        Console.WriteLine($"  2. {pointerPath}   (no-cache) <- this makes {version} live on {channel}");
    }

    /// <summary>Every file the manifest promises must be present and byte-identical in the uploaded copy.</summary>
    private static async Task VerifyFilesAsync(ReleaseManifest manifest, string versionDir)
    {
        var problems = new List<string>();
        foreach (var f in manifest.Files)
        {
            var path = ManifestPaths.Under(versionDir, f.Path);
            if (!File.Exists(path)) problems.Add($"missing  {f.Path}");
            else if (new FileInfo(path).Length != f.Size) problems.Add($"size     {f.Path}");
            else if (await FileHasher.ComputeSha256Async(path, CancellationToken.None) != f.Sha256) problems.Add($"hash     {f.Path}");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException($"Uploaded files do not match the manifest:\n  {string.Join("\n  ", problems.Take(50))}");

        var expected = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var extra = Directory.Exists(versionDir)
            ? Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(versionDir, p).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(p => !expected.Contains(p)).ToList()
            : [];
        foreach (var e in extra.Take(20)) Console.WriteLine($"warning: {e} is uploaded but not in the manifest; clients ignore it");
        Console.WriteLine($"verified  {manifest.Files.Count} files");
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
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

        if (platform == "Windows")
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
        // Framework-style directory symlink, which S3 cannot store and the manifest must carry.
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

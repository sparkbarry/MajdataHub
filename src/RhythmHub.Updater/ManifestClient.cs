using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

namespace RhythmHub.Updater;

public sealed class ManifestClient(HttpClient http, ILogger<ManifestClient> logger)
{
    public const int SchemaVersion = 2;
    private const int MaxManifestBytes = 32 * 1024 * 1024;

    public async Task<ChannelPointer> FetchPointerAsync(UpdaterOptions options, string gameId, CancellationToken ct)
    {
        var uri = FeedLayout.ChannelUri(options.FeedBaseUri, gameId, options.PlatformId, options.Channel);
        var body = await GetAsync(uri, $"No {options.Channel} release published for {gameId} on {options.PlatformId}.", ct);
        var pointer = Parse<ChannelPointer>(body, options.TrustedKeys);
        ValidatePointer(pointer, gameId, options.PlatformId, options.Channel);
        return pointer;
    }

    public async Task<(ReleaseManifest Manifest, byte[] Bytes)> FetchReleaseAsync(UpdaterOptions options, ChannelPointer pointer,
        CancellationToken ct)
    {
        var uri = FeedLayout.ManifestUri(options.FeedBaseUri, pointer.GameId, pointer.Platform, pointer.Version);
        var body = await GetAsync(uri, $"Release manifest for {pointer.Version} is missing.", ct);
        if (Convert.ToHexStringLower(SHA256.HashData(body)) != pointer.ManifestSha256)
            throw new UpdateException(UpdateErrorCode.SignatureInvalid, "Release manifest does not match the channel pointer.");
        var manifest = Parse<ReleaseManifest>(body, options.TrustedKeys);
        ValidateRelease(manifest, pointer.GameId, pointer.Platform);
        if (manifest.Version != pointer.Version)
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Manifest is for {manifest.Version}, pointer says {pointer.Version}.");
        CheckLauncherVersion(manifest, options.LauncherVersion);
        return (manifest, body);
    }

    private async Task<byte[]> GetAsync(Uri uri, string notFound, CancellationToken ct)
    {
        RequireTrustedTransport(uri);
        logger.LogInformation("Fetching {Uri}", uri);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                throw new UpdateException(UpdateErrorCode.ManifestNotFound, notFound);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                throw new UpdateException(UpdateErrorCode.RateLimited, $"Update server is busy ({(int)response.StatusCode}).");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxManifestBytes)
                throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest is too large.");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Failed to fetch {uri}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorCode.Timeout, $"Timed out fetching {uri}.", ex);
        }
    }

    /// <summary>Verifies the signature over the bytes as received, then binds the model. Nothing is trusted before that.</summary>
    internal static T Parse<T>(byte[] body, IReadOnlyDictionary<string, string> trustedKeys) where T : class
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject node)
                throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest must be a JSON object.");
            ManifestSigning.Verify(node, trustedKeys);
            return node.Deserialize<T>(UpdaterJson.Options)
                ?? throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Manifest is not valid: {ex.Message}", ex);
        }
    }

    internal static void ValidatePointer(ChannelPointer p, string gameId, string platform, string channel)
    {
        void Fail(string why) => throw new UpdateException(UpdateErrorCode.ManifestInvalid, why);
        if (p.SchemaVersion != SchemaVersion) Fail($"Unsupported channel schemaVersion {p.SchemaVersion}.");
        if (p.GameId != gameId) Fail($"Channel is for game '{p.GameId}', expected '{gameId}'.");
        if (p.Platform != platform) Fail($"Channel is for platform '{p.Platform}', expected '{platform}'.");
        if (p.Channel != channel) Fail($"Channel is '{p.Channel}', expected '{channel}'.");
        ManifestPaths.Version(p.Version);
        if (!IsSha256(p.ManifestSha256)) Fail("manifestSha256 must be 64 lowercase hex characters.");
    }

    /// <summary>Structural checks shared by the launcher and the release tool.</summary>
    public static void ValidateRelease(ReleaseManifest m, string gameId, string platform)
    {
        void Fail(string why) => throw new UpdateException(UpdateErrorCode.ManifestInvalid, why);

        if (m.SchemaVersion != SchemaVersion) Fail($"Unsupported manifest schemaVersion {m.SchemaVersion}.");
        if (m.GameId != gameId) Fail($"Manifest is for game '{m.GameId}', expected '{gameId}'.");
        if (m.Platform != platform) Fail($"Manifest is for platform '{m.Platform}', expected '{platform}'.");
        ManifestPaths.Version(m.Version);
        if (m.ReleaseNotesUrl is { } notes &&
            !(Uri.TryCreate(notes, UriKind.Absolute, out var notesUri) && notesUri.Scheme == Uri.UriSchemeHttps))
            Fail("releaseNotesUrl must be an absolute https URL.");

        // Case-insensitive: both target file systems usually are, so "a.txt" and "A.txt" would collide.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Claim(string path)
        {
            ManifestPaths.RelativePath(path);
            if (!seen.Add(path)) Fail($"Duplicate path '{path}'.");
        }

        foreach (var f in m.Files)
        {
            Claim(f.Path);
            if (f.Size < 0) Fail($"Negative size for '{f.Path}'.");
            if (!IsSha256(f.Sha256)) Fail($"Bad sha256 for '{f.Path}'.");
            if (f.Mode is < 0 or > 0x1FF) Fail($"Mode for '{f.Path}' must be permission bits only.");
        }
        foreach (var d in m.Directories) Claim(d);
        foreach (var link in m.Symlinks)
        {
            Claim(link.Path);
            if (string.IsNullOrEmpty(link.Target) || link.Target.StartsWith('/') || link.Target.Contains('\\') ||
                link.Target.Contains(':') || link.Target.Length > 1024)
                Fail($"Symlink '{link.Path}' must have a relative target.");
            var parent = link.Path.Contains('/') ? link.Path[..link.Path.LastIndexOf('/')] : "";
            if (!StaysInside(parent, link.Target)) Fail($"Symlink '{link.Path}' points outside the release.");
        }

        // Nothing may be written through a symlink: no entry may live below one.
        var linkPrefixes = m.Symlinks.Select(l => l.Path + "/").ToArray();
        foreach (var path in seen)
        {
            if (linkPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                Fail($"'{path}' is inside a symlinked directory.");
        }

        var entry = m.Entrypoint;
        if (string.IsNullOrWhiteSpace(entry) || entry.Contains('/') || entry.Contains('\\'))
            Fail($"Entrypoint '{entry}' must be a top-level file or bundle name.");
        ManifestPaths.RelativePath(entry);
        var exists = m.Files.Any(f => f.Path == entry) ||
                     m.Files.Any(f => f.Path.StartsWith(entry + "/", StringComparison.Ordinal));
        if (!exists) Fail($"Entrypoint '{entry}' is not part of the release.");
    }

    internal static void CheckLauncherVersion(ReleaseManifest m, Version launcher)
    {
        if (m.MinimumLauncherVersion is { } min && VersionComparer.TryParse(min, out var minVersion) && launcher < minVersion)
            throw new UpdateException(UpdateErrorCode.LauncherTooOld,
                $"This release needs launcher {min} or newer (current {launcher}).");
    }

    /// <summary>HTTPS everywhere; plain HTTP only for loopback so a local dev feed works.</summary>
    internal static void RequireTrustedTransport(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
        throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Refusing non-HTTPS url {uri}.");
    }

    internal static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Lexically resolves <paramref name="target"/> from <paramref name="fromDirectory"/>; false if it climbs above the root.</summary>
    private static bool StaysInside(string fromDirectory, string target)
    {
        var stack = new Stack<string>(fromDirectory.Length == 0 ? [] : fromDirectory.Split('/'));
        foreach (var part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) return false;
                stack.Pop();
            }
            else stack.Push(part);
        }
        return true;
    }
}

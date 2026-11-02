using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;
using RhythmHub.Updater.Security;

namespace RhythmHub.Updater;

public sealed class ManifestClient(HttpClient http, ILogger<ManifestClient> logger)
{
    public static Uri BuildManifestUri(Uri feedBase, string gameId, string platform, string channel) =>
        new(EnsureTrailingSlash(feedBase),
            $"game/{GameInstallLayout.SafeName(gameId)}/{GameInstallLayout.SafeName(platform)}/{GameInstallLayout.SafeName(channel)}.json");

    public async Task<GameManifest> FetchAsync(UpdaterOptions options, string gameId, CancellationToken ct)
    {
        var uri = BuildManifestUri(options.FeedBaseUri, gameId, options.PlatformId, options.Channel);
        RequireTrustedTransport(uri);
        logger.LogInformation("Fetching manifest {Uri}", uri);

        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException(UpdateErrorCode.ManifestNotFound, $"No {options.Channel} release published for {gameId} on {options.PlatformId}.");
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                throw new UpdateException(UpdateErrorCode.RateLimited, $"Update server is busy ({(int)response.StatusCode}).");
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Failed to fetch manifest: {ex.Message}", ex);
        }

        var manifest = Parse(body, options.TrustedKeys);
        Validate(manifest, options, gameId);
        return manifest;
    }

    /// <summary>Verifies the signature over the bytes as received, then binds the model. Nothing is trusted before that.</summary>
    internal static GameManifest Parse(byte[] body, IReadOnlyDictionary<string, string> trustedKeys)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject node)
                throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest must be a JSON object.");
            ManifestSigning.Verify(node, trustedKeys);
            return node.Deserialize<GameManifest>(UpdaterJson.Options)
                ?? throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Manifest is not valid: {ex.Message}", ex);
        }
    }

    internal static void Validate(GameManifest m, UpdaterOptions options, string gameId)
    {
        void Fail(string why) => throw new UpdateException(UpdateErrorCode.ManifestInvalid, why);

        if (m.SchemaVersion != 1) Fail($"Unsupported manifest schemaVersion {m.SchemaVersion}.");
        if (m.GameId != gameId) Fail($"Manifest is for game '{m.GameId}', expected '{gameId}'.");
        if (m.Platform != options.PlatformId) Fail($"Manifest is for platform '{m.Platform}', expected '{options.PlatformId}'.");
        if (m.Channel != options.Channel) Fail($"Manifest is for channel '{m.Channel}', expected '{options.Channel}'.");
        GameInstallLayout.SafeName(m.Version);
        if (m.Package.SizeBytes <= 0) Fail("Package size must be positive.");
        if (m.Package.Sha256.Length != 64 || !m.Package.Sha256.All(char.IsAsciiHexDigit)) Fail("Package sha256 must be 64 hex characters.");
        if (!Uri.TryCreate(m.Package.Url, UriKind.Absolute, out var pkg)) Fail("Package url is not absolute.");
        RequireTrustedTransport(pkg!);
        if (string.IsNullOrWhiteSpace(m.Entrypoint) || Path.IsPathRooted(m.Entrypoint) ||
            m.Entrypoint.Contains("..") || m.Entrypoint.IndexOfAny(['/', '\\']) >= 0)
            Fail($"Entrypoint '{m.Entrypoint}' must be a plain file or bundle name.");

        if (m.MinimumLauncherVersion is { } min && VersionComparer.TryParse(min, out var minVersion) &&
            options.LauncherVersion < minVersion)
        {
            throw new UpdateException(UpdateErrorCode.LauncherTooOld,
                $"This release needs launcher {min} or newer (current {options.LauncherVersion}).");
        }
    }

    /// <summary>HTTPS everywhere; plain HTTP only for loopback so a local dev feed works.</summary>
    internal static void RequireTrustedTransport(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
        throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Refusing non-HTTPS url {uri}.");
    }

    private static Uri EnsureTrailingSlash(Uri u) =>
        u.AbsoluteUri.EndsWith('/') ? u : new Uri(u.AbsoluteUri + "/");
}

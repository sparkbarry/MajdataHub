using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

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

        GameManifest? manifest;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException(UpdateErrorCode.ManifestNotFound, $"No {options.Channel} release published for {gameId} on {options.PlatformId}.");
            response.EnsureSuccessStatusCode();
            manifest = await response.Content.ReadFromJsonAsync<GameManifest>(UpdaterJson.Options, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Failed to fetch manifest: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, $"Manifest is not valid JSON: {ex.Message}", ex);
        }

        if (manifest is null)
            throw new UpdateException(UpdateErrorCode.ManifestInvalid, "Manifest is empty.");

        Validate(manifest, options, gameId);
        // TODO(phase B): verify manifest.Signature against the built-in public key before trusting anything in it.
        return manifest;
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

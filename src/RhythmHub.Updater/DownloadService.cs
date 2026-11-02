using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace RhythmHub.Updater;

/// <summary>
/// Fetches one immutable object into a partial file. Version paths never change once published and every object is
/// checked against its manifest hash afterwards, so an existing prefix is resumed with a plain Range request.
/// </summary>
public sealed class DownloadService(HttpClient http, ILogger<DownloadService> logger)
{
    private const int BufferSize = 1 << 17;

    public int MaxAttempts { get; init; } = 4;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No bytes for this long counts as a stalled connection and triggers a retry.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <param name="onBytes">Receives byte deltas (negative when a prefix is thrown away). Called from any thread.</param>
    public async Task DownloadAsync(Uri uri, string partialPath, long expectedSize, Action<long> onBytes, CancellationToken ct)
    {
        ManifestClient.RequireTrustedTransport(uri);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await AttemptAsync(uri, partialPath, expectedSize, onBytes, ct);
                return;
            }
            catch (UpdateException ex) when (IsTransient(ex.Code) && attempt < MaxAttempts)
            {
                var delay = Backoff(attempt, ex);
                logger.LogWarning("Attempt {Attempt} for {Uri} failed ({Code}: {Message}); retrying in {Delay}",
                    attempt, uri, ex.Code, ex.Message, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task AttemptAsync(Uri uri, string partialPath, long expectedSize, Action<long> onBytes, CancellationToken ct)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > expectedSize)
        {
            File.Delete(partialPath);
            onBytes(-existing);
            existing = 0;
        }
        if (existing == expectedSize) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Download failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorCode.Timeout, "Download request timed out.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent when existing > 0:
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != existing || range.Length != expectedSize)
                        throw new UpdateException(UpdateErrorCode.DownloadIncomplete,
                            $"Server returned range {range} for a resume at {existing} of {expectedSize}.");
                    break;
                case HttpStatusCode.OK:
                    if (existing > 0)
                    {
                        File.Delete(partialPath);
                        onBytes(-existing);
                        existing = 0;
                    }
                    if (response.Content.Headers.ContentLength is { } len && len != expectedSize)
                        throw new UpdateException(UpdateErrorCode.DownloadIncomplete,
                            $"Server reports {len} bytes for {uri}, manifest says {expectedSize}.");
                    break;
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    File.Delete(partialPath);
                    onBytes(-existing);
                    throw new UpdateException(UpdateErrorCode.Network, "Partial download no longer matches the server; restarting.");
                // S3 answers 403 for missing keys when the bucket does not allow listing.
                case HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden:
                    throw new UpdateException(UpdateErrorCode.PackageNotFound, $"File not found at {uri}.");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                    throw new RateLimitedException(response.Headers.RetryAfter?.Delta);
                case >= HttpStatusCode.InternalServerError:
                    throw new UpdateException(UpdateErrorCode.Network, $"Server error {(int)response.StatusCode}.");
                default:
                    throw new UpdateException(UpdateErrorCode.Network, $"Unexpected HTTP status {(int)response.StatusCode}.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await CopyAsync(source, target, existing, expectedSize, onBytes, ct);
        }
    }

    private async Task CopyAsync(Stream source, FileStream target, long done, long expectedSize, Action<long> onBytes,
        CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (true)
            {
                stall.CancelAfter(InactivityTimeout);
                var read = await source.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                done += read;
                if (done > expectedSize)
                    throw new UpdateException(UpdateErrorCode.DownloadIncomplete, "Server sent more data than the manifest declares.");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                onBytes(read);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorCode.Timeout, $"No data received for {InactivityTimeout.TotalSeconds:0}s.", ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Download interrupted: {ex.Message}", ex);
        }
        finally
        {
            await target.FlushAsync(CancellationToken.None);
        }

        if (done != expectedSize)
            throw new UpdateException(UpdateErrorCode.Network, $"Connection closed after {done} of {expectedSize} bytes.");
    }

    private static bool IsTransient(UpdateErrorCode code) =>
        code is UpdateErrorCode.Network or UpdateErrorCode.Timeout or UpdateErrorCode.RateLimited;

    private TimeSpan Backoff(int attempt, UpdateException ex)
    {
        if (ex is RateLimitedException { RetryAfter: { } hint })
            return hint < MaxBackoff ? hint : MaxBackoff;
        var delay = InitialBackoff * Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4);
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    private sealed class RateLimitedException(TimeSpan? retryAfter)
        : UpdateException(UpdateErrorCode.RateLimited, "Update server is rate limiting downloads.")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

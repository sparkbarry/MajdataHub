using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

/// <summary>Validator captured from the server so a later attempt can resume with If-Range.</summary>
public sealed record DownloadCheckpoint(string? ETag, string? LastModified)
{
    public bool CanResume => ETag is { } e && !e.StartsWith("W/", StringComparison.Ordinal) || LastModified is not null;
}

public sealed class DownloadService(HttpClient http, ILogger<DownloadService> logger)
{
    private const int BufferSize = 1 << 17;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public int MaxAttempts { get; init; } = 4;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No bytes for this long counts as a stalled connection and triggers a retry.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fills <paramref name="partialPath"/> with exactly <paramref name="expectedSize"/> bytes, resuming any existing
    /// prefix when <paramref name="checkpoint"/> proves the remote file is unchanged. Hash checking is the caller's job.
    /// </summary>
    /// <param name="onCheckpoint">Called before body bytes are written, so the validator is persisted first.</param>
    public async Task DownloadAsync(Uri uri, string partialPath, long expectedSize, DownloadCheckpoint? checkpoint,
        Func<DownloadCheckpoint, Task> onCheckpoint, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ManifestClient.RequireTrustedTransport(uri);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

        // Keep the newest validator even when an attempt fails mid-body, so the retry can resume.
        async Task Track(DownloadCheckpoint cp)
        {
            checkpoint = cp;
            await onCheckpoint(cp);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await AttemptAsync(uri, partialPath, expectedSize, checkpoint, Track, progress, ct);
                return;
            }
            catch (UpdateException ex) when (IsTransient(ex.Code) && attempt < MaxAttempts)
            {
                var delay = Backoff(attempt, ex);
                logger.LogWarning("Download attempt {Attempt} failed ({Code}: {Message}); retrying in {Delay}",
                    attempt, ex.Code, ex.Message, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task AttemptAsync(Uri uri, string partialPath, long expectedSize,
        DownloadCheckpoint? checkpoint, Func<DownloadCheckpoint, Task> onCheckpoint,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > expectedSize || (existing > 0 && checkpoint is not { CanResume: true }))
        {
            logger.LogInformation("Discarding {Bytes} bytes of unverifiable partial download", existing);
            File.Delete(partialPath);
            existing = 0;
        }
        if (existing == expectedSize)
        {
            progress?.Report(new DownloadProgress(existing, expectedSize));
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            request.Headers.IfRange = checkpoint!.ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal)
                ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag))
                : new RangeConditionHeaderValue(DateTimeOffset.Parse(checkpoint.LastModified!, System.Globalization.CultureInfo.InvariantCulture));
            logger.LogInformation("Resuming {Uri} at byte {Offset}", uri, existing);
        }
        else
        {
            logger.LogInformation("Downloading {Uri} ({Size} bytes)", uri, expectedSize);
        }

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
                        // Remote file changed (If-Range failed) or ranges unsupported: the old prefix is worthless.
                        logger.LogInformation("Server sent the full file instead of a range; restarting from zero");
                        File.Delete(partialPath);
                        existing = 0;
                    }
                    if (response.Content.Headers.ContentLength is { } len && len != expectedSize)
                        throw new UpdateException(UpdateErrorCode.DownloadIncomplete,
                            $"Server reports {len} bytes, manifest says {expectedSize}.");
                    break;
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    File.Delete(partialPath);
                    throw new UpdateException(UpdateErrorCode.Network, "Partial download no longer matches the server; restarting.");
                case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    throw new UpdateException(UpdateErrorCode.PackageNotFound, $"Package not found at {uri}.");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable:
                    throw new RateLimitedException(response.Headers.RetryAfter?.Delta);
                case >= HttpStatusCode.InternalServerError:
                    throw new UpdateException(UpdateErrorCode.Network, $"Server error {(int)response.StatusCode}.");
                default:
                    throw new UpdateException(UpdateErrorCode.Network, $"Unexpected HTTP status {(int)response.StatusCode}.");
            }

            var next = new DownloadCheckpoint(response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"));
            await onCheckpoint(next);

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await CopyAsync(source, target, existing, expectedSize, progress, ct);
        }
    }

    private async Task CopyAsync(Stream source, FileStream target, long done, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        progress?.Report(new DownloadProgress(done, expectedSize));
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
                if (sw.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = sw.Elapsed;
                    progress?.Report(new DownloadProgress(done, expectedSize));
                }
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

        progress?.Report(new DownloadProgress(done, expectedSize));
        if (done != expectedSize)
            throw new UpdateException(UpdateErrorCode.Network, $"Connection closed after {done} of {expectedSize} bytes.");
    }

    private static bool IsTransient(UpdateErrorCode code) =>
        code is UpdateErrorCode.Network or UpdateErrorCode.Timeout or UpdateErrorCode.RateLimited;

    private TimeSpan Backoff(int attempt, UpdateException ex)
    {
        if (ex is RateLimitedException { RetryAfter: { } hint })
            return hint < MaxBackoff ? hint : MaxBackoff;
        var exp = InitialBackoff * Math.Pow(2, attempt - 1);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        var delay = exp * jitter;
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    private sealed class RateLimitedException(TimeSpan? retryAfter)
        : UpdateException(UpdateErrorCode.RateLimited, "Update server is rate limiting downloads.")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

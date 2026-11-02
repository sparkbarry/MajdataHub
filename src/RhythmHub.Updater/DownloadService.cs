using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RhythmHub.Updater.Models;

namespace RhythmHub.Updater;

public sealed class DownloadService(HttpClient http, ILogger<DownloadService> logger)
{
    private const int BufferSize = 1 << 17;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Streams <paramref name="uri"/> into <paramref name="partialPath"/>. The caller renames the file once its hash is verified.
    /// TODO(phase B): resume via HTTP Range + ETag instead of restarting.
    /// </summary>
    public async Task DownloadAsync(Uri uri, string partialPath, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ManifestClient.RequireTrustedTransport(uri);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        logger.LogInformation("Downloading {Uri} ({Size} bytes) to {Path}", uri, expectedSize, partialPath);

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } len && len != expectedSize)
                throw new UpdateException(UpdateErrorCode.DownloadIncomplete,
                    $"Server reports {len} bytes, manifest says {expectedSize}.");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            var buffer = new byte[BufferSize];
            long done = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
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
            await target.FlushAsync(ct);
            progress?.Report(new DownloadProgress(done, expectedSize));

            if (done != expectedSize)
                throw new UpdateException(UpdateErrorCode.DownloadIncomplete, $"Downloaded {done} of {expectedSize} bytes.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Download failed: {ex.Message}", ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new UpdateException(UpdateErrorCode.Network, $"Download interrupted: {ex.Message}", ex);
        }
    }
}

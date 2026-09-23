using System.Net;
using System.Net.Http.Headers;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Downloads;

public sealed record DownloadProgress(
    string Component,
    long ReceivedBytes,
    long TotalBytes,
    string? Notice = null,
    int Attempt = 1);

public sealed class VerifiedDownloader
{
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly Func<int, TimeSpan> _retryDelay;
    private readonly Func<Uri, Uri>? _sourceRewriter;
    private readonly TimeSpan _stallTimeout;
    private readonly int _maxAttempts;

    /// <summary>Optional progress sink shared by every download through this instance.</summary>
    public IProgress<DownloadProgress>? Progress { get; set; }

    public VerifiedDownloader(HttpClient httpClient)
        : this(httpClient, null, null)
    {
    }

    public VerifiedDownloader(
        HttpClient httpClient,
        Func<int, TimeSpan>? retryDelay)
        : this(httpClient, retryDelay, null)
    {
    }

    public VerifiedDownloader(
        HttpClient httpClient,
        Func<int, TimeSpan>? retryDelay,
        Func<Uri, Uri>? sourceRewriter,
        TimeSpan? stallTimeout = null,
        int maxAttempts = 8)
    {
        _httpClient = httpClient;
        _retryDelay = retryDelay ?? (attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt) - 1)));
        _sourceRewriter = sourceRewriter;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public async Task DownloadAsync(
        Uri source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(source, destination, expectedSha256, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                attempt < _maxAttempts &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or OperationCanceledException or IOException)
            {
                // A stalled or dropped connection resumes from the verified partial file.
                Progress?.Report(new DownloadProgress(
                    Path.GetFileName(destination),
                    PartialLength(destination),
                    0,
                    $"连接停滞或中断，正在重试（第 {attempt + 1}/{_maxAttempts} 次）",
                    attempt + 1));
                await Task.Delay(_retryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static long PartialLength(string destination)
    {
        var partial = Path.GetFullPath(destination) + ".partial";
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    private async Task DownloadOnceAsync(
        Uri source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
        }

        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(directory);

        var effectiveSource = _sourceRewriter?.Invoke(source) ?? source;
        var component = Path.GetFileName(fullDestination);
        var partialPath = fullDestination + ".partial";
        try
        {
            var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existingLength > 0)
            {
                var partialHash = await Sha256Verifier.ComputeAsync(partialPath, cancellationToken)
                    .ConfigureAwait(false);
                if (partialHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(partialPath, fullDestination, overwrite: true);
                    return;
                }
            }

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(_stallTimeout);
            HttpResponseMessage? response = null;
            try
            {
                response = await SendAsync(effectiveSource, existingLength, stall.Token).ConfigureAwait(false);
                if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.Dispose();
                    File.Delete(partialPath);
                    existingLength = 0;
                    response = await SendAsync(effectiveSource, 0, stall.Token).ConfigureAwait(false);
                }

                response.EnsureSuccessStatusCode();
                var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (existingLength > 0 && !append)
                {
                    File.Delete(partialPath);
                }

                var contentLength = response.Content.Headers.ContentLength ?? 0;
                var totalLength = append ? existingLength + contentLength : contentLength;
                var received = existingLength;
                Progress?.Report(new DownloadProgress(component, received, totalLength));

                await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
                await using (var output = new FileStream(
                    partialPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                        received += read;
                        stall.CancelAfter(_stallTimeout);
                        Progress?.Report(new DownloadProgress(component, received, totalLength));
                    }
                }
            }
            finally
            {
                response?.Dispose();
            }

            var actualSha256 = await Sha256Verifier.ComputeAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                var detail = effectiveSource == source ? source.ToString() : $"{source} (mirror {effectiveSource})";
                throw new InvalidDataException(
                    $"SHA-256 mismatch for '{detail}'. Expected {expectedSha256}, got {actualSha256}.");
            }

            File.Move(partialPath, fullDestination, overwrite: true);
            var size = new FileInfo(fullDestination).Length;
            Progress?.Report(new DownloadProgress(component, size, size));
        }
        catch (InvalidDataException)
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        long existingLength,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;

namespace LISSTech.EntitySync.Adapters;

internal static class VendorHttpClientFactory
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int MaximumConnectionsPerOrigin = 8;
    internal static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(500);

    public static HttpClient Create(Uri? baseAddress = null)
    {
        var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = MaximumConnectionsPerOrigin,
            AutomaticDecompression = DecompressionMethods.None
        };
        return Create(baseAddress, transport, MaximumResponseBytes, MaximumConnectionsPerOrigin, MinimumRequestInterval);
    }

    internal static HttpClient Create(
        Uri? baseAddress,
        HttpMessageHandler innerHandler,
        int maximumResponseBytes = MaximumResponseBytes,
        int maximumConcurrency = MaximumConnectionsPerOrigin,
        TimeSpan? minimumRequestInterval = null)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        var client = new HttpClient(new VendorHttpPolicyHandler(
            innerHandler,
            maximumResponseBytes,
            maximumConcurrency,
            minimumRequestInterval ?? MinimumRequestInterval), disposeHandler: true);
        client.BaseAddress = baseAddress;
        return client;
    }
}

internal sealed class VendorHttpPolicyHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<EndpointPolicyKey, EndpointGate> EndpointGates = new();
    private readonly int maximumResponseBytes;
    private readonly int maximumConcurrency;
    private readonly TimeSpan minimumRequestInterval;

    public VendorHttpPolicyHandler(
        HttpMessageHandler innerHandler,
        int maximumResponseBytes,
        int maximumConcurrency,
        TimeSpan minimumRequestInterval)
        : base(innerHandler)
    {
        if (maximumResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        if (maximumConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (minimumRequestInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        this.maximumResponseBytes = maximumResponseBytes;
        this.maximumConcurrency = maximumConcurrency;
        this.minimumRequestInterval = minimumRequestInterval;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
            throw new InvalidOperationException("Vendor HTTP requests require an absolute URI after BaseAddress resolution.");

        var key = new EndpointPolicyKey(uri.GetLeftPart(UriPartial.Authority), maximumConcurrency, minimumRequestInterval);
        var gate = EndpointGates.GetOrAdd(key, static item => new EndpointGate(item.MaximumConcurrency, item.MinimumRequestInterval));
        await gate.WaitForStartAsync(cancellationToken).ConfigureAwait(false);
        await gate.Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await BufferBoundedContentAsync(response, maximumResponseBytes, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            gate.Concurrency.Release();
        }
    }

    private static async Task BufferBoundedContentAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        var content = response.Content;
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength > maximumBytes)
            throw new InvalidDataException($"Vendor response exceeded the {maximumBytes}-byte limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(
            declaredLength.HasValue && declaredLength.Value > 0 && declaredLength.Value <= maximumBytes
                ? (int)declaredLength.Value
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (destination.Length + read > maximumBytes)
                    throw new InvalidDataException($"Vendor response exceeded the {maximumBytes}-byte limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var replacement = new ByteArrayContent(destination.ToArray());
        foreach (var header in content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        content.Dispose();
    }

    private readonly record struct EndpointPolicyKey(string Origin, int MaximumConcurrency, TimeSpan MinimumRequestInterval);

    private sealed class EndpointGate(int maximumConcurrency, TimeSpan minimumRequestInterval)
    {
        private readonly SemaphoreSlim schedule = new(1, 1);
        private DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;
        public SemaphoreSlim Concurrency { get; } = new(maximumConcurrency, maximumConcurrency);

        public async Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            await schedule.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (nextRequestAt > now)
                    await Task.Delay(nextRequestAt - now, cancellationToken).ConfigureAwait(false);
                nextRequestAt = DateTimeOffset.UtcNow + minimumRequestInterval;
            }
            finally
            {
                schedule.Release();
            }
        }
    }
}

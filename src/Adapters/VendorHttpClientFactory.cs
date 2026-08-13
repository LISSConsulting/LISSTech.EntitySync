using System.Buffers;
using System.Collections.Concurrent;
using System.Net;

namespace LISSTech.EntitySync.Adapters;

internal static class VendorHttpClientFactory
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int MaximumConnectionsPerOrigin = 8;
    internal static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(500);
    internal const int MaximumCachedEndpointGates = 256;
    internal static readonly TimeSpan EndpointGateIdleLifetime = TimeSpan.FromMinutes(30);

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
    private static long requestsSinceEndpointGateCleanup;
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
        var gate = AcquireGate(key);
        var concurrencyEntered = false;
        HttpResponseMessage? response = null;
        try
        {
            await gate.WaitForStartAsync(cancellationToken).ConfigureAwait(false);
            await gate.Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            concurrencyEntered = true;
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
            if (concurrencyEntered) gate.Concurrency.Release();
            gate.Release();
            if (EndpointGates.Count > VendorHttpClientFactory.MaximumCachedEndpointGates
                || Interlocked.Increment(ref requestsSinceEndpointGateCleanup) % 64 == 0)
            {
                TrimEndpointGates(
                    VendorHttpClientFactory.MaximumCachedEndpointGates,
                    VendorHttpClientFactory.EndpointGateIdleLifetime);
            }
        }
    }

    private static EndpointGate AcquireGate(EndpointPolicyKey key)
    {
        while (true)
        {
            var gate = EndpointGates.GetOrAdd(
                key,
                static item => new EndpointGate(item.MaximumConcurrency, item.MinimumRequestInterval));
            if (gate.TryAcquire()) return gate;
            RemoveExact(key, gate);
        }
    }

    internal static int TrimEndpointGates(int maximumCachedGates, TimeSpan idleLifetime)
    {
        if (maximumCachedGates < 0) throw new ArgumentOutOfRangeException(nameof(maximumCachedGates));
        if (idleLifetime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleLifetime));

        var now = DateTimeOffset.UtcNow;
        var cutoff = idleLifetime == TimeSpan.MaxValue ? DateTimeOffset.MinValue : now - idleLifetime;
        var candidates = EndpointGates.ToArray();
        var removed = 0;
        foreach (var candidate in candidates)
        {
            var overCapacity = EndpointGates.Count > maximumCachedGates;
            if (!candidate.Value.TryRetire(cutoff, overCapacity)) continue;
            if (RemoveExact(candidate.Key, candidate.Value)) removed++;
        }

        return removed;
    }

    internal static int CachedEndpointGateCount => EndpointGates.Count;

    private static bool RemoveExact(EndpointPolicyKey key, EndpointGate gate)
    {
        return ((ICollection<KeyValuePair<EndpointPolicyKey, EndpointGate>>)EndpointGates)
            .Remove(new KeyValuePair<EndpointPolicyKey, EndpointGate>(key, gate));
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

    private sealed class EndpointGate
    {
        private readonly object lifecycle = new();
        private readonly SemaphoreSlim schedule = new(1, 1);
        private readonly TimeSpan minimumRequestInterval;
        private DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;
        private DateTimeOffset lastUsedAt = DateTimeOffset.UtcNow;
        private int users;
        private bool retired;

        public EndpointGate(int maximumConcurrency, TimeSpan minimumRequestInterval)
        {
            this.minimumRequestInterval = minimumRequestInterval;
            Concurrency = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        }

        public SemaphoreSlim Concurrency { get; }

        public DateTimeOffset LastUsedAt
        {
            get
            {
                lock (lifecycle) return lastUsedAt;
            }
        }

        public bool TryAcquire()
        {
            lock (lifecycle)
            {
                if (retired) return false;
                users++;
                return true;
            }
        }

        public void Release()
        {
            lock (lifecycle)
            {
                if (users <= 0) throw new InvalidOperationException("Endpoint gate user count is invalid.");
                users--;
                lastUsedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool TryRetire(DateTimeOffset idleCutoff, bool force)
        {
            lock (lifecycle)
            {
                if (retired || users != 0 || (!force && lastUsedAt > idleCutoff)) return false;
                retired = true;
                return true;
            }
        }

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

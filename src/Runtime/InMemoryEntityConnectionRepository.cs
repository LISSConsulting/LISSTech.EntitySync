using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class InMemoryEntityConnectionRepository
    : IEntityConnectionRepository, IConnectionRuntimeFactory, IDisposable
{
    private const int MaxConnectionsPerTenant = 32;
    private readonly object gate = new();
    private readonly Dictionary<string, ConnectionEntry> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> registrationAdmissions = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public static InMemoryEntityConnectionRepository CreateLocalProfile() => new();


    public IEntityConnectionAdmission BeginRegistration(string tenantId, string? connectionId, string vendor)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        vendor = EntitySyncVendors.Normalize(vendor);
        connectionId = string.IsNullOrWhiteSpace(connectionId) ? vendor.ToLowerInvariant() : connectionId.Trim();
        ValidateConnectionId(connectionId);
        var key = Key(tenantId, connectionId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!registrationAdmissions.Add(key))
                throw new InvalidOperationException($"Connection '{connectionId}' is already being configured.");
            if (!connections.ContainsKey(key)
                && connections.Values.Count(entry => entry.Current?.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true)
                    + registrationAdmissions.Count(admission => admission.StartsWith(tenantId + "\n", StringComparison.OrdinalIgnoreCase)) - 1
                    >= MaxConnectionsPerTenant)
            {
                registrationAdmissions.Remove(key);
                throw new InvalidOperationException($"Tenant connection limit of {MaxConnectionsPerTenant} has been reached.");
            }

            return new ConnectionAdmission(tenantId, connectionId, () => ReleaseAdmission(key));
        }
    }

    public EntityConnectionRegistration Register(string tenantId, string? connectionId, IEntityAdapter adapter)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        var vendor = EntitySyncVendors.Normalize(adapter.Vendor);
        connectionId = string.IsNullOrWhiteSpace(connectionId) ? vendor.ToLowerInvariant() : connectionId.Trim();
        ValidateConnectionId(connectionId);
        var key = Key(tenantId, connectionId);

        IDisposable? displaced = null;
        EntityConnectionRegistration registration;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!connections.TryGetValue(key, out var entry))
            {
                if (connections.Values.Count(entry => entry.Current?.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true) >= MaxConnectionsPerTenant)
                    throw new InvalidOperationException($"Tenant connection limit of {MaxConnectionsPerTenant} has been reached.");
                registration = new EntityConnectionRegistration(connectionId, tenantId, vendor, 1, adapter);
                connections.Add(key, new ConnectionEntry(registration));
                return registration;
            }

            var current = entry.Current ?? throw new ObjectDisposedException(nameof(InMemoryEntityConnectionRepository));
            if (ReferenceEquals(current.Adapter, adapter)) return current;
            registration = new EntityConnectionRegistration(connectionId, tenantId, vendor, current.Generation + 1, adapter);
            entry.Current = registration;
            if (current.Adapter is IDisposable disposable)
            {
                if (entry.Leases.ContainsKey(current.Generation)) entry.Retired[current.Generation] = disposable;
                else displaced = disposable;
            }
        }

        displaced?.Dispose();
        return registration;
    }

    public EntityConnectionRegistration Resolve(string tenantId, string vendor, string? connectionId = null)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        vendor = EntitySyncVendors.Normalize(vendor);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ResolveLocked(tenantId, vendor, connectionId).Current!;
        }
    }

    public IEntityConnectionLease Acquire(string tenantId, string vendor, string? connectionId = null, long? generation = null)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        vendor = EntitySyncVendors.Normalize(vendor);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = ResolveLocked(tenantId, vendor, connectionId);
            var current = entry.Current!;
            if (generation.HasValue && current.Generation != generation.Value)
                throw new StaleConnectionGenerationException(
                    current.Id,
                    generation.Value,
                    current.Generation);
            entry.Leases[current.Generation] = entry.Leases.GetValueOrDefault(current.Generation) + 1;
            return new ConnectionLease(current, () => Release(entry, current.Generation));
        }
    }

    public Task<IConnectionRuntimeLease> AcquireAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registration = List(tenantId).SingleOrDefault(connection =>
            connection.Id.Equals(connectionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConnectionNotFoundException(tenantId, connectionId);
        var lease = Acquire(
            tenantId,
            registration.Vendor,
            registration.Id,
            expectedGeneration);
        return Task.FromResult<IConnectionRuntimeLease>(
            new RuntimeLease(lease, Definition(registration)));
    }

    public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = Acquire(tenantId, vendor, connectionId);
        return Task.FromResult<IConnectionRuntimeLease>(
            new RuntimeLease(lease, Definition(lease.Connection)));
    }

    public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedVendor = EntitySyncVendors.Normalize(vendor);
        var matches = List(tenantId)
            .Where(connection =>
                connection.Vendor.Equals(normalizedVendor, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(connectionId)
                    || connection.Id.Equals(connectionId.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var registration = matches.Length switch
        {
            1 => matches[0],
            0 when !string.IsNullOrWhiteSpace(connectionId) =>
                throw new ConnectionNotFoundException(tenantId, connectionId.Trim()),
            0 => throw new InvalidOperationException(
                $"No enabled connection exists for vendor '{normalizedVendor}'."),
            _ => throw new InvalidOperationException(
                $"Multiple enabled connections exist for vendor '{normalizedVendor}'. "
                + "Specify a connection ID.")
        };
        return Task.FromResult(Definition(registration));
    }

    private static EntitySyncConnectionDefinition Definition(
        EntityConnectionRegistration registration)
    {
        var now = DateTimeOffset.UnixEpoch;
        var actor = new EntitySyncActor("local-profile");
        return new EntitySyncConnectionDefinition(
            registration.TenantId,
            registration.Id,
            registration.Vendor,
            registration.Id,
            registration.Generation,
            true,
            new EntitySyncJsonValue("{}"),
            "local-profile",
            now,
            actor,
            now,
            actor);
    }

    public IReadOnlyList<EntityConnectionRegistration> List(string tenantId)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return connections.Values
                .Select(entry => entry.Current)
                .Where(connection => connection != null && connection.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(connection => connection!.Vendor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(connection => connection!.Id, StringComparer.OrdinalIgnoreCase)
                .Cast<EntityConnectionRegistration>()
                .ToArray();
        }
    }

    public void Dispose()
    {
        List<IDisposable> disposables = [];
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in connections.Values)
            {
                if (entry.Current?.Adapter is IDisposable current)
                {
                    if (entry.Leases.ContainsKey(entry.Current.Generation)) entry.Retired[entry.Current.Generation] = current;
                    else disposables.Add(current);
                }
                entry.Current = null;
                foreach (var retired in entry.Retired.Where(pair => !entry.Leases.ContainsKey(pair.Key)).ToArray())
                {
                    disposables.Add(retired.Value);
                    entry.Retired.Remove(retired.Key);
                }
            }
        }
        foreach (var disposable in disposables.Distinct(ReferenceEqualityComparer.Instance).OfType<IDisposable>()) disposable.Dispose();
    }

    private void ReleaseAdmission(string key)
    {
        lock (gate)
        {
            registrationAdmissions.Remove(key);
        }
    }

    private ConnectionEntry ResolveLocked(string tenantId, string vendor, string? connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            if (connections.TryGetValue(Key(tenantId, connectionId.Trim()), out var exact)
                && exact.Current?.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase) == true) return exact;
            throw new InvalidOperationException($"Connection '{connectionId.Trim()}' for vendor '{vendor}' was not found.");
        }

        var matches = connections.Values
            .Where(entry => entry.Current?.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true
                && entry.Current.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No connection exists for vendor '{vendor}'. Connect it first."),
            _ => throw new InvalidOperationException($"Multiple connections exist for vendor '{vendor}'. Specify a connection ID.")
        };
    }

    private void Release(ConnectionEntry entry, long generation)
    {
        IDisposable? disposable = null;
        lock (gate)
        {
            if (!entry.Leases.TryGetValue(generation, out var count)) return;
            if (count > 1) entry.Leases[generation] = count - 1;
            else
            {
                entry.Leases.Remove(generation);
                if (entry.Retired.Remove(generation, out var retired)) disposable = retired;
            }
        }
        disposable?.Dispose();
    }

    private static string Key(string tenantId, string connectionId) => $"{tenantId.Trim()}\n{connectionId.Trim()}";

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name} is required.", name)
        : value.Trim();

    private static void ValidateConnectionId(string connectionId)
    {
        if (connectionId.Length > 64 || !char.IsLetterOrDigit(connectionId[0]) || connectionId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
            throw new ArgumentException("Connection ID must be 1-64 letters, numbers, dots, underscores, or hyphens and must start with a letter or number.", nameof(connectionId));
    }

    private sealed class ConnectionEntry(EntityConnectionRegistration current)
    {
        public EntityConnectionRegistration? Current { get; set; } = current;
        public Dictionary<long, int> Leases { get; } = [];
        public Dictionary<long, IDisposable> Retired { get; } = [];
    }

    private sealed class ConnectionAdmission(string tenantId, string connectionId, Action release) : IEntityConnectionAdmission
    {
        private Action? release = release;
        public string TenantId { get; } = tenantId;
        public string ConnectionId { get; } = connectionId;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    private sealed class RuntimeLease(
        IEntityConnectionLease lease,
        EntitySyncConnectionDefinition definition) : IConnectionRuntimeLease
    {
        private IEntityConnectionLease? lease = lease;
        public EntitySyncConnectionDefinition Definition { get; } = definition;
        public IEntityAdapter Adapter => lease?.Connection.Adapter
            ?? throw new ObjectDisposedException(nameof(RuntimeLease));

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref lease, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectionLease(EntityConnectionRegistration connection, Action release) : IEntityConnectionLease
    {
        private Action? release = release;
        public EntityConnectionRegistration Connection { get; } = connection;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

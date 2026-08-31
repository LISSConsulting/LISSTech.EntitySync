using System.Reflection;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Builder;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ConnectionDefinitionServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly EntitySyncActor Actor = new("operator");

    [Fact]
    public async Task Create_encrypts_secrets_and_never_returns_plaintext()
    {
        var repository = new ConnectionRepository();
        var protector = new RecordingProtector();
        var runtime = new FakeRuntimeFactory(repository);
        var service = CreateService(repository, protector, runtime);

        var created = await service.CreateAsync("tenant-a", Request(), Actor, default);

        Assert.Equal(1, created.Generation);
        Assert.DoesNotContain("super-secret", created.SecretCiphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", created.PublicConfiguration.Json, StringComparison.Ordinal);
        Assert.Contains("super-secret", protector.LastProtectedPlaintext!, StringComparison.Ordinal);
        Assert.Null(await repository.GetAsync("tenant-b", created.ConnectionId, default));
    }

    [Fact]
    public async Task Get_and_list_are_tenant_scoped_and_do_not_decrypt_secrets()
    {
        var repository = new ConnectionRepository();
        var protector = new RecordingProtector();
        var service = CreateService(repository, protector, new FakeRuntimeFactory(repository));
        await service.CreateAsync("tenant-a", Request(), Actor, default);
        await service.CreateAsync("tenant-b", Request(), Actor, default);

        var found = await service.GetAsync("tenant-a", "halo-main", default);
        var listed = await service.ListAsync("tenant-a", null, null, default);

        Assert.Equal("tenant-a", found.TenantId);
        Assert.Single(listed);
        Assert.Equal(0, protector.UnprotectCalls);
        await Assert.ThrowsAsync<ConnectionNotFoundException>(
            () => service.GetAsync("tenant-c", "halo-main", default));
    }

    [Fact]
    public async Task Test_uses_an_exact_generation_lease_and_disposes_the_adapter()
    {
        var repository = new ConnectionRepository();
        var runtime = new FakeRuntimeFactory(repository);
        var service = CreateService(repository, new RecordingProtector(), runtime);
        var created = await service.CreateAsync("tenant", Request(), Actor, default);

        Assert.True(await service.TestAsync("tenant", created.ConnectionId, created.Generation, default));

        Assert.Equal(1, runtime.AcquireCalls);
        Assert.Equal(created.Generation, runtime.LastGeneration);
        Assert.True(runtime.LastAdapter!.Disposed);
    }

    [Fact]
    public async Task Updating_credentials_increments_generation_and_invalidates_old_lease()
    {
        var repository = new ConnectionRepository();
        var runtime = new FakeRuntimeFactory(repository);
        var service = CreateService(repository, new RecordingProtector(), runtime);
        var first = await service.CreateAsync("tenant", Request(), Actor, default);
        var second = await service.UpdateAsync(
            "tenant",
            first.ConnectionId,
            first.Generation,
            Request(secret: "rotated-secret"),
            Actor,
            default);

        Assert.Equal(first.Generation + 1, second.Generation);
        await Assert.ThrowsAsync<StaleConnectionGenerationException>(
            () => runtime.AcquireAsync("tenant", second.ConnectionId, first.Generation, default));
    }

    [Fact]
    public async Task Concurrent_updates_allow_exactly_one_generation_compare_and_swap()
    {
        var repository = new ConnectionRepository();
        var service = CreateService(repository, new RecordingProtector(), new FakeRuntimeFactory(repository));
        var first = await service.CreateAsync("tenant", Request(), Actor, default);

        var results = await Task.WhenAll(
            Capture(() => service.UpdateAsync("tenant", first.ConnectionId, 1, Request(secret: "secret-a"), Actor, default)),
            Capture(() => service.UpdateAsync("tenant", first.ConnectionId, 1, Request(secret: "secret-b"), Actor, default)));

        Assert.Single(results, result => result.Definition is not null);
        Assert.Single(results, result => result.Error is ConnectionGenerationConflictException);
        Assert.Equal(2, (await service.GetAsync("tenant", first.ConnectionId, default)).Generation);
    }

    [Fact]
    public async Task Disable_is_generation_fenced_and_delete_falls_back_when_referenced()
    {
        var repository = new ConnectionRepository { Referenced = true };
        var service = CreateService(repository, new RecordingProtector(), new FakeRuntimeFactory(repository));
        var created = await service.CreateAsync("tenant", Request(), Actor, default);

        var result = await service.DeleteAsync("tenant", created.ConnectionId, created.Generation, Actor, default);

        Assert.Equal(ConnectionDeleteOutcome.Disabled, result.Outcome);
        Assert.False(result.Definition!.Enabled);
        Assert.Equal(2, result.Definition.Generation);
        await Assert.ThrowsAsync<ConnectionGenerationConflictException>(
            () => service.DisableAsync("tenant", created.ConnectionId, 1, Actor, default));
    }

    [Fact]
    public async Task Updating_a_disabled_connection_does_not_reenable_it()
    {
        var repository = new ConnectionRepository();
        var service = CreateService(
            repository,
            new RecordingProtector(),
            new FakeRuntimeFactory(repository));
        var created = await service.CreateAsync("tenant", Request(), Actor, default);
        var disabled = await service.DisableAsync(
            "tenant", created.ConnectionId, created.Generation, Actor, default);

        var updated = await service.UpdateAsync(
            "tenant",
            disabled.ConnectionId,
            disabled.Generation,
            Request(secret: "rotated-secret"),
            Actor,
            default);

        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task Delete_removes_an_unreferenced_connection_without_crossing_tenants()
    {
        var repository = new ConnectionRepository();
        var service = CreateService(repository, new RecordingProtector(), new FakeRuntimeFactory(repository));
        var created = await service.CreateAsync("tenant-a", Request(), Actor, default);

        var result = await service.DeleteAsync("tenant-a", created.ConnectionId, 1, Actor, default);

        Assert.Equal(ConnectionDeleteOutcome.Deleted, result.Outcome);
        Assert.Null(result.Definition);
        await Assert.ThrowsAsync<ConnectionNotFoundException>(
            () => service.GetAsync("tenant-a", created.ConnectionId, default));
        await Assert.ThrowsAsync<ConnectionNotFoundException>(
            () => service.DeleteAsync("tenant-b", created.ConnectionId, 1, Actor, default));
    }

    [Fact]
    public async Task Strict_request_validation_rejects_invalid_identity_and_configuration()
    {
        var repository = new ConnectionRepository();
        var service = CreateService(repository, new RecordingProtector(), new FakeRuntimeFactory(repository));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(" ", Request(), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            "tenant", Request(connectionId: "bad/id"), Actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            "tenant",
            Request(
                publicConfiguration: new Dictionary<string, JsonElement> { ["Token"] = Json("public") },
                secretConfiguration: new Dictionary<string, string> { ["token"] = "secret" }),
            Actor,
            default));
    }

    [Fact]
    public async Task Runtime_factory_decrypts_per_generation_without_caching_and_disposes_each_adapter()
    {
        var repository = new ConnectionRepository();
        var protector = new RecordingProtector();
        var seedingService = CreateService(
            repository,
            protector,
            new FakeRuntimeFactory(repository));
        var first = await seedingService.CreateAsync("tenant", Request(), Actor, default);
        var adapterFactory = new RecordingDurableAdapterFactory();
        var runtime = new ConnectionRuntimeFactory(repository, protector, adapterFactory);

        var firstLease = await runtime.AcquireAsync(
            "tenant", first.ConnectionId, first.Generation, default);
        var firstAdapter = Assert.IsType<DisposableAdapter>(firstLease.Adapter);
        await firstLease.DisposeAsync();
        var second = await seedingService.UpdateAsync(
            "tenant",
            first.ConnectionId,
            first.Generation,
            Request(secret: "rotated-secret"),
            Actor,
            default);
        await Assert.ThrowsAsync<StaleConnectionGenerationException>(
            () => runtime.AcquireAsync(
                "tenant", first.ConnectionId, first.Generation, default));
        await using var secondLease = await runtime.AcquireAsync(
            "tenant", second.ConnectionId, second.Generation, default);
        var secondAdapter = Assert.IsType<DisposableAdapter>(secondLease.Adapter);

        Assert.True(firstAdapter.Disposed);
        Assert.NotSame(firstAdapter, secondAdapter);
        Assert.Equal(2, adapterFactory.Adapters.Count);
        Assert.Equal("super-secret", adapterFactory.SecretSnapshots[0]["HaloClientSecret"]);
        Assert.Equal("rotated-secret", adapterFactory.SecretSnapshots[1]["HaloClientSecret"]);
        await Assert.ThrowsAsync<ConnectionNotFoundException>(
            () => runtime.AcquireAsync(
                "other", second.ConnectionId, second.Generation, default));
    }

    [Fact]
    public async Task Runtime_factory_disposes_adapter_when_generation_rotates_during_creation()
    {
        var repository = new ConnectionRepository();
        var protector = new RecordingProtector();
        var seedingService = CreateService(
            repository,
            protector,
            new FakeRuntimeFactory(repository));
        var first = await seedingService.CreateAsync("tenant", Request(), Actor, default);
        var adapterFactory = new RecordingDurableAdapterFactory
        {
            AfterCreate = async () =>
            {
                var current = (await repository.GetAsync(
                    "tenant", first.ConnectionId, default))!;
                var rotated = current.NextGeneration(
                    current.DisplayName,
                    true,
                    current.PublicConfiguration,
                    current.SecretCiphertext,
                    Actor,
                    Instant.AddMinutes(1));
                Assert.True(await repository.TryReplaceAsync(
                    "tenant",
                    current.ConnectionId,
                    current.Generation,
                    rotated,
                    default));
            }
        };
        var runtime = new ConnectionRuntimeFactory(repository, protector, adapterFactory);

        await Assert.ThrowsAsync<StaleConnectionGenerationException>(
            () => runtime.AcquireAsync(
                "tenant", first.ConnectionId, first.Generation, default));

        Assert.Single(adapterFactory.Adapters);
        Assert.True(adapterFactory.Adapters[0].Disposed);
    }

    [Fact]
    public async Task Durable_adapter_creation_uses_only_the_persisted_definition()
    {
        var factory = new ServerManagedEntityAdapterFactory(
            new Dictionary<string, string?>
            {
                ["NETSUITE_ACCOUNT_ID"] = "environment-account",
                ["NETSUITE_CONSUMER_KEY"] = "environment-key",
                ["NETSUITE_CONSUMER_SECRET"] = "environment-secret",
                ["NETSUITE_TOKEN_ID"] = "environment-token",
                ["NETSUITE_TOKEN_SECRET"] = "environment-token-secret"
            });
        var exported = factory.GetConnectionConfiguration(
            "NetSuite",
            profileSettings: null);
        Assert.Equal(
            "environment-account",
            exported.PublicConfiguration["NetSuiteAccountId"].GetString());
        Assert.False(exported.PublicConfiguration.ContainsKey("NetSuiteConsumerSecret"));
        Assert.Equal(
            "environment-secret",
            exported.SecretConfiguration["NetSuiteConsumerSecret"]);
        var publicConfiguration = new Dictionary<string, JsonElement>
        {
            ["NetSuiteAccountId"] = Json("stored-account"),
            ["NetSuiteConsumerKey"] = Json("stored-key"),
            ["NetSuiteTokenId"] = Json("stored-token")
        };
        var secretConfiguration = new Dictionary<string, string>
        {
            ["NetSuiteConsumerSecret"] = "stored-secret",
            ["NetSuiteTokenSecret"] = "stored-token-secret"
        };

        var adapter = await factory.CreateDurableAsync(
            "NetSuite",
            publicConfiguration,
            secretConfiguration,
            default);
        Assert.Equal("NetSuite", adapter.Vendor);

        secretConfiguration.Remove("NetSuiteConsumerSecret");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateDurableAsync(
                "NetSuite",
                publicConfiguration,
                secretConfiguration,
                default));
    }

    [Fact]
    public async Task Production_planner_acquires_current_durable_generations_and_disposes_leases()
    {
        var repository = new ConnectionRepository();
        var protector = new RecordingProtector();
        var adapterFactory = new RecordingDurableAdapterFactory();
        var runtime = new ConnectionRuntimeFactory(repository, protector, adapterFactory);
        var definitions = CreateService(repository, protector, runtime);
        await definitions.CreateAsync(
            "tenant",
            Request(vendor: "NetSuite", connectionId: "netsuite"),
            Actor,
            default);
        await definitions.CreateAsync(
            "tenant",
            Request(connectionId: "halo"),
            Actor,
            default);
        var planner = new EntitySyncPlanner(
            runtime,
            new InMemoryEntitySyncPlanRepository(),
            new InMemoryEntityExclusionRepository(),
            new WeightedEntityMatcher(),
            new DefaultEntityMapper(),
            new InMemoryEntitySyncChangeStateRepository());

        var plan = await planner.CreateAsync(
            new CreateEntitySyncPlanRequest
            {
                TenantId = "tenant",
                SourceVendor = "NetSuite",
                SourceConnectionId = "netsuite",
                TargetVendor = "HaloPSA",
                TargetConnectionId = "halo"
            },
            default);

        Assert.Equal(1, plan.Execution.SourceConnectionGeneration);
        Assert.Equal(1, plan.Execution.TargetConnectionGeneration);
        Assert.Equal(2, adapterFactory.Adapters.Count);
        Assert.All(adapterFactory.Adapters, adapter => Assert.True(adapter.Disposed));
    }

    [Fact]
    public void Host_mode_registration_keeps_in_memory_connections_only_for_local_stdio()
    {
        var keyPath = Directory.CreateTempSubdirectory("entitysync-task4-keys-").FullName;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    keyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var previous = Environment.GetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH");
            try
            {
                Environment.SetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyPath);
                using var local = BuildProvider(EntitySyncHostMode.LocalStdio);
                using var http = BuildProvider(EntitySyncHostMode.Http);
                using var scheduler = BuildProvider(EntitySyncHostMode.Scheduler);

                Assert.IsType<InMemoryEntityConnectionRepository>(
                    local.GetRequiredService<IEntityConnectionRepository>());
                Assert.Same(
                    local.GetRequiredService<IEntityConnectionRepository>(),
                    local.GetRequiredService<IConnectionRuntimeFactory>());
                Assert.Null(http.GetService<IEntityConnectionRepository>());
                Assert.Null(scheduler.GetService<IEntityConnectionRepository>());
                Assert.IsType<ConnectionRuntimeFactory>(http.GetRequiredService<IConnectionRuntimeFactory>());
                Assert.IsType<ConnectionRuntimeFactory>(scheduler.GetRequiredService<IConnectionRuntimeFactory>());
                Assert.NotNull(http.GetRequiredService<EntitySyncPlanner>());
                Assert.NotNull(http.GetRequiredService<EntitySyncService>());
                Assert.NotNull(http.GetRequiredService<EntityExclusionService>());
                Assert.NotNull(scheduler.GetRequiredService<EntitySyncPlanner>());
                Assert.NotNull(scheduler.GetRequiredService<EntitySyncService>());
                Assert.NotNull(scheduler.GetRequiredService<EntityExclusionService>());
            }
            finally
            {
                Environment.SetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH", previous);
            }
        }
        finally
        {
            Directory.Delete(keyPath, true);
        }
    }

    [Fact]
    public async Task Scheduler_host_constructs_with_durable_runtime_and_no_in_memory_repository()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        var hostType = typeof(EntitySyncSchedulerWorker).Assembly.GetType(
            "LISSTech.EntitySync.Scheduler.EntitySyncSchedulerHost");
        Assert.NotNull(hostType);
        var build = hostType.GetMethod(
            "Build",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string[])]);
        Assert.NotNull(build);
        await using var app = Assert.IsType<WebApplication>(
            build.Invoke(null, [Array.Empty<string>()]));

        Assert.Null(app.Services.GetService<IEntityConnectionRepository>());
        Assert.IsType<ConnectionRuntimeFactory>(
            app.Services.GetRequiredService<IConnectionRuntimeFactory>());
        Assert.IsType<EntitySyncScheduledRun>(
            app.Services.GetRequiredService<IEntitySyncScheduledRun>());
    }

    private static ServiceProvider BuildProvider(EntitySyncHostMode mode)
    {
        var services = new ServiceCollection();
        services.AddEntitySyncPlatform(
            "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            mode);
        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static ConnectionDefinitionService CreateService(
        ConnectionRepository repository,
        RecordingProtector protector,
        IConnectionRuntimeFactory runtime) =>
        new(repository, protector, runtime, new FixedTimeProvider(Instant));

    private static ConnectionDefinitionRequest Request(
        string vendor = "HaloPSA",
        string connectionId = "halo-main",
        string secret = "super-secret",
        IReadOnlyDictionary<string, JsonElement>? publicConfiguration = null,
        IReadOnlyDictionary<string, string>? secretConfiguration = null) =>
        new(
            vendor,
            connectionId,
            "Primary Halo",
            publicConfiguration ?? new Dictionary<string, JsonElement>
            {
                ["HaloBaseUrl"] = Json("https://halo.example/")
            },
            secretConfiguration ?? new Dictionary<string, string>
            {
                ["HaloClientId"] = "client-id",
                ["HaloClientSecret"] = secret
            });

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static async Task<(EntitySyncConnectionDefinition? Definition, Exception? Error)> Capture(
        Func<Task<EntitySyncConnectionDefinition>> operation)
    {
        try
        {
            return (await operation(), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProtector : IEntitySyncDataProtector
    {
        public string? LastProtectedPlaintext { get; private set; }
        public int UnprotectCalls { get; private set; }

        public string Protect(EntitySyncDataProtectionPurpose purpose, string plaintext)
        {
            Assert.Equal(EntitySyncDataProtectionPurpose.ConnectionSecret, purpose);
            LastProtectedPlaintext = plaintext;
            return $"ciphertext:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
        }

        public string Unprotect(EntitySyncDataProtectionPurpose purpose, string ciphertext)
        {
            UnprotectCalls++;
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext["ciphertext:".Length..]));
        }
    }

    private sealed class ConnectionRepository : IConnectionDefinitionRepository
    {
        private readonly object gate = new();
        private readonly Dictionary<string, EntitySyncConnectionDefinition> values = new(StringComparer.OrdinalIgnoreCase);
        public bool Referenced { get; set; }

        public Task InsertAsync(string tenantId, EntitySyncConnectionDefinition definition, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!values.TryAdd(Key(tenantId, definition.ConnectionId), definition))
                    throw new InvalidOperationException("Duplicate connection.");
            }
            return Task.CompletedTask;
        }

        public Task<EntitySyncConnectionDefinition?> GetAsync(string tenantId, string connectionId, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return Task.FromResult(values.GetValueOrDefault(Key(tenantId, connectionId)));
            }
        }

        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
            string tenantId,
            string? vendor,
            bool? enabled,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                IReadOnlyList<EntitySyncConnectionDefinition> result = values.Values
                    .Where(value => value.TenantId == tenantId)
                    .Where(value => vendor is null || value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                    .Where(value => enabled is null || value.Enabled == enabled)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<bool> TryReplaceAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            EntitySyncConnectionDefinition nextGeneration,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var key = Key(tenantId, connectionId);
                if (!values.TryGetValue(key, out var current) || current.Generation != expectedGeneration)
                    return Task.FromResult(false);
                values[key] = nextGeneration;
                return Task.FromResult(true);
            }
        }

        public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var key = Key(tenantId, connectionId);
                if (!values.TryGetValue(key, out var current))
                    return Task.FromResult(ConnectionDefinitionDeleteResult.NotFound);
                if (current.Generation != expectedGeneration)
                    return Task.FromResult(ConnectionDefinitionDeleteResult.GenerationMismatch);
                if (Referenced)
                    return Task.FromResult(ConnectionDefinitionDeleteResult.Referenced);
                values.Remove(key);
                return Task.FromResult(ConnectionDefinitionDeleteResult.Deleted);
            }
        }

        private static string Key(string tenantId, string connectionId) => $"{tenantId}\n{connectionId}";
    }

    private sealed class RecordingDurableAdapterFactory
        : IServerManagedEntityAdapterFactory
    {
        public List<DisposableAdapter> Adapters { get; } = [];
        public List<IReadOnlyDictionary<string, string>> SecretSnapshots { get; } = [];
        public Func<Task>? AfterCreate { get; init; }

        public Task<IEntityAdapter> CreateAsync(
            string vendor,
            IReadOnlyDictionary<string, string>? profileSettings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<IEntityAdapter> CreateDurableAsync(
            string vendor,
            IReadOnlyDictionary<string, JsonElement> publicConfiguration,
            IReadOnlyDictionary<string, string> secretConfiguration,
            CancellationToken cancellationToken)
        {
            SecretSnapshots.Add(
                new Dictionary<string, string>(
                    secretConfiguration,
                    StringComparer.OrdinalIgnoreCase));
            var adapter = new DisposableAdapter(vendor);
            Adapters.Add(adapter);
            if (AfterCreate is not null) await AfterCreate();
            return adapter;
        }

        public ServerManagedConnectionConfiguration GetConnectionConfiguration(
            string vendor,
            IReadOnlyDictionary<string, string>? profileSettings) =>
            new(
                new Dictionary<string, JsonElement>(),
                new Dictionary<string, string>());

        public void ValidateNetSuiteHaloFixedRouteConfiguration()
        {
        }

        public string GetNetSuiteHaloChangeStateScope() => "unused";
    }

    private sealed class FakeRuntimeFactory(IConnectionDefinitionRepository repository) : IConnectionRuntimeFactory
    {
        public int AcquireCalls { get; private set; }
        public long LastGeneration { get; private set; }
        public DisposableAdapter? LastAdapter { get; private set; }

        public async Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            AcquireCalls++;
            LastGeneration = expectedGeneration;
            var definition = await repository.GetAsync(tenantId, connectionId, cancellationToken)
                ?? throw new ConnectionNotFoundException(tenantId, connectionId);
            if (definition.Generation != expectedGeneration)
                throw new StaleConnectionGenerationException(connectionId, expectedGeneration, definition.Generation);
            LastAdapter = new DisposableAdapter(definition.Vendor);
            return new RuntimeLease(definition, LastAdapter);
        }

        public async Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            var matches = await repository.ListAsync(
                tenantId,
                vendor,
                enabled: true,
                cancellationToken);
            var definition = connectionId is null
                ? Assert.Single(matches)
                : matches.Single(value => value.ConnectionId == connectionId);
            return await AcquireAsync(
                tenantId,
                definition.ConnectionId,
                definition.Generation,
                cancellationToken);
        }
    }

    private sealed class RuntimeLease(
        EntitySyncConnectionDefinition definition,
        DisposableAdapter adapter) : IConnectionRuntimeLease
    {
        public EntitySyncConnectionDefinition Definition { get; } = definition;
        public IEntityAdapter Adapter { get; } = adapter;

        public ValueTask DisposeAsync()
        {
            adapter.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableAdapter(string vendor) : IEntityAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalEntity>>([]);
        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }
}

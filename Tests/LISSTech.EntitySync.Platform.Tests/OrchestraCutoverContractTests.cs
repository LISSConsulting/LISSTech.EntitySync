using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Scheduler;
using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[Collection(nameof(ControlPlaneEndToEndCollection))]
public sealed class OrchestraCutoverContractTests
{
    private const string Tenant = "orchestra-cutover-tenant";
    private static readonly EntitySyncActor Actor = new("orchestra-cutover-operator");

    [Fact]
    public async Task Production_rehearsal_preserves_control_and_canonical_work_across_restart()
    {
        var ncentralCapabilities = EntityAdapterCapabilities.ForVendor("NCentral");
        Assert.True(ncentralCapabilities.TryGetEntityType("Customer", out var customer));
        Assert.True(customer.IsScheduledSafe("Phone"));
        Assert.False(customer.IsScheduledSafe("Name"));

        // The production repository/worker path proves connections, policy, plan inspection,
        // exact-digest approval, dry-run, one-time apply, encrypted audit, and one vendor write.
        await new ControlPlaneEndToEndTests()
            .Restart_preserves_approved_plan_operation_and_correlated_audit_with_one_write();

        await using var database = await PostgresContainer.StartAsync();
        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            await EntitySyncDatabaseMigrator.ApplyAsync(dataSource);

        Guid policyId;
        var scheduleId = Guid.Parse("72222222-2222-4222-8222-222222222222");
        var entityId = Guid.Parse("73333333-3333-4333-8333-333333333333");
        const string eventId = "74444444-4444-4444-8444-444444444444";
        var occurredAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var runtime = new StatefulRuntimeFactory();

        using (var first = new ControlApiFactory(
                   database.ConnectionString,
                   preserveProductionQueries: true,
                   connectionRuntime: runtime))
        {
            using var scope = first.Services.CreateScope();
            var sourceConfiguration = new ServerManagedEntityAdapterFactory(
                new Dictionary<string, string?>
                {
                    ["ORCHESTRA_BASE_URL"] =
                        "https://directory.example.test/api/v1/internal/client-directory/",
                    ["ORCHESTRA_AUTHORITY"] = "https://login.example.test/tenant",
                    ["ORCHESTRA_TENANT_ID"] = "tenant",
                    ["ORCHESTRA_CLIENT_ID"] = "cutover-source",
                    ["ORCHESTRA_RESOURCE"] = "api://orchestra-directory",
                    ["ORCHESTRA_CLIENT_SECRET"] = Guid.NewGuid().ToString("N")
                },
                "Testing").GetConnectionConfiguration(EntitySyncVendors.OrchestraMSP, null);
            var targetConfiguration = new ServerManagedEntityAdapterFactory(
                new Dictionary<string, string?>
                {
                    ["NCENTRAL_BASE_URL"] = "https://ncentral.example.test/",
                    ["NCENTRAL_USER_API_TOKEN"] = Guid.NewGuid().ToString("N"),
                    ["NCENTRAL_SERVICE_ORG_ID"] = "100"
                },
                "Testing").GetConnectionConfiguration("NCentral", null);
            var connections = scope.ServiceProvider
                .GetRequiredService<ConnectionDefinitionService>();
            var sourceDefinition = await connections.CreateAsync(
                Tenant,
                new ConnectionDefinitionRequest(
                    EntitySyncVendors.OrchestraMSP,
                    "orchestra-source",
                    "Orchestra source",
                    sourceConfiguration.PublicConfiguration,
                    sourceConfiguration.SecretConfiguration),
                Actor,
                default);
            runtime.Register(sourceDefinition);
            var targetDefinition = await connections.CreateAsync(
                Tenant,
                new ConnectionDefinitionRequest(
                    "NCentral",
                    "ncentral-target",
                    "N-central target",
                    targetConfiguration.PublicConfiguration,
                    targetConfiguration.SecretConfiguration),
                Actor,
                default);
            runtime.Register(targetDefinition);
            var policy = await scope.ServiceProvider.GetRequiredService<SyncPolicyService>()
                .CreateAsync(
                    Tenant,
                    new SyncPolicyRequest(
                        "Canonical cutover route",
                        "orchestra-to-ncentral",
                        new EntitySyncPolicyDefinition(
                            EntitySyncVendors.OrchestraMSP,
                            "orchestra-source",
                            "Client",
                            "NCentral",
                            "ncentral-target",
                            "Customer",
                            false,
                            true,
                            90,
                            60,
                            "Id",
                            "NCentralCustomerId",
                            EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
                            new HashSet<string>(["Phone"], StringComparer.OrdinalIgnoreCase),
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            true),
                        true),
                    Actor,
                    default);
            policyId = policy.PolicyId;

            var schedule = await scope.ServiceProvider.GetRequiredService<SyncScheduleService>()
                .CreateAsync(
                    Tenant,
                    scheduleId,
                    new SyncScheduleRequest(
                        "Stable cutover schedule",
                        policyId,
                        policy.Version,
                        "0 3 * * *",
                        "UTC",
                        true),
                    Actor,
                    default);
            Assert.Equal(1, schedule.Version);
            var dataSource = first.Services.GetRequiredService<NpgsqlDataSource>();
            var worker = new EntitySyncControlWorker(
                new PostgresSyncWorkQueue(dataSource),
                new PostgresRouteLock(dataSource),
                scope.ServiceProvider.GetRequiredService<ISyncPolicyRepository>(),
                scope.ServiceProvider.GetRequiredService<IConnectionDefinitionRepository>(),
                runtime,
                scope.ServiceProvider.GetRequiredService<DurablePlanService>(),
                scope.ServiceProvider.GetRequiredService<SyncOperationService>(),
                scope.ServiceProvider.GetRequiredService<EntitySyncOperationWorker>(),
                TimeProvider.System,
                new EntitySyncControlOptions(
                    [Tenant],
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(1)));

            await ForceScheduleDueAsync(dataSource, scheduleId);
            Assert.Equal(1, await worker.TickAsync(default));
            await DrainWorkerAsync(worker);
            Assert.True(
                runtime.Target.WriteCount == 1,
                await ReadPipelineStateAsync(dataSource));
            Assert.Equal("+1-512-555-0199", runtime.Target.Phone);
            Assert.True(runtime.Target.LookupCount > 0);
            Assert.True(runtime.Target.LastLookupMatched);
            var firstOutcome = await ReadOperationOutcomesAsync(dataSource);
            Assert.DoesNotContain("request=NULL", firstOutcome, StringComparison.Ordinal);
            Assert.DoesNotContain("target=NULL", firstOutcome, StringComparison.Ordinal);
            Assert.DoesNotContain("source=NULL", firstOutcome, StringComparison.Ordinal);
            Assert.Contains("Update:Succeeded", firstOutcome, StringComparison.Ordinal);

            await ForceScheduleDueAsync(dataSource, scheduleId);
            Assert.Equal(1, await worker.TickAsync(default));
            await DrainWorkerAsync(worker);
            Assert.Equal(1, runtime.Target.WriteCount);

            using var delegated = first.CreateClient();
            AddClaims(delegated, $"tid={Tenant};oid=operator;scp=EntitySync.Operate");
            using var delegatedDenied = await SendCanonicalAsync(
                delegated, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Forbidden, delegatedDenied.StatusCode);

            using var wrongWorkload = first.CreateClient();
            AddClaims(
                wrongWorkload,
                $"tid={Tenant};azp=not-orchestra;roles=EntitySync.Operate.Application");
            using var wrongWorkloadDenied = await SendCanonicalAsync(
                wrongWorkload, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Forbidden, wrongWorkloadDenied.StatusCode);

            using var workload = first.CreateClient();
            AddClaims(workload, $"tid={Tenant};azp=om-workload;roles=EntitySync.Operate.Application");
            using var accepted = await SendCanonicalAsync(workload, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await DrainWorkerAsync(worker);
            Assert.Equal(1, runtime.Target.WriteCount);
            Assert.Equal(0, runtime.Source.EchoWriteCount);
        }

        using (var restarted = new ControlApiFactory(
                   database.ConnectionString,
                   preserveProductionQueries: true,
                   connectionRuntime: runtime))
        {
            using var reader = restarted.CreateClient();
            AddClaims(reader, $"tid={Tenant};oid=viewer;scp=EntitySync.Read");
            using var schedules = await reader.GetAsync("/api/v1/control/schedules");
            schedules.EnsureSuccessStatusCode();
            var schedulesJson = await schedules.Content.ReadAsStringAsync();
            Assert.Contains(scheduleId.ToString("D"), schedulesJson, StringComparison.OrdinalIgnoreCase);

            using var workload = restarted.CreateClient();
            AddClaims(workload, $"tid={Tenant};azp=om-workload;roles=EntitySync.Operate.Application");
            using var replay = await SendCanonicalAsync(workload, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var counts = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM entitysync.canonical_change_events
                 WHERE tenant_id = @tenant AND om_event_id = @event),
                (SELECT count(*) FROM entitysync.sync_control_work
                 WHERE tenant_id = @tenant AND work_kind = 'CanonicalChange'),
                (SELECT count(*) FROM entitysync.sync_schedules
                 WHERE tenant_id = @tenant AND schedule_id = @schedule),
                (SELECT count(*) FROM entitysync.sync_control_work
                 WHERE tenant_id = @tenant AND work_kind = 'Schedule'),
                (SELECT count(*) FROM entitysync.sync_control_work
                 WHERE tenant_id = @tenant AND state NOT IN ('Completed', 'Held')),
                (SELECT count(*) FROM entitysync.sync_operations
                 WHERE tenant_id = @tenant
                   AND status NOT IN ('Succeeded', 'Partial', 'Failed', 'Cancelled')),
                (SELECT count(*) FROM entitysync.sync_operation_items
                 WHERE tenant_id = @tenant AND outcome = 'Succeeded')
            """, connection);
        counts.Parameters.AddWithValue("tenant", Tenant);
        counts.Parameters.AddWithValue("event", eventId);
        counts.Parameters.AddWithValue("schedule", scheduleId);
        await using var rows = await counts.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(1, rows.GetInt64(0));
        Assert.Equal(1, rows.GetInt64(1));
        Assert.Equal(1, rows.GetInt64(2));
        Assert.Equal(2, rows.GetInt64(3));
        Assert.Equal(0, rows.GetInt64(4));
        Assert.Equal(0, rows.GetInt64(5));
        Assert.Equal(1, rows.GetInt64(6));
    }

    private static async Task ForceScheduleDueAsync(
        NpgsqlDataSource dataSource,
        Guid scheduleId)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE entitysync.sync_schedules
            SET next_run_at = clock_timestamp() - interval '1 second',
                runtime_revision = runtime_revision + 1
            WHERE tenant_id = @tenant
              AND schedule_id = @schedule
              AND version = 1
            """);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("schedule", scheduleId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task DrainWorkerAsync(EntitySyncControlWorker worker)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!await worker.ExecuteOneAsync(default)) return;
        }
        throw new InvalidOperationException("Control worker did not become idle.");
    }

    private static async Task<string> ReadPipelineStateAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
              (SELECT COALESCE(string_agg(
                 work_kind || ':' || state || ':' || COALESCE(hold_reason, '') || ':'
                 || COALESCE(plan_id::text, '') || ':' || COALESCE(operation_id::text, ''),
                 '|' ORDER BY created_at), 'no-work')
               FROM entitysync.sync_control_work WHERE tenant_id = @tenant)
              || ';plans=' ||
              (SELECT COALESCE(string_agg(action || ':' || match_type, '|'), 'no-plan-items')
               FROM entitysync.sync_plan_items WHERE tenant_id = @tenant)
              || ';operations=' ||
              (SELECT COALESCE(string_agg(action || ':' || outcome, '|'), 'no-operation-items')
               FROM entitysync.sync_operation_items WHERE tenant_id = @tenant)
            """);
        command.Parameters.AddWithValue("tenant", Tenant);
        return (string)(await command.ExecuteScalarAsync() ?? "no-result");
    }

    private static async Task<string> ReadOperationOutcomesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT COALESCE(string_agg(
                action || ':' || outcome || ':' || COALESCE(error_code, '') || ':'
                || COALESCE(safe_write_code, '') || ':request='
                || COALESCE(vendor_request_id, 'NULL') || ':target='
                || COALESCE(vendor_target_entity_id, 'NULL') || ':source='
                || COALESCE(source_entity_id, 'NULL'),
                '|' ORDER BY item_index), 'no-operation-items')
            FROM entitysync.sync_operation_items
            WHERE tenant_id = @tenant
            """);
        command.Parameters.AddWithValue("tenant", Tenant);
        return (string)(await command.ExecuteScalarAsync() ?? "no-result");
    }

    private static void AddClaims(HttpClient client, string claims)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        client.DefaultRequestHeaders.Add("X-Test-Claims", claims);
    }

    private static async Task<HttpResponseMessage> SendCanonicalAsync(
        HttpClient client,
        string eventId,
        Guid entityId,
        DateTimeOffset occurredAt)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/control/canonical-changes")
        {
            Content = new StringContent(
                $$"""
                {"outboxEventId":"{{eventId}}","canonicalEntityType":"Client",
                 "canonicalEntityId":"{{entityId:D}}","canonicalVersion":7,
                 "changedFields":["Phone"],
                 "payloadSha256":"{{new string('a', 64)}}",
                 "occurredAt":"{{occurredAt:O}}"}
                """,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Idempotency-Key", $"canonical-{eventId}");
        request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class StatefulRuntimeFactory : IConnectionRuntimeFactory
    {
        private readonly Dictionary<string, EntitySyncConnectionDefinition> definitions =
            new(StringComparer.Ordinal);

        internal StatefulSourceAdapter Source { get; } = new();
        internal StatefulTargetAdapter Target { get; } = new();

        internal void Register(EntitySyncConnectionDefinition definition) =>
            definitions[definition.ConnectionId] = definition;

        public Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            var definition = definitions[connectionId];
            Assert.Equal(tenantId, definition.TenantId);
            Assert.Equal(expectedGeneration, definition.Generation);
            IEntityAdapter adapter = connectionId == "orchestra-source" ? Source : Target;
            return Task.FromResult<IConnectionRuntimeLease>(new RuntimeLease(definition, adapter));
        }

        public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            var definition = definitions.Values.Single(value =>
                value.TenantId == tenantId
                && value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase)
                && (connectionId is null || value.ConnectionId == connectionId));
            IEntityAdapter adapter = definition.ConnectionId == "orchestra-source"
                ? Source
                : Target;
            return Task.FromResult<IConnectionRuntimeLease>(new RuntimeLease(definition, adapter));
        }

        public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(definitions.Values.Single(value =>
                value.TenantId == tenantId
                && value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase)
                && (connectionId is null || value.ConnectionId == connectionId)));

        private sealed class RuntimeLease(
            EntitySyncConnectionDefinition definition,
            IEntityAdapter adapter) : IConnectionRuntimeLease
        {
            public EntitySyncConnectionDefinition Definition => definition;
            public IEntityAdapter Adapter => adapter;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StatefulSourceAdapter : IEntityAdapter, ICanonicalEntityVersionAdapter
    {
        private readonly ExternalEntity source = new()
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Client",
            Id = "73333333-3333-4333-8333-333333333333",
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = "73333333-3333-4333-8333-333333333333"
            },
            Version = 7,
            Name = "Acme Source",
            Phone = "+1-512-555-0199",
            IsActive = true
        };

        internal int EchoWriteCount { get; private set; }
        public string Vendor => EntitySyncVendors.OrchestraMSP;
        public IReadOnlyList<string> LookupTypes => [];

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalEntity>>([source]);

        public Task<CanonicalEntityVersion?> ReadCanonicalAsync(
            string entityType,
            Guid canonicalEntityId,
            long assertedVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<CanonicalEntityVersion?>(
                new(canonicalEntityId, assertedVersion, source));

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken)
        {
            EchoWriteCount++;
            return Task.FromResult(Success(request, "Create"));
        }

        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken)
        {
            EchoWriteCount++;
            return Task.FromResult(Success(request, "Update"));
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        private EntityWriteResult Success(EntityWriteRequest request, string action) =>
            new()
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = request.Id ?? source.Id,
                Action = action,
                Success = true,
                VendorRequestId = request.VendorRequestId,
                SafeCode = "OK"
            };
    }

    private sealed class StatefulTargetAdapter : IEntityAdapter
    {
        private readonly ExternalEntity target = new()
        {
            Vendor = "NCentral",
            EntityType = "Customer",
            Id = "501",
            Name = "Acme Source",
            Phone = "+1-512-555-0100",
            IsActive = true,
            ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NCentralCustomerId"] = "501"
            },
            CustomFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["NCentralCustomerId"] = "73333333-3333-4333-8333-333333333333"
            }
        };
        private string? lastVendorRequestId;

        internal int WriteCount { get; private set; }
        internal string? Phone => target.Phone;
        internal int LookupCount { get; private set; }
        internal bool LastLookupMatched { get; private set; }
        public string Vendor => "NCentral";
        public IReadOnlyList<string> LookupTypes => [];

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalEntity>>([target]);

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            WriteAsync(request, "Create");

        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            WriteAsync(request, "Update");

        public Task<EntityWriteResult> LookupWriteByRequestIdAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            LastLookupMatched = request.VendorRequestId == lastVendorRequestId;
            return Task.FromResult(new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = target.Id,
                VendorRequestId = request.VendorRequestId,
                RequestLookupOutcome = LastLookupMatched
                    ? VendorRequestLookupOutcome.Applied
                    : VendorRequestLookupOutcome.NotApplied,
                Action = "Lookup",
                Success = true,
                SafeCode = "REQUEST_ID_LOOKUP_COMPLETE"
            });
        }

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        private Task<EntityWriteResult> WriteAsync(
            EntityWriteRequest request,
            string action)
        {
            WriteCount++;
            lastVendorRequestId = request.VendorRequestId;
            if (request.Fields.TryGetValue("Phone", out var phone))
                target.Phone = Convert.ToString(
                    phone,
                    System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(new EntityWriteResult
            {
                Vendor = Vendor,
                EntityType = request.EntityType,
                Id = request.Id ?? target.Id,
                Action = action,
                Success = true,
                VendorRequestId = request.VendorRequestId,
                SafeCode = "OK"
            });
        }
    }

    private sealed class PostgresContainer(string name, string connectionString) : IAsyncDisposable
    {
        internal string ConnectionString { get; } = connectionString;

        internal static async Task<PostgresContainer> StartAsync()
        {
            var name = $"entitysync-cutover-{Guid.NewGuid():N}";
            var port = FreePort();
            await RunAsync(
                "docker",
                "run", "--detach", "--rm", "--name", name,
                "--env", "POSTGRES_PASSWORD=entitysync-cutover",
                "--publish", $"127.0.0.1:{port}:5432",
                "postgres:18-trixie@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636");
            var connectionString =
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;" +
                "Password=entitysync-cutover;Pooling=false;Timeout=2";
            Exception? last = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync();
                    return new PostgresContainer(name, connectionString);
                }
                catch (Exception exception)
                {
                    last = exception;
                    await Task.Delay(500);
                }
            }
            await RunAsync("docker", "rm", "--force", name);
            throw new InvalidOperationException(
                "PostgreSQL cutover test container did not become ready.", last);
        }

        public async ValueTask DisposeAsync()
        {
            try { await RunAsync("docker", "rm", "--force", name); }
            catch { }
        }
    }

    private static async Task RunAsync(string executable, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} exited {process.ExitCode}: {error}");
    }
}

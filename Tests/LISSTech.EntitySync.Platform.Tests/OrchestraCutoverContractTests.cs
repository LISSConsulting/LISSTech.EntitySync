using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
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

        using (var first = new ControlApiFactory(
                   database.ConnectionString,
                   preserveProductionQueries: true))
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
            await connections.CreateAsync(
                Tenant,
                new ConnectionDefinitionRequest(
                    EntitySyncVendors.OrchestraMSP,
                    "orchestra-source",
                    "Orchestra source",
                    sourceConfiguration.PublicConfiguration,
                    sourceConfiguration.SecretConfiguration),
                Actor,
                default);
            await connections.CreateAsync(
                Tenant,
                new ConnectionDefinitionRequest(
                    "NCentral",
                    "ncentral-target",
                    "N-central target",
                    targetConfiguration.PublicConfiguration,
                    targetConfiguration.SecretConfiguration),
                Actor,
                default);
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
                            null,
                            null,
                            EntitySyncUpdatePolicy.Standard,
                            new HashSet<string>(["Name"], StringComparer.OrdinalIgnoreCase),
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

            using var delegated = first.CreateClient();
            AddClaims(delegated, $"tid={Tenant};oid=operator;scp=EntitySync.Operate");
            using var delegatedDenied = await SendCanonicalAsync(
                delegated, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Forbidden, delegatedDenied.StatusCode);

            using var wrongWorkload = first.CreateClient();
            AddClaims(
                wrongWorkload,
                $"tid={Tenant};azp=not-orchestra;roles=EntitySync.Operate");
            using var wrongWorkloadDenied = await SendCanonicalAsync(
                wrongWorkload, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Forbidden, wrongWorkloadDenied.StatusCode);

            using var workload = first.CreateClient();
            AddClaims(workload, $"tid={Tenant};azp=om-workload;roles=EntitySync.Operate");
            using var accepted = await SendCanonicalAsync(workload, eventId, entityId, occurredAt);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using (var restarted = new ControlApiFactory(
                   database.ConnectionString,
                   preserveProductionQueries: true))
        {
            using var reader = restarted.CreateClient();
            AddClaims(reader, $"tid={Tenant};oid=viewer;scp=EntitySync.Read");
            using var schedules = await reader.GetAsync("/api/v1/control/schedules");
            schedules.EnsureSuccessStatusCode();
            var schedulesJson = await schedules.Content.ReadAsStringAsync();
            Assert.Contains(scheduleId.ToString("D"), schedulesJson, StringComparison.OrdinalIgnoreCase);

            using var workload = restarted.CreateClient();
            AddClaims(workload, $"tid={Tenant};azp=om-workload;roles=EntitySync.Operate");
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
                 WHERE tenant_id = @tenant AND schedule_id = @schedule)
            """, connection);
        counts.Parameters.AddWithValue("tenant", Tenant);
        counts.Parameters.AddWithValue("event", eventId);
        counts.Parameters.AddWithValue("schedule", scheduleId);
        await using var rows = await counts.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(1, rows.GetInt64(0));
        Assert.Equal(1, rows.GetInt64(1));
        Assert.Equal(1, rows.GetInt64(2));
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
                 "changedFields":["Name"],
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

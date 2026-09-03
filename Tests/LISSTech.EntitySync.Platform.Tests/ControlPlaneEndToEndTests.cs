using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]


namespace LISSTech.EntitySync.Platform.Tests;

[CollectionDefinition(nameof(ControlPlaneEndToEndCollection), DisableParallelization = true)]
public sealed class ControlPlaneEndToEndCollection;

[Collection(nameof(ControlPlaneEndToEndCollection))]
public sealed class ControlPlaneEndToEndTests
{
    private const string Tenant = "tenant-control-e2e";
    private static readonly EntitySyncActor Actor = new("control-e2e-operator");

    [Fact]
    public void Production_worker_contract_exposes_explicit_bounded_settings()
    {
        var settings = EntitySyncWorkerSettings.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ENTITYSYNC_WORKER_LEASE_SECONDS"] = "120",
                ["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"] = "10",
                ["ENTITYSYNC_WORKER_RETRY_SECONDS"] = "4"
            });

        Assert.Equal(TimeSpan.FromSeconds(120), settings.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), settings.RetryInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.MaximumHeartbeatAge);
        Assert.Contains(
            "ENTITYSYNC_CONFIG_WORKER_INTERVAL_INVALID",
            Assert.Throws<InvalidOperationException>(() =>
                EntitySyncWorkerSettings.FromEnvironment(
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["ENTITYSYNC_WORKER_LEASE_SECONDS"] = "120",
                        ["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"] = "0",
                        ["ENTITYSYNC_WORKER_RETRY_SECONDS"] = "4"
                    })).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_preserves_approved_plan_operation_and_correlated_audit_with_one_write()
    {
        await using var database = await PostgresContainer.StartAsync();
        await using var orchestra = await OrchestraContractServer.StartAsync();
        await using var ncentral = await NCentralContractServer.StartAsync();
        var keyPath = Path.Combine(Path.GetTempPath(), $"entitysync-e2e-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                keyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var previousKeyPath = Environment.GetEnvironmentVariable(
            "ENTITYSYNC_DATA_PROTECTION_KEY_PATH");
        Environment.SetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyPath);

        try
        {
            await using (var migrations = BuildProvider(database.ConnectionString))
            {
                await migrations.GetServices<IHostedService>()
                    .OfType<EntitySyncDatabaseMigrationHostedService>()
                    .Single()
                    .StartAsync(default);
            }

            Guid planId;
            Guid approvalId;
            Guid dryRunId;
            await using (var api = BuildProvider(database.ConnectionString))
            {
                var source = ConnectionConfiguration(
                    EntitySyncVendors.OrchestraMSP,
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["ORCHESTRA_BASE_URL"] = orchestra.DirectoryBaseUrl,
                        ["ORCHESTRA_AUTHORITY"] = orchestra.Authority,
                        ["ORCHESTRA_TENANT_ID"] = "tenant",
                        ["ORCHESTRA_CLIENT_ID"] = "source-client",
                        ["ORCHESTRA_RESOURCE"] = "api://orchestra-directory",
                        ["ORCHESTRA_CLIENT_SECRET"] = "source-secret",
                        ["ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"] = "true"
                    });
                var target = ConnectionConfiguration(
                    "NCentral",
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["NCENTRAL_BASE_URL"] = ncentral.BaseUrl,
                        ["NCENTRAL_USER_API_TOKEN"] = "ncentral-token",
                        ["NCENTRAL_SERVICE_ORG_ID"] = "100"
                    });
                await CreateConnectionAsync(
                    api, EntitySyncVendors.OrchestraMSP, "orchestra-source",
                    "Orchestra source", source);
                await CreateConnectionAsync(
                    api, "NCentral", "ncentral-target", "N-central target", target);

                using var scope = api.CreateScope();
                var policy = await scope.ServiceProvider
                    .GetRequiredService<SyncPolicyService>()
                    .CreateAsync(
                        Tenant,
                        new SyncPolicyRequest(
                            "Orchestra clients to N-central",
                            "orchestra-client-to-ncentral-customer",
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
                                new HashSet<string>(
                                    ["Name"],
                                    StringComparer.OrdinalIgnoreCase),
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                false),
                            true),
                        Actor,
                        default);
                var commands = scope.ServiceProvider
                    .GetRequiredService<IEntitySyncControlCommands>();
                var created = await commands.CreatePlanAsync(
                    new CreateDurablePlanRequest
                    {
                        TenantId = Tenant,
                        IdempotencyKey = "e2e-create-plan",
                        PolicyId = policy.PolicyId,
                        PlanLifetime = TimeSpan.FromHours(1)
                    },
                    Actor,
                    default);
                planId = created.Plan.PlanId;
                Assert.Equal(1, created.Plan.ItemCount);

                DurablePlanInspectionPage inspection = null!;
                for (var page = 1; page <= created.Result.PageCount(1); page++)
                {
                    inspection = await commands.InspectPlanAsync(
                        Tenant, planId, page, 1, Actor, default);
                }
                Assert.True(inspection.InspectionComplete);
                Assert.Equal(created.Plan.ItemCount, inspection.CoveredItemCount);

                var approval = await commands.ApprovePlanAsync(
                    Tenant,
                    planId,
                    created.Plan.PlanDigestSha256.Value,
                    "e2e-approve-plan",
                    Actor,
                    default);
                approvalId = approval.ApprovalId;
                var dryRun = await commands.QueueDryRunAsync(Tenant, planId, "e2e-dry-run", Guid.NewGuid(), Actor, default);
                dryRunId = dryRun.OperationId;
            }

            await using (var firstWorker = BuildProvider(database.ConnectionString))
            {
                var completed = await firstWorker
                    .GetRequiredService<EntitySyncOperationWorker>()
                    .ExecuteOneAsync(Tenant, "e2e-worker-before-restart", default);
                Assert.Equal(dryRunId, completed!.OperationId);
                Assert.Equal(EntitySyncOperationStatus.Succeeded, completed.Status);
                Assert.Equal(0, ncentral.WriteCount);
            }

            Guid applyRunId;
            await using (var restartedApi = BuildProvider(database.ConnectionString))
            {
                using var scope = restartedApi.CreateScope();
                var persistedPlan = await scope.ServiceProvider
                    .GetRequiredService<IDurableSyncPlanRepository>()
                    .GetAsync(Tenant, planId, default);
                Assert.NotNull(persistedPlan);
                Assert.Equal(EntitySyncDurablePlanStatus.Approved, persistedPlan!.Status);
                var queued = await scope.ServiceProvider
                    .GetRequiredService<IEntitySyncControlCommands>().QueueApplyAsync(Tenant, planId, approvalId, "e2e-apply-once", Guid.NewGuid(), Actor, default);
                applyRunId = queued.OperationId;
            }

            await using (var restartedWorker = BuildProvider(database.ConnectionString))
            {
                var completed = await restartedWorker
                    .GetRequiredService<EntitySyncOperationWorker>()
                    .ExecuteOneAsync(Tenant, "e2e-worker-after-restart", default);
                Assert.NotNull(completed);
                Assert.Equal(applyRunId, completed!.OperationId);
                var item = Assert.Single(await restartedWorker
                    .GetRequiredService<ISyncOperationRepository>()
                    .GetItemsAsync(Tenant, applyRunId, default));
                Assert.True(
                    completed.Status == EntitySyncOperationStatus.Succeeded,
                    $"Apply ended {completed.Status}; outcome={item.Outcome}; " +
                    $"code={item.ErrorCode}; message={item.ErrorMessage}; " +
                    $"writes={ncentral.WriteCount}");
                Assert.Equal(1, completed.SucceededCount);
            }

            await using (var restartedReader = BuildProvider(database.ConnectionString))
            {
                var operations = restartedReader
                    .GetRequiredService<ISyncOperationRepository>();
                var run = await operations.GetAsync(Tenant, applyRunId, default);
                Assert.NotNull(run);
                Assert.Equal(planId, run!.PlanId);
                Assert.Equal(EntitySyncOperationStatus.Succeeded, run.Status);
                var item = Assert.Single(await operations.GetItemsAsync(
                    Tenant, applyRunId, default));
                Assert.Equal(EntitySyncItemOutcome.Succeeded, item.Outcome);
                var snapshot = await operations.GetSnapshotAsync(
                    Tenant, applyRunId, item.ItemId, default);
                Assert.NotNull(snapshot?.EncryptedBeforeCiphertext);
                Assert.NotNull(snapshot?.EncryptedAfterCiphertext);
                var protector = restartedReader
                    .GetRequiredService<IEntitySyncDataProtector>();
                var beforeDesired = protector.Unprotect(
                    EntitySyncDataProtectionPurpose.AuditValue,
                    snapshot!.EncryptedBeforeCiphertext!);
                var result = protector.Unprotect(
                    EntitySyncDataProtectionPurpose.AuditValue,
                    snapshot.EncryptedAfterCiphertext!);
                Assert.Contains("before", beforeDesired, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("desired", beforeDesired, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Acme Source", result, StringComparison.Ordinal);

                await using var guardConnection =
                    new NpgsqlConnection(database.ConnectionString);
                await guardConnection.OpenAsync();
                await using var mutate = new NpgsqlCommand("""
                    UPDATE entitysync.sync_operation_item_snapshots
                    SET encrypted_after_ciphertext = 'mutated'
                    WHERE tenant_id = @tenant
                      AND operation_id = @operation
                      AND item_id = @item
                    """, guardConnection);
                mutate.Parameters.AddWithValue("tenant", Tenant);
                mutate.Parameters.AddWithValue("operation", applyRunId);
                mutate.Parameters.AddWithValue("item", item.ItemId);
                var mutationError = await Assert.ThrowsAsync<PostgresException>(
                    () => mutate.ExecuteNonQueryAsync());
                Assert.Equal("55000", mutationError.SqlState);

                await using var scrub = new NpgsqlCommand("""
                    UPDATE entitysync.sync_operation_item_snapshots
                    SET encrypted_before_ciphertext = NULL,
                        encrypted_after_ciphertext = NULL
                    WHERE tenant_id = @tenant
                      AND operation_id = @operation
                      AND item_id = @item
                    """, guardConnection);
                scrub.Parameters.AddWithValue("tenant", Tenant);
                scrub.Parameters.AddWithValue("operation", applyRunId);
                scrub.Parameters.AddWithValue("item", item.ItemId);
                var scrubError = await Assert.ThrowsAsync<PostgresException>(
                    () => scrub.ExecuteNonQueryAsync());
                Assert.Equal("55000", scrubError.SqlState);

                var preserved = await operations.GetSnapshotAsync(
                    Tenant, applyRunId, item.ItemId, default);
                Assert.Equal(
                    snapshot.EncryptedBeforeCiphertext,
                    preserved!.EncryptedBeforeCiphertext);
                Assert.Equal(
                    snapshot.EncryptedAfterCiphertext,
                    preserved.EncryptedAfterCiphertext);

                var auditRepository = restartedReader
                    .GetRequiredService<ISyncAuditRepository>();
                var audit = await auditRepository.ListAsync(
                    Tenant, null, null, 100, default);
                var succeeded = Assert.Single(
                    audit.Events,
                    value => value.EventType == "SyncOperationItemSucceeded"
                             && value.OperationId == applyRunId
                             && value.PlanId == planId
                             && value.ItemId == item.ItemId);
                Assert.Equal(item.VendorRequestId, succeeded.CorrelationId);
                Assert.NotNull(await auditRepository.GetFullValuesAsync(
                    Tenant, succeeded.AuditEventId, default));
            }

            Assert.Equal(1, ncentral.WriteCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", previousKeyPath);
            if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
        }
    }

    [Fact]
    public async Task Actual_hosts_complete_signed_control_lifecycle_across_restart()
    {
        await using var database = await PostgresContainer.StartAsync();
        await using var orchestra = await OrchestraContractServer.StartAsync();
        await using var ncentral = await NCentralContractServer.StartAsync();
        await using var issuer = await JwtIssuerServer.StartAsync();
        var apiPort = FreePort();
        var schedulerPort = FreePort();
        var keyPath = Path.Combine(
            Path.GetTempPath(), $"entitysync-process-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                keyPath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["ENTITYSYNC_TEST_ALLOW_HTTP_OAUTH_AUTHORITY"] = "true",
            ["ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"] = "true",
            ["DATABASE_URL"] = database.ConnectionString,
            ["ENTITYSYNC_DATA_PROTECTION_KEY_PATH"] = keyPath,
            ["ENTITYSYNC_TENANT_IDS"] = Tenant,
            ["ENTITYSYNC_WORKER_LEASE_SECONDS"] = "60",
            ["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"] = "2",
            ["ENTITYSYNC_WORKER_RETRY_SECONDS"] = "1",
            ["ORCHESTRA_BASE_URL"] = orchestra.DirectoryBaseUrl,
            ["ORCHESTRA_AUTHORITY"] = orchestra.Authority,
            ["ORCHESTRA_TENANT_ID"] = "tenant",
            ["ORCHESTRA_CLIENT_ID"] = "client",
            ["ORCHESTRA_RESOURCE"] = "api://orchestra",
            ["ORCHESTRA_CLIENT_SECRET"] = Guid.NewGuid().ToString("N"),
            ["NCENTRAL_BASE_URL"] = ncentral.BaseUrl,
            ["NCENTRAL_USER_API_TOKEN"] = Guid.NewGuid().ToString("N"),
            ["NCENTRAL_SERVICE_ORG_ID"] = "100",
            ["MCP_TRANSPORT"] = "http",
            ["MCP_OAUTH_AUTHORITY"] = issuer.Authority,
            ["MCP_OAUTH_RESOURCE"] = "https://entitysync.test/mcp",
            ["MCP_OAUTH_AUDIENCE"] = "api://entitysync-control",
            ["MCP_OAUTH_SCOPES"] = "mcp:tools",
            ["MCP_OAUTH_REQUIRED_SCOPE"] = "mcp:tools",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] =
                "https://logfire-us.pydantic.dev/v1/logs",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization={Guid.NewGuid():N}",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
        };
        var token = issuer.CreateDelegatedToken(
            Tenant,
            "process-smoke-operator",
            "api://entitysync-control",
            [
                "mcp:tools",
                "EntitySync.Read",
                "EntitySync.Operate",
                "EntitySync.Approve",
                "EntitySync.Manage",
                "EntitySync.Audit",
                "EntitySync.Expert"
            ]);

        try
        {
            await using var scheduler = HostProcess.Start(
                "scheduler/LISSTech.EntitySync.Scheduler.csproj",
                schedulerPort,
                environment,
                "lisstech-entitysync-scheduler-process-smoke");
            await using var api = HostProcess.Start(
                "mcp/LISSTech.EntitySync.Mcp.csproj",
                apiPort,
                environment,
                "lisstech-entitysync-mcp-process-smoke");
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            await HostProcess.WaitForStatusAsync(
                client, "health", "healthy", api, scheduler);
            await HostProcess.WaitForStatusAsync(
                client, "health/ready", "ready", api, scheduler);

            var source = await PostJsonAsync(
                client,
                "api/v1/control/connections",
                new
                {
                    vendor = "OrchestraMSP",
                    connectionId = "orchestra-source",
                    displayName = "Orchestra Source"
                },
                "process-connect-source");
            var target = await PostJsonAsync(
                client,
                "api/v1/control/connections",
                new
                {
                    vendor = "NCentral",
                    connectionId = "ncentral-target",
                    displayName = "N-central Target"
                },
                "process-connect-target");
            Assert.Equal(1, source.GetProperty("generation").GetInt64());
            Assert.Equal(1, target.GetProperty("generation").GetInt64());
            Assert.True((await PostJsonAsync(
                client,
                "api/v1/control/connections/orchestra-source/test",
                new { expectedGeneration = 1 },
                "process-test-source")).GetProperty("connected").GetBoolean());
            Assert.True((await PostJsonAsync(
                client,
                "api/v1/control/connections/ncentral-target/test",
                new { expectedGeneration = 1 },
                "process-test-target")).GetProperty("connected").GetBoolean());

            var policy = await PostJsonAsync(
                client,
                "api/v1/control/policies",
                new
                {
                    name = "Process smoke policy",
                    routeScope = "process-smoke-route",
                    definition = new
                    {
                        sourceVendor = "OrchestraMSP",
                        sourceConnectionId = "orchestra-source",
                        sourceEntityType = "Client",
                        targetVendor = "NCentral",
                        targetConnectionId = "ncentral-target",
                        targetEntityType = "Customer",
                        includeInactive = true,
                        createMissing = true,
                        autoLinkScore = 90,
                        reviewScore = 60,
                        sourceExternalIdName = (string?)null,
                        targetCustomFieldName = (string?)null,
                        updatePolicy = 0,
                        allowedFields = new[] { "Name" },
                        blockedFields = Array.Empty<string>(),
                        scheduledApplySafeSubset = false
                    },
                    enabled = true
                },
                "process-policy");
            var policyId = policy.GetProperty("policyId").GetGuid();
            JsonElement plan;
            try
            {
                plan = await PostJsonAsync(
                    client,
                    "api/v1/control/plans",
                    new
                    {
                        policyId,
                        policyVersion = 1,
                        sourceSearch = (string?)null,
                        sourceCount = (int?)null,
                        sourceEntityId = (string?)null,
                        lifetimeMinutes = 60
                    },
                    "process-plan");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"{exception.Message}{Environment.NewLine}API:{api.Output}" +
                    $"{Environment.NewLine}Scheduler:{scheduler.Output}",
                    exception);
            }
            var planId = plan.GetProperty("planId").GetGuid();
            var digest = plan.GetProperty("digest").GetString()!;

            string? cursor = null;
            var inspected = 0;
            var inspectionIndex = 0;
            do
            {
                var inspection = await PostJsonAsync(
                    client,
                    $"api/v1/control/plans/{planId:D}/inspections",
                    new { cursor, pageSize = 1 },
                    $"process-inspection-{inspectionIndex++}");
                inspected += inspection.GetProperty("inspectedItems").GetInt32();
                cursor = inspection.TryGetProperty("nextCursor", out var next)
                         && next.ValueKind != JsonValueKind.Null
                    ? next.GetString()
                    : null;
                if (cursor is null)
                    Assert.True(inspection.GetProperty("complete").GetBoolean());
            } while (cursor is not null);
            Assert.Equal(plan.GetProperty("itemCount").GetInt32(), inspected);

            var approval = await PostJsonAsync(
                client,
                $"api/v1/control/plans/{planId:D}/approvals",
                new { digest },
                "process-approval");
            var approvalId = approval.GetProperty("approvalId").GetGuid();
            var dryRun = await PostJsonAsync(
                client,
                $"api/v1/control/plans/{planId:D}/dry-run",
                new { },
                "process-dry-run");
            await WaitForRunAsync(client, dryRun.GetProperty("runId").GetGuid());

            await scheduler.DisposeAsync();
            var apply = await PostJsonAsync(
                client,
                $"api/v1/control/plans/{planId:D}/apply",
                new { approvalId },
                "process-apply");
            var applyRunId = apply.GetProperty("runId").GetGuid();
            await api.DisposeAsync();

            await using var restartedScheduler = HostProcess.Start(
                "scheduler/LISSTech.EntitySync.Scheduler.csproj",
                schedulerPort,
                environment,
                "lisstech-entitysync-scheduler-process-smoke-restart");
            await using var restartedApi = HostProcess.Start(
                "mcp/LISSTech.EntitySync.Mcp.csproj",
                apiPort,
                environment,
                "lisstech-entitysync-mcp-process-smoke-restart");
            using var restartedClient = new HttpClient
            {
                BaseAddress = client.BaseAddress,
                Timeout = TimeSpan.FromSeconds(10)
            };
            restartedClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            await HostProcess.WaitForStatusAsync(
                restartedClient, "health/ready", "ready", restartedApi, restartedScheduler);
            var terminal = await WaitForRunAsync(restartedClient, applyRunId);
            Assert.Equal("Succeeded", terminal.GetProperty("status").GetString());
            Assert.Equal(1, terminal.GetProperty("succeededCount").GetInt32());
            Assert.Equal(planId, terminal.GetProperty("planId").GetGuid());

            var audit = await GetJsonAsync(
                restartedClient, "api/v1/control/audit?pageSize=100");
            var successEvent = audit.GetProperty("items")
                .EnumerateArray()
                .Single(value =>
                    value.GetProperty("eventType").GetString()
                    == "SyncOperationItemSucceeded"
                    && value.GetProperty("operationId").GetGuid() == applyRunId);
            var values = await GetJsonAsync(
                restartedClient,
                $"api/v1/control/audit/{successEvent.GetProperty("auditEventId").GetGuid():D}/values");
            Assert.NotEqual(JsonValueKind.Null, values.GetProperty("valuesJson").ValueKind);
            Assert.Equal(1, ncentral.WriteCount);
        }
        finally
        {
            if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
        }
    }

    private static async Task<JsonElement> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{request.Method} {path} returned {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {path} returned {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForRunAsync(
        HttpClient client,
        Guid runId)
    {
        JsonElement current = default;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            current = await GetJsonAsync(
                client, $"api/v1/control/runs/{runId:D}");
            var status = current.GetProperty("status").GetString();
            if (status is "Succeeded" or "Partial" or "Failed" or "Cancelled")
                return current;
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"Run {runId:D} did not reach a terminal status: {current}");
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddEntitySyncPlatform(connectionString, EntitySyncHostMode.Http);
        services.AddSingleton<IServerManagedEntityAdapterFactory>(
            new ServerManagedEntityAdapterFactory(
                new Dictionary<string, string?>
                {
                    ["ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"] = "true"
                },
                "Testing"));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static ServerManagedConnectionConfiguration ConnectionConfiguration(
        string vendor,
        IReadOnlyDictionary<string, string?> environment) =>
        new ServerManagedEntityAdapterFactory(environment, "Testing")
            .GetConnectionConfiguration(vendor, null);

    private static async Task CreateConnectionAsync(
        ServiceProvider provider,
        string vendor,
        string connectionId,
        string displayName,
        ServerManagedConnectionConfiguration configuration)
    {
        try
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ConnectionDefinitionService>()
                .CreateAsync(
                    Tenant,
                    new ConnectionDefinitionRequest(
                        vendor,
                        connectionId,
                        displayName,
                        configuration.PublicConfiguration,
                        configuration.SecretConfiguration,
                        configuration.PlatformInstanceId),
                    Actor,
                    default);
        }
        finally
        {
            if (configuration.SecretConfiguration is IDictionary<string, string> secrets)
                secrets.Clear();
        }
    }

    private sealed class PostgresContainer : IAsyncDisposable
    {
        private readonly string name;
        private PostgresContainer(string name, string connectionString)
        {
            this.name = name;
            ConnectionString = connectionString;
        }

        internal string ConnectionString { get; }

        internal static async Task<PostgresContainer> StartAsync()
        {
            var name = $"entitysync-e2e-{Guid.NewGuid():N}";
            var port = FreePort();
            await RunAsync(
                "docker",
                "run", "--detach", "--rm", "--name", name,
                "--env", "POSTGRES_PASSWORD=entitysync-e2e",
                "--publish", $"127.0.0.1:{port}:5432",
                "postgres:18-trixie@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636");
            var connectionString =
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;" +
                "Password=entitysync-e2e;Pooling=false;Timeout=2";
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
            throw new InvalidOperationException("PostgreSQL test container did not become ready.", last);
        }

        public async ValueTask DisposeAsync()
        {
            try { await RunAsync("docker", "rm", "--force", name); }
            catch { }
        }
    }

    private sealed class OrchestraContractServer : IAsyncDisposable
    {
        private static readonly Guid ClientId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly WebApplication app;

        private OrchestraContractServer(WebApplication app, string baseUrl)
        {
            this.app = app;
            Authority = baseUrl;
            DirectoryBaseUrl = baseUrl + "api/v1/internal/client-directory/";
        }

        internal string Authority { get; }
        internal string DirectoryBaseUrl { get; }

        internal static async Task<OrchestraContractServer> StartAsync()
        {
            var baseUrl = $"http://127.0.0.1:{FreePort()}/";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(baseUrl);
            var app = builder.Build();
            var server = new OrchestraContractServer(app, baseUrl);
            app.MapPost("/tenant/oauth2/v2.0/token", () => Results.Json(new
            {
                access_token = "orchestra-test-access-token",
                expires_in = 3600
            }));
            app.MapGet("/api/v1/internal/client-directory/clients", () =>
                Results.Text(
                    $$"""{"items":[{{ClientJson()}}],"next_cursor":null}""",
                    "application/json"));
            app.MapGet("/api/v1/internal/client-directory/clients/{id:guid}",
                (Guid id) => id == ClientId
                    ? Results.Text(ClientJson(), "application/json")
                    : Results.NotFound());
            await app.StartAsync();
            return server;
        }

        private static string ClientJson() => $$"""
            {"client_id":"{{ClientId:D}}","version":7,"name":"Acme Source",
             "lifecycle_status":"active","is_deleted":false,
             "merged_into_client_id":null,"merged_from_client_ids":[],
             "fields":{},"tags":[],"sites":[],"addresses":[],"platform_links":[]}
            """;

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private sealed class NCentralContractServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private int writeCount;
        private int created;

        private NCentralContractServer(WebApplication app, string baseUrl)
        {
            this.app = app;
            BaseUrl = baseUrl;
        }

        internal string BaseUrl { get; }
        internal int WriteCount => Volatile.Read(ref writeCount);

        internal static async Task<NCentralContractServer> StartAsync()
        {
            var baseUrl = $"http://127.0.0.1:{FreePort()}/";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(baseUrl);
            var app = builder.Build();
            var server = new NCentralContractServer(app, baseUrl);
            app.MapPost("/api/auth/authenticate", () => Results.Json(new
            {
                tokens = new { access = new { token = "ncentral-access", expirySeconds = 3600 } }
            }));
            app.MapGet("/api/auth/validate", () => Results.Ok());
            app.MapGet("/api/service-orgs/100/customers", () =>
                Results.Json(new
                {
                    data = Volatile.Read(ref server.created) == 0
                        ? Array.Empty<object>()
                        : [new { customerId = "501", customerName = "Acme Source" }],
                    totalPages = 1
                }));
            app.MapPost("/api/service-orgs/100/customers", () =>
            {
                Interlocked.Increment(ref server.writeCount);
                Volatile.Write(ref server.created, 1);
                return Results.Json(new { customerId = "501" }, statusCode: 201);
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private sealed class JwtIssuerServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly RSA rsa;
        private readonly SigningCredentials credentials;

        private JwtIssuerServer(
            WebApplication app,
            RSA rsa,
            SigningCredentials credentials,
            string authority)
        {
            this.app = app;
            this.rsa = rsa;
            this.credentials = credentials;
            Authority = authority;
        }

        internal string Authority { get; }

        internal static async Task<JwtIssuerServer> StartAsync()
        {
            var authority = $"http://127.0.0.1:{FreePort()}";
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "entitysync-process-smoke" };
            var credentials = new SigningCredentials(
                key, SecurityAlgorithms.RsaSha256);
            var parameters = rsa.ExportParameters(false);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(authority);
            var app = builder.Build();
            app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
            {
                issuer = authority,
                jwks_uri = authority + "/jwks",
                authorization_endpoint = authority + "/authorize",
                token_endpoint = authority + "/token",
                id_token_signing_alg_values_supported = new[] { "RS256" }
            }));
            app.MapGet("/jwks", () => Results.Json(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = key.KeyId,
                        alg = "RS256",
                        n = Base64UrlEncoder.Encode(parameters.Modulus!),
                        e = Base64UrlEncoder.Encode(parameters.Exponent!)
                    }
                }
            }));
            await app.StartAsync();
            return new JwtIssuerServer(app, rsa, credentials, authority);
        }

        internal string CreateDelegatedToken(
            string tenantId,
            string objectId,
            string audience,
            IReadOnlyList<string> scopes)
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Authority,
                Audience = audience,
                Expires = DateTime.UtcNow.AddMinutes(15),
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                SigningCredentials = credentials,
                Claims = new Dictionary<string, object>
                {
                    ["tid"] = tenantId,
                    ["oid"] = objectId,
                    ["scp"] = string.Join(' ', scopes)
                }
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            rsa.Dispose();
        }
    }

    private sealed class HostProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new();
        private int disposed;

        private HostProcess(Process process)
        {
            this.process = process;
            process.OutputDataReceived += (_, eventArgs) => Append(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Append(eventArgs.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        internal static HostProcess Start(
            string project,
            int port,
            IReadOnlyDictionary<string, string> environment,
            string serviceName)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = FindRepositoryRoot()
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("--configuration");
            start.ArgumentList.Add("Release");
            start.ArgumentList.Add("--no-build");
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["OTEL_SERVICE_NAME"] = serviceName;
            foreach (var value in environment)
                start.Environment[value.Key] = value.Value;
            var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {project}.");
            return new HostProcess(process);
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "docker-compose.yaml")))
                    return directory.FullName;
            }
            throw new InvalidOperationException(
                "Could not locate the EntitySync repository root.");
        }

        internal static async Task WaitForStatusAsync(
            HttpClient client,
            string path,
            string expected,
            params HostProcess[] processes)
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                foreach (var process in processes)
                {
                    if (process.process.HasExited)
                        throw new InvalidOperationException(
                            $"Host exited {process.process.ExitCode}: {process.Output}");
                }
                try
                {
                    using var response = await client.GetAsync(path);
                    var text = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(text);
                        if (document.RootElement.GetProperty("status").GetString() == expected)
                            return;
                    }
                    last = new InvalidOperationException(
                        $"{path} returned {(int)response.StatusCode}: {text}");
                }
                catch (Exception exception)
                    when (exception is HttpRequestException or TaskCanceledException)
                {
                    last = exception;
                }
                await Task.Delay(250);
            }
            throw new TimeoutException(
                $"{path} did not report {expected}. " +
                $"{string.Join(Environment.NewLine, processes.Select(p => p.Output))}",
                last);
        }

        internal string Output
        {
            get
            {
                lock (output) return output.ToString();
            }
        }

        private void Append(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} exited {process.ExitCode}: {error}");
        return output.Trim();
    }
}

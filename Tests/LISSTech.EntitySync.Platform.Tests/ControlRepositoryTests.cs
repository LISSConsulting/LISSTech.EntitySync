using System.Security.Cryptography;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlRepositoryTests : IAsyncLifetime
{
    private readonly string databaseName = $"entitysync_control_repo_{Guid.NewGuid():N}";
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource? database;

    [Fact]
    public void Data_protection_isolated_by_purpose_application_and_key_ring()
    {
        using var keyRingA = new TemporaryDirectory();
        using var keyRingB = new TemporaryDirectory();
        var protectorA = CreateProtector(keyRingA.Path, "application-a");
        var sameApplication = CreateProtector(keyRingA.Path, "application-a");
        var otherApplication = CreateProtector(keyRingA.Path, "application-b");
        var otherKeyRing = CreateProtector(keyRingB.Path, "application-a");

        const string secret = "vendor-secret-that-must-never-be-plaintext";
        var ciphertext = protectorA.Protect(EntitySyncDataProtectionPurpose.ConnectionSecret, secret);

        Assert.DoesNotContain(secret, ciphertext, StringComparison.Ordinal);
        Assert.Equal(secret, sameApplication.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            protectorA.Unprotect(EntitySyncDataProtectionPurpose.AuditValue, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            otherApplication.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
        Assert.Throws<CryptographicException>(() =>
            otherKeyRing.Unprotect(EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
    }

    [Fact]
    public void Hosting_resolves_control_repositories_with_external_data_protection()
    {
        using var keyRing = new TemporaryDirectory();
        using var insecureKeyRing = new TemporaryDirectory();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                keyRing.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                insecureKeyRing.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        var original = Environment.GetEnvironmentVariable("ENTITYSYNC_DATA_PROTECTION_KEY_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", null);
            Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddEntitySyncPlatform(
                    "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                    EntitySyncHostMode.Http));
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable(
                    "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", insecureKeyRing.Path);
                Assert.Throws<InvalidOperationException>(() =>
                    new ServiceCollection().AddEntitySyncPlatform(
                        "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                        EntitySyncHostMode.Http));
            }
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyRing.Path);
            var services = new ServiceCollection();
            services.AddEntitySyncPlatform(
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                EntitySyncHostMode.Http);
            using var provider = services.BuildServiceProvider();
            Assert.IsType<PostgresConnectionDefinitionRepository>(
                provider.GetRequiredService<IConnectionDefinitionRepository>());
            Assert.IsType<PostgresSyncPolicyRepository>(
                provider.GetRequiredService<ISyncPolicyRepository>());
            Assert.IsType<PostgresDurableSyncPlanRepository>(
                provider.GetRequiredService<IDurableSyncPlanRepository>());
            Assert.IsType<PostgresSyncOperationRepository>(
                provider.GetRequiredService<ISyncOperationRepository>());
            Assert.IsType<PostgresSyncScheduleRepository>(
                provider.GetRequiredService<ISyncScheduleRepository>());
            Assert.IsType<PostgresSyncAuditRepository>(
                provider.GetRequiredService<ISyncAuditRepository>());
            Assert.Same(
                provider.GetRequiredService<IIdempotencyRepository>(),
                provider.GetRequiredService<IIdempotentCommandExecutor>());
            var protector = provider.GetRequiredService<IEntitySyncDataProtector>();
            var ciphertext = protector.Protect(
                EntitySyncDataProtectionPurpose.ConnectionSecret, "host-secret");
            Assert.Equal(
                "host-secret",
                protector.Unprotect(
                    EntitySyncDataProtectionPurpose.ConnectionSecret, ciphertext));
            if (!OperatingSystem.IsWindows())
            {
                var keyFiles = Directory.GetFiles(keyRing.Path, "*.xml");
                Assert.NotEmpty(keyFiles);
                foreach (var keyFile in keyFiles)
                {
                    var mode = File.GetUnixFileMode(keyFile);
                    Assert.Equal(
                        (UnixFileMode)0,
                        mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                            | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "ENTITYSYNC_DATA_PROTECTION_KEY_PATH", original);
        }
    }

    [Fact]
    public async Task Connection_and_policy_repositories_are_tenant_isolated_and_lossless()
    {
        var connectionRepository = new PostgresConnectionDefinitionRepository(Database);
        var policyRepository = new PostgresSyncPolicyRepository(Database);
        var now = DateTimeOffset.UtcNow;
        using var keyRing = new TemporaryDirectory();
        var protector = CreateProtector(keyRing.Path, "repository-test");
        var secret = protector.Protect(EntitySyncDataProtectionPurpose.ConnectionSecret, "plain-secret");
        var platformInstanceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var connection = Connection(
            "tenant-a", "source", "NetSuite", 1, secret, now, platformInstanceId);

        await connectionRepository.InsertAsync("tenant-a", connection, default);
        await using (var raw = Database.CreateCommand("""
            SELECT secret_ciphertext
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            """))
        {
            raw.Parameters.AddWithValue("tenant_id", connection.TenantId);
            raw.Parameters.AddWithValue("connection_id", connection.ConnectionId);
            var storedCiphertext = Assert.IsType<string>(await raw.ExecuteScalarAsync());
            Assert.DoesNotContain("plain-secret", storedCiphertext, StringComparison.Ordinal);
        }

        var reloadedConnection = await new PostgresConnectionDefinitionRepository(Database)
            .GetAsync("tenant-a", "source", default);
        Assert.Equal(connection, reloadedConnection);
        Assert.Equal(platformInstanceId, reloadedConnection!.PlatformInstanceId);
        Assert.Null(await connectionRepository.GetAsync("tenant-b", "source", default));
        Assert.Single(await connectionRepository.ListAsync("tenant-a", "NetSuite", true, default));
        Assert.Empty(await connectionRepository.ListAsync("tenant-b", null, null, default));
        var replacement = connection.NextGeneration(
            "Source v2", false, new EntitySyncJsonValue("{\"region\":\"us\"}"), secret,
            new EntitySyncActor("updater"), now.AddMinutes(1));
        await Assert.ThrowsAsync<ArgumentException>(() => connectionRepository.TryReplaceAsync(
            "tenant-b", "source", 1, replacement, default));
        Assert.NotNull(await connectionRepository.TryReplaceAsync(
            "tenant-a", "source", 1, replacement, default));
        Assert.Null(await connectionRepository.TryReplaceAsync(
            "tenant-a", "source", 1, replacement, default));

        var policy = Policy("tenant-a", connection.ConnectionId, "target", now);
        await connectionRepository.InsertAsync(
            "tenant-a", Connection("tenant-a", "target", "HaloPSA", 1, secret, now), default);
        await policyRepository.InsertAsync("tenant-a", policy, default);

        var storedPolicy = await policyRepository.GetAsync("tenant-a", policy.PolicyId, 1, default);
        var latestPolicy = await policyRepository.GetLatestAsync("tenant-a", policy.PolicyId, default);
        Assert.NotNull(storedPolicy);
        Assert.NotNull(latestPolicy);
        Assert.Equal(policy.DefinitionSha256, storedPolicy.DefinitionSha256);
        Assert.Equal(policy.Definition.AllowedFields.Order(), storedPolicy.Definition.AllowedFields.Order());
        Assert.Equal(policy.DefinitionSha256, latestPolicy.DefinitionSha256);
        Assert.Null(await policyRepository.GetAsync("tenant-b", policy.PolicyId, 1, default));
        Assert.Single(await policyRepository.ListLatestAsync("tenant-a", "route-a", true, default));
    }

    [Fact]
    public async Task Policy_idempotency_token_is_tenant_scoped_and_bound_to_exact_version()
    {
        const string tenantId = "policy-token-tenant";
        var now = DateTimeOffset.UtcNow;
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var policies = new PostgresSyncPolicyRepository(Database);
        await connections.InsertAsync(
            tenantId, Connection(tenantId, "source", "NetSuite", 1, "cipher", now), default);
        await connections.InsertAsync(
            tenantId, Connection(tenantId, "target", "HaloPSA", 1, "cipher", now), default);
        var policy = Policy(tenantId, "source", "target", now);
        var token = new string('a', 64);

        Assert.True(await policies.TryInsertValidatedWithTokenAsync(
            tenantId, policy, "source", 1, "target", 1, token, default));
        var recovered = await policies.GetByIdempotencyTokenAsync(
            tenantId, policy.PolicyId, token, default);
        Assert.NotNull(recovered);
        Assert.Equal(policy.PolicyId, recovered.PolicyId);
        Assert.Equal(policy.Version, recovered.Version);
        Assert.Equal(policy.DefinitionSha256, recovered.DefinitionSha256);
        Assert.Null(await policies.GetByIdempotencyTokenAsync(
            "other-tenant", policy.PolicyId, token, default));
        Assert.Null(await policies.GetByIdempotencyTokenAsync(
            tenantId, policy.PolicyId, new string('b', 64), default));
    }
    [Fact]
    public async Task Deleted_connection_id_recreation_never_reuses_a_generation()
    {
        var repository = new PostgresConnectionDefinitionRepository(Database);
        var tenantId = $"tenant-aba-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var first = Connection(tenantId, "shared", "NetSuite", 1, "cipher-one", now);
        await repository.InsertAsync(tenantId, first, default);
        Assert.Equal(
            ConnectionDefinitionDeleteResult.Deleted,
            await repository.TryDeleteAsync(tenantId, first.ConnectionId, 1, default));

        var recreated = Connection(
            tenantId,
            first.ConnectionId,
            first.Vendor,
            1,
            "cipher-two",
            now.AddMinutes(1));
        await repository.InsertAsync(tenantId, recreated, default);
        var stored = await repository.GetAsync(tenantId, first.ConnectionId, default);

        Assert.NotNull(stored);
        Assert.True(stored.Generation > first.Generation);
    }

    [Fact]
    public async Task Plan_insert_and_connection_delete_serialize_on_exact_generations()
    {
        var context = await SeedControlContextAsync($"plan-delete-{Guid.NewGuid():N}");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var manifest = Manifest(context, 1);
        await using var blocker = await Database.OpenConnectionAsync(default);
        await using var transaction = await blocker.BeginTransactionAsync(default);
        await using (var command = new NpgsqlCommand(
            """
            SELECT connection_id
            FROM entitysync.connection_definitions
            WHERE tenant_id = @tenant_id AND connection_id = @connection_id
            FOR UPDATE
            """,
            blocker,
            transaction))
        {
            command.Parameters.AddWithValue("tenant_id", context.TenantId);
            command.Parameters.AddWithValue(
                "connection_id",
                context.Source.ConnectionId);
            await command.ExecuteScalarAsync();
        }

        var insert = plans.InsertAsync(context.TenantId, manifest, default);
        await AssertStillRunningAsync(insert);
        var delete = connections.TryDeleteAsync(
            context.TenantId,
            context.Source.ConnectionId,
            context.Source.Generation,
            default);
        await transaction.CommitAsync();
        await insert;

        Assert.Equal(ConnectionDefinitionDeleteResult.Referenced, await delete);
        Assert.NotNull(await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default));
        Assert.NotNull(await connections.GetAsync(
            context.TenantId,
            context.Source.ConnectionId,
            default));
    }


    [Fact]
    public async Task Durable_import_serializes_with_ordinary_create_on_plan_identity()
    {
        var context = await SeedControlContextAsync("import-plan-lock");
        var manifest = Manifest(context, 1);
        await using var blocker = await Database.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = blocker.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT pg_advisory_xact_lock(
                    hashtextextended(@plan_identity, 0))
                """;
            command.Parameters.AddWithValue(
                "plan_identity",
                $"{context.TenantId}:{manifest.Plan.PlanId:N}");
            await command.ExecuteNonQueryAsync();
        }

        IDurableSyncPlanRepository importing =
            new PostgresDurableSyncPlanRepository(Database);
        var import = importing.ImportAsync(
            context.TenantId,
            manifest,
            "plan-lock-key",
            new EntitySyncActor("importer"),
            default);
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await import.WaitAsync(TimeSpan.FromMilliseconds(250)));
        var ordinary = new PostgresDurableSyncPlanRepository(Database)
            .InsertAsync(context.TenantId, manifest, default);
        await transaction.CommitAsync();

        await ordinary;
        var result = await import;
        Assert.True(
            result.State is DurablePlanImportPersistenceState.Inserted
                or DurablePlanImportPersistenceState.Replayed);
    }

    [Theory]
    [InlineData(EntitySyncDurablePlanStatus.Approved)]
    [InlineData(EntitySyncDurablePlanStatus.Consumed)]
    [InlineData(EntitySyncDurablePlanStatus.Expired)]
    public async Task Durable_import_replay_returns_current_persisted_status(
        EntitySyncDurablePlanStatus currentStatus)
    {
        var context = await SeedControlContextAsync($"import-status-{currentStatus}");
        var manifest = Manifest(context, 1);
        const string key = "status-replay-key";
        IDurableSyncPlanRepository firstRepository =
            new PostgresDurableSyncPlanRepository(Database);
        Assert.Equal(
            DurablePlanImportPersistenceState.Inserted,
            (await firstRepository.ImportAsync(
                context.TenantId,
                manifest,
                key,
                new EntitySyncActor("importer"),
                default)).State);
        await using (var update = Database.CreateCommand(
                         """
                         UPDATE entitysync.sync_plans
                         SET status = @status
                         WHERE tenant_id = @tenant_id AND plan_id = @plan_id
                         """))
        {
            update.Parameters.AddWithValue("status", currentStatus.ToString());
            update.Parameters.AddWithValue("tenant_id", context.TenantId);
            update.Parameters.AddWithValue("plan_id", manifest.Plan.PlanId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        IDurableSyncPlanRepository reconstructed =
            new PostgresDurableSyncPlanRepository(Database);
        var replay = await reconstructed.ImportAsync(
            context.TenantId,
            manifest,
            key,
            new EntitySyncActor("importer"),
            default);

        Assert.Equal(DurablePlanImportPersistenceState.Replayed, replay.State);
        Assert.Equal(currentStatus, replay.Plan?.Status);
        Assert.Equal(manifest.Plan.PlanId, replay.Plan?.PlanId);
        Assert.Equal(
            manifest.Plan.PlanDigestSha256,
            replay.Plan?.PlanDigestSha256);
    }

    [Fact]
    public async Task Durable_import_receipts_are_isolated_from_http_idempotency_keys()
    {
        var context = await SeedControlContextAsync("import-receipt-isolation");
        var idempotency = new PostgresIdempotencyRepository(Database);
        var now = DateTimeOffset.UtcNow;
        foreach (var key in new[] { "x", "plan.import:x" })
        {
            Assert.True(await idempotency.TryInsertAsync(
                context.TenantId,
                new EntitySyncIdempotencyReceipt(
                    context.TenantId,
                    key,
                    new EntitySyncSha256(new string('a', 64)),
                    null,
                    null,
                    now,
                    null,
                    now.AddHours(1)),
                default));
        }
        var manifest = Manifest(context, 1);
        IDurableSyncPlanRepository plans =
            new PostgresDurableSyncPlanRepository(Database);

        var imported = await plans.ImportAsync(
            context.TenantId,
            manifest,
            "x",
            new EntitySyncActor("importer"),
            default);

        Assert.Equal(DurablePlanImportPersistenceState.Inserted, imported.State);
        Assert.NotNull(await idempotency.GetAsync(context.TenantId, "x", default));
        Assert.NotNull(await idempotency.GetAsync(
            context.TenantId, "plan.import:x", default));
        await using var count = Database.CreateCommand(
            """
            SELECT count(*)
            FROM entitysync.plan_import_receipts
            WHERE tenant_id = @tenant_id AND caller_key = 'x'
            """);
        count.Parameters.AddWithValue("tenant_id", context.TenantId);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        await using var cascade = Database.CreateCommand(
            """
            SELECT pg_get_constraintdef(constraint_row.oid)
            FROM pg_constraint constraint_row
            JOIN pg_class table_row
              ON table_row.oid = constraint_row.conrelid
            JOIN pg_namespace schema_row
              ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = 'entitysync'
              AND table_row.relname = 'plan_import_receipts'
              AND constraint_row.conname =
                  'plan_import_receipts_plan_fk'
            """);
        Assert.Contains(
            "ON DELETE CASCADE",
            Assert.IsType<string>(await cascade.ExecuteScalarAsync()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durable_import_replay_fails_closed_on_mismatched_receipt_plan()
    {
        var context = await SeedControlContextAsync("import-receipt-corruption");
        var manifest = Manifest(context, 1);
        const string key = "corrupt-receipt-key";
        IDurableSyncPlanRepository plans =
            new PostgresDurableSyncPlanRepository(Database);
        await plans.ImportAsync(
            context.TenantId,
            manifest,
            key,
            new EntitySyncActor("importer"),
            default);
        await using (var corrupt = Database.CreateCommand(
                         """
                         UPDATE entitysync.plan_import_receipts
                         SET plan_digest_sha256 = @digest
                         WHERE tenant_id = @tenant_id AND caller_key = @caller_key
                         """))
        {
            corrupt.Parameters.AddWithValue("digest", new string('f', 64));
            corrupt.Parameters.AddWithValue("tenant_id", context.TenantId);
            corrupt.Parameters.AddWithValue("caller_key", key);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostgresDurableSyncPlanRepository(Database).ImportAsync(
                context.TenantId,
                manifest,
                key,
                new EntitySyncActor("importer"),
                default));
        Assert.Contains("missing or mismatched plan", exception.Message);
    }

    [Fact]
    public async Task Durable_import_rejects_database_expired_plan_without_mutation()
    {
        var context = await SeedControlContextAsync("import-expired-boundary");
        await using var clock = Database.CreateCommand(
            "SELECT clock_timestamp()");
        var databaseClock = await clock.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Database clock unavailable.");
        var databaseNow = databaseClock switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Database clock type unavailable.")
        };
        var expired = ManifestWithExpiration(context, databaseNow);
        IDurableSyncPlanRepository plans =
            new PostgresDurableSyncPlanRepository(Database);

        var rejected = await plans.ImportAsync(
            context.TenantId,
            expired,
            "expired-key",
            new EntitySyncActor("importer"),
            default);

        Assert.Equal("Expired", rejected.State.ToString());
        Assert.Null(await plans.GetAsync(
            context.TenantId, expired.Plan.PlanId, default));
        var future = ManifestWithExpiration(
            context, databaseNow.AddHours(1));
        Assert.Equal(
            DurablePlanImportPersistenceState.Inserted,
            (await plans.ImportAsync(
                context.TenantId,
                future,
                "future-key",
                new EntitySyncActor("importer"),
                default)).State);
    }

    [Fact]
    public async Task Durable_import_receipt_replays_and_conflicts_after_repository_reconstruction()
    {
        var context = await SeedControlContextAsync("import-receipt");
        var actor = new EntitySyncActor("importer");
        var manifest = Manifest(context, 1);
        const string key = "import-receipt-key";
        IDurableSyncPlanRepository firstRepository =
            new PostgresDurableSyncPlanRepository(Database);
        var first = await firstRepository.ImportAsync(
            context.TenantId, manifest, key, actor, default);
        IDurableSyncPlanRepository replayRepository =
            new PostgresDurableSyncPlanRepository(Database);
        var replay = await replayRepository.ImportAsync(
            context.TenantId, manifest, key, actor, default);
        IDurableSyncPlanRepository differentPlanRepository =
            new PostgresDurableSyncPlanRepository(Database);
        var differentPlan = await differentPlanRepository.ImportAsync(
            context.TenantId, Manifest(context, 1), key, actor, default);
        IDurableSyncPlanRepository differentActorRepository =
            new PostgresDurableSyncPlanRepository(Database);
        var differentActor = await differentActorRepository.ImportAsync(
            context.TenantId, manifest, key, new EntitySyncActor("other-importer"), default);

        Assert.Equal(DurablePlanImportPersistenceState.Inserted, first.State);
        Assert.Equal(DurablePlanImportPersistenceState.Replayed, replay.State);
        Assert.Equal(manifest.Plan, replay.Plan);
        Assert.Equal(DurablePlanImportPersistenceState.Conflict, differentPlan.State);
        Assert.Equal(DurablePlanImportPersistenceState.Conflict, differentActor.State);
    }

    [Theory]
    [InlineData(EntitySyncDurablePlanStatus.Draft)]
    [InlineData(EntitySyncDurablePlanStatus.Expired)]
    public async Task Durable_import_lost_response_replays_before_policy_and_expiry_checks(
        EntitySyncDurablePlanStatus persistedStatus)
    {
        var context = await SeedControlContextAsync(
            $"import-lost-policy-{persistedStatus}");
        var manifest = Manifest(context, 1);
        var actor = new EntitySyncActor("importer");
        const string key = "lost-response-policy-key";
        _ = await RecoveryService(
                context,
                new PostgresDurableSyncPlanRepository(Database),
                new BlockingReadAdapter("NetSuite", []))
            .ImportManifestAsync(
                context.TenantId, manifest, key, actor, default);
        if (persistedStatus == EntitySyncDurablePlanStatus.Expired)
        {
            await using var expire = Database.CreateCommand(
                """
                UPDATE entitysync.sync_plans
                SET status = 'Expired'
                WHERE tenant_id = @tenant_id AND plan_id = @plan_id
                """);
            expire.Parameters.AddWithValue("tenant_id", context.TenantId);
            expire.Parameters.AddWithValue("plan_id", manifest.Plan.PlanId);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        var policies = new PostgresSyncPolicyRepository(Database);
        await policies.InsertAsync(
            context.TenantId,
            context.Policy.NextVersion(
                new EntitySyncActor("disabler"),
                context.Policy.Definition,
                context.Now.AddMinutes(1),
                enabled: false),
            default);
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var rotated = context.Source.NextGeneration(
            context.Source.DisplayName,
            context.Source.Enabled,
            context.Source.PublicConfiguration,
            context.Source.SecretCiphertext,
            new EntitySyncActor("rotator"),
            context.Now.AddMinutes(1));
        Assert.NotNull(await connections.TryReplaceAsync(
            context.TenantId,
            context.Source.ConnectionId,
            context.Source.Generation,
            rotated,
            default));

        var replay = await RecoveryService(
                context,
                new PostgresDurableSyncPlanRepository(Database),
                new BlockingReadAdapter("NetSuite", []))
            .ImportManifestAsync(
                context.TenantId, manifest, key, actor, default);

        Assert.Equal(manifest.Plan.PlanId, replay.PlanId);
        Assert.Equal(manifest.Plan.PlanDigestSha256, replay.PlanDigestSha256);
        Assert.Equal(persistedStatus, replay.Status);
        await Assert.ThrowsAsync<DurablePlanPolicyChangedException>(
            () => RecoveryService(
                    context,
                    new PostgresDurableSyncPlanRepository(Database),
                    new BlockingReadAdapter("NetSuite", []))
                .ImportManifestAsync(
                    context.TenantId,
                    manifest,
                    "new-policy-key",
                    actor,
                    default));
    }

    [Fact]
    public async Task Durable_import_lost_response_replays_before_connection_checks()
    {
        var context = await SeedControlContextAsync("import-lost-connection");
        var manifest = Manifest(context, 1);
        var actor = new EntitySyncActor("importer");
        const string key = "lost-response-connection-key";
        _ = await RecoveryService(
                context,
                new PostgresDurableSyncPlanRepository(Database),
                new BlockingReadAdapter("NetSuite", []))
            .ImportManifestAsync(
                context.TenantId, manifest, key, actor, default);
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var rotated = context.Source.NextGeneration(
            context.Source.DisplayName,
            context.Source.Enabled,
            context.Source.PublicConfiguration,
            context.Source.SecretCiphertext,
            new EntitySyncActor("rotator"),
            context.Now.AddMinutes(1));
        Assert.NotNull(await connections.TryReplaceAsync(
            context.TenantId,
            context.Source.ConnectionId,
            context.Source.Generation,
            rotated,
            default));

        var replay = await RecoveryService(
                context,
                new PostgresDurableSyncPlanRepository(Database),
                new BlockingReadAdapter("NetSuite", []))
            .ImportManifestAsync(
                context.TenantId, manifest, key, actor, default);

        Assert.Equal(manifest.Plan.PlanId, replay.PlanId);
        Assert.Equal(manifest.Plan.PlanDigestSha256, replay.PlanDigestSha256);
        Assert.Equal(EntitySyncDurablePlanStatus.Draft, replay.Status);
        await Assert.ThrowsAsync<DurablePlanConnectionChangedException>(
            () => RecoveryService(
                    context,
                    new PostgresDurableSyncPlanRepository(Database),
                    new BlockingReadAdapter("NetSuite", []))
                .ImportManifestAsync(
                    context.TenantId,
                    manifest,
                    "new-connection-key",
                    actor,
                    default));
    }

    [Fact]
    public async Task Durable_import_repository_rejects_policy_change_for_new_request()
    {
        var context = await SeedControlContextAsync("import-policy-guard");
        var policies = new PostgresSyncPolicyRepository(Database);
        await policies.InsertAsync(
            context.TenantId,
            context.Policy.NextVersion(
                new EntitySyncActor("disabler"),
                context.Policy.Definition,
                context.Now.AddMinutes(1),
                enabled: false),
            default);
        var manifest = Manifest(context, 1);

        var result = await new PostgresDurableSyncPlanRepository(Database)
            .ImportAsync(
                context.TenantId,
                manifest,
                "import-policy-guard-key",
                new EntitySyncActor("importer"),
                default);

        Assert.Equal(
            DurablePlanImportPersistenceState.PolicyChanged,
            result.State);
        Assert.Null(await new PostgresDurableSyncPlanRepository(Database).GetAsync(
            context.TenantId, manifest.Plan.PlanId, default));
    }

    [Fact]
    public async Task Durable_import_repository_rejects_connection_change_for_new_request()
    {
        var context = await SeedControlContextAsync("import-connection-guard");
        var connections =
            new PostgresConnectionDefinitionRepository(Database);
        var rotated = context.Source.NextGeneration(
            context.Source.DisplayName,
            context.Source.Enabled,
            context.Source.PublicConfiguration,
            context.Source.SecretCiphertext,
            new EntitySyncActor("rotator"),
            context.Now.AddMinutes(1));
        Assert.NotNull(await connections.TryReplaceAsync(
            context.TenantId,
            context.Source.ConnectionId,
            context.Source.Generation,
            rotated,
            default));
        var manifest = Manifest(context, 1);

        var result = await new PostgresDurableSyncPlanRepository(Database)
            .ImportAsync(
                context.TenantId,
                manifest,
                "import-connection-guard-key",
                new EntitySyncActor("importer"),
                default);

        Assert.Equal(
            DurablePlanImportPersistenceState.ConnectionChanged,
            result.State);
        Assert.Null(await new PostgresDurableSyncPlanRepository(Database).GetAsync(
            context.TenantId, manifest.Plan.PlanId, default));
    }

    [Fact]
    public async Task Manifest_round_trips_and_item_failure_rolls_back_plan()
    {
        var context = await SeedControlContextAsync("manifest");
        var repository = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 2, withResolvedParent: true);

        await repository.InsertAsync(context.TenantId, manifest, default);
        await repository.InsertAsync(context.TenantId, manifest, default);

        Assert.Equal(
            manifest.Plan,
            await new PostgresDurableSyncPlanRepository(Database).GetAsync(
                context.TenantId, manifest.Plan.PlanId, default));
        Assert.Null(await repository.GetAsync("other-tenant", manifest.Plan.PlanId, default));
        var page = await repository.GetPageAsync(context.TenantId, manifest.Plan.PlanId, 1, 10, default);
        Assert.Equal(
            manifest.Items.Select(item => (
                item.ItemId, item.ItemOrdinal, item.MatchEvidence.Score,
                item.MatchEvidence.MatchType, item.RedactedBefore.Json,
                item.RedactedDesired.Json, item.DesiredPayloadSha256.Value)),
            page.Items.Select(item => (
                item.ItemId, item.ItemOrdinal, item.MatchEvidence.Score,
                item.MatchEvidence.MatchType, item.RedactedBefore.Json,
                item.RedactedDesired.Json, item.DesiredPayloadSha256.Value)));
        Assert.Equal(
            manifest.Items.SelectMany(item => item.MatchEvidence.Reasons),
            page.Items.SelectMany(item => item.MatchEvidence.Reasons));
        Assert.Equal(
            manifest.Items.SelectMany(item => item.FieldDiffs)
                .Select(diff => (
                    diff.Field, diff.Before.Json, diff.Desired.Json,
                    diff.BeforeSha256, diff.DesiredSha256, diff.Sensitive)),
            page.Items.SelectMany(item => item.FieldDiffs)
                .Select(diff => (
                    diff.Field, diff.Before.Json, diff.Desired.Json,
                    diff.BeforeSha256, diff.DesiredSha256, diff.Sensitive)));
        Assert.Equal(
            manifest.Items.Select(item => item.ResolvedTargetParent),
            page.Items.Select(item => item.ResolvedTargetParent));
        Assert.Equal(2, page.TotalItems);

        var badContext = await SeedControlContextAsync("rollback");
        var badManifest = Manifest(badContext, itemCount: 2, reason: "bad\0reason");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            repository.InsertAsync(badContext.TenantId, badManifest, default));
        Assert.Null(await repository.GetAsync(badContext.TenantId, badManifest.Plan.PlanId, default));
    }

    [Fact]
    public async Task Durable_plan_creation_claim_serializes_and_binds_the_exact_request()
    {
        var plans = new PostgresDurableSyncPlanRepository(Database);
        const string tenantId = "claim-tenant";
        var planId = Guid.NewGuid();
        var firstRequest = new EntitySyncSha256(new string('a', 64));
        var changedRequest = new EntitySyncSha256(new string('b', 64));
        var leaseDuration = TimeSpan.FromMinutes(5);
        var firstOwner = Guid.NewGuid();
        var first = await plans.TryClaimCreationAsync(
            tenantId,
            planId,
            firstRequest,
            firstOwner,
            leaseDuration,
            default);
        Assert.Equal(DurablePlanCreationClaimState.Owner, first.State);

        var retryOwner = Guid.NewGuid();
        var waiting = await plans.TryClaimCreationAsync(
            tenantId,
            planId,
            firstRequest,
            retryOwner,
            leaseDuration,
            default);
        Assert.Equal(DurablePlanCreationClaimState.Waiting, waiting.State);

        await plans.ReleaseCreationAsync(
            tenantId, planId, firstRequest, firstOwner, default);
        var retry = await plans.TryClaimCreationAsync(
            tenantId,
            planId,
            firstRequest,
            retryOwner,
            leaseDuration,
            default);
        Assert.Equal(DurablePlanCreationClaimState.Owner, retry.State);

        var conflict = await plans.TryClaimCreationAsync(
            tenantId,
            planId,
            changedRequest,
            Guid.NewGuid(),
            leaseDuration,
            default);
        Assert.Equal(DurablePlanCreationClaimState.Conflict, conflict.State);
    }


    [Fact]
    public async Task Postgres_composed_creation_completes_and_concurrent_retry_plans_once()
    {
        var context = await SeedControlContextAsync("composed-create");
        var poolOptions = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("DATABASE_URL"))
        {
            Database = databaseName,
            Pooling = true,
            MaxPoolSize = 2
        };
        await using var tinyPool = NpgsqlDataSource.Create(poolOptions.ConnectionString);
        var sourceAdapter = new BlockingReadAdapter(
            "NetSuite",
            [new ExternalEntity
            {
                Vendor = "NetSuite",
                EntityType = "Customer",
                Id = "SOURCE-1",
                Name = "Source"
            }]);
        var runtime = new TestRuntimeFactory(
            context.Source,
            sourceAdapter,
            context.Target,
            new BlockingReadAdapter("HaloPSA", []));
        var mapper = new DefaultEntityMapper();
        var exclusions = new PostgresEntityExclusionRepository(tinyPool);
        var plans = new PostgresDurableSyncPlanRepository(tinyPool);
        var planner = new EntitySyncPlanner(
            runtime,
            new TestEntitySyncPlanRepository(),
            exclusions,
            new WeightedEntityMatcher(),
            mapper,
            new InMemoryEntitySyncChangeStateRepository());
        var service = new DurablePlanService(
            planner,
            new PlanManifestBuilder(mapper),
            new PostgresSyncPolicyRepository(tinyPool),
            new PostgresConnectionDefinitionRepository(tinyPool),
            runtime,
            exclusions,
            plans,
            TimeProvider.System,
            new DurablePlanCreationOptions(
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(10)));
        var request = new CreateDurablePlanRequest
        {
            TenantId = context.TenantId,
            IdempotencyKey = "composed-key",
            PolicyId = context.Policy.PolicyId,
            PolicyVersion = context.Policy.Version,
            PlanLifetime = TimeSpan.FromHours(1)
        };
        sourceAdapter.BlockNextRead();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = service.CreatePlanAsync(
            request, new EntitySyncActor("planner"), timeout.Token);
        var readStarted = sourceAdapter.WaitForReadAsync();
        if (await Task.WhenAny(readStarted, first) == first)
            await first;
        await readStarted.WaitAsync(timeout.Token);
        var retries = Enumerable.Range(0, 8)
            .Select(_ => service.CreatePlanAsync(
                request, new EntitySyncActor("planner"), timeout.Token))
            .ToArray();
        using var canceledWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreatePlanAsync(
                request,
                new EntitySyncActor("planner"),
                canceledWait.Token));
        await Task.Delay(400, timeout.Token);
        sourceAdapter.ReleaseRead();

        var results = await Task.WhenAll([first, .. retries]).WaitAsync(timeout.Token);

        Assert.All(results, result => Assert.Equal(results[0].PlanId, result.PlanId));
        Assert.All(results, result => Assert.Equal(results[0].Digest, result.Digest));
        Assert.Equal(1, sourceAdapter.GetEntitiesCalls);
        Assert.Equal(
            results[0].Digest,
            (await plans.GetAsync(
                context.TenantId,
                results[0].PlanId,
                timeout.Token))!.PlanDigestSha256.Value);
    }
    [Fact]
    public async Task Expired_creation_claim_is_reclaimed_with_a_new_fencing_token()
    {
        var context = await SeedControlContextAsync("expired-claim");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var planId = Guid.NewGuid();
        var requestSha256 = new EntitySyncSha256(new string('c', 64));
        var firstOwner = Guid.NewGuid();
        Assert.Equal(
            DurablePlanCreationClaimState.Owner,
            (await plans.TryClaimCreationAsync(
                context.TenantId,
                planId,
                requestSha256,
                firstOwner,
                TimeSpan.FromMilliseconds(75),
                default)).State);
        await Task.Delay(125);

        var replacementOwner = Guid.NewGuid();
        var replacement = await plans.TryClaimCreationAsync(
            context.TenantId,
            planId,
            requestSha256,
            replacementOwner,
            TimeSpan.FromMinutes(5),
            default);

        Assert.Equal(DurablePlanCreationClaimState.Owner, replacement.State);
        Assert.Equal(replacementOwner, replacement.OwnerToken);
        var manifest = Manifest(context, itemCount: 1, planId: planId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plans.InsertClaimedAsync(
                context.TenantId,
                manifest,
                requestSha256,
                firstOwner,
                default));
        await plans.InsertClaimedAsync(
            context.TenantId,
            manifest,
            requestSha256,
            replacementOwner,
            default);
        var completed = await plans.TryClaimCreationAsync(
            context.TenantId,
            planId,
            requestSha256,
            Guid.NewGuid(),
            TimeSpan.FromMinutes(5),
            default);
        Assert.Equal(DurablePlanCreationClaimState.Completed, completed.State);
        Assert.Equal(manifest.Plan.PlanDigestSha256, completed.ResultPlanDigestSha256);
    }

    [Fact]
    public async Task Direct_plan_at_deterministic_identity_cannot_complete_an_unbound_claim()
    {
        var context = await SeedControlContextAsync("creation-mismatch");
        var actor = new EntitySyncActor("planner");
        var request = RecoveryRequest(context, "mismatch-key");
        var planId = DurablePlanId(context.TenantId, request.IdempotencyKey);
        var requestSha256 = DurableCreateRequestDigest(request, actor);
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var owner = Guid.NewGuid();
        Assert.Equal(
            DurablePlanCreationClaimState.Owner,
            (await plans.TryClaimCreationAsync(
                context.TenantId,
                planId,
                requestSha256,
                owner,
                TimeSpan.FromMinutes(5),
                default)).State);
        await plans.InsertAsync(
            context.TenantId,
            Manifest(context, itemCount: 1, planId: planId),
            default);
        await plans.ReleaseCreationAsync(
            context.TenantId, planId, requestSha256, owner, default);
        var sourceAdapter = new BlockingReadAdapter("NetSuite", []);
        var service = RecoveryService(context, plans, sourceAdapter);

        await Assert.ThrowsAsync<DurablePlanCreationConflictException>(() =>
            service.CreatePlanAsync(request, actor, default));

        Assert.Equal(0, sourceAdapter.GetEntitiesCalls);
    }

    [Fact]
    public async Task Atomic_claimed_insert_recovers_after_owner_loses_the_response()
    {
        var context = await SeedControlContextAsync("creation-recovery");
        var actor = new EntitySyncActor("planner");
        var request = RecoveryRequest(context, "recovery-key");
        var planId = DurablePlanId(context.TenantId, request.IdempotencyKey);
        var requestSha256 = DurableCreateRequestDigest(request, actor);
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var owner = Guid.NewGuid();
        Assert.Equal(
            DurablePlanCreationClaimState.Owner,
            (await plans.TryClaimCreationAsync(
                context.TenantId,
                planId,
                requestSha256,
                owner,
                TimeSpan.FromMinutes(5),
                default)).State);
        var manifest = Manifest(context, itemCount: 1, planId: planId);
        await plans.InsertClaimedAsync(
            context.TenantId,
            manifest,
            requestSha256,
            owner,
            default);
        var sourceAdapter = new BlockingReadAdapter("NetSuite", []);
        var service = RecoveryService(context, plans, sourceAdapter);

        var recovered = await service.CreatePlanAsync(request, actor, default);

        Assert.Equal(planId, recovered.PlanId);
        Assert.Equal(manifest.Plan.PlanDigestSha256.Value, recovered.Digest);
        Assert.Equal(0, sourceAdapter.GetEntitiesCalls);
        var completed = await plans.TryClaimCreationAsync(
            context.TenantId,
            planId,
            requestSha256,
            Guid.NewGuid(),
            TimeSpan.FromMinutes(5),
            default);
        Assert.Equal(DurablePlanCreationClaimState.Completed, completed.State);
        Assert.Equal(planId, completed.ResultPlanId);
        Assert.Equal(manifest.Plan.PlanDigestSha256, completed.ResultPlanDigestSha256);
    }

    [Fact]
    public async Task Exclusion_change_wins_a_route_race_with_plan_creation_atomically()
    {
        var context = await SeedControlContextAsync("exclusion-create-race");
        var route = Route(context);
        var exclusions = new PostgresEntityExclusionRepository(Database);
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        var (lockConnection, lockTransaction) = await AcquireRouteLockAsync(route);
        await using (lockConnection)
        await using (lockTransaction)
        {
            var add = exclusions.AddAsync(
                route, "SOURCE-0", "Source", "operator exclusion", "operator", default);
            await AssertStillRunningAsync(add);
            var insert = plans.InsertAsync(context.TenantId, manifest, default);
            await AssertStillRunningAsync(insert);

            await lockTransaction.CommitAsync();
            await add;
            await Assert.ThrowsAnyAsync<Exception>(() => insert);
        }

        Assert.Null(await plans.GetAsync(
            context.TenantId, manifest.Plan.PlanId, default));
        Assert.Single(await exclusions.ListActiveAsync(route, default));
    }

    [Fact]
    public async Task Exclusion_change_wins_a_route_race_with_approval_and_rolls_back_audit()
    {
        var context = await SeedControlContextAsync("exclusion-approval-race");
        var route = Route(context);
        var exclusions = new PostgresEntityExclusionRepository(Database);
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var inspectionId = Guid.NewGuid();
        var now = context.Now.AddMinutes(1);
        await plans.GetOrOpenInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now, default);
        var approvalId = Guid.NewGuid();
        var (lockConnection, lockTransaction) = await AcquireRouteLockAsync(route);
        await using (lockConnection)
        await using (lockTransaction)
        {
            var add = exclusions.AddAsync(
                route, "SOURCE-0", "Source", "operator exclusion", "operator", default);
            await AssertStillRunningAsync(add);
            var approve = plans.ApproveInspectionAsync(
                context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
                context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
                now.AddMinutes(1), now.AddMinutes(10),
                ApprovalAudit(
                    context.TenantId,
                    manifest.Plan.PlanId,
                    approvalId,
                    "reviewer",
                    now.AddMinutes(1)),
                default);
            await AssertStillRunningAsync(approve);

            await lockTransaction.CommitAsync();
            await add;
            await Assert.ThrowsAnyAsync<Exception>(() => approve);
        }

        Assert.Equal(
            EntitySyncDurablePlanStatus.Draft,
            (await plans.GetAsync(
                context.TenantId, manifest.Plan.PlanId, default))!.Status);
        var audit = await new PostgresSyncAuditRepository(Database).ListAsync(
            context.TenantId, null, null, 10, default);
        Assert.DoesNotContain(
            audit.Events,
            auditEvent => auditEvent.CorrelationId == approvalId.ToString("N"));

        Assert.True(await exclusions.RevokeAsync(
            route, "SOURCE-0", "operator", default));
        var successfulApprovalId = Guid.NewGuid();
        await plans.ApproveInspectionAsync(
            context.TenantId, successfulApprovalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
            now.AddMinutes(2), now.AddMinutes(10),
            ApprovalAudit(
                context.TenantId,
                manifest.Plan.PlanId,
                successfulApprovalId,
                "reviewer",
                now.AddMinutes(2)),
            default);
        await Assert.ThrowsAnyAsync<Exception>(() => exclusions.AddAsync(
            route, "SOURCE-0", "Source", "late exclusion", "operator", default));
        Assert.Equal(
            EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(
                context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Inspection_completion_requires_exact_nonoverlapping_range_coverage()
    {
        var context = await SeedControlContextAsync("inspection-nonoverlap");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 684);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var inspectionId = Guid.NewGuid();
        var now = context.Now.AddMinutes(1);
        await plans.GetOrOpenInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"), now, default);

        foreach (var range in new[] { (0, 99), (100, 149), (150, 399), (400, 683) })
        {
            await plans.RecordInspectionRangeAsync(
                context.TenantId,
                inspectionId,
                Guid.NewGuid(),
                range.Item1,
                range.Item2,
                now,
                default);
        }

        var completed = await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now, default);
        Assert.Equal(EntitySyncInspectionStatus.Completed, completed.Status);
        Assert.True(await plans.HasCompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, default));
    }

    [Fact]
    public async Task Large_manifest_copy_round_trips_every_item_atomically()
    {
        var context = await SeedControlContextAsync("large-manifest");
        var repository = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 2_500);

        await repository.InsertAsync(context.TenantId, manifest, default);

        var finalPage = await repository.GetPageAsync(
            context.TenantId, manifest.Plan.PlanId, 25, 100, default);
        Assert.Equal(2_500, finalPage.TotalItems);
        Assert.Equal(100, finalPage.Items.Count);
        Assert.Equal(2_400, finalPage.Items[0].ItemOrdinal);
        Assert.Equal(2_499, finalPage.Items[^1].ItemOrdinal);
    }

    [Fact]
    public async Task Inspection_requires_exact_coverage_and_approval_is_single_use_and_expiring()
    {
        var context = await SeedControlContextAsync("approval");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 2);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var inspectionId = Guid.NewGuid();
        var now = context.Now.AddMinutes(1);
        await plans.GetOrOpenInspectionAsync(context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
        context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
        new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await Assert.ThrowsAnyAsync<Exception>(() => plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            now.AddMinutes(1), default));
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 1, 1, now, default);
        var completed = await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            now.AddMinutes(1), default);
        Assert.Equal(EntitySyncInspectionStatus.Completed, completed.Status);
        Assert.True(await plans.HasCompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, default));

        var approvalId = Guid.NewGuid();
        var approval = await plans.ApproveInspectionAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
            now.AddMinutes(2), now.AddMinutes(10),
            ApprovalAudit(context.TenantId, manifest.Plan.PlanId, approvalId, "reviewer", now.AddMinutes(2)),
            default);
        Assert.Equal(approvalId, approval.ApprovalId);
        Assert.Equal(EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);
        var approvalAudit = await new PostgresSyncAuditRepository(Database).ListAsync(
            context.TenantId, null, null, 10, default);
        var approvedEvent = Assert.Single(
            approvalAudit.Events,
            auditEvent => auditEvent.CorrelationId == approvalId.ToString("N"));
        Assert.Equal("SyncPlanApproved", approvedEvent.EventType);
        Assert.Equal(manifest.Plan.PlanId, approvedEvent.PlanId);


        var operation = EntitySyncOperation.QueueApply(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), approvalId, "apply-once", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, now.AddMinutes(3));
        var operationItems = OperationItems(operation, manifest.Items, now.AddDays(1));
        var operationTwo = EntitySyncOperation.QueueApply(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), approvalId, "apply-twice", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, now.AddMinutes(3));
        var attempts = await Task.WhenAll(
            plans.TryConsumeApprovalAsync(
                context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
                context.Target.ConnectionId, 1, operation, operationItems,
                now.AddMinutes(3), default),
            plans.TryConsumeApprovalAsync(
                context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
                context.Target.ConnectionId, 1, operationTwo,
                OperationItems(operationTwo, manifest.Items, now.AddDays(1)),
                now.AddMinutes(3), default));
        Assert.Single(attempts, value => value);
        Assert.Equal(EntitySyncDurablePlanStatus.Consumed,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Approval_and_audit_commit_or_roll_back_together()
    {
        var context = await SeedControlContextAsync("approval-audit-atomic");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var audits = new PostgresSyncAuditRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var now = context.Now.AddMinutes(1);
        var inspectionId = Guid.NewGuid();
        await plans.GetOrOpenInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(1), default);

        var conflictingApprovalId = Guid.NewGuid();
        var conflictingAudit = ApprovalAudit(
            context.TenantId,
            manifest.Plan.PlanId,
            conflictingApprovalId,
            "reviewer",
            now.AddMinutes(2));
        await audits.AppendAsync(context.TenantId, conflictingAudit, null, default);
        await Assert.ThrowsAsync<PostgresException>(() =>
            plans.ApproveInspectionAsync(
                context.TenantId, conflictingApprovalId, inspectionId,
                manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
                context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
                new EntitySyncActor("reviewer"), now.AddMinutes(2),
                now.AddMinutes(10), conflictingAudit, default));
        Assert.Equal(
            EntitySyncDurablePlanStatus.Draft,
            (await plans.GetAsync(
                context.TenantId, manifest.Plan.PlanId, default))!.Status);

        var successfulApprovalId = Guid.NewGuid();
        var successfulAudit = ApprovalAudit(
            context.TenantId,
            manifest.Plan.PlanId,
            successfulApprovalId,
            "reviewer",
            now.AddMinutes(3));
        var approval = await plans.ApproveInspectionAsync(
            context.TenantId, successfulApprovalId, inspectionId,
            manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            new EntitySyncActor("reviewer"), now.AddMinutes(3),
            now.AddMinutes(10), successfulAudit, default);
        Assert.Equal(successfulApprovalId, approval.ApprovalId);
        Assert.Equal(
            EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(
                context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Expired_approval_cannot_be_consumed()
    {
        var context = await SeedControlContextAsync("expired-approval");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var now = context.Now.AddMinutes(-10);
        var inspectionId = Guid.NewGuid();
        await plans.GetOrOpenInspectionAsync(context.TenantId, inspectionId, manifest.Plan.PlanId,
        manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
        context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"), now, default);
        await plans.RecordInspectionRangeAsync(
            context.TenantId, inspectionId, Guid.NewGuid(), 0, 0, now, default);
        await plans.CompleteInspectionAsync(
            context.TenantId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, now.AddMinutes(1), default);
        var approvalId = Guid.NewGuid();
        await plans.ApproveInspectionAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
            now.AddMinutes(2), now.AddMinutes(3),
            ApprovalAudit(context.TenantId, manifest.Plan.PlanId, approvalId, "reviewer", now.AddMinutes(2)),
            default);
        var operation = EntitySyncOperation.QueueApply(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), approvalId, "expired-apply", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, now.AddMinutes(4));

        Assert.False(await plans.TryConsumeApprovalAsync(
            context.TenantId, approvalId, inspectionId, manifest.Plan.PlanId,
            manifest.Plan.PlanDigestSha256, context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, operation,
            OperationItems(operation, manifest.Items, now.AddDays(1)),
            now.AddMinutes(4), default));
        Assert.Equal(
            EntitySyncDurablePlanStatus.Approved,
            (await plans.GetAsync(context.TenantId, manifest.Plan.PlanId, default))!.Status);
    }

    [Fact]
    public async Task Connection_generation_changes_fence_inspection_approval_and_consumption()
    {
        var connections = new PostgresConnectionDefinitionRepository(Database);

        var openContext = await SeedControlContextAsync("generation-open");
        var openPlans = new PostgresDurableSyncPlanRepository(Database);
        var openManifest = Manifest(openContext, 1);
        await openPlans.InsertAsync(openContext.TenantId, openManifest, default);
        await BumpAsync(openContext, openContext.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => openPlans.GetOrOpenInspectionAsync(openContext.TenantId, Guid.NewGuid(), openManifest.Plan.PlanId,
        openManifest.Plan.PlanDigestSha256, openContext.Source.ConnectionId, 1,
        openContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        openContext.Now.AddMinutes(1), default));

        var rangeContext = await SeedControlContextAsync("generation-range");
        var rangePlans = new PostgresDurableSyncPlanRepository(Database);
        var rangeManifest = Manifest(rangeContext, 1);
        await rangePlans.InsertAsync(rangeContext.TenantId, rangeManifest, default);
        var rangeInspectionId = Guid.NewGuid();
        await rangePlans.GetOrOpenInspectionAsync(rangeContext.TenantId, rangeInspectionId, rangeManifest.Plan.PlanId,
        rangeManifest.Plan.PlanDigestSha256, rangeContext.Source.ConnectionId, 1,
        rangeContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        rangeContext.Now.AddMinutes(1), default);
        await BumpAsync(rangeContext, rangeContext.Target);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rangePlans.RecordInspectionRangeAsync(
                rangeContext.TenantId, rangeInspectionId, Guid.NewGuid(), 0, 0,
                rangeContext.Now.AddMinutes(2), default));

        var completeContext = await SeedControlContextAsync("generation-complete");
        var completePlans = new PostgresDurableSyncPlanRepository(Database);
        var completeManifest = Manifest(completeContext, 1);
        await completePlans.InsertAsync(completeContext.TenantId, completeManifest, default);
        var completeInspectionId = Guid.NewGuid();
        await completePlans.GetOrOpenInspectionAsync(completeContext.TenantId, completeInspectionId, completeManifest.Plan.PlanId,
        completeManifest.Plan.PlanDigestSha256,
        completeContext.Source.ConnectionId, 1,
        completeContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        completeContext.Now.AddMinutes(1), default);
        await completePlans.RecordInspectionRangeAsync(
            completeContext.TenantId, completeInspectionId, Guid.NewGuid(), 0, 0,
            completeContext.Now.AddMinutes(1), default);
        await BumpAsync(completeContext, completeContext.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            completePlans.CompleteInspectionAsync(
                completeContext.TenantId, completeInspectionId,
                completeManifest.Plan.PlanId, completeManifest.Plan.PlanDigestSha256,
                completeContext.Source.ConnectionId, 1,
                completeContext.Target.ConnectionId, 1,
                completeContext.Now.AddMinutes(2), default));

        var approveContext = await SeedControlContextAsync("generation-approve");
        var approvePlans = new PostgresDurableSyncPlanRepository(Database);
        var approveManifest = Manifest(approveContext, 1);
        await approvePlans.InsertAsync(approveContext.TenantId, approveManifest, default);
        var approveInspectionId = Guid.NewGuid();
        var approveNow = approveContext.Now.AddMinutes(1);
        await approvePlans.GetOrOpenInspectionAsync(approveContext.TenantId, approveInspectionId, approveManifest.Plan.PlanId,
        approveManifest.Plan.PlanDigestSha256, approveContext.Source.ConnectionId, 1,
        approveContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        approveNow, default);
        await approvePlans.RecordInspectionRangeAsync(
            approveContext.TenantId, approveInspectionId, Guid.NewGuid(), 0, 0,
            approveNow, default);
        await approvePlans.CompleteInspectionAsync(
            approveContext.TenantId, approveInspectionId, approveManifest.Plan.PlanId,
            approveManifest.Plan.PlanDigestSha256, approveContext.Source.ConnectionId, 1,
            approveContext.Target.ConnectionId, 1, approveNow.AddMinutes(1), default);
        await BumpAsync(approveContext, approveContext.Source);
        var approveApprovalId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            approvePlans.ApproveInspectionAsync(
                approveContext.TenantId, approveApprovalId, approveInspectionId,
                approveManifest.Plan.PlanId, approveManifest.Plan.PlanDigestSha256,
                approveContext.Source.ConnectionId, 1, approveContext.Target.ConnectionId, 1,
                new EntitySyncActor("reviewer"), approveNow.AddMinutes(2),
                approveNow.AddMinutes(10),
                ApprovalAudit(
                    approveContext.TenantId,
                    approveManifest.Plan.PlanId,
                    approveApprovalId,
                    "reviewer",
                    approveNow.AddMinutes(2)),
                default));
        Assert.Equal(EntitySyncDurablePlanStatus.Draft,
            (await approvePlans.GetAsync(
                approveContext.TenantId, approveManifest.Plan.PlanId, default))!.Status);

        var consumeContext = await SeedControlContextAsync("generation-consume");
        var consumePlans = new PostgresDurableSyncPlanRepository(Database);
        var consumeManifest = Manifest(consumeContext, 1);
        await consumePlans.InsertAsync(consumeContext.TenantId, consumeManifest, default);
        var consumeInspectionId = Guid.NewGuid();
        var consumeNow = consumeContext.Now.AddMinutes(1);
        await consumePlans.GetOrOpenInspectionAsync(consumeContext.TenantId, consumeInspectionId, consumeManifest.Plan.PlanId,
        consumeManifest.Plan.PlanDigestSha256, consumeContext.Source.ConnectionId, 1,
        consumeContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        consumeNow, default);
        await consumePlans.RecordInspectionRangeAsync(
            consumeContext.TenantId, consumeInspectionId, Guid.NewGuid(), 0, 0,
            consumeNow, default);
        await consumePlans.CompleteInspectionAsync(
            consumeContext.TenantId, consumeInspectionId, consumeManifest.Plan.PlanId,
            consumeManifest.Plan.PlanDigestSha256, consumeContext.Source.ConnectionId, 1,
            consumeContext.Target.ConnectionId, 1, consumeNow.AddMinutes(1), default);
        var consumeApprovalId = Guid.NewGuid();
        await consumePlans.ApproveInspectionAsync(
            consumeContext.TenantId, consumeApprovalId, consumeInspectionId,
            consumeManifest.Plan.PlanId, consumeManifest.Plan.PlanDigestSha256,
            consumeContext.Source.ConnectionId, 1, consumeContext.Target.ConnectionId, 1,
            new EntitySyncActor("reviewer"), consumeNow.AddMinutes(2),
            consumeNow.AddMinutes(10),
            ApprovalAudit(
                consumeContext.TenantId,
                consumeManifest.Plan.PlanId,
                consumeApprovalId,
                "reviewer",
                consumeNow.AddMinutes(2)),
            default);
        await BumpAsync(consumeContext, consumeContext.Target);
        var apply = EntitySyncOperation.QueueApply(consumeContext.TenantId, Guid.NewGuid(), consumeManifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), consumeApprovalId, "generation-apply", "route-a", consumeContext.Source.ConnectionId, 1, consumeContext.Target.ConnectionId, 1, consumeNow.AddMinutes(3));
        Assert.False(await consumePlans.TryConsumeApprovalAsync(
            consumeContext.TenantId, consumeApprovalId, consumeInspectionId,
            consumeManifest.Plan.PlanId, consumeManifest.Plan.PlanDigestSha256,
            consumeContext.Source.ConnectionId, 1, consumeContext.Target.ConnectionId, 1,
            apply, OperationItems(apply, consumeManifest.Items, consumeNow.AddDays(1)),
            consumeNow.AddMinutes(3), default));

        async Task BumpAsync(
            ControlContext context,
            EntitySyncConnectionDefinition definition)
        {
            var next = definition.NextGeneration(
                definition.DisplayName, definition.Enabled, definition.PublicConfiguration,
                definition.SecretCiphertext, new EntitySyncActor("rotator"),
                context.Now.AddSeconds(1));
            Assert.NotNull(await connections.TryReplaceAsync(
                context.TenantId, definition.ConnectionId, 1, next, default));
        }
    }

    [Fact]
    public async Task Concurrent_connection_rotation_blocks_then_fences_inspection_and_consumption()
    {
        var completionContext = await SeedControlContextAsync("rotation-completion");
        var completionPlans = new PostgresDurableSyncPlanRepository(Database);
        var completionManifest = Manifest(completionContext, 1);
        await completionPlans.InsertAsync(
            completionContext.TenantId, completionManifest, default);
        var completionInspectionId = Guid.NewGuid();
        await completionPlans.GetOrOpenInspectionAsync(completionContext.TenantId, completionInspectionId,
        completionManifest.Plan.PlanId, completionManifest.Plan.PlanDigestSha256,
        completionContext.Source.ConnectionId, 1,
        completionContext.Target.ConnectionId, 1,
        new EntitySyncActor("reviewer"), completionContext.Now.AddMinutes(1), default);
        await completionPlans.RecordInspectionRangeAsync(
            completionContext.TenantId, completionInspectionId, Guid.NewGuid(), 0, 0,
            completionContext.Now.AddMinutes(1), default);
        await using (var rotationConnection =
            await Database.OpenConnectionAsync(default))
        await using (var rotationTransaction =
            await rotationConnection.BeginTransactionAsync(default))
        {
            await RotateAsync(
                rotationConnection, rotationTransaction, completionContext,
                completionContext.Source);
            var completionTask = completionPlans.CompleteInspectionAsync(
                completionContext.TenantId, completionInspectionId,
                completionManifest.Plan.PlanId,
                completionManifest.Plan.PlanDigestSha256,
                completionContext.Source.ConnectionId, 1,
                completionContext.Target.ConnectionId, 1,
                completionContext.Now.AddMinutes(2), default);
            await AssertStillRunningAsync(completionTask);
            await rotationTransaction.CommitAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await completionTask);
        }
        Assert.False(await completionPlans.HasCompleteInspectionAsync(
            completionContext.TenantId, completionInspectionId,
            completionManifest.Plan.PlanId, completionManifest.Plan.PlanDigestSha256,
            completionContext.Source.ConnectionId, 1,
            completionContext.Target.ConnectionId, 1, default));

        var consumeContext = await SeedControlContextAsync("rotation-consume");
        var consumePlans = new PostgresDurableSyncPlanRepository(Database);
        var consumeOperations = new PostgresSyncOperationRepository(Database);
        var consumeManifest = Manifest(consumeContext, 1);
        await consumePlans.InsertAsync(consumeContext.TenantId, consumeManifest, default);
        var consumeInspectionId = Guid.NewGuid();
        await consumePlans.GetOrOpenInspectionAsync(consumeContext.TenantId, consumeInspectionId, consumeManifest.Plan.PlanId,
        consumeManifest.Plan.PlanDigestSha256,
        consumeContext.Source.ConnectionId, 1,
        consumeContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
        consumeContext.Now.AddMinutes(1), default);
        await consumePlans.RecordInspectionRangeAsync(
            consumeContext.TenantId, consumeInspectionId, Guid.NewGuid(), 0, 0,
            consumeContext.Now.AddMinutes(1), default);
        await consumePlans.CompleteInspectionAsync(
            consumeContext.TenantId, consumeInspectionId, consumeManifest.Plan.PlanId,
            consumeManifest.Plan.PlanDigestSha256,
            consumeContext.Source.ConnectionId, 1,
            consumeContext.Target.ConnectionId, 1,
            consumeContext.Now.AddMinutes(2), default);
        var consumeApprovalId = Guid.NewGuid();
        await consumePlans.ApproveInspectionAsync(
            consumeContext.TenantId, consumeApprovalId, consumeInspectionId,
            consumeManifest.Plan.PlanId, consumeManifest.Plan.PlanDigestSha256,
            consumeContext.Source.ConnectionId, 1,
            consumeContext.Target.ConnectionId, 1, new EntitySyncActor("reviewer"),
            consumeContext.Now.AddMinutes(3), consumeContext.Now.AddMinutes(20),
            ApprovalAudit(
                consumeContext.TenantId,
                consumeManifest.Plan.PlanId,
                consumeApprovalId,
                "reviewer",
                consumeContext.Now.AddMinutes(3)),
            default);
        var apply = EntitySyncOperation.QueueApply(consumeContext.TenantId, Guid.NewGuid(), consumeManifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), consumeApprovalId, "rotation-consume", "route-a", consumeContext.Source.ConnectionId, 1, consumeContext.Target.ConnectionId, 1, consumeContext.Now.AddMinutes(4));
        await using (var rotationConnection =
            await Database.OpenConnectionAsync(default))
        await using (var rotationTransaction =
            await rotationConnection.BeginTransactionAsync(default))
        {
            await RotateAsync(
                rotationConnection, rotationTransaction, consumeContext,
                consumeContext.Target);
            var consumeTask = consumePlans.TryConsumeApprovalAsync(
                consumeContext.TenantId, consumeApprovalId, consumeInspectionId,
                consumeManifest.Plan.PlanId, consumeManifest.Plan.PlanDigestSha256,
                consumeContext.Source.ConnectionId, 1,
                consumeContext.Target.ConnectionId, 1, apply,
                OperationItems(
                    apply, consumeManifest.Items, consumeContext.Now.AddDays(1)),
                consumeContext.Now.AddMinutes(4), default);
            await AssertStillRunningAsync(consumeTask);
            await rotationTransaction.CommitAsync();
            Assert.False(await consumeTask);
        }
        Assert.Equal(
            EntitySyncDurablePlanStatus.Approved,
            (await consumePlans.GetAsync(
                consumeContext.TenantId, consumeManifest.Plan.PlanId, default))!.Status);
        Assert.Null(await consumeOperations.GetAsync(
            consumeContext.TenantId, apply.OperationId, default));

        static async Task RotateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            ControlContext context,
            EntitySyncConnectionDefinition definition)
        {
            const string sql = """
                UPDATE entitysync.connection_definitions
                SET generation = generation + 1
                WHERE tenant_id = @tenant_id AND connection_id = @connection_id
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("tenant_id", context.TenantId);
            command.Parameters.AddWithValue("connection_id", definition.ConnectionId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task Sparse_authoritative_plan_ordinal_persists_exactly_and_wrong_ordinal_reaches_database_guard()
    {
        var context = await SeedControlContextAsync("sparse-operation-ordinal");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var operations = new PostgresSyncOperationRepository(Database);
        var manifest = Manifest(context, itemCount: 6);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var planItem = manifest.Items[5];

        var correctOperation = EntitySyncOperation.QueueDryRun(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId,
            Guid.NewGuid(), Guid.NewGuid(), "sparse-correct", "route-a",
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            context.Now.AddMinutes(1));
        var correctItem = Assert.Single(OperationItems(
            correctOperation, [planItem], context.Now.AddDays(1)));
        Assert.Equal(5, correctItem.ItemIndex);
        await operations.InsertAsync(
            context.TenantId, correctOperation, [correctItem], default);
        var restarted = new PostgresSyncOperationRepository(Database);
        var reloadedOperation = await restarted.GetAsync(
            context.TenantId, correctOperation.OperationId, default);
        Assert.NotNull(reloadedOperation);
        var reloadedItem = Assert.Single(await restarted.GetItemsAsync(
            context.TenantId, correctOperation.OperationId, default));
        Assert.Equal(5, reloadedItem.ItemIndex);
        Assert.Equal(
            5,
            EntitySyncOperationWorker.CreateCorrelation(
                reloadedOperation!,
                reloadedItem).ItemIndex);

        var wrongOperation = EntitySyncOperation.QueueDryRun(
            context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId,
            Guid.NewGuid(), Guid.NewGuid(), "sparse-wrong", "route-a",
            context.Source.ConnectionId, 1, context.Target.ConnectionId, 1,
            context.Now.AddMinutes(2));
        var expected = Assert.Single(OperationItems(
            wrongOperation, [planItem], context.Now.AddDays(1)));
        var wrongItem = EntitySyncOperationItem.Rehydrate(
            expected.TenantId, expected.OperationId, expected.PlanId, expected.ItemId, 4,
            expected.SourceVendor, expected.SourceConnectionId, expected.SourceEntityType,
            expected.SourceEntityKey, expected.SourceEntityId, expected.TargetVendor,
            expected.TargetConnectionId, expected.TargetEntityType, expected.TargetEntityId,
            expected.Action, expected.RedactedBefore, expected.RedactedDesired,
            expected.BeforePayloadSha256, expected.DesiredPayloadSha256,
            expected.AfterPayloadSha256, expected.SnapshotsExpireAt,
            expected.VendorRequestId, expected.Outcome, expected.ErrorCode,
            expected.ErrorMessage, expected.StartedAt, expected.CompletedAt,
            expected.ResolvedTargetParent);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            operations.InsertAsync(context.TenantId, wrongOperation, [wrongItem], default));
        Assert.Equal("55000", error.SqlState);
        Assert.Null(await operations.GetAsync(
            context.TenantId, wrongOperation.OperationId, default));
    }

    [Fact]
    public async Task Configured_lease_reclaims_after_database_expiry_across_worker_reconstruction()
    {
        var context = await SeedControlContextAsync("lease");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var operations = new PostgresSyncOperationRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var operation = EntitySyncOperation.QueueDryRun(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), "dry-run", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, context.Now.AddMinutes(1));
        var items = OperationItems(operation, manifest.Items, context.Now.AddDays(1));
        await operations.InsertAsync(context.TenantId, operation, items, default);

        var configuredLease = TimeSpan.FromSeconds(30);
        var now = context.Now.AddMinutes(2);
        var firstRace = await Task.WhenAll(
            operations.TryLeaseNextAsync(
                context.TenantId, "worker-a", now, now + configuredLease, default),
            operations.TryLeaseNextAsync(
                context.TenantId, "worker-b", now, now + configuredLease, default));
        Assert.Single(firstRace, value => value is not null);
        var firstLease = firstRace.Single(value => value is not null)!;
        await using (var expireLease = Database.CreateCommand("""
            UPDATE entitysync.sync_operations
            SET lease_expires_at = clock_timestamp() - interval '1 second'
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
            """))
        {
            expireLease.Parameters.AddWithValue("tenant_id", context.TenantId);
            expireLease.Parameters.AddWithValue("operation_id", operation.OperationId);
            Assert.Equal(1, await expireLease.ExecuteNonQueryAsync());
        }
        var restartedOperations = new PostgresSyncOperationRepository(Database);
        var reclaimed = await restartedOperations.TryLeaseNextAsync(
            context.TenantId,
            "worker-after-restart",
            now.AddMinutes(2),
            now.AddMinutes(2) + configuredLease,
            default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed.Attempt);
        Assert.Equal("worker-after-restart", reclaimed.LeaseOwner);

        var completed = EntitySyncOperationItem.Rehydrate(
            items[0].TenantId, items[0].OperationId, items[0].PlanId, items[0].ItemId,
            items[0].ItemIndex, items[0].SourceVendor, items[0].SourceConnectionId,
            items[0].SourceEntityType,
            items[0].SourceEntityKey, items[0].SourceEntityId, items[0].TargetVendor,
            items[0].TargetConnectionId, items[0].TargetEntityType, items[0].TargetEntityId,
            items[0].Action, items[0].RedactedBefore, items[0].RedactedDesired,
            items[0].BeforePayloadSha256, items[0].DesiredPayloadSha256,
            new EntitySyncSha256(new string('c', 64)), items[0].SnapshotsExpireAt,
            "request-1", EntitySyncItemOutcome.Succeeded, null, null,
            now.AddMinutes(2), now.AddMinutes(2));
        Assert.False(await operations.TryReplaceItemAsync(
            context.TenantId, operation.OperationId, operation.PlanId, items[0].ItemId,
            firstLease.Attempt, firstLease.LeaseOwner!, now.AddMinutes(2),
            EntitySyncItemOutcome.Pending, completed, default));
        Assert.True(await operations.TryReplaceItemAsync(
            context.TenantId, operation.OperationId, operation.PlanId, items[0].ItemId,
            reclaimed.Attempt, reclaimed.LeaseOwner!, now.AddMinutes(2),
            EntitySyncItemOutcome.Pending, completed, default));
    }

    [Fact]
    public async Task Concurrent_reclaim_and_cancel_block_then_fence_item_outcomes()
    {
        var reclaim = await CreateRunningAsync("item-reclaim-race");
        await using (var reclaimConnection = await Database.OpenConnectionAsync(default))
        await using (var reclaimTransaction =
            await reclaimConnection.BeginTransactionAsync(default))
        {
            const string reclaimSql = """
                UPDATE entitysync.sync_operations
                SET status = 'Leased', lease_owner = 'replacement-worker',
                    lease_expires_at = @lease_expires_at, attempt = attempt + 1,
                    started_at = NULL, completed_at = NULL
                WHERE tenant_id = @tenant_id AND operation_id = @operation_id
                """;
            await using var command = new NpgsqlCommand(
                reclaimSql, reclaimConnection, reclaimTransaction);
            command.Parameters.AddWithValue(
                "lease_expires_at", reclaim.Context.Now.AddMinutes(30));
            command.Parameters.AddWithValue("tenant_id", reclaim.Context.TenantId);
            command.Parameters.AddWithValue("operation_id", reclaim.Running.OperationId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            var itemTask = reclaim.Repository.TryReplaceItemAsync(
                reclaim.Context.TenantId, reclaim.Running.OperationId,
                reclaim.Running.PlanId, reclaim.Item.ItemId,
                reclaim.Running.Attempt, reclaim.Running.LeaseOwner!,
                DateTimeOffset.UtcNow, EntitySyncItemOutcome.Pending,
                CompleteItem(
                    reclaim.Item, reclaim.Context.Now.AddMinutes(2),
                    reclaim.Context.Now.AddMinutes(3)),
                default);
            await AssertStillRunningAsync(itemTask);
            await reclaimTransaction.CommitAsync();
            Assert.False(await itemTask);
        }
        Assert.Equal(
            EntitySyncItemOutcome.Pending,
            Assert.Single(await reclaim.Repository.GetItemsAsync(
                reclaim.Context.TenantId, reclaim.Running.OperationId, default)).Outcome);

        var cancel = await CreateRunningAsync("item-cancel-race");
        await using (var cancelConnection = await Database.OpenConnectionAsync(default))
        await using (var cancelTransaction =
            await cancelConnection.BeginTransactionAsync(default))
        {
            const string cancelSql = """
                UPDATE entitysync.sync_operations
                SET status = 'Cancelled', lease_owner = NULL,
                    lease_expires_at = NULL, completed_at = @completed_at
                WHERE tenant_id = @tenant_id AND operation_id = @operation_id
                """;
            await using var command = new NpgsqlCommand(
                cancelSql, cancelConnection, cancelTransaction);
            command.Parameters.AddWithValue(
                "completed_at", cancel.Context.Now.AddMinutes(3));
            command.Parameters.AddWithValue("tenant_id", cancel.Context.TenantId);
            command.Parameters.AddWithValue("operation_id", cancel.Running.OperationId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            var itemTask = cancel.Repository.TryReplaceItemAsync(
                cancel.Context.TenantId, cancel.Running.OperationId,
                cancel.Running.PlanId, cancel.Item.ItemId,
                cancel.Running.Attempt, cancel.Running.LeaseOwner!,
                DateTimeOffset.UtcNow, EntitySyncItemOutcome.Pending,
                CompleteItem(
                    cancel.Item, cancel.Context.Now.AddMinutes(2),
                    cancel.Context.Now.AddMinutes(3)),
                default);
            await AssertStillRunningAsync(itemTask);
            await cancelTransaction.CommitAsync();
            Assert.False(await itemTask);
        }
        Assert.Equal(
            EntitySyncItemOutcome.Pending,
            Assert.Single(await cancel.Repository.GetItemsAsync(
                cancel.Context.TenantId, cancel.Running.OperationId, default)).Outcome);

        async Task<(
            ControlContext Context,
            PostgresSyncOperationRepository Repository,
            EntitySyncOperation Running,
            EntitySyncOperationItem Item)> CreateRunningAsync(string suffix)
        {
            var context = await SeedControlContextAsync(suffix);
            var plans = new PostgresDurableSyncPlanRepository(Database);
            var repository = new PostgresSyncOperationRepository(Database);
            var manifest = Manifest(context, 1);
            await plans.InsertAsync(context.TenantId, manifest, default);
            var queued = EntitySyncOperation.QueueDryRun(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), $"{suffix}-operation", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, context.Now);
            var item = Assert.Single(
                OperationItems(queued, manifest.Items, context.Now.AddDays(1)));
            await repository.InsertAsync(context.TenantId, queued, [item], default);
            var leased = await repository.TryLeaseNextAsync(
                context.TenantId, $"{suffix}-worker", context.Now.AddMinutes(1),
                context.Now.AddMinutes(20), default)
                ?? throw new InvalidOperationException("Expected operation lease.");
            var running = leased.Start(context.Now.AddMinutes(2));
            Assert.True(await repository.TryReplaceAsync(
                context.TenantId, running.OperationId,
                EntitySyncOperationStatus.Leased, running, default));
            return (context, repository, running, item);
        }
    }

    [Fact]
    public async Task Operation_graph_and_transitions_enforce_queue_identity_and_terminal_consistency()
    {
        var context = await SeedControlContextAsync("operation-transitions");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var operations = new PostgresSyncOperationRepository(Database);
        var manifest = Manifest(context, 1, withResolvedParent: true);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var now = context.Now.AddMinutes(1);
        var queued = EntitySyncOperation.QueueDryRun(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), "transition-op", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, now);
        var items = OperationItems(queued, manifest.Items, now.AddDays(1));
        var preLeased = queued.Lease("invalid-worker", now.AddMinutes(5));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            operations.InsertAsync(context.TenantId, preLeased, items, default));

        await operations.InsertAsync(context.TenantId, queued, items, default);
        Assert.Equal(
            items[0].ResolvedTargetParent,
            (await new PostgresSyncOperationRepository(Database).GetItemAsync(
                context.TenantId, queued.OperationId, items[0].ItemId, default))!
                .ResolvedTargetParent);
        var illegalTerminal = EntitySyncOperation.Rehydrate(
            queued.TenantId, queued.OperationId, queued.PlanId, queued.RunId,
            queued.CorrelationId, queued.ApprovalId, queued.RouteScope,
            queued.SourceConnectionId, queued.SourceConnectionGeneration,
            queued.TargetConnectionId, queued.TargetConnectionGeneration, queued.Mode,
            EntitySyncOperationStatus.Succeeded, queued.IdempotencyKey, null, null, 0,
            queued.CreatedAt, queued.QueuedAt, null, now.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.TryReplaceAsync(
            context.TenantId, queued.OperationId, EntitySyncOperationStatus.Queued,
            illegalTerminal, default));
        Assert.Equal(
            EntitySyncOperationStatus.Queued,
            (await new PostgresSyncOperationRepository(Database).GetAsync(
                context.TenantId, queued.OperationId, default))!.Status);

        var leased = await operations.TryLeaseNextAsync(
            context.TenantId, "transition-worker", now.AddMinutes(1),
            now.AddMinutes(10), default);
        Assert.NotNull(leased);
        var running = leased.Start(now.AddMinutes(2));
        Assert.True(await operations.TryReplaceAsync(
            context.TenantId, leased.OperationId, EntitySyncOperationStatus.Leased,
            running, default));
        var succeeded = running.Complete(
            EntitySyncOperationStatus.Succeeded, now.AddMinutes(3));
        Assert.False(await operations.TryReplaceAsync(
            context.TenantId, running.OperationId, EntitySyncOperationStatus.Running,
            succeeded, default));

        var completedItem = CompleteItem(items[0], now.AddMinutes(2), now.AddMinutes(3));
        Assert.True(await operations.TryReplaceItemAsync(

            context.TenantId, running.OperationId, running.PlanId, items[0].ItemId,
            running.Attempt, running.LeaseOwner!, now.AddMinutes(3),
            EntitySyncItemOutcome.Pending, completedItem, default));
        Assert.True(await operations.TryReplaceAsync(
            context.TenantId, running.OperationId, EntitySyncOperationStatus.Running,
            succeeded, default));

        var changedIdentity = EntitySyncOperation.Rehydrate(
            succeeded.TenantId, succeeded.OperationId, succeeded.PlanId, succeeded.RunId,
            succeeded.CorrelationId, succeeded.ApprovalId, "changed-route",
            succeeded.SourceConnectionId,
            succeeded.SourceConnectionGeneration, succeeded.TargetConnectionId,
            succeeded.TargetConnectionGeneration, succeeded.Mode, succeeded.Status,
            succeeded.IdempotencyKey, succeeded.LeaseOwner, succeeded.LeaseExpiresAt,
            succeeded.Attempt, succeeded.CreatedAt, succeeded.QueuedAt,
            succeeded.StartedAt, succeeded.CompletedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.TryReplaceAsync(
            context.TenantId, succeeded.OperationId, EntitySyncOperationStatus.Succeeded,
            changedIdentity, default));
    }
    [Fact]
    public async Task Operation_failed_partial_and_cancelled_terminal_paths_match_item_outcomes()
    {
        await RunTerminalAsync(
            "terminal-failed",
            EntitySyncOperationStatus.Failed,
            [EntitySyncItemOutcome.Failed]);
        await RunTerminalAsync(
            "terminal-partial",
            EntitySyncOperationStatus.Partial,
            [EntitySyncItemOutcome.Succeeded, EntitySyncItemOutcome.Failed]);
        await RunTerminalAsync(
            "terminal-mixed-failed",
            EntitySyncOperationStatus.Failed,
            [EntitySyncItemOutcome.Succeeded, EntitySyncItemOutcome.Failed],
            expectedSuccess: false);

        var cancelContext = await SeedControlContextAsync("terminal-cancelled");
        var cancelPlans = new PostgresDurableSyncPlanRepository(Database);
        var cancelOperations = new PostgresSyncOperationRepository(Database);
        var cancelManifest = Manifest(cancelContext, 1);
        await cancelPlans.InsertAsync(cancelContext.TenantId, cancelManifest, default);
        var cancelQueued = EntitySyncOperation.QueueDryRun(cancelContext.TenantId, Guid.NewGuid(), cancelManifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), "cancel-op", "route-a", cancelContext.Source.ConnectionId, 1, cancelContext.Target.ConnectionId, 1, cancelContext.Now);
        await cancelOperations.InsertAsync(
            cancelContext.TenantId, cancelQueued,
            OperationItems(cancelQueued, cancelManifest.Items, cancelContext.Now.AddDays(1)),
            default);
        Assert.True(await cancelOperations.TryReplaceAsync(
            cancelContext.TenantId, cancelQueued.OperationId,
            EntitySyncOperationStatus.Queued,
            cancelQueued.Cancel(cancelContext.Now.AddMinutes(1)), default));

        async Task RunTerminalAsync(
            string suffix,
            EntitySyncOperationStatus terminalStatus,
            IReadOnlyList<EntitySyncItemOutcome> outcomes,
            bool expectedSuccess = true)
        {
            var context = await SeedControlContextAsync(suffix);
            var plans = new PostgresDurableSyncPlanRepository(Database);
            var operations = new PostgresSyncOperationRepository(Database);
            var manifest = Manifest(context, outcomes.Count);
            await plans.InsertAsync(context.TenantId, manifest, default);
            var queued = EntitySyncOperation.QueueDryRun(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), $"{suffix}-op", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, context.Now);
            var items = OperationItems(queued, manifest.Items, context.Now.AddDays(1));
            await operations.InsertAsync(context.TenantId, queued, items, default);
            var leased = await operations.TryLeaseNextAsync(
                context.TenantId, $"{suffix}-worker", context.Now.AddMinutes(1),
                context.Now.AddMinutes(10), default)
                ?? throw new InvalidOperationException("Expected operation lease.");
            var running = leased.Start(context.Now.AddMinutes(2));
            Assert.True(await operations.TryReplaceAsync(
                context.TenantId, leased.OperationId, EntitySyncOperationStatus.Leased,
                running, default));
            for (var index = 0; index < items.Count; index++)
            {
                var replacement = outcomes[index] == EntitySyncItemOutcome.Succeeded
                    ? CompleteItem(
                        items[index], context.Now.AddMinutes(2),
                        context.Now.AddMinutes(3))
                    : EntitySyncOperationItem.Rehydrate(
                        items[index].TenantId, items[index].OperationId,
                        items[index].PlanId, items[index].ItemId, items[index].ItemIndex,
                        items[index].SourceVendor, items[index].SourceConnectionId,
                        items[index].SourceEntityType, items[index].SourceEntityKey,
                        items[index].SourceEntityId, items[index].TargetVendor,
                        items[index].TargetConnectionId, items[index].TargetEntityType,
                        items[index].TargetEntityId, items[index].Action,
                        items[index].RedactedBefore, items[index].RedactedDesired,
                        items[index].BeforePayloadSha256,
                        items[index].DesiredPayloadSha256, null,
                        items[index].SnapshotsExpireAt, "failed-request",
                        EntitySyncItemOutcome.Failed, "vendor_error", "failed",
                        context.Now.AddMinutes(2), context.Now.AddMinutes(3));
                Assert.True(await operations.TryReplaceItemAsync(
                    context.TenantId, running.OperationId, running.PlanId,
                    items[index].ItemId, running.Attempt, running.LeaseOwner!,
                    context.Now.AddMinutes(3), EntitySyncItemOutcome.Pending,
                    replacement, default));
            }
            Assert.Equal(
                expectedSuccess,
                await operations.TryReplaceAsync(
                    context.TenantId, running.OperationId,
                    EntitySyncOperationStatus.Running,
                    running.Complete(
                        terminalStatus, context.Now.AddMinutes(4)), default));
            if (!expectedSuccess)
            {
                Assert.Equal(
                    EntitySyncOperationStatus.Running,
                    (await operations.GetAsync(
                        context.TenantId, running.OperationId, default))!.Status);
            }
        }
    }

    [Fact]
    public async Task Snapshot_and_direct_idempotency_repository_methods_are_bounded_and_tenant_scoped()
    {
        var context = await SeedControlContextAsync("direct-repositories");
        var plans = new PostgresDurableSyncPlanRepository(Database);
        var operations = new PostgresSyncOperationRepository(Database);
        var manifest = Manifest(context, 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        var operation = EntitySyncOperation.QueueDryRun(context.TenantId, Guid.NewGuid(), manifest.Plan.PlanId, Guid.NewGuid(), Guid.NewGuid(), "snapshot-op", "route-a", context.Source.ConnectionId, 1, context.Target.ConnectionId, 1, context.Now);
        var expiredAt = context.Now.AddMinutes(-1);
        var items = OperationItems(operation, manifest.Items, expiredAt);
        await operations.InsertAsync(context.TenantId, operation, items, default);
        var snapshot = new EntitySyncOperationItemSnapshot(
            context.TenantId, operation.OperationId, items[0].ItemId,
            "encrypted-before", null, expiredAt);
        await operations.InsertSnapshotAsync(context.TenantId, snapshot, default);
        Assert.Equal(snapshot, await operations.GetSnapshotAsync(
            context.TenantId, operation.OperationId, items[0].ItemId, default));
        Assert.Null(await operations.GetSnapshotAsync(
            "other-tenant", operation.OperationId, items[0].ItemId, default));
        Assert.Equal(1, await operations.DeleteExpiredSnapshotsAsync(
            context.TenantId, DateTimeOffset.UtcNow, 1, default));

        var idempotency = new PostgresIdempotencyRepository(Database);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var receipt = new EntitySyncIdempotencyReceipt(
            context.TenantId, "direct-key", new EntitySyncSha256(new string('1', 64)),
            null, null, createdAt, null, createdAt.AddHours(1));
        Assert.True(await idempotency.TryInsertAsync(context.TenantId, receipt, default));
        Assert.False(await idempotency.TryInsertAsync(context.TenantId, receipt, default));
        Assert.False(await idempotency.TryCompleteAsync(
            context.TenantId, receipt.IdempotencyKey,
            new EntitySyncSha256(new string('2', 64)), 200,
            new EntitySyncJsonValue("{}"), DateTimeOffset.UtcNow, default));
        Assert.True(await idempotency.TryCompleteAsync(
            context.TenantId, receipt.IdempotencyKey, receipt.RequestSha256, 200,
            new EntitySyncJsonValue("{\"ok\":true}"), DateTimeOffset.UtcNow, default));
        var stored = await idempotency.GetAsync(
            context.TenantId, receipt.IdempotencyKey, default);
        Assert.Equal(200, stored!.ResponseStatusCode);
        Assert.Equal("{\"ok\":true}", stored.ResponseBody!.Json);

        var expiredReceipt = new EntitySyncIdempotencyReceipt(
            context.TenantId, "expired-direct-key",
            new EntitySyncSha256(new string('3', 64)), null, null,
            createdAt, null, createdAt.AddMinutes(1));
        Assert.True(await idempotency.TryInsertAsync(
            context.TenantId, expiredReceipt, default));
        Assert.Equal(1, await idempotency.DeleteExpiredAsync(
            context.TenantId, DateTimeOffset.UtcNow, 1, default));
    }

    [Fact]
    public async Task Schedule_audit_retention_and_plan_expiration_are_tenant_scoped()
    {
        var context = await SeedControlContextAsync("schedule");
        var schedules = new PostgresSyncScheduleRepository(Database);
        var schedule = new EntitySyncSchedule(
            context.TenantId, Guid.NewGuid(), 1, "nightly", context.Policy.PolicyId, 1,
            "0 0 * * *", "UTC", true, context.Now.AddHours(1), null,
            context.Now, new EntitySyncActor("scheduler"));
        await schedules.InsertVersionAsync(context.TenantId, schedule, default);
        Assert.Equal(schedule, await schedules.GetLatestAsync(context.TenantId, schedule.ScheduleId, default));
        Assert.Null(await schedules.GetAsync("other-tenant", schedule.ScheduleId, 1, default));
        Assert.Single(await schedules.ListDueAsync(
            context.TenantId, context.Now.AddHours(2), 10, default));
        var change = new EntitySyncCanonicalChangeEvent(
            context.TenantId, Guid.NewGuid(), "Company", "company-1", 1,
            new EntitySyncJsonValue("[\"name\"]"), context.Now, context.Now,
            EntitySyncCanonicalChangeStatus.Pending);
        await schedules.InsertChangeEventAsync(context.TenantId, change, default);
        Assert.Single(await schedules.ListPendingChangeEventsAsync(
            context.TenantId, 10, default));
        Assert.Empty(await schedules.ListPendingChangeEventsAsync(
            "other-tenant", 10, default));
        Assert.True(await schedules.TrySetChangeEventStatusAsync(
            context.TenantId, change.EventId, EntitySyncCanonicalChangeStatus.Pending,
            EntitySyncCanonicalChangeStatus.Planned, default));
        Assert.False(await schedules.TrySetChangeEventStatusAsync(
            "other-tenant", change.EventId, EntitySyncCanonicalChangeStatus.Planned,
            EntitySyncCanonicalChangeStatus.Failed, default));

        using var keyRing = new TemporaryDirectory();
        var protector = CreateProtector(keyRing.Path, "audit-test");
        var audits = new PostgresSyncAuditRepository(Database);
        var eventId = Guid.NewGuid();
        var audit = new EntitySyncAuditEvent(
            context.TenantId, eventId, context.Now, "PlanCreated", new EntitySyncActor("actor"),
            null, null, null, null, "correlation", new EntitySyncJsonValue("{\"secret\":\"[redacted]\"}"),
            new EntitySyncSha256(new string('d', 64)), new EntitySyncSha256(new string('e', 64)),
            context.Now.AddMinutes(-1));
        var fullCiphertext = protector.Protect(
            EntitySyncDataProtectionPurpose.AuditValue, "{\"secret\":\"full\"}");
        await audits.AppendAsync(context.TenantId, audit,
            new EntitySyncAuditEventFullValues(context.TenantId, eventId, fullCiphertext,
                context.Now.AddMinutes(-1)), default);
        for (var index = 1; index <= 2; index++)
        {
            var pagedAudit = new EntitySyncAuditEvent(
                context.TenantId, Guid.NewGuid(), context.Now.AddMinutes(index),
                $"Paged{index}", new EntitySyncActor("actor"), null, null, null, null,
                $"correlation-{index}", new EntitySyncJsonValue("{}"),
                new EntitySyncSha256(new string((char)('a' + index), 64)), null, null);
            await audits.AppendAsync(context.TenantId, pagedAudit, null, default);
        }
        var firstAuditPage = await audits.ListAsync(
            context.TenantId, null, null, 2, default);
        Assert.Equal(2, firstAuditPage.Events.Count);
        Assert.NotNull(firstAuditPage.ContinuationOccurredAt);
        var secondAuditPage = await audits.ListAsync(
            context.TenantId, firstAuditPage.ContinuationOccurredAt,
            firstAuditPage.ContinuationEventId, 2, default);
        Assert.Single(secondAuditPage.Events);
        Assert.Null(secondAuditPage.ContinuationEventId);
        Assert.Null(await audits.GetFullValuesAsync("other-tenant", eventId, default));
        Assert.Null(await audits.GetFullValuesAsync(context.TenantId, eventId, default));
        Assert.Equal(1, await audits.DeleteExpiredFullValuesAsync(
            context.TenantId, DateTimeOffset.UtcNow, 1, default));

        var plans = new PostgresDurableSyncPlanRepository(Database);
        var manifest = Manifest(context, itemCount: 1);
        await plans.InsertAsync(context.TenantId, manifest, default);
        Assert.False(await plans.TryExpireAsync(
            "other-tenant", manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            EntitySyncDurablePlanStatus.Draft, context.Now.AddDays(2), default));
        Assert.True(await plans.TryExpireAsync(
            context.TenantId, manifest.Plan.PlanId, manifest.Plan.PlanDigestSha256,
            EntitySyncDurablePlanStatus.Draft, context.Now.AddDays(2), default));
    }

    [Fact]
    public async Task Idempotent_executor_replays_conflicts_and_executes_concurrent_command_once()
    {
        var executor = new PostgresIdempotencyRepository(Database);
        var executions = 0;
        var hash = new string('f', 64);
        Task<IdempotentResponse> Command(
            IdempotencyExecutionContext context,
            CancellationToken _)
        {
            Assert.Equal("idempotency-tenant", context.TenantId);
            Assert.Equal("same-key", context.Key);
            Assert.False(string.IsNullOrWhiteSpace(context.Token));
            Interlocked.Increment(ref executions);
            return Task.FromResult(new IdempotentResponse(201, new EntitySyncJsonValue("{\"id\":1}")));
        }

        var responses = await Task.WhenAll(
            executor.ExecuteAsync(
                "idempotency-tenant", "same-key", hash,
                IdempotencyExecutionMode.Recoverable, Command, default),
            executor.ExecuteAsync(
                "idempotency-tenant", "same-key", hash,
                IdempotencyExecutionMode.Recoverable, Command, default));

        Assert.Equal(1, executions);
        Assert.All(responses, response => Assert.Equal(201, response.StatusCode));
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => executor.ExecuteAsync(
            "idempotency-tenant", "same-key", new string('a', 64),
            IdempotencyExecutionMode.Recoverable, Command, default));
        Assert.Null(await executor.GetAsync("other-tenant", "same-key", default));
        var downstreamTokens = new HashSet<string>(StringComparer.Ordinal);
        var logicalEffects = 0;
        var crashInvocations = 0;
        async Task<IdempotentResponse> CrashSafeCommand(
            IdempotencyExecutionContext context,
            CancellationToken _)
        {
            if (downstreamTokens.Add(context.Token)) logicalEffects++;
            if (Interlocked.Increment(ref crashInvocations) == 1)
                throw new InvalidOperationException("simulated process crash after downstream effect");
            await Task.Yield();
            return new IdempotentResponse(202, new EntitySyncJsonValue("{\"resumed\":true}"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "idempotency-tenant", "crash-key", hash,
            IdempotencyExecutionMode.Recoverable, CrashSafeCommand, default));
        var durableClaim = await executor.GetAsync(
            "idempotency-tenant", "crash-key", default);
        Assert.NotNull(durableClaim);
        Assert.Null(durableClaim.ResponseStatusCode);
        var conflictingInvocations = 0;
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => executor.ExecuteAsync(
            "idempotency-tenant", "crash-key", new string('e', 64),
            IdempotencyExecutionMode.Recoverable,
            (_, _) =>
            {
                conflictingInvocations++;
                return Task.FromResult(
                    new IdempotentResponse(200, new EntitySyncJsonValue("{}")));
            },
            default));
        Assert.Equal(0, conflictingInvocations);
        var resumed = await executor.ExecuteAsync(
            "idempotency-tenant", "crash-key", hash,
            IdempotencyExecutionMode.Recoverable, CrashSafeCommand, default);
        Assert.Equal(202, resumed.StatusCode);
        Assert.Equal(1, logicalEffects);
        Assert.Equal(2, crashInvocations);
    }

    [Fact]
    public async Task Twenty_five_live_same_key_contenders_run_one_callback_and_replay_exactly()
    {
        var executor = new PostgresIdempotencyRepository(
            Database,
            new PostgresIdempotencyExecutionOptions(
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(5)));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        async Task<IdempotentResponse> Command(
            IdempotencyExecutionContext _,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new IdempotentResponse(
                StatusCodes.Status202Accepted,
                new EntitySyncJsonValue("{\"queued\":true,\"runId\":\"stable\"}"));
        }

        var first = executor.ExecuteAsync(
            "heartbeat-tenant", "heartbeat-key", new string('3', 64),
            IdempotencyExecutionMode.Recoverable, Command, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executions = Enumerable.Range(0, 24)
            .Select(_ => executor.ExecuteAsync(
                "heartbeat-tenant", "heartbeat-key", new string('3', 64),
                IdempotencyExecutionMode.Recoverable, Command, default))
            .Prepend(first)
            .ToArray();

        await Task.Delay(TimeSpan.FromMilliseconds(350));
        Assert.Equal(1, Volatile.Read(ref invocations));
        release.SetResult();
        var responses = await Task.WhenAll(executions);
        Assert.All(responses, response =>
        {
            Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
            Assert.Equal(
                "{\"queued\":true,\"runId\":\"stable\"}",
                response.ResponseBody.Json);
        });
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task Lost_heartbeat_cancels_and_awaits_stale_owner_then_one_takeover_recovers()
    {
        const string tenantId = "takeover-tenant";
        const string key = "takeover-key";
        var hash = new string('4', 64);
        var executor = new PostgresIdempotencyRepository(
            Database,
            new PostgresIdempotencyExecutionOptions(
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(5)));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStaleExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleOwner = executor.ExecuteAsync(
            tenantId,
            key,
            hash,
            IdempotencyExecutionMode.Recoverable,
            async (_, cancellationToken) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Cancellation was not observed.");
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.SetResult();
                    await allowStaleExit.Task;
                    return new IdempotentResponse(
                        StatusCodes.Status201Created,
                        new EntitySyncJsonValue("{\"stale\":true}"));
                }
            },
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var revoke = Database.CreateCommand(
                         """
                         UPDATE entitysync.api_idempotency_records
                         SET execution_owner = @replacement_owner,
                             execution_lease_expires_at = clock_timestamp() - interval '1 second'
                         WHERE tenant_id = @tenant_id
                           AND idempotency_key = @idempotency_key
                         """))
        {
            revoke.Parameters.AddWithValue("replacement_owner", Guid.NewGuid());
            revoke.Parameters.AddWithValue("tenant_id", tenantId);
            revoke.Parameters.AddWithValue("idempotency_key", key);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(staleOwner.IsCompleted);
        allowStaleExit.SetResult();
        var lost = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => staleOwner);
        Assert.Contains("lease was lost", lost.Message, StringComparison.OrdinalIgnoreCase);

        await using (var incomplete = Database.CreateCommand(
                         """
                         SELECT response_status_code IS NULL
                                AND response_body IS NULL
                                AND completed_at IS NULL
                         FROM entitysync.api_idempotency_records
                         WHERE tenant_id = @tenant_id
                           AND idempotency_key = @idempotency_key
                         """))
        {
            incomplete.Parameters.AddWithValue("tenant_id", tenantId);
            incomplete.Parameters.AddWithValue("idempotency_key", key);
            Assert.True((bool)(await incomplete.ExecuteScalarAsync())!);
        }

        var recoveryInvocations = 0;
        Task<IdempotentResponse> Recover(
            IdempotencyExecutionContext context,
            CancellationToken _)
        {
            Assert.True(context.IsRecovery);
            Interlocked.Increment(ref recoveryInvocations);
            return Task.FromResult(new IdempotentResponse(
                StatusCodes.Status200OK,
                new EntitySyncJsonValue("{\"recovered\":true}")));
        }
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => executor.ExecuteAsync(
                tenantId,
                key,
                hash,
                IdempotencyExecutionMode.Recoverable,
                Recover,
                default)));
        Assert.Equal(1, recoveryInvocations);
        Assert.All(responses, response =>
        {
            Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
            Assert.Equal("{\"recovered\":true}", response.ResponseBody.Json);
        });

        await using var inspect = Database.CreateCommand(
            """
            SELECT execution_attempt, execution_owner, completed_at IS NOT NULL
            FROM entitysync.api_idempotency_records
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """);
        inspect.Parameters.AddWithValue("tenant_id", tenantId);
        inspect.Parameters.AddWithValue("idempotency_key", key);
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Atomic_database_idempotency_rolls_back_effect_before_incomplete_receipt()
    {
        const string tenantId = "atomic-idempotency-tenant";
        var route = EntityExclusionRoute.Create(
            tenantId,
            "NetSuite",
            "source",
            "Customer",
            "HaloPSA",
            "target",
            "Client");
        var exclusions = new PostgresEntityExclusionRepository(Database);
        var executor = new PostgresIdempotencyRepository(Database);
        var invocation = 0;
        async Task<IdempotentResponse> Command(
            IdempotencyExecutionContext context,
            CancellationToken cancellationToken)
        {
            await exclusions.AddAsync(
                route,
                "source-1",
                "Source One",
                "atomic recovery",
                "actor",
                cancellationToken);
            if (Interlocked.Increment(ref invocation) == 1)
                throw new InvalidOperationException("simulated crash before receipt completion");
            return new IdempotentResponse(
                StatusCodes.Status201Created,
                new EntitySyncJsonValue("{\"sourceEntityId\":\"source-1\"}"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            tenantId,
            "atomic-key",
            new string('1', 64),
            IdempotencyExecutionMode.AtomicDatabase,
            Command,
            default));
        Assert.Empty(await exclusions.ListActiveAsync(route, default));

        var response = await executor.ExecuteAsync(
            tenantId,
            "atomic-key",
            new string('1', 64),
            IdempotencyExecutionMode.AtomicDatabase,
            Command,
            default);
        Assert.Equal(StatusCodes.Status201Created, response.StatusCode);
        Assert.Single(await exclusions.ListActiveAsync(route, default));
        Assert.Equal(2, invocation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_atomic_owner_never_commits_effect_after_takeover(
        bool ownerThrows)
    {
        var suffix = ownerThrows ? "throws" : "returns";
        var tenantId = $"atomic-stale-{suffix}";
        var key = $"atomic-stale-key-{suffix}";
        var hash = new string(ownerThrows ? '6' : '5', 64);
        var now = DateTimeOffset.UtcNow;
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var executor = new PostgresIdempotencyRepository(
            Database,
            new PostgresIdempotencyExecutionOptions(
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(5)));
        var ownerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = executor.ExecuteAsync(
            tenantId,
            key,
            hash,
            IdempotencyExecutionMode.AtomicDatabase,
            async (_, cancellationToken) =>
            {
                await connections.InsertAsync(
                    tenantId,
                    Connection(
                        tenantId, "owner-a", "NetSuite", 1, "cipher-a", now),
                    cancellationToken);
                ownerStarted.SetResult();
                await releaseOwner.Task.WaitAsync(cancellationToken);
                if (ownerThrows)
                    throw new InvalidOperationException("owner callback failed");
                return new IdempotentResponse(
                    StatusCodes.Status201Created,
                    new EntitySyncJsonValue("{\"owner\":\"a\"}"));
            },
            default);
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var revoke = Database.CreateCommand(
                         """
                         UPDATE entitysync.api_idempotency_records
                         SET execution_owner = @replacement_owner,
                             execution_lease_expires_at = clock_timestamp() - interval '1 second'
                         WHERE tenant_id = @tenant_id
                           AND idempotency_key = @idempotency_key
                         """))
        {
            revoke.Parameters.AddWithValue("replacement_owner", Guid.NewGuid());
            revoke.Parameters.AddWithValue("tenant_id", tenantId);
            revoke.Parameters.AddWithValue("idempotency_key", key);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }

        var takeover = await executor.ExecuteAsync(
            tenantId,
            key,
            hash,
            IdempotencyExecutionMode.AtomicDatabase,
            async (context, cancellationToken) =>
            {
                Assert.True(context.IsRecovery);
                await connections.InsertAsync(
                    tenantId,
                    Connection(
                        tenantId, "owner-b", "NetSuite", 1, "cipher-b", now),
                    cancellationToken);
                return new IdempotentResponse(
                    StatusCodes.Status202Accepted,
                    new EntitySyncJsonValue("{\"owner\":\"b\"}"));
            },
            default);
        Assert.Equal(StatusCodes.Status202Accepted, takeover.StatusCode);

        releaseOwner.SetResult();
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => owner);
        Assert.Contains(
            ownerThrows ? "callback failed" : "lease was lost",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(await connections.GetAsync(tenantId, "owner-a", default));
        Assert.NotNull(await connections.GetAsync(tenantId, "owner-b", default));
    }

    [Fact]
    public async Task Recoverable_external_command_holds_no_database_connection_while_blocked()
    {
        var executor = new PostgresIdempotencyRepository(Database);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = executor.ExecuteAsync(
            "external-idempotency-tenant",
            "external-key",
            new string('2', 64),
            IdempotencyExecutionMode.Recoverable,
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new IdempotentResponse(
                    StatusCodes.Status200OK,
                    new EntitySyncJsonValue("{}"));
            },
            default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var command = Database.CreateCommand(
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
            """);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);

        release.SetResult();
        Assert.Equal(StatusCodes.Status200OK, (await execution).StatusCode);
    }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = $"Host=127.0.0.1;Database=postgres;Username={Environment.UserName};Pooling=false";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        admin = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        await using (var command = admin.CreateCommand($"CREATE DATABASE \"{databaseName}\""))
            await command.ExecuteNonQueryAsync();
        var databaseBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false
        };
        database = NpgsqlDataSource.Create(databaseBuilder.ConnectionString);
        await EntitySyncDatabaseMigrator.ApplyAsync(Database);
    }

    public async Task DisposeAsync()
    {
        if (database is not null) await database.DisposeAsync();
        if (admin is not null)
        {
            await using var command = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
            await command.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private async Task<ControlContext> SeedControlContextAsync(string suffix)
    {
        var tenantId = $"tenant-{suffix}-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var connections = new PostgresConnectionDefinitionRepository(Database);
        var policies = new PostgresSyncPolicyRepository(Database);
        var source = Connection(tenantId, $"source-{suffix}", "NetSuite", 1, "cipher-source", now);
        var target = Connection(tenantId, $"target-{suffix}", "HaloPSA", 1, "cipher-target", now);
        await connections.InsertAsync(tenantId, source, default);
        await connections.InsertAsync(tenantId, target, default);
        var policy = Policy(tenantId, source.ConnectionId, target.ConnectionId, now);
        await policies.InsertAsync(tenantId, policy, default);
        return new ControlContext(tenantId, now, source, target, policy);
    }
    private static CreateDurablePlanRequest RecoveryRequest(
        ControlContext context,
        string idempotencyKey) =>
        new()
        {
            TenantId = context.TenantId,
            IdempotencyKey = idempotencyKey,
            PolicyId = context.Policy.PolicyId,
            PolicyVersion = context.Policy.Version,
            PlanLifetime = TimeSpan.FromHours(1)
        };

    private DurablePlanService RecoveryService(
        ControlContext context,
        IDurableSyncPlanRepository plans,
        BlockingReadAdapter sourceAdapter,
        ISyncPolicyRepository? policies = null,
        IConnectionDefinitionRepository? connections = null)
    {
        var runtime = new TestRuntimeFactory(
            context.Source,
            sourceAdapter,
            context.Target,
            new BlockingReadAdapter("HaloPSA", []));
        var mapper = new DefaultEntityMapper();
        var exclusions = new PostgresEntityExclusionRepository(Database);
        return new DurablePlanService(
            new EntitySyncPlanner(
                runtime,
                new TestEntitySyncPlanRepository(),
                exclusions,
                new WeightedEntityMatcher(),
                mapper,
                new InMemoryEntitySyncChangeStateRepository()),
            new PlanManifestBuilder(mapper),
            policies ?? new PostgresSyncPolicyRepository(Database),
            connections ?? new PostgresConnectionDefinitionRepository(Database),
            runtime,
            exclusions,
            plans,
            TimeProvider.System);
    }


    private static EntitySyncConnectionDefinition Connection(
        string tenantId, string connectionId, string vendor, long generation,
        string ciphertext, DateTimeOffset now, Guid? platformInstanceId = null) =>
        new(
            tenantId, connectionId, vendor, connectionId, generation, true,
            new EntitySyncJsonValue("{\"region\":\"us\"}"), ciphertext,
            now, new EntitySyncActor("creator"), now, new EntitySyncActor("creator"),
            platformInstanceId);

    private static EntitySyncPolicy Policy(
        string tenantId, string sourceConnectionId, string targetConnectionId,
        DateTimeOffset now) =>
        EntitySyncPolicy.Create(
            tenantId, Guid.NewGuid(), "policy", "route-a",
            new EntitySyncPolicyDefinition(
                "NetSuite", sourceConnectionId, "Customer", "HaloPSA", targetConnectionId,
                "Client", true, false, 90, 70, "externalId", "customField",
                EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly, ["name"], ["password"], true),
            true, now, new EntitySyncActor("creator"));

    private static Guid DurablePlanId(string tenantId, string idempotencyKey)
    {
        var digest = EntitySyncCanonicalDigest.Compute(new
        {
            Namespace = "entitysync-durable-plan-idempotency-v1",
            TenantId = tenantId,
            IdempotencyKey = idempotencyKey
        });
        return new Guid(Convert.FromHexString(digest.Value).AsSpan(0, 16));
    }

    private static EntitySyncSha256 DurableCreateRequestDigest(
        CreateDurablePlanRequest request,
        EntitySyncActor actor)
    {
        var selection = new EntitySyncSelectionBounds(
            request.SourceSearch,
            request.SourceCount,
            request.SourceEntityId);
        return EntitySyncCanonicalDigest.Compute(new
        {
            SchemaVersion = 1,
            TenantId = request.TenantId.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim(),
            request.PolicyId,
            PolicyVersionSpecified = request.PolicyVersion.HasValue,
            request.PolicyVersion,
            selection.SourceSearch,
            selection.SourceCount,
            selection.SourceEntityId,
            PinnedCanonicalVersion = request.PinnedCanonicalSource?.CanonicalVersion,
            PinnedCanonicalEntitySha256 = request.PinnedCanonicalSource is null
                ? null
                : EntitySyncCanonicalDigest.Compute(request.PinnedCanonicalSource.Entity).Value,
            PlanLifetimeTicks = request.PlanLifetime.Ticks,
            CreatedBy = actor.ActorId
        });
    }

    private static EntitySyncDurablePlanManifest Manifest(
        ControlContext context,
        int itemCount,
        string reason = "exact-id",
        Guid? planId = null,
        bool withResolvedParent = false)
    {
        var manifestPlanId = planId ?? Guid.NewGuid();
        var unsealedPlan = new EntitySyncDurablePlan(
            context.TenantId, manifestPlanId, context.Policy.PolicyId, context.Policy.Version,
            context.Policy.DefinitionSha256, "route-a", context.Source.ConnectionId, 1,
            context.Target.ConnectionId, 1, new EntitySyncSha256(new string('0', 64)),
            EntitySyncDurablePlanStatus.Draft,
            new EntitySyncSelectionBounds("active", 10, "source-1"), 0,
            context.Now, new EntitySyncActor("planner"), context.Now.AddDays(1));
        var items = Enumerable.Range(0, itemCount).Select(index =>
            new EntitySyncDurablePlanItem(
                context.TenantId, manifestPlanId, Guid.NewGuid(), index, "NetSuite",
                context.Source.ConnectionId, "Customer", $"legacy-key-{index}", $"SOURCE-{index}",
                withResolvedParent && index == 0 ? "OrchestraMSP" : "HaloPSA",
                context.Target.ConnectionId,
                withResolvedParent && index == 0 ? "Site" : "Client",
                $"TARGET-{index}",
                withResolvedParent && index == 0 ? "Create" : "Update",
                new EntitySyncMatchEvidence(95, "Exact", [reason]),
                new EntitySyncJsonValue($"{{\"name\":\"before-{index}\"}}"),
                new EntitySyncJsonValue($"{{\"name\":\"desired-{index}\"}}"),
                new EntitySyncSha256(new string('a', 64)),
                new EntitySyncSha256(new string('b', 64)),
                [new EntityFieldChange(
                    "name",
                    new EntitySyncJsonValue("\"before\""),
                    new EntitySyncJsonValue("\"desired\""),
                    new EntitySyncSha256(new string('a', 64)),
                    new EntitySyncSha256(new string('b', 64)),
                    false)],
                withResolvedParent && index == 0
                    ? new EntityWriteParent(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        null,
                        "Client",
                        context.Source.ConnectionId,
                        "customer-42",
                        "active",
                        new string('c', 64),
                        7)
                    : null)).ToArray();
        return EntitySyncDurablePlanManifest.Create(unsealedPlan, items);
    }

    private static EntitySyncDurablePlanManifest ManifestWithExpiration(
        ControlContext context,
        DateTimeOffset expiresAt)
    {
        var manifest = Manifest(context, 1);
        var plan = manifest.Plan;
        return EntitySyncDurablePlanManifest.Create(
            new EntitySyncDurablePlan(
                plan.TenantId,
                plan.PlanId,
                plan.PolicyId,
                plan.PolicyVersion,
                plan.PolicyDefinitionSha256,
                plan.RouteScope,
                plan.SourceConnectionId,
                plan.SourceConnectionGeneration,
                plan.TargetConnectionId,
                plan.TargetConnectionGeneration,
                plan.PlanDigestSha256,
                EntitySyncDurablePlanStatus.Draft,
                plan.SelectionBounds,
                plan.ItemCount,
                plan.CreatedAt,
                plan.CreatedBy,
                expiresAt),
            manifest.Items);
    }

    private static EntityExclusionRoute Route(ControlContext context) =>
        EntityExclusionRoute.Create(
            context.TenantId,
            "NetSuite",
            context.Source.ConnectionId,
            "Customer",
            "HaloPSA",
            context.Target.ConnectionId,
            "Client");

    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)>
        AcquireRouteLockAsync(EntityExclusionRoute route)
    {
        var connection = await Database.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_advisory_xact_lock(entitysync.entity_route_lock_key(
                    @tenant_id, @source_connection_id, @target_connection_id))
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("tenant_id", route.TenantId);
            command.Parameters.AddWithValue("source_connection_id", route.SourceConnectionId);
            command.Parameters.AddWithValue("target_connection_id", route.TargetConnectionId);
            await command.ExecuteNonQueryAsync();
            return (connection, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }
    private sealed class TestRuntimeFactory(
        EntitySyncConnectionDefinition sourceDefinition,
        IEntityAdapter sourceAdapter,
        EntitySyncConnectionDefinition targetDefinition,
        IEntityAdapter targetAdapter) : IConnectionRuntimeFactory
    {
        private readonly IReadOnlyDictionary<string, (EntitySyncConnectionDefinition, IEntityAdapter)>
            registrations = new Dictionary<string, (EntitySyncConnectionDefinition, IEntityAdapter)>
            {
                [sourceDefinition.ConnectionId] = (sourceDefinition, sourceAdapter),
                [targetDefinition.ConnectionId] = (targetDefinition, targetAdapter)
            };

        public Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            var registration = registrations[connectionId];
            if (registration.Item1.TenantId != tenantId
                || registration.Item1.Generation != expectedGeneration)
                throw new InvalidOperationException("Connection identity mismatch.");
            return Task.FromResult<IConnectionRuntimeLease>(
                new TestRuntimeLease(registration.Item1, registration.Item2));
        }

        public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record TestRuntimeLease(
        EntitySyncConnectionDefinition Definition,
        IEntityAdapter Adapter) : IConnectionRuntimeLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingReadAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity> entities) : IEntityAdapter
    {
        private TaskCompletionSource? readStarted;
        private TaskCompletionSource? releaseRead;

        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int GetEntitiesCalls { get; private set; }

        public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken)
        {
            GetEntitiesCalls++;
            readStarted?.TrySetResult();
            if (releaseRead is not null)
                await releaseRead.Task.WaitAsync(cancellationToken);
            return entities;
        }

        public void BlockNextRead()
        {
            readStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitForReadAsync() => readStarted?.Task
            ?? throw new InvalidOperationException("The read was not blocked.");

        public void ReleaseRead()
        {
            releaseRead?.TrySetResult();
            releaseRead = null;
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }


    private static async Task AssertStillRunningAsync(Task task)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(
            task.IsCompleted,
            $"The mutation did not wait for the conflicting row lock: {task.Exception}");
    }
    private static EntitySyncAuditEvent ApprovalAudit(
        string tenantId,
        Guid planId,
        Guid approvalId,
        string actorId,
        DateTimeOffset occurredAt)
    {
        var values = new EntitySyncJsonValue("{}");
        return new EntitySyncAuditEvent(
            tenantId,
            Guid.NewGuid(),
            occurredAt,
            "SyncPlanApproved",
            new EntitySyncActor(actorId),
            null,
            null,
            planId,
            null,
            approvalId.ToString("N"),
            values,
            EntitySyncCanonicalDigest.Compute(new { }),
            null,
            null);
    }


    private static EntitySyncOperationItem CompleteItem(
        EntitySyncOperationItem item,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        EntitySyncOperationItem.Rehydrate(
            item.TenantId, item.OperationId, item.PlanId, item.ItemId, item.ItemIndex,
            item.SourceVendor, item.SourceConnectionId, item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, item.RedactedBefore, item.RedactedDesired,
            item.BeforePayloadSha256, item.DesiredPayloadSha256,
            new EntitySyncSha256(new string('c', 64)), item.SnapshotsExpireAt,
            "request-complete", EntitySyncItemOutcome.Succeeded, null, null,
            startedAt, completedAt, item.ResolvedTargetParent);

    private static IReadOnlyList<EntitySyncOperationItem> OperationItems(
        EntitySyncOperation operation, IReadOnlyList<EntitySyncDurablePlanItem> planItems,
        DateTimeOffset expiresAt) =>
        planItems.Select(item => EntitySyncOperationItem.Rehydrate(
            operation.TenantId, operation.OperationId, operation.PlanId, item.ItemId,
            item.ItemOrdinal, item.SourceVendor, item.SourceConnectionId,
            item.SourceEntityType,
            item.SourceEntityKey, item.SourceEntityId, item.TargetVendor,
            item.TargetConnectionId, item.TargetEntityType, item.TargetEntityId,
            item.Action, item.RedactedBefore, item.RedactedDesired,
            item.BeforePayloadSha256, item.DesiredPayloadSha256, null, expiresAt,
            null, EntitySyncItemOutcome.Pending, null, null, null, null,
            item.ResolvedTargetParent)).ToArray();

    private static IEntitySyncDataProtector CreateProtector(string keyPath, string applicationName)
    {
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(keyPath), builder => builder.SetApplicationName(applicationName));
        return new EntitySyncDataProtector(provider);
    }

    private NpgsqlDataSource Database =>
        database ?? throw new InvalidOperationException("The test database is not initialized.");



    private sealed record ControlContext(
        string TenantId,
        DateTimeOffset Now,
        EntitySyncConnectionDefinition Source,
        EntitySyncConnectionDefinition Target,
        EntitySyncPolicy Policy);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"entitysync-dp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresDurableSyncPlanRepository(NpgsqlDataSource dataSource)
    : IDurableSyncPlanRepository
{
    public async Task<DurablePlanCreationClaim> TryClaimCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid proposedOwnerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        tenantId = ValidateCreationArguments(
            tenantId, planId, requestSha256, proposedOwnerToken);
        ValidateLeaseDuration(leaseDuration);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string insertSql = """
            INSERT INTO entitysync.sync_plan_creation_claims (
                tenant_id, plan_id, request_sha256, owner_token,
                lease_expires_at, state, result_plan_id, created_at, updated_at)
            VALUES (
                @tenant_id, @plan_id, @request_sha256, @owner_token,
                clock_timestamp() + @lease_duration,
                'InProgress', NULL, clock_timestamp(), clock_timestamp())
            ON CONFLICT (tenant_id, plan_id) DO NOTHING
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddPlanKey(insert, tenantId, planId);
            AddCreationParameters(
                insert, requestSha256, proposedOwnerToken, leaseDuration);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string readSql = """
            SELECT request_sha256, owner_token, lease_expires_at, state, result_plan_id,
                   result_plan_digest_sha256,
                   lease_expires_at > clock_timestamp()
            FROM entitysync.sync_plan_creation_claims
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
            FOR UPDATE
            """;
        string storedRequest;
        Guid storedOwner;
        DateTimeOffset storedLeaseExpiry;
        string storedState;
        Guid? resultPlanId;
        EntitySyncSha256? resultPlanDigestSha256;
        bool leaseActive;
        await using (var read = new NpgsqlCommand(readSql, connection, transaction))
        {
            AddPlanKey(read, tenantId, planId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The durable plan creation identity could not be claimed.");
            storedRequest = reader.GetString(0).Trim();
            storedOwner = reader.GetGuid(1);
            storedLeaseExpiry = reader.GetFieldValue<DateTimeOffset>(2);
            storedState = reader.GetString(3);
            resultPlanId = PostgresControlPersistence.NullableGuid(reader, 4);
            resultPlanDigestSha256 = PostgresControlPersistence.NullableHash(reader, 5);
            leaseActive = reader.GetBoolean(6);
        }

        if (!string.Equals(storedRequest, requestSha256.Value, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurablePlanCreationClaim(
                DurablePlanCreationClaimState.Conflict, null, null, null, null);
        }

        if (storedState == "Completed")
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurablePlanCreationClaim(
                DurablePlanCreationClaimState.Completed,
                storedOwner,
                storedLeaseExpiry,
                resultPlanId,
                resultPlanDigestSha256);
        }

        if (storedOwner != proposedOwnerToken && leaseActive)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DurablePlanCreationClaim(
                DurablePlanCreationClaimState.Waiting,
                storedOwner,
                storedLeaseExpiry,
                null,
                null);
        }

        const string claimSql = """
            UPDATE entitysync.sync_plan_creation_claims
            SET owner_token = @owner_token,
                lease_expires_at = clock_timestamp() + @lease_duration,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
            RETURNING lease_expires_at
            """;
        DateTimeOffset claimedUntil;
        await using (var claim = new NpgsqlCommand(claimSql, connection, transaction))
        {
            AddPlanKey(claim, tenantId, planId);
            AddCreationParameters(
                claim, requestSha256, proposedOwnerToken, leaseDuration);
            claimedUntil = ToDateTimeOffset(
                await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The durable plan creation lease could not be acquired."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DurablePlanCreationClaim(
            DurablePlanCreationClaimState.Owner,
            proposedOwnerToken,
            claimedUntil,
            null,
            null);
    }

    public async Task<bool> RenewCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        tenantId = ValidateCreationArguments(
            tenantId, planId, requestSha256, ownerToken);
        ValidateLeaseDuration(leaseDuration);
        const string sql = """
            UPDATE entitysync.sync_plan_creation_claims
            SET lease_expires_at = clock_timestamp() + @lease_duration,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant_id
              AND plan_id = @plan_id
              AND request_sha256 = @request_sha256
              AND owner_token = @owner_token
              AND state = 'InProgress'
              AND lease_expires_at > clock_timestamp()
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddPlanKey(command, tenantId, planId);
        AddCreationParameters(command, requestSha256, ownerToken, leaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }


    public async Task ReleaseCreationAsync(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        tenantId = ValidateCreationArguments(
            tenantId, planId, requestSha256, ownerToken);
        const string sql = """
            UPDATE entitysync.sync_plan_creation_claims
            SET lease_expires_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant_id
              AND plan_id = @plan_id
              AND request_sha256 = @request_sha256
              AND owner_token = @owner_token
              AND state = 'InProgress'
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "owner_token", NpgsqlDbType.Uuid, ownerToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task InsertAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        CancellationToken cancellationToken) =>
        InsertCoreAsync(
            tenantId, manifest, null, null, cancellationToken);

    public Task InsertClaimedAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        ValidateCreationArguments(
            tenantId, manifest.Plan.PlanId, requestSha256, ownerToken);
        return InsertCoreAsync(
            tenantId,
            manifest,
            requestSha256,
            ownerToken,
            cancellationToken);
    }

    public async Task<DurablePlanImportPersistenceResult> ImportAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        string callerKey,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actor);
        tenantId = tenantId?.Trim()
            ?? throw new ArgumentNullException(nameof(tenantId));
        if (tenantId.Length == 0)
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        callerKey = callerKey?.Trim()
            ?? throw new ArgumentNullException(nameof(callerKey));
        if (callerKey.Length == 0)
            throw new ArgumentException("Caller key is required.", nameof(callerKey));
        PostgresControlPersistence.RequireTenant(
            tenantId, manifest.Plan.TenantId, nameof(manifest));
        if (manifest.Items.Any(
                item => item.TenantId != tenantId
                    || item.PlanId != manifest.Plan.PlanId))
            throw new ArgumentException(
                "Every manifest item must belong to the plan.",
                nameof(manifest));

        var requestSha256 = EntitySyncCanonicalDigest.Compute(
            new DurablePlanImportRequestDigest(
                tenantId,
                manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256.Value,
                actor.ActorId));
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await LockPlanIdentityAsync(
            connection,
            transaction,
            tenantId,
            manifest.Plan.PlanId,
            cancellationToken).ConfigureAwait(false);
        var databaseNow = await ReadDatabaseClockAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockImportReceiptAsync(
            connection, transaction, tenantId, callerKey, cancellationToken)
            .ConfigureAwait(false);
        var receiptResult = await ReadImportReceiptAsync(
                connection,
                transaction,
                tenantId,
                callerKey,
                requestSha256,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        if (receiptResult is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receiptResult;
        }
        if (manifest.Plan.ExpiresAt <= databaseNow)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(DurablePlanImportPersistenceState.Expired, null);
        }

        await LockPolicyIdentityAsync(
            connection,
            transaction,
            tenantId,
            manifest.Plan.PolicyId,
            cancellationToken).ConfigureAwait(false);
        if (!await MatchesCurrentPolicyAsync(
                connection, transaction, manifest.Plan, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(DurablePlanImportPersistenceState.PolicyChanged, null);
        }
        if (!await LockCurrentConnectionGenerationsAsync(
                connection,
                transaction,
                tenantId,
                manifest.Plan.SourceConnectionId,
                manifest.Plan.SourceConnectionGeneration,
                manifest.Plan.TargetConnectionId,
                manifest.Plan.TargetConnectionGeneration,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(DurablePlanImportPersistenceState.ConnectionChanged, null);
        }

        var existing = await MatchesExistingPlanAsync(
                connection, transaction, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (existing == false)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(DurablePlanImportPersistenceState.Conflict, null);
        }
        if (existing is null)
        {
            await InsertPlanAsync(
                connection, transaction, manifest.Plan, cancellationToken)
                .ConfigureAwait(false);
            await CopyItemsAsync(connection, manifest.Items, cancellationToken)
                .ConfigureAwait(false);
        }
        await InsertImportReceiptAsync(
                connection,
                transaction,
                tenantId,
                callerKey,
                requestSha256,
                actor,
                manifest.Plan,
                cancellationToken)
            .ConfigureAwait(false);
        var persisted = await ReadPersistedPlanAsync(
                connection,
                transaction,
                tenantId,
                manifest.Plan.PlanId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The imported durable plan is unavailable after persistence.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            existing is null
                ? DurablePlanImportPersistenceState.Inserted
                : DurablePlanImportPersistenceState.Replayed,
            persisted);
    }

    private async Task InsertCoreAsync(
        string tenantId,
        EntitySyncDurablePlanManifest manifest,
        EntitySyncSha256? requestSha256,
        Guid? ownerToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        PostgresControlPersistence.RequireTenant(tenantId, manifest.Plan.TenantId, nameof(manifest));
        if (manifest.Items.Any(item => item.TenantId != tenantId || item.PlanId != manifest.Plan.PlanId))
            throw new ArgumentException("Every manifest item must belong to the plan.", nameof(manifest));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (requestSha256 is not null)
            await LockCreationOwnershipAsync(
                connection,
                transaction,
                tenantId,
                manifest.Plan.PlanId,
                requestSha256,
                ownerToken!.Value,
                cancellationToken).ConfigureAwait(false);
        await LockPlanIdentityAsync(
            connection,
            transaction,
            tenantId,
            manifest.Plan.PlanId,
            cancellationToken).ConfigureAwait(false);
        const string existingSql = """
            SELECT plan_digest_sha256,
                   (SELECT count(*)::integer
                    FROM entitysync.sync_plan_items item
                    WHERE item.tenant_id = plan.tenant_id
                      AND item.plan_id = plan.plan_id)
            FROM entitysync.sync_plans plan
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
            """;
        await using (var existing = new NpgsqlCommand(
                         existingSql, connection, transaction))
        {
            AddPlanKey(existing, tenantId, manifest.Plan.PlanId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (requestSha256 is not null)
                    throw new InvalidOperationException(
                        "The creation claim does not atomically own the existing plan.");
                var sameManifest =
                    reader.GetString(0).Equals(
                        manifest.Plan.PlanDigestSha256.Value,
                        StringComparison.Ordinal)
                    && reader.GetInt32(1) == manifest.Items.Count;
                await reader.DisposeAsync().ConfigureAwait(false);
                if (!sameManifest)
                    throw new InvalidOperationException(
                        "The deterministic plan identity already binds a different manifest.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        await LockPolicyIdentityAsync(
            connection,
            transaction,
            tenantId,
            manifest.Plan.PolicyId,
            cancellationToken).ConfigureAwait(false);
        if (!await LockCurrentConnectionGenerationsAsync(
                connection,
                transaction,
                tenantId,
                manifest.Plan.SourceConnectionId,
                manifest.Plan.SourceConnectionGeneration,
                manifest.Plan.TargetConnectionId,
                manifest.Plan.TargetConnectionGeneration,
                cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The plan connection generations are no longer current.");
        await InsertPlanAsync(connection, transaction, manifest.Plan, cancellationToken).ConfigureAwait(false);
        await CopyItemsAsync(connection, manifest.Items, cancellationToken)
            .ConfigureAwait(false);
        if (requestSha256 is not null)
            await CompleteCreationInTransactionAsync(
                connection,
                transaction,
                tenantId,
                manifest.Plan.PlanId,
                manifest.Plan.PlanDigestSha256,
                requestSha256,
                ownerToken!.Value,
                cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntitySyncDurablePlan?> GetAsync(string tenantId, Guid planId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT plan.tenant_id, plan.plan_id, plan.policy_id, plan.policy_version,
                   policy.definition_sha256, plan.route_scope, plan.source_connection_id,
                   plan.source_connection_generation, plan.target_connection_id,
                   plan.target_connection_generation, plan.plan_digest_sha256, plan.status,
                   plan.source_search, plan.source_count, plan.source_entity_id,
                   (SELECT count(*)::integer FROM entitysync.sync_plan_items item
                     WHERE item.tenant_id = plan.tenant_id AND item.plan_id = plan.plan_id),
                   plan.created_at, plan.created_by, plan.expires_at
            FROM entitysync.sync_plans plan
            JOIN entitysync.sync_policies policy
              ON policy.tenant_id = plan.tenant_id AND policy.policy_id = plan.policy_id
             AND policy.version = plan.policy_version
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddPlanKey(command, tenantId, planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPlan(reader) : null;
    }

    public async Task<IReadOnlyList<EntitySyncDurablePlan>> ListAsync(
        string tenantId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumRows is <= 0 or > 101)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            SELECT plan.tenant_id, plan.plan_id, plan.policy_id, plan.policy_version,
                   policy.definition_sha256, plan.route_scope, plan.source_connection_id,
                   plan.source_connection_generation, plan.target_connection_id,
                   plan.target_connection_generation, plan.plan_digest_sha256, plan.status,
                   plan.source_search, plan.source_count, plan.source_entity_id,
                   (SELECT count(*)::integer FROM entitysync.sync_plan_items item
                     WHERE item.tenant_id = plan.tenant_id AND item.plan_id = plan.plan_id),
                   plan.created_at, plan.created_by, plan.expires_at
            FROM entitysync.sync_plans plan
            JOIN entitysync.sync_policies policy
              ON policy.tenant_id = plan.tenant_id AND policy.policy_id = plan.policy_id
             AND policy.version = plan.policy_version
            WHERE plan.tenant_id = @tenant_id
            ORDER BY plan.created_at DESC, plan.plan_id
            LIMIT @maximum_rows OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(
            command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(
            command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        PostgresControlPersistence.Add(command, "offset", NpgsqlDbType.Integer, offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<EntitySyncDurablePlan>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadPlan(reader));
        return result;
    }

    public async Task<EntitySyncDurablePlanPage> GetPageAsync(string tenantId, Guid planId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page <= 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var offset = checked((page - 1) * pageSize);
        const string sql = """
            SELECT tenant_id, plan_id, item_id, item_ordinal, source_vendor,
                   source_connection_id, source_entity_type, source_entity_key,
                   source_entity_id, target_vendor, target_connection_id, target_entity_type,
                   target_entity_id, action, match_score, match_type, match_reasons::text,
                   field_diffs::text, redacted_before::text, redacted_desired::text,
                   before_payload_sha256, desired_payload_sha256,
                   resolved_target_parent::text,
                   count(*) OVER ()::integer AS total_items
            FROM entitysync.sync_plan_items
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
            ORDER BY item_ordinal
            LIMIT @page_size OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(command, "page_size", NpgsqlDbType.Integer, pageSize);
        PostgresControlPersistence.Add(command, "offset", NpgsqlDbType.Integer, offset);
        var items = new List<EntitySyncDurablePlanItem>();
        var totalItems = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalItems = reader.GetInt32(23);
                items.Add(ReadPlanItem(reader));
            }
        }
        if (items.Count == 0)
        {
            const string countSql = """
                SELECT count(*)::integer FROM entitysync.sync_plan_items
                WHERE tenant_id = @tenant_id AND plan_id = @plan_id
                """;
            await using var count = dataSource.CreateCommand(countSql);
            AddPlanKey(count, tenantId, planId);
            totalItems = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        return new EntitySyncDurablePlanPage(tenantId, planId, page, pageSize, totalItems, items);
    }

    public async Task<EntitySyncInspectionSession> GetOrOpenInspectionAsync(
        string tenantId, Guid proposedInspectionId, Guid planId,
        EntitySyncSha256 planDigestSha256, string sourceConnectionId,
        long sourceConnectionGeneration, string targetConnectionId,
        long targetConnectionGeneration, EntitySyncActor actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockCurrentConnectionGenerationsAsync(
                connection, transaction, tenantId, sourceConnectionId,
                sourceConnectionGeneration, targetConnectionId,
                targetConnectionGeneration, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The plan connection generations are no longer current.");

        const string lockPlanSql = """
            SELECT 1
            FROM entitysync.sync_plans
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
              AND plan_digest_sha256 = @plan_digest_sha256
              AND source_connection_id = @source_connection_id
              AND source_connection_generation = @source_connection_generation
              AND target_connection_id = @target_connection_id
              AND target_connection_generation = @target_connection_generation
              AND status = 'Draft' AND expires_at > @now
            FOR UPDATE
            """;
        await using (var lockPlan = new NpgsqlCommand(lockPlanSql, connection, transaction))
        {
            AddInspectionIdentity(
                lockPlan, tenantId, proposedInspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration,
                targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(lockPlan, "now", NpgsqlDbType.TimestampTz, now);
            if (await lockPlan.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException(
                    "The exact draft plan was not available for inspection.");
        }

        const string existingSql = """
            SELECT inspection_id, status, inspected_at, inspected_by, completed_at
            FROM entitysync.sync_plan_inspections
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
              AND plan_digest_sha256 = @plan_digest_sha256
              AND source_connection_generation = @source_connection_generation
              AND target_connection_generation = @target_connection_generation
              AND inspected_by = @actor
            ORDER BY inspected_at, inspection_id
            LIMIT 1
            """;
        await using (var existing = new NpgsqlCommand(existingSql, connection, transaction))
        {
            AddInspectionIdentity(
                existing, tenantId, proposedInspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration,
                targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(existing, "actor", NpgsqlDbType.Text, actor.ActorId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var session = ReadInspection(
                    reader, tenantId, planId, planDigestSha256,
                    sourceConnectionId, sourceConnectionGeneration,
                    targetConnectionId, targetConnectionGeneration);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
        }

        const string insertSql = """
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by, completed_at)
            VALUES (
                @tenant_id, @inspection_id, @plan_id, @plan_digest_sha256,
                @source_connection_generation, @target_connection_generation,
                'Open', @now, @actor, NULL)
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddInspectionIdentity(
                insert, tenantId, proposedInspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration,
                targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(insert, "now", NpgsqlDbType.TimestampTz, now);
            PostgresControlPersistence.Add(insert, "actor", NpgsqlDbType.Text, actor.ActorId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EntitySyncInspectionSession(
            tenantId, proposedInspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration,
            targetConnectionId, targetConnectionGeneration,
            EntitySyncInspectionStatus.Open, now, actor, null);
    }

    public async Task<EntitySyncInspectionSession?> FindInspectionAsync(
        string tenantId, Guid planId, EntitySyncSha256 planDigestSha256,
        EntitySyncActor actor, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT inspection.inspection_id, inspection.status,
                   inspection.inspected_at, inspection.inspected_by,
                   inspection.completed_at, plan.source_connection_id,
                   plan.source_connection_generation, plan.target_connection_id,
                   plan.target_connection_generation
            FROM entitysync.sync_plan_inspections inspection
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = inspection.tenant_id
             AND plan.plan_id = inspection.plan_id
            WHERE inspection.tenant_id = @tenant_id
              AND inspection.plan_id = @plan_id
              AND inspection.plan_digest_sha256 = @plan_digest_sha256
              AND inspection.inspected_by = @actor
            ORDER BY inspection.inspected_at, inspection.inspection_id
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(
            command, "plan_digest_sha256", NpgsqlDbType.Char, planDigestSha256.Value);
        PostgresControlPersistence.Add(command, "actor", NpgsqlDbType.Text, actor.ActorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadInspection(
            reader, tenantId, planId, planDigestSha256,
            reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8));
    }

    public async Task<IReadOnlyList<EntitySyncInspectionRange>> ListInspectionRangesAsync(
        string tenantId, Guid inspectionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT range_id, range_start, range_end, inspected_at
            FROM entitysync.sync_plan_inspection_ranges
            WHERE tenant_id = @tenant_id AND inspection_id = @inspection_id
            ORDER BY range_start, range_end, range_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "inspection_id", NpgsqlDbType.Uuid, inspectionId);
        var ranges = new List<EntitySyncInspectionRange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ranges.Add(new EntitySyncInspectionRange(
                tenantId, inspectionId, reader.GetGuid(0), reader.GetInt32(1),
                reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return ranges;
    }

    public async Task<EntitySyncInspectionRange> RecordInspectionRangeAsync(
        string tenantId, Guid inspectionId, Guid rangeId, int rangeStart, int rangeEnd,
        DateTimeOffset inspectedAt, CancellationToken cancellationToken)
    {
        var range = new EntitySyncInspectionRange(
            tenantId, inspectionId, rangeId, rangeStart, rangeEnd, inspectedAt);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockInspectionConnectionGenerationsAsync(
            connection, transaction, tenantId, inspectionId, cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (
                @tenant_id, @inspection_id, @range_id, @range_start, @range_end,
                @inspected_at)
            ON CONFLICT (tenant_id, inspection_id, range_id) DO NOTHING
            RETURNING inspected_at
            """;
        DateTimeOffset persistedInspectedAt;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            AddRange(command, range);
            var inserted = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inserted is not null)
            {
                persistedInspectedAt = ToDateTimeOffset(inserted);
            }
            else
            {
                const string existingSql = """
                    SELECT inspected_at
                    FROM entitysync.sync_plan_inspection_ranges
                    WHERE tenant_id = @tenant_id AND inspection_id = @inspection_id
                      AND range_id = @range_id AND range_start = @range_start
                      AND range_end = @range_end
                    """;
                await using var existing = new NpgsqlCommand(
                    existingSql, connection, transaction);
                AddRange(existing, range);
                var persisted = await existing.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The persisted inspection range conflicts with the requested range.");
                persistedInspectedAt = ToDateTimeOffset(persisted);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EntitySyncInspectionRange(
            tenantId, inspectionId, rangeId, rangeStart, rangeEnd, persistedInspectedAt);
    }

    public async Task<EntitySyncInspectionSession> CompleteInspectionAsync(
        string tenantId, Guid inspectionId, Guid planId,
        EntitySyncSha256 planDigestSha256, string sourceConnectionId,
        long sourceConnectionGeneration, string targetConnectionId,
        long targetConnectionGeneration, DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockCurrentConnectionGenerationsAsync(
                connection, transaction, tenantId, sourceConnectionId,
                sourceConnectionGeneration, targetConnectionId,
                targetConnectionGeneration, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The plan connection generations are no longer current.");
        const string sql = """
            UPDATE entitysync.sync_plan_inspections inspection
            SET status = 'Completed', completed_at = @completed_at
            FROM entitysync.sync_plans plan
            WHERE inspection.tenant_id = @tenant_id
              AND inspection.inspection_id = @inspection_id
              AND inspection.plan_id = @plan_id
              AND inspection.plan_digest_sha256 = @plan_digest_sha256
              AND inspection.source_connection_generation = @source_connection_generation
              AND inspection.target_connection_generation = @target_connection_generation
              AND inspection.status = 'Open'
              AND plan.tenant_id = inspection.tenant_id
              AND plan.plan_id = inspection.plan_id
              AND plan.source_connection_id = @source_connection_id
              AND plan.target_connection_id = @target_connection_id
              AND plan.tenant_id = @tenant_id
            RETURNING inspection.inspected_at, inspection.inspected_by,
                      inspection.completed_at
            """;
        DateTimeOffset inspectedAt = default;
        string? inspectedBy = null;
        DateTimeOffset persistedCompletedAt = default;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            AddInspectionIdentity(
                command, tenantId, inspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration,
                targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(
                command, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                inspectedAt = reader.GetFieldValue<DateTimeOffset>(0);
                inspectedBy = reader.GetString(1);
                persistedCompletedAt = reader.GetFieldValue<DateTimeOffset>(2);
            }
        }
        if (inspectedBy is null)
        {
            const string completedSql = """
                SELECT inspected_at, inspected_by, completed_at
                FROM entitysync.sync_plan_inspections
                WHERE tenant_id = @tenant_id AND inspection_id = @inspection_id
                  AND plan_id = @plan_id
                  AND plan_digest_sha256 = @plan_digest_sha256
                  AND source_connection_generation = @source_connection_generation
                  AND target_connection_generation = @target_connection_generation
                  AND status = 'Completed' AND completed_at IS NOT NULL
                """;
            await using var completed = new NpgsqlCommand(
                completedSql, connection, transaction);
            AddInspectionIdentity(
                completed, tenantId, inspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration,
                targetConnectionId, targetConnectionGeneration);
            await using var reader = await completed.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The exact inspection was not available for completion.");
            inspectedAt = reader.GetFieldValue<DateTimeOffset>(0);
            inspectedBy = reader.GetString(1);
            persistedCompletedAt = reader.GetFieldValue<DateTimeOffset>(2);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EntitySyncInspectionSession(
            tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration,
            targetConnectionId, targetConnectionGeneration,
            EntitySyncInspectionStatus.Completed, inspectedAt,
            new EntitySyncActor(inspectedBy), persistedCompletedAt);
    }

    public async Task<bool> HasCompleteInspectionAsync(
        string tenantId, Guid inspectionId, Guid planId, EntitySyncSha256 planDigestSha256,
        string sourceConnectionId, long sourceConnectionGeneration, string targetConnectionId,
        long targetConnectionGeneration, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM entitysync.sync_plan_inspections inspection
                JOIN entitysync.sync_plans plan
                  ON plan.tenant_id = inspection.tenant_id AND plan.plan_id = inspection.plan_id
                WHERE inspection.tenant_id = @tenant_id AND inspection.inspection_id = @inspection_id
                  AND inspection.plan_id = @plan_id AND inspection.plan_digest_sha256 = @plan_digest_sha256
                  AND inspection.source_connection_generation = @source_connection_generation
                  AND inspection.target_connection_generation = @target_connection_generation
                  AND inspection.status = 'Completed' AND inspection.completed_at IS NOT NULL
                  AND plan.source_connection_id = @source_connection_id
                  AND plan.target_connection_id = @target_connection_id AND plan.tenant_id = @tenant_id)
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddInspectionIdentity(command, tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    public async Task<EntitySyncApproval> ApproveInspectionAsync(
        string tenantId, Guid approvalId, Guid inspectionId, Guid planId,
        EntitySyncSha256 planDigestSha256, string sourceConnectionId,
        long sourceConnectionGeneration, string targetConnectionId, long targetConnectionGeneration,
        EntitySyncActor actor, DateTimeOffset approvedAt, DateTimeOffset? expiresAt,
        EntitySyncAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var approval = new EntitySyncApproval(tenantId, approvalId, inspectionId, planId,
            planDigestSha256, sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
            targetConnectionGeneration, approvedAt, actor, expiresAt);
        ArgumentNullException.ThrowIfNull(auditEvent);
        using var auditDocument = System.Text.Json.JsonDocument.Parse(
            auditEvent.RedactedValues.Json);
        var expectedAuditHash = EntitySyncCanonicalDigest.Compute(
            auditDocument.RootElement);
        if (auditEvent.TenantId != tenantId
            || auditEvent.PlanId != planId
            || auditEvent.Actor != actor
            || auditEvent.OccurredAt != approvedAt
            || auditEvent.EventType != "SyncPlanApproved"
            || auditEvent.CorrelationId != approvalId.ToString("N")
            || auditEvent.RedactedValuesSha256 != expectedAuditHash
            || auditEvent.FullValuesSha256 is not null
            || auditEvent.FullValuesExpiresAt is not null)
            throw new ArgumentException(
                "The approval audit event must be redacted, digest-valid, and match the approval identity.",
                nameof(auditEvent));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockPlanPolicyIdentityAsync(
            connection, transaction, tenantId, planId, cancellationToken)
            .ConfigureAwait(false);
        if (!await LockCurrentConnectionGenerationsAsync(
                connection, transaction, tenantId, sourceConnectionId,
                sourceConnectionGeneration, targetConnectionId,
                targetConnectionGeneration, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The plan connection generations are no longer current.");
        const string advanceSql = """
            UPDATE entitysync.sync_plans plan SET status = 'Approved'
            FROM entitysync.sync_plan_inspections inspection
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
              AND plan.plan_digest_sha256 = @plan_digest_sha256
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
              AND plan.status = 'Draft' AND plan.expires_at > clock_timestamp()
              AND EXISTS (
                    SELECT 1
                    FROM entitysync.sync_policies policy
                    WHERE policy.tenant_id = plan.tenant_id
                      AND policy.policy_id = plan.policy_id
                      AND policy.version = plan.policy_version
                      AND policy.definition_sha256 = (
                          SELECT latest.definition_sha256
                          FROM entitysync.sync_policies latest
                          WHERE latest.tenant_id = plan.tenant_id
                            AND latest.policy_id = plan.policy_id
                          ORDER BY latest.version DESC
                          LIMIT 1)
                      AND policy.version = (
                          SELECT max(latest.version)
                          FROM entitysync.sync_policies latest
                          WHERE latest.tenant_id = plan.tenant_id
                            AND latest.policy_id = plan.policy_id)
                      AND policy.enabled)
              AND EXISTS (
                    SELECT 1 FROM entitysync.connection_definitions source_connection
                    WHERE source_connection.tenant_id = @tenant_id
                      AND source_connection.connection_id = plan.source_connection_id
                      AND source_connection.generation = plan.source_connection_generation)
              AND EXISTS (
                    SELECT 1 FROM entitysync.connection_definitions target_connection
                    WHERE target_connection.tenant_id = @tenant_id
                      AND target_connection.connection_id = plan.target_connection_id
                      AND target_connection.generation = plan.target_connection_generation)
              AND inspection.tenant_id = plan.tenant_id AND inspection.inspection_id = @inspection_id
              AND inspection.plan_id = plan.plan_id
              AND inspection.plan_digest_sha256 = plan.plan_digest_sha256
              AND inspection.source_connection_generation = plan.source_connection_generation
              AND inspection.target_connection_generation = plan.target_connection_generation
              AND inspection.status = 'Completed' AND inspection.completed_at IS NOT NULL
              AND inspection.inspected_by = @approved_by
              AND inspection.tenant_id = @tenant_id
            """;
        await using (var advance = new NpgsqlCommand(advanceSql, connection, transaction))
        {
            AddInspectionIdentity(advance, tenantId, inspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(advance, "approved_at", NpgsqlDbType.TimestampTz, approvedAt);
            PostgresControlPersistence.Add(
                advance, "approved_by", NpgsqlDbType.Text, actor.ActorId);
            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The exact completed inspection could not be approved.");
        }
        const string insertSql = """
            INSERT INTO entitysync.sync_approvals (
                tenant_id, approval_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                approved_at, approved_by, expires_at)
            VALUES (@tenant_id, @approval_id, @inspection_id, @plan_id, @plan_digest_sha256,
                @source_connection_generation, @target_connection_generation,
                @approved_at, @approved_by, @expires_at)
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddApproval(insert, approval);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        const string auditSql = """
            INSERT INTO entitysync.audit_events (
                tenant_id, audit_event_id, occurred_at, event_type, actor_id,
                operation_id, run_id, plan_id, item_id, correlation_id,
                redacted_values, redacted_values_sha256, full_values_sha256,
                full_values_expires_at)
            VALUES (
                @audit_tenant_id, @audit_event_id, @audit_occurred_at,
                @audit_event_type, @audit_actor_id, NULL, NULL, @audit_plan_id,
                NULL, @audit_correlation_id, @audit_redacted_values,
                @audit_redacted_values_sha256, NULL, NULL)
            """;
        await using (var appendAudit = new NpgsqlCommand(
                         auditSql, connection, transaction))
        {
            AddApprovalAudit(appendAudit, auditEvent);
            await appendAudit.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return approval;
    }

    public async Task<EntitySyncApproval?> GetApprovalAsync(
        string tenantId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT approval.tenant_id, approval.approval_id, approval.inspection_id,
                   approval.plan_id, approval.plan_digest_sha256,
                   plan.source_connection_id, approval.source_connection_generation,
                   plan.target_connection_id, approval.target_connection_generation,
                   approval.approved_at, approval.approved_by, approval.expires_at
            FROM entitysync.sync_approvals approval
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = approval.tenant_id
             AND plan.plan_id = approval.plan_id
            WHERE approval.tenant_id = @tenant_id
              AND approval.approval_id = @approval_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "approval_id", NpgsqlDbType.Uuid, approvalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new EntitySyncApproval(
            reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            new EntitySyncSha256(reader.GetString(4)), reader.GetString(5),
            reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8),
            reader.GetFieldValue<DateTimeOffset>(9), new EntitySyncActor(reader.GetString(10)),
            PostgresControlPersistence.NullableTime(reader, 11));
    }

    public async Task<bool> TryConsumeApprovalAsync(
        string tenantId, Guid approvalId, Guid inspectionId, Guid planId,
        EntitySyncSha256 planDigestSha256, string sourceConnectionId,
        long sourceConnectionGeneration, string targetConnectionId, long targetConnectionGeneration,
        EntitySyncOperation applyOperation, IReadOnlyList<EntitySyncOperationItem> operationItems,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        PostgresSyncOperationRepository.ValidateGraph(tenantId, applyOperation, operationItems);
        if (applyOperation.Mode != EntitySyncOperationMode.Apply || applyOperation.ApprovalId != approvalId
            || applyOperation.PlanId != planId || applyOperation.SourceConnectionId != sourceConnectionId
            || applyOperation.SourceConnectionGeneration != sourceConnectionGeneration
            || applyOperation.TargetConnectionId != targetConnectionId
            || applyOperation.TargetConnectionGeneration != targetConnectionGeneration)
            throw new ArgumentException("Apply operation must bind the exact approval and plan identity.", nameof(applyOperation));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockCurrentConnectionGenerationsAsync(
                connection, transaction, tenantId, sourceConnectionId,
                sourceConnectionGeneration, targetConnectionId,
                targetConnectionGeneration, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        const string consumeSql = """
            UPDATE entitysync.sync_plans plan SET status = 'Consumed'
            FROM entitysync.sync_approvals approval
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
              AND plan.plan_digest_sha256 = @plan_digest_sha256
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
              AND plan.status = 'Approved' AND plan.expires_at > clock_timestamp()
              AND EXISTS (
                    SELECT 1 FROM entitysync.connection_definitions source_connection
                    WHERE source_connection.tenant_id = @tenant_id
                      AND source_connection.connection_id = plan.source_connection_id
                      AND source_connection.generation = plan.source_connection_generation)
              AND EXISTS (
                    SELECT 1 FROM entitysync.connection_definitions target_connection
                    WHERE target_connection.tenant_id = @tenant_id
                      AND target_connection.connection_id = plan.target_connection_id
                      AND target_connection.generation = plan.target_connection_generation)
              AND approval.tenant_id = plan.tenant_id AND approval.approval_id = @approval_id
              AND approval.inspection_id = @inspection_id AND approval.plan_id = plan.plan_id
              AND approval.plan_digest_sha256 = plan.plan_digest_sha256
              AND approval.source_connection_generation = plan.source_connection_generation
              AND approval.target_connection_generation = plan.target_connection_generation
              AND (approval.expires_at IS NULL
                   OR approval.expires_at > clock_timestamp())
              AND approval.tenant_id = @tenant_id
              AND (SELECT count(*) FROM entitysync.sync_plan_items item
                   WHERE item.tenant_id = @tenant_id AND item.plan_id = @plan_id)
                  = @operation_item_count
              AND NOT EXISTS (SELECT 1 FROM entitysync.sync_operations existing
                    WHERE existing.tenant_id = @tenant_id AND existing.approval_id = @approval_id
                      AND existing.mode = 'Apply')
            """;
        await using (var consume = new NpgsqlCommand(consumeSql, connection, transaction))
        {
            AddInspectionIdentity(consume, tenantId, inspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(consume, "approval_id", NpgsqlDbType.Uuid, approvalId);
            PostgresControlPersistence.Add(consume, "operation_item_count", NpgsqlDbType.Integer, operationItems.Count);
            PostgresControlPersistence.Add(consume, "now", NpgsqlDbType.TimestampTz, now);
            if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await PostgresSyncOperationRepository.InsertGraphAsync(connection, transaction, applyOperation,
            operationItems, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryExpireAsync(string tenantId, Guid planId,
        EntitySyncSha256 planDigestSha256, EntitySyncDurablePlanStatus expectedStatus,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (expectedStatus is not (EntitySyncDurablePlanStatus.Draft or EntitySyncDurablePlanStatus.Approved))
            throw new ArgumentOutOfRangeException(nameof(expectedStatus), expectedStatus, "Only draft or approved plans can expire.");
        const string sql = """
            UPDATE entitysync.sync_plans SET status = 'Expired'
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
              AND plan_digest_sha256 = @plan_digest_sha256 AND status = @expected_status
              AND expires_at <= @now
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, planId);
        PostgresControlPersistence.Add(command, "plan_digest_sha256", NpgsqlDbType.Char, planDigestSha256.Value);
        PostgresControlPersistence.Add(command, "expected_status", NpgsqlDbType.Text, expectedStatus.ToString());
        PostgresControlPersistence.Add(command, "now", NpgsqlDbType.TimestampTz, now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task LockPlanIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(
                hashtextextended(@plan_identity, 0))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(
            command,
            "plan_identity",
            NpgsqlDbType.Text,
            $"{tenantId}:{planId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DateTimeOffset> ReadDatabaseClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT clock_timestamp()", connection, transaction);
        return ToDateTimeOffset(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The database clock was unavailable."));
    }

    private static async Task LockImportReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string callerKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(
                hashtextextended(@receipt_identity, 2))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(
            command,
            "receipt_identity",
            NpgsqlDbType.Text,
            $"{tenantId}:{callerKey}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DurablePlanImportPersistenceResult?>
        ReadImportReceiptAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string tenantId,
            string callerKey,
            EntitySyncSha256 requestSha256,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_sha256, actor_id, plan_id, plan_digest_sha256
            FROM entitysync.plan_import_receipts
            WHERE tenant_id = @tenant_id
              AND caller_key = @caller_key
            FOR UPDATE
            """;
        string storedRequestSha256;
        string storedActorId;
        Guid storedPlanId;
        EntitySyncSha256 storedPlanDigestSha256;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            PostgresControlPersistence.Add(
                command, "tenant_id", NpgsqlDbType.Text, tenantId);
            PostgresControlPersistence.Add(
                command, "caller_key", NpgsqlDbType.Text, callerKey);
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            storedRequestSha256 = reader.GetString(0).Trim();
            storedActorId = reader.GetString(1);
            storedPlanId = reader.GetGuid(2);
            storedPlanDigestSha256 =
                new EntitySyncSha256(reader.GetString(3));
        }
        if (!storedRequestSha256.Equals(
                requestSha256.Value, StringComparison.Ordinal))
            return new(DurablePlanImportPersistenceState.Conflict, null);
        if (!storedActorId.Equals(actor.ActorId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The durable plan import receipt actor binding is invalid.");
        var persisted = await ReadPersistedPlanAsync(
                connection,
                transaction,
                tenantId,
                storedPlanId,
                cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null
            || persisted.PlanDigestSha256 != storedPlanDigestSha256)
            throw new InvalidOperationException(
                "The durable plan import receipt references a missing or mismatched plan.");
        return new(DurablePlanImportPersistenceState.Replayed, persisted);
    }

    private static async Task<bool> MatchesCurrentPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySyncDurablePlan plan,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM entitysync.sync_policies policy
                WHERE policy.tenant_id = @tenant_id
                  AND policy.policy_id = @policy_id
                  AND policy.version = @policy_version
                  AND policy.definition_sha256 = @policy_definition_sha256
                  AND policy.enabled
                  AND policy.route_scope = @route_scope
                  AND COALESCE(
                      policy.definition->>'SourceConnectionId',
                      policy.definition->>'sourceConnectionId')
                      = @source_connection_id
                  AND COALESCE(
                      policy.definition->>'TargetConnectionId',
                      policy.definition->>'targetConnectionId')
                      = @target_connection_id
                  AND policy.version = (
                      SELECT max(latest.version)
                      FROM entitysync.sync_policies latest
                      WHERE latest.tenant_id = policy.tenant_id
                        AND latest.policy_id = policy.policy_id))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlan(command, plan);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? false);
    }

    private static async Task<bool?> MatchesExistingPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySyncDurablePlanManifest manifest,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT plan.plan_digest_sha256,
                   (SELECT count(*)::integer
                    FROM entitysync.sync_plan_items item
                    WHERE item.tenant_id = plan.tenant_id
                      AND item.plan_id = plan.plan_id)
            FROM entitysync.sync_plans plan
            WHERE plan.tenant_id = @tenant_id
              AND plan.plan_id = @plan_id
            FOR SHARE
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlanKey(command, manifest.Plan.TenantId, manifest.Plan.PlanId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return reader.GetString(0).Trim().Equals(
                manifest.Plan.PlanDigestSha256.Value,
                StringComparison.Ordinal)
            && reader.GetInt32(1) == manifest.Items.Count;
    }

    private static async Task<EntitySyncDurablePlan?> ReadPersistedPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT plan.tenant_id, plan.plan_id, plan.policy_id, plan.policy_version,
                   policy.definition_sha256, plan.route_scope,
                   plan.source_connection_id, plan.source_connection_generation,
                   plan.target_connection_id, plan.target_connection_generation,
                   plan.plan_digest_sha256, plan.status,
                   plan.source_search, plan.source_count, plan.source_entity_id,
                   (SELECT count(*)::integer
                    FROM entitysync.sync_plan_items item
                    WHERE item.tenant_id = plan.tenant_id
                      AND item.plan_id = plan.plan_id),
                   plan.created_at, plan.created_by, plan.expires_at
            FROM entitysync.sync_plans plan
            JOIN entitysync.sync_policies policy
              ON policy.tenant_id = plan.tenant_id
             AND policy.policy_id = plan.policy_id
             AND policy.version = plan.policy_version
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlanKey(command, tenantId, planId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPlan(reader)
            : null;
    }

    private static async Task InsertImportReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string callerKey,
        EntitySyncSha256 requestSha256,
        EntitySyncActor actor,
        EntitySyncDurablePlan plan,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO entitysync.plan_import_receipts (
                tenant_id, caller_key, request_sha256, actor_id,
                plan_id, plan_digest_sha256, created_at, expires_at)
            VALUES (
                @tenant_id, @caller_key, @request_sha256, @actor_id,
                @plan_id, @plan_digest_sha256,
                clock_timestamp(), 'infinity'::timestamptz)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(
            command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(
            command, "caller_key", NpgsqlDbType.Text, callerKey);
        PostgresControlPersistence.Add(
            command,
            "request_sha256",
            NpgsqlDbType.Char,
            requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "actor_id", NpgsqlDbType.Text, actor.ActorId);
        PostgresControlPersistence.Add(
            command, "plan_id", NpgsqlDbType.Uuid, plan.PlanId);
        PostgresControlPersistence.Add(
            command,
            "plan_digest_sha256",
            NpgsqlDbType.Char,
            plan.PlanDigestSha256.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockPlanPolicyIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT policy_id
            FROM entitysync.sync_plans
            WHERE tenant_id = @tenant_id AND plan_id = @plan_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlanKey(command, tenantId, planId);
        var policyId = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (policyId is not Guid value)
            throw new InvalidOperationException(
                "The durable plan policy identity was not available.");
        await LockPolicyIdentityAsync(
            connection, transaction, tenantId, value, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task LockPolicyIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid policyId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(hashtextextended(@policy_identity, 1))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(
            command,
            "policy_identity",
            NpgsqlDbType.Text,
            $"{tenantId}:{policyId:N}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockInspectionConnectionGenerationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid inspectionId,
        CancellationToken cancellationToken)
    {
        const string identitySql = """
            SELECT plan.source_connection_id, plan.source_connection_generation,
                   plan.target_connection_id, plan.target_connection_generation
            FROM entitysync.sync_plan_inspections inspection
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = inspection.tenant_id
             AND plan.plan_id = inspection.plan_id
            WHERE inspection.tenant_id = @tenant_id
              AND inspection.inspection_id = @inspection_id
              AND inspection.status = 'Open'
            """;
        string sourceConnectionId;
        long sourceConnectionGeneration;
        string targetConnectionId;
        long targetConnectionGeneration;
        await using (var identity = new NpgsqlCommand(
            identitySql, connection, transaction))
        {
            PostgresControlPersistence.Add(
                identity, "tenant_id", NpgsqlDbType.Text, tenantId);
            PostgresControlPersistence.Add(
                identity, "inspection_id", NpgsqlDbType.Uuid, inspectionId);
            await using var reader = await identity
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The exact open inspection was not available.");
            sourceConnectionId = reader.GetString(0);
            sourceConnectionGeneration = reader.GetInt64(1);
            targetConnectionId = reader.GetString(2);
            targetConnectionGeneration = reader.GetInt64(3);
        }
        if (!await LockCurrentConnectionGenerationsAsync(
                connection, transaction, tenantId, sourceConnectionId,
                sourceConnectionGeneration, targetConnectionId,
                targetConnectionGeneration, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The plan connection generations are no longer current.");
    }

    private static async Task<bool> LockCurrentConnectionGenerationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        CancellationToken cancellationToken)
    {
        const string lockSql = """
            SELECT connection.connection_id, connection.generation
            FROM entitysync.connection_definitions connection
            WHERE connection.tenant_id = @tenant_id
              AND connection.connection_id IN (
                  @source_connection_id, @target_connection_id)
              AND connection.enabled
            ORDER BY connection.connection_id
            FOR SHARE
            """;
        var sourceMatches = false;
        var targetMatches = false;
        await using var command = new NpgsqlCommand(lockSql, connection, transaction);
        PostgresControlPersistence.Add(
            command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(
            command, "source_connection_id", NpgsqlDbType.Text, sourceConnectionId);
        PostgresControlPersistence.Add(
            command, "target_connection_id", NpgsqlDbType.Text, targetConnectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var connectionId = reader.GetString(0);
            var generation = reader.GetInt64(1);
            sourceMatches |=
                string.Equals(
                    connectionId, sourceConnectionId, StringComparison.Ordinal)
                && generation == sourceConnectionGeneration;
            targetMatches |=
                string.Equals(
                    connectionId, targetConnectionId, StringComparison.Ordinal)
                && generation == targetConnectionGeneration;
        }
        return sourceMatches && targetMatches;
    }

    private static async Task InsertPlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        EntitySyncDurablePlan plan, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO entitysync.sync_plans (
                tenant_id, plan_id, policy_id, policy_version, route_scope,
                source_connection_id, target_connection_id, source_connection_generation,
                target_connection_generation, source_search, source_count, source_entity_id,
                plan_digest_sha256, status, created_at, created_by, expires_at)
            SELECT @tenant_id, @plan_id, policy.policy_id, policy.version, @route_scope,
                   @source_connection_id, @target_connection_id, @source_connection_generation,
                   @target_connection_generation, @source_search, @source_count, @source_entity_id,
                   @plan_digest_sha256, @status, @created_at, @created_by, @expires_at
            FROM entitysync.sync_policies policy
            WHERE policy.tenant_id = @tenant_id AND policy.policy_id = @policy_id
              AND policy.version = @policy_version
              AND policy.definition_sha256 = @policy_definition_sha256
              AND policy.enabled
              AND policy.version = (
                  SELECT max(latest.version)
                  FROM entitysync.sync_policies latest
                  WHERE latest.tenant_id = policy.tenant_id
                    AND latest.policy_id = policy.policy_id)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlan(command, plan);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The exact policy version and digest were not available for the plan.");
    }

    private static async Task CopyItemsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<EntitySyncDurablePlanItem> items,
        CancellationToken cancellationToken)
    {
        const string copySql = """
            COPY entitysync.sync_plan_items (
                tenant_id, plan_id, item_id, item_ordinal, source_vendor,
                source_connection_id, source_entity_type, source_entity_key,
                source_entity_id, target_vendor, target_connection_id,
                target_entity_type, target_entity_id, action, match_score,
                match_type, match_reasons, field_diffs, redacted_before,
                redacted_desired, before_payload_sha256, desired_payload_sha256,
                resolved_target_parent)
            FROM STDIN (FORMAT BINARY)
            """;
        await using var importer = await connection
            .BeginBinaryImportAsync(copySql, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(item.TenantId, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.PlanId, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.ItemId, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.ItemOrdinal, NpgsqlDbType.Integer, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.SourceVendor, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.SourceConnectionId, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.SourceEntityType, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.SourceEntityKey, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.SourceEntityId, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.TargetVendor, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.TargetConnectionId, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.TargetEntityType, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            if (item.TargetEntityId is null)
                await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            else
                await importer.WriteAsync(item.TargetEntityId, NpgsqlDbType.Text, cancellationToken)
                    .ConfigureAwait(false);
            await importer.WriteAsync(item.Action, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.MatchEvidence.Score, NpgsqlDbType.Integer, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.MatchEvidence.MatchType, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(
                    PostgresControlPersistence.SerializeStringList(item.MatchEvidence.Reasons),
                    NpgsqlDbType.Jsonb, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(
                    PostgresControlPersistence.SerializeFieldDiffs(item.FieldDiffs),
                    NpgsqlDbType.Jsonb, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.RedactedBefore.Json, NpgsqlDbType.Jsonb, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(item.RedactedDesired.Json, NpgsqlDbType.Jsonb, cancellationToken)
                .ConfigureAwait(false);
            if (item.BeforePayloadSha256 is null)
                await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            else
                await importer.WriteAsync(
                        item.BeforePayloadSha256.Value, NpgsqlDbType.Char, cancellationToken)
                    .ConfigureAwait(false);
            await importer.WriteAsync(
                    item.DesiredPayloadSha256.Value, NpgsqlDbType.Char, cancellationToken)
                .ConfigureAwait(false);
            var parentJson = PostgresControlPersistence.SerializeWriteParent(
                item.ResolvedTargetParent);
            if (parentJson is null)
                await importer.WriteNullAsync(cancellationToken)
                    .ConfigureAwait(false);
            else
                await importer.WriteAsync(
                    parentJson, NpgsqlDbType.Jsonb, cancellationToken)
                    .ConfigureAwait(false);
        }
        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddPlanKey(NpgsqlCommand command, string tenantId, Guid planId)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, planId);
    }

    private static void AddPlan(NpgsqlCommand command, EntitySyncDurablePlan plan)
    {
        AddPlanKey(command, plan.TenantId, plan.PlanId);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, plan.PolicyId);
        PostgresControlPersistence.Add(command, "policy_version", NpgsqlDbType.Integer, plan.PolicyVersion);
        PostgresControlPersistence.Add(command, "policy_definition_sha256", NpgsqlDbType.Char, plan.PolicyDefinitionSha256.Value);
        PostgresControlPersistence.Add(command, "route_scope", NpgsqlDbType.Text, plan.RouteScope);
        PostgresControlPersistence.Add(command, "source_connection_id", NpgsqlDbType.Text, plan.SourceConnectionId);
        PostgresControlPersistence.Add(command, "source_connection_generation", NpgsqlDbType.Bigint, plan.SourceConnectionGeneration);
        PostgresControlPersistence.Add(command, "target_connection_id", NpgsqlDbType.Text, plan.TargetConnectionId);
        PostgresControlPersistence.Add(command, "target_connection_generation", NpgsqlDbType.Bigint, plan.TargetConnectionGeneration);
        PostgresControlPersistence.Add(command, "source_search", NpgsqlDbType.Text, plan.SelectionBounds.SourceSearch);
        PostgresControlPersistence.Add(command, "source_count", NpgsqlDbType.Integer, plan.SelectionBounds.SourceCount);
        PostgresControlPersistence.Add(command, "source_entity_id", NpgsqlDbType.Text, plan.SelectionBounds.SourceEntityId);
        PostgresControlPersistence.Add(command, "plan_digest_sha256", NpgsqlDbType.Char, plan.PlanDigestSha256.Value);
        PostgresControlPersistence.Add(command, "status", NpgsqlDbType.Text, plan.Status.ToString());
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, plan.CreatedAt);
        PostgresControlPersistence.Add(command, "created_by", NpgsqlDbType.Text, plan.CreatedBy.ActorId);
        PostgresControlPersistence.Add(command, "expires_at", NpgsqlDbType.TimestampTz, plan.ExpiresAt);
    }


    private static void AddInspectionIdentity(NpgsqlCommand command, string tenantId, Guid inspectionId,
        Guid planId, EntitySyncSha256 planDigestSha256, string sourceConnectionId,
        long sourceConnectionGeneration, string targetConnectionId, long targetConnectionGeneration)
    {
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(command, "inspection_id", NpgsqlDbType.Uuid, inspectionId);
        PostgresControlPersistence.Add(command, "plan_digest_sha256", NpgsqlDbType.Char, planDigestSha256.Value);
        PostgresControlPersistence.Add(command, "source_connection_id", NpgsqlDbType.Text, sourceConnectionId);
        PostgresControlPersistence.Add(command, "source_connection_generation", NpgsqlDbType.Bigint, sourceConnectionGeneration);
        PostgresControlPersistence.Add(command, "target_connection_id", NpgsqlDbType.Text, targetConnectionId);
        PostgresControlPersistence.Add(command, "target_connection_generation", NpgsqlDbType.Bigint, targetConnectionGeneration);
    }

    private static void AddApproval(NpgsqlCommand command, EntitySyncApproval approval)
    {
        AddInspectionIdentity(command, approval.TenantId, approval.InspectionId, approval.PlanId,
            approval.PlanDigestSha256, approval.SourceConnectionId, approval.SourceConnectionGeneration,
            approval.TargetConnectionId, approval.TargetConnectionGeneration);
        PostgresControlPersistence.Add(command, "approval_id", NpgsqlDbType.Uuid, approval.ApprovalId);
        PostgresControlPersistence.Add(command, "approved_at", NpgsqlDbType.TimestampTz, approval.ApprovedAt);
        PostgresControlPersistence.Add(command, "approved_by", NpgsqlDbType.Text, approval.ApprovedBy.ActorId);
        PostgresControlPersistence.Add(command, "expires_at", NpgsqlDbType.TimestampTz, approval.ExpiresAt);
    }

    private static void AddRange(
        NpgsqlCommand command,
        EntitySyncInspectionRange range)
    {
        PostgresControlPersistence.Add(
            command, "tenant_id", NpgsqlDbType.Text, range.TenantId);
        PostgresControlPersistence.Add(
            command, "inspection_id", NpgsqlDbType.Uuid, range.InspectionId);
        PostgresControlPersistence.Add(
            command, "range_id", NpgsqlDbType.Uuid, range.RangeId);
        PostgresControlPersistence.Add(
            command, "range_start", NpgsqlDbType.Integer, range.RangeStart);
        PostgresControlPersistence.Add(
            command, "range_end", NpgsqlDbType.Integer, range.RangeEnd);
        PostgresControlPersistence.Add(
            command, "inspected_at", NpgsqlDbType.TimestampTz, range.InspectedAt);
    }

    private static void AddApprovalAudit(
        NpgsqlCommand command,
        EntitySyncAuditEvent auditEvent)
    {
        PostgresControlPersistence.Add(
            command, "audit_tenant_id", NpgsqlDbType.Text, auditEvent.TenantId);
        PostgresControlPersistence.Add(
            command, "audit_event_id", NpgsqlDbType.Uuid, auditEvent.AuditEventId);
        PostgresControlPersistence.Add(
            command, "audit_occurred_at", NpgsqlDbType.TimestampTz, auditEvent.OccurredAt);
        PostgresControlPersistence.Add(
            command, "audit_event_type", NpgsqlDbType.Text, auditEvent.EventType);
        PostgresControlPersistence.Add(
            command, "audit_actor_id", NpgsqlDbType.Text, auditEvent.Actor.ActorId);
        PostgresControlPersistence.Add(
            command, "audit_plan_id", NpgsqlDbType.Uuid, auditEvent.PlanId);
        PostgresControlPersistence.Add(
            command, "audit_correlation_id", NpgsqlDbType.Text, auditEvent.CorrelationId);
        PostgresControlPersistence.Add(
            command, "audit_redacted_values", NpgsqlDbType.Jsonb, auditEvent.RedactedValues.Json);
        PostgresControlPersistence.Add(
            command,
            "audit_redacted_values_sha256",
            NpgsqlDbType.Char,
            auditEvent.RedactedValuesSha256.Value);
    }

    private static EntitySyncInspectionSession ReadInspection(
        NpgsqlDataReader reader,
        string tenantId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration) =>
        new(
            tenantId,
            reader.GetGuid(0),
            planId,
            planDigestSha256,
            sourceConnectionId,
            sourceConnectionGeneration,
            targetConnectionId,
            targetConnectionGeneration,
            PostgresControlPersistence.ParseEnum<EntitySyncInspectionStatus>(
                reader.GetString(1)),
            reader.GetFieldValue<DateTimeOffset>(2),
            new EntitySyncActor(reader.GetString(3)),
            PostgresControlPersistence.NullableTime(reader, 4));

    private static DateTimeOffset ToDateTimeOffset(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Database timestamp has unsupported type '{value.GetType().Name}'.")
        };

    private static async Task CompleteCreationInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.sync_plan_creation_claims
            SET state = 'Completed',
                result_plan_id = @plan_id,
                result_plan_digest_sha256 = @plan_digest_sha256,
                lease_expires_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant_id
              AND plan_id = @plan_id
              AND request_sha256 = @request_sha256
              AND owner_token = @owner_token
              AND state = 'InProgress'
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(
            command, "plan_digest_sha256", NpgsqlDbType.Char, planDigestSha256.Value);
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "owner_token", NpgsqlDbType.Uuid, ownerToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException(
                "The durable creation claim could not be completed atomically.");
    }

    private static async Task LockCreationOwnershipAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM entitysync.sync_plan_creation_claims
            WHERE tenant_id = @tenant_id
              AND plan_id = @plan_id
              AND request_sha256 = @request_sha256
              AND owner_token = @owner_token
              AND state = 'InProgress'
              AND lease_expires_at > clock_timestamp()
            FOR UPDATE
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddPlanKey(command, tenantId, planId);
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "owner_token", NpgsqlDbType.Uuid, ownerToken);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException(
                "Ownership of the durable creation claim was lost before persistence.");
    }

    private static string ValidateCreationArguments(
        string tenantId,
        Guid planId,
        EntitySyncSha256 requestSha256,
        Guid ownerToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (planId == Guid.Empty)
            throw new ArgumentException("Plan ID is required.", nameof(planId));
        ArgumentNullException.ThrowIfNull(requestSha256);
        if (ownerToken == Guid.Empty)
            throw new ArgumentException("Owner token is required.", nameof(ownerToken));
        return tenantId.Trim();
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), "The creation lease duration must be positive.");
    }

    private static void AddCreationParameters(
        NpgsqlCommand command,
        EntitySyncSha256 requestSha256,
        Guid ownerToken,
        TimeSpan leaseDuration)
    {
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "owner_token", NpgsqlDbType.Uuid, ownerToken);
        PostgresControlPersistence.Add(
            command, "lease_duration", NpgsqlDbType.Interval, leaseDuration);
    }

    private static EntitySyncDurablePlan ReadPlan(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3),
        new EntitySyncSha256(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
        reader.GetInt64(7), reader.GetString(8), reader.GetInt64(9),
        new EntitySyncSha256(reader.GetString(10)),
        PostgresControlPersistence.ParseEnum<EntitySyncDurablePlanStatus>(reader.GetString(11)),
        new EntitySyncSelectionBounds(PostgresControlPersistence.NullableString(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetInt32(13),
            PostgresControlPersistence.NullableString(reader, 14)), reader.GetInt32(15),
        reader.GetFieldValue<DateTimeOffset>(16), new EntitySyncActor(reader.GetString(17)),
        reader.GetFieldValue<DateTimeOffset>(18));

    private static EntitySyncDurablePlanItem ReadPlanItem(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
        PostgresControlPersistence.NullableString(reader, 12), reader.GetString(13),
        new EntitySyncMatchEvidence(reader.GetInt32(14), reader.GetString(15),
            PostgresControlPersistence.DeserializeStringList(reader.GetString(16))),
        new EntitySyncJsonValue(reader.GetString(18)), new EntitySyncJsonValue(reader.GetString(19)),
        PostgresControlPersistence.NullableHash(reader, 20),
        new EntitySyncSha256(reader.GetString(21)),
        PostgresControlPersistence.DeserializeFieldDiffs(reader.GetString(17)),
        PostgresControlPersistence.DeserializeWriteParent(
            PostgresControlPersistence.NullableString(reader, 22)));


    private sealed record DurablePlanImportRequestDigest(
        string TenantId,
        Guid PlanId,
        string PlanDigestSha256,
        string ActorId);

}

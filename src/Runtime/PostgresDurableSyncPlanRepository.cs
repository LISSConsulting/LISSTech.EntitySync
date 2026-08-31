using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresDurableSyncPlanRepository(NpgsqlDataSource dataSource)
    : IDurableSyncPlanRepository
{
    public async Task InsertAsync(string tenantId, EntitySyncDurablePlanManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        PostgresControlPersistence.RequireTenant(tenantId, manifest.Plan.TenantId, nameof(manifest));
        if (manifest.Items.Any(item => item.TenantId != tenantId || item.PlanId != manifest.Plan.PlanId))
            throw new ArgumentException("Every manifest item must belong to the plan.", nameof(manifest));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertPlanAsync(connection, transaction, manifest.Plan, cancellationToken).ConfigureAwait(false);
        await CopyItemsAsync(connection, manifest.Items, cancellationToken)
            .ConfigureAwait(false);
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
                totalItems = reader.GetInt32(22);
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

    public async Task<EntitySyncInspectionSession> OpenInspectionAsync(
        string tenantId, Guid inspectionId, Guid planId, EntitySyncSha256 planDigestSha256,
        string sourceConnectionId, long sourceConnectionGeneration, string targetConnectionId,
        long targetConnectionGeneration, EntitySyncActor actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO entitysync.sync_plan_inspections (
                tenant_id, inspection_id, plan_id, plan_digest_sha256,
                source_connection_generation, target_connection_generation,
                status, inspected_at, inspected_by, completed_at)
            SELECT plan.tenant_id, @inspection_id, plan.plan_id, plan.plan_digest_sha256,
                   plan.source_connection_generation, plan.target_connection_generation,
                   'Open', @now, @actor, NULL
            FROM entitysync.sync_plans plan
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
              AND plan.plan_digest_sha256 = @plan_digest_sha256
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
              AND plan.status = 'Draft' AND plan.expires_at > @now
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
            RETURNING inspected_at, inspected_by
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddInspectionIdentity(command, tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
        PostgresControlPersistence.Add(command, "now", NpgsqlDbType.TimestampTz, now);
        PostgresControlPersistence.Add(command, "actor", NpgsqlDbType.Text, actor.ActorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The exact draft plan was not available for inspection.");
        return new EntitySyncInspectionSession(tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration,
            EntitySyncInspectionStatus.Open, reader.GetFieldValue<DateTimeOffset>(0),
            new EntitySyncActor(reader.GetString(1)), null);
    }

    public async Task<EntitySyncInspectionRange> RecordInspectionRangeAsync(
        string tenantId, Guid inspectionId, Guid rangeId, int rangeStart, int rangeEnd,
        DateTimeOffset inspectedAt, CancellationToken cancellationToken)
    {
        var range = new EntitySyncInspectionRange(tenantId, inspectionId, rangeId, rangeStart, rangeEnd, inspectedAt);
        const string sql = """
            INSERT INTO entitysync.sync_plan_inspection_ranges (
                tenant_id, inspection_id, range_id, range_start, range_end, inspected_at)
            VALUES (@tenant_id, @inspection_id, @range_id, @range_start, @range_end, @inspected_at)
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "inspection_id", NpgsqlDbType.Uuid, inspectionId);
        PostgresControlPersistence.Add(command, "range_id", NpgsqlDbType.Uuid, rangeId);
        PostgresControlPersistence.Add(command, "range_start", NpgsqlDbType.Integer, rangeStart);
        PostgresControlPersistence.Add(command, "range_end", NpgsqlDbType.Integer, rangeEnd);
        PostgresControlPersistence.Add(command, "inspected_at", NpgsqlDbType.TimestampTz, inspectedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return range;
    }

    public async Task<EntitySyncInspectionSession> CompleteInspectionAsync(
        string tenantId, Guid inspectionId, Guid planId, EntitySyncSha256 planDigestSha256,
        string sourceConnectionId, long sourceConnectionGeneration, string targetConnectionId,
        long targetConnectionGeneration, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.sync_plan_inspections inspection
            SET status = 'Completed', completed_at = @completed_at
            FROM entitysync.sync_plans plan
            WHERE inspection.tenant_id = @tenant_id AND inspection.inspection_id = @inspection_id
              AND inspection.plan_id = @plan_id AND inspection.plan_digest_sha256 = @plan_digest_sha256
              AND inspection.source_connection_generation = @source_connection_generation
              AND inspection.target_connection_generation = @target_connection_generation
              AND inspection.status = 'Open'
              AND plan.tenant_id = inspection.tenant_id AND plan.plan_id = inspection.plan_id
              AND plan.source_connection_id = @source_connection_id
              AND plan.target_connection_id = @target_connection_id AND plan.tenant_id = @tenant_id
            RETURNING inspection.inspected_at, inspection.inspected_by, inspection.completed_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddInspectionIdentity(command, tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
        PostgresControlPersistence.Add(command, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The exact open inspection was not available for completion.");
        return new EntitySyncInspectionSession(tenantId, inspectionId, planId, planDigestSha256,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration,
            EntitySyncInspectionStatus.Completed, reader.GetFieldValue<DateTimeOffset>(0),
            new EntitySyncActor(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2));
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
        CancellationToken cancellationToken)
    {
        var approval = new EntitySyncApproval(tenantId, approvalId, inspectionId, planId,
            planDigestSha256, sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
            targetConnectionGeneration, approvedAt, actor, expiresAt);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string advanceSql = """
            UPDATE entitysync.sync_plans plan SET status = 'Approved'
            FROM entitysync.sync_plan_inspections inspection
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
              AND plan.plan_digest_sha256 = @plan_digest_sha256
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
              AND plan.status = 'Draft' AND plan.expires_at > @approved_at
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
              AND inspection.tenant_id = @tenant_id
            """;
        await using (var advance = new NpgsqlCommand(advanceSql, connection, transaction))
        {
            AddInspectionIdentity(advance, tenantId, inspectionId, planId, planDigestSha256,
                sourceConnectionId, sourceConnectionGeneration, targetConnectionId, targetConnectionGeneration);
            PostgresControlPersistence.Add(advance, "approved_at", NpgsqlDbType.TimestampTz, approvedAt);
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return approval;
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
        const string consumeSql = """
            UPDATE entitysync.sync_plans plan SET status = 'Consumed'
            FROM entitysync.sync_approvals approval
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
              AND plan.plan_digest_sha256 = @plan_digest_sha256
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
              AND plan.status = 'Approved' AND plan.expires_at > @now
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
              AND (approval.expires_at IS NULL OR approval.expires_at > @now)
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
              AND policy.version = @policy_version AND policy.definition_sha256 = @policy_definition_sha256
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
                redacted_desired, before_payload_sha256, desired_payload_sha256)
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
        PostgresControlPersistence.NullableHash(reader, 20), new EntitySyncSha256(reader.GetString(21)),
        PostgresControlPersistence.DeserializeFieldDiffs(reader.GetString(17)));
}

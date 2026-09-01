using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresSyncOperationRepository(NpgsqlDataSource dataSource) : ISyncOperationRepository
{
    public async Task InsertAsync(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken)
    {
        ValidateGraph(tenantId, operation, items);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertGraphAsync(connection, transaction, operation, items, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<bool> TryInsertAsync(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            await InsertAsync(tenantId, operation, items, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<EntitySyncOperation?> FindByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT operation.tenant_id, operation.operation_id, operation.plan_id,
                   operation.approval_id, operation.route_scope,
                   plan.source_connection_id, operation.source_connection_generation,
                   plan.target_connection_id, operation.target_connection_generation,
                   operation.mode, operation.status, operation.idempotency_key,
                   operation.lease_owner, operation.lease_expires_at, operation.attempt,
                   operation.created_at, operation.queued_at, operation.started_at,
                   operation.completed_at, operation.request_sha256,
                   operation.total_count, operation.succeeded_count,
                   operation.failed_count, operation.skipped_count, operation.unknown_count
            FROM entitysync.sync_operations operation
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = operation.tenant_id
             AND plan.plan_id = operation.plan_id
            WHERE operation.tenant_id = @tenant_id
              AND operation.idempotency_key = @idempotency_key
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(
            command, "idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadOperation(reader)
            : null;
    }


    public async Task<EntitySyncOperation?> GetAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT operation.tenant_id, operation.operation_id, operation.plan_id,
                   operation.approval_id, operation.route_scope,
                   plan.source_connection_id, operation.source_connection_generation,
                   plan.target_connection_id, operation.target_connection_generation,
                   operation.mode, operation.status, operation.idempotency_key,
                   operation.lease_owner, operation.lease_expires_at, operation.attempt,
                   operation.created_at, operation.queued_at, operation.started_at,
                   operation.completed_at, operation.request_sha256,
                   operation.total_count, operation.succeeded_count,
                   operation.failed_count, operation.skipped_count, operation.unknown_count
            FROM entitysync.sync_operations operation
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = operation.tenant_id AND plan.plan_id = operation.plan_id
            WHERE operation.tenant_id = @tenant_id AND operation.operation_id = @operation_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddOperationKey(command, tenantId, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperation(reader) : null;
    }

    public async Task<IReadOnlyList<EntitySyncOperationItem>> GetItemsAsync(
        string tenantId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, operation_id, plan_id, item_id, source_vendor,
                   source_connection_id, source_entity_type, source_entity_key,
                   source_entity_id, target_vendor, target_connection_id,
                   target_entity_type, target_entity_id, action, redacted_before::text,
                   redacted_desired::text, before_payload_sha256,
                   desired_payload_sha256, after_payload_sha256, snapshots_expires_at,
                   vendor_request_id, outcome, error_code, error_message, started_at,
                   completed_at, dispatch_started_at, vendor_target_entity_id,
                   safe_write_code
            FROM entitysync.sync_operation_items
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
            ORDER BY item_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddOperationKey(command, tenantId, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EntitySyncOperationItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadItem(reader));
        return result;
    }
    public async Task<EntitySyncOperationItem?> GetItemAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, operation_id, plan_id, item_id, source_vendor,
                   source_connection_id, source_entity_type, source_entity_key,
                   source_entity_id, target_vendor, target_connection_id,
                   target_entity_type, target_entity_id, action, redacted_before::text,
                   redacted_desired::text, before_payload_sha256,
                   desired_payload_sha256, after_payload_sha256, snapshots_expires_at,
                   vendor_request_id, outcome, error_code, error_message, started_at,
                   completed_at, dispatch_started_at, vendor_target_entity_id,
                   safe_write_code
            FROM entitysync.sync_operation_items
            WHERE tenant_id = @tenant_id
              AND operation_id = @operation_id
              AND item_id = @item_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadItem(reader)
            : null;
    }


    public async Task<EntitySyncOperation?> TryLeaseNextAsync(
        string tenantId,
        string leaseOwner,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwner));
        if (leaseExpiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt), leaseExpiresAt, "Lease expiry must follow transaction time.");
        const string sql = """
            WITH candidate AS (
                SELECT operation.tenant_id, operation.operation_id
                FROM entitysync.sync_operations operation
                WHERE operation.tenant_id = @tenant_id
                  AND (operation.status = 'Queued'
                       OR (operation.status IN ('Leased','Running')
                           AND operation.lease_expires_at <= @now))
                ORDER BY operation.queued_at, operation.operation_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            ), leased AS (
                UPDATE entitysync.sync_operations operation
                SET status = 'Leased',
                    lease_owner = @lease_owner,
                    lease_expires_at = @lease_expires_at,
                    attempt = operation.attempt + 1,
                    started_at = NULL,
                    completed_at = NULL
                FROM candidate
                WHERE operation.tenant_id = candidate.tenant_id
                  AND operation.operation_id = candidate.operation_id
                  AND operation.tenant_id = @tenant_id
                RETURNING operation.*
            )
            SELECT leased.tenant_id, leased.operation_id, leased.plan_id,
                   leased.approval_id, leased.route_scope,
                   plan.source_connection_id, leased.source_connection_generation,
                   plan.target_connection_id, leased.target_connection_generation,
                   leased.mode, leased.status, leased.idempotency_key,
                   leased.lease_owner, leased.lease_expires_at, leased.attempt,
                   leased.created_at, leased.queued_at, leased.started_at,
                   leased.completed_at, leased.request_sha256, leased.total_count,
                   leased.succeeded_count, leased.failed_count, leased.skipped_count,
                   leased.unknown_count
            FROM leased
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = leased.tenant_id AND plan.plan_id = leased.plan_id
            WHERE leased.tenant_id = @tenant_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "now", NpgsqlDbType.TimestampTz, now);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner.Trim());
        PostgresControlPersistence.Add(command, "lease_expires_at", NpgsqlDbType.TimestampTz, leaseExpiresAt);
        EntitySyncOperation? leasedOperation;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            leasedOperation = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadOperation(reader)
                : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return leasedOperation;
    }

    public async Task<bool> TryReplaceAsync(
        string tenantId,
        Guid operationId,
        EntitySyncOperationStatus expectedStatus,
        EntitySyncOperation replacement,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(
            tenantId, replacement.TenantId, nameof(replacement));
        if (operationId != replacement.OperationId)
            throw new ArgumentException(
                "Replacement operation ID must match.", nameof(replacement));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string currentSql = """
            SELECT operation.tenant_id, operation.operation_id, operation.plan_id,
                   operation.approval_id, operation.route_scope,
                   plan.source_connection_id, operation.source_connection_generation,
                   plan.target_connection_id, operation.target_connection_generation,
                   operation.mode, operation.status, operation.idempotency_key,
                   operation.lease_owner, operation.lease_expires_at, operation.attempt,
                   operation.created_at, operation.queued_at, operation.started_at,
                   operation.completed_at, operation.request_sha256,
                   operation.total_count, operation.succeeded_count,
                   operation.failed_count, operation.skipped_count, operation.unknown_count
            FROM entitysync.sync_operations operation
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = operation.tenant_id
             AND plan.plan_id = operation.plan_id
            WHERE operation.tenant_id = @tenant_id
              AND operation.operation_id = @operation_id
            FOR UPDATE OF operation
            """;
        EntitySyncOperation? current;
        await using (var read = new NpgsqlCommand(currentSql, connection, transaction))
        {
            AddOperationKey(read, tenantId, operationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadOperation(reader)
                : null;
        }
        if (current is null || current.Status != expectedStatus)
            return false;
        ValidateImmutableIdentity(current, replacement);
        ValidateTransition(current, replacement);

        const string updateSql = """
            UPDATE entitysync.sync_operations operation
            SET status = @status,
                lease_owner = @lease_owner,
                lease_expires_at = @lease_expires_at,
                attempt = @replacement_attempt,
                started_at = @started_at,
                completed_at = @completed_at
            WHERE operation.tenant_id = @tenant_id
              AND operation.operation_id = @operation_id
              AND operation.status = @expected_status
              AND operation.attempt = @current_attempt
              AND (@expected_status NOT IN ('Leased','Running')
                   OR operation.lease_expires_at > now())
              AND (
                    @status = 'Cancelled'
                    OR @status NOT IN ('Succeeded','Partial','Failed')
                    OR (
                        @status = 'Succeeded'
                        AND NOT EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome NOT IN ('Succeeded','Skipped')))
                    OR (
                        @status = 'Partial'
                        AND EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome = 'Failed')
                        AND EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome IN ('Succeeded','Skipped'))
                        AND NOT EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome IN ('Pending','Unknown')))
                    OR (
                        @status = 'Failed'
                        AND EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome = 'Failed')
                        AND NOT EXISTS (
                            SELECT 1 FROM entitysync.sync_operation_items item
                            WHERE item.tenant_id = @tenant_id
                              AND item.operation_id = @operation_id
                              AND item.outcome IN (
                                  'Succeeded','Skipped','Pending','Unknown'))))
            """;
        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        AddOperationKey(update, tenantId, operationId);
        PostgresControlPersistence.Add(
            update, "expected_status", NpgsqlDbType.Text, expectedStatus.ToString());
        PostgresControlPersistence.Add(
            update, "status", NpgsqlDbType.Text, replacement.Status.ToString());
        PostgresControlPersistence.Add(
            update, "lease_owner", NpgsqlDbType.Text, replacement.LeaseOwner);
        PostgresControlPersistence.Add(
            update, "lease_expires_at", NpgsqlDbType.TimestampTz,
            replacement.LeaseExpiresAt);
        PostgresControlPersistence.Add(
            update, "current_attempt", NpgsqlDbType.Integer, current.Attempt);
        PostgresControlPersistence.Add(
            update, "replacement_attempt", NpgsqlDbType.Integer, replacement.Attempt);
        PostgresControlPersistence.Add(
            update, "started_at", NpgsqlDbType.TimestampTz, replacement.StartedAt);
        PostgresControlPersistence.Add(
            update, "completed_at", NpgsqlDbType.TimestampTz, replacement.CompletedAt);
        var replaced = await update.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return replaced;
    }

    public async Task<bool> TryReplaceItemAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset now,
        EntitySyncItemOutcome expectedOutcome,
        EntitySyncOperationItem replacement,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, replacement.TenantId, nameof(replacement));
        if (replacement.OperationId != operationId || replacement.PlanId != planId || replacement.ItemId != itemId)
            throw new ArgumentException("Replacement item identity must match.", nameof(replacement));
        if (expectedOutcome is not (EntitySyncItemOutcome.Pending or EntitySyncItemOutcome.Unknown)
            || replacement.Outcome == EntitySyncItemOutcome.Pending)
            throw new InvalidOperationException(
                "Operation items allow only Pending/Unknown-to-terminal transitions.");
        if (string.IsNullOrWhiteSpace(leaseOwner))
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwner));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string operationSql = """
            SELECT operation.attempt, operation.lease_owner, operation.status,
                   operation.lease_expires_at > now()
            FROM entitysync.sync_operations operation
            WHERE operation.tenant_id = @tenant_id
              AND operation.operation_id = @operation_id
              AND operation.plan_id = @plan_id
            FOR UPDATE
            """;
        var operationIsMutable = false;
        await using (var operationCommand = new NpgsqlCommand(
            operationSql, connection, transaction))
        {
            AddOperationKey(operationCommand, tenantId, operationId);
            PostgresControlPersistence.Add(
                operationCommand, "plan_id", NpgsqlDbType.Uuid, planId);
            await using var reader = await operationCommand
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = PostgresControlPersistence.ParseEnum<EntitySyncOperationStatus>(
                    reader.GetString(2));
                operationIsMutable =
                    reader.GetInt32(0) == expectedOperationAttempt
                    && !reader.IsDBNull(1)
                    && string.Equals(
                        reader.GetString(1), leaseOwner, StringComparison.Ordinal)
                    && status is EntitySyncOperationStatus.Leased
                        or EntitySyncOperationStatus.Running
                    && reader.GetBoolean(3);
            }
        }
        if (!operationIsMutable)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        const string sql = """
            UPDATE entitysync.sync_operation_items item
            SET redacted_before = @redacted_before,
                before_payload_sha256 = @before_payload_sha256,
                after_payload_sha256 = @after_payload_sha256,
                vendor_request_id = @vendor_request_id,
                dispatch_started_at = @dispatch_started_at,
                vendor_target_entity_id = @vendor_target_entity_id,
                safe_write_code = @safe_write_code,
                outcome = @outcome,
                error_code = @error_code,
                error_message = @error_message,
                started_at = @started_at,
                completed_at = @completed_at
            WHERE item.tenant_id = @tenant_id
              AND item.operation_id = @operation_id
              AND item.plan_id = @plan_id
              AND item.item_id = @item_id
              AND item.outcome = @expected_outcome
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddItemMutable(command, replacement);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, operationId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, planId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(
            command, "expected_outcome", NpgsqlDbType.Text, expectedOutcome.ToString());
        var replaced = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return replaced;
    }

    public async Task<DispatchPreparationResult> TryPrepareDispatchAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        Guid policyId,
        int policyVersion,
        EntitySyncSha256 policyDefinitionSha256,
        EntitySyncOperationItem preparedItem,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, preparedItem.TenantId, nameof(preparedItem));
        PostgresControlPersistence.RequireTenant(tenantId, snapshot.TenantId, nameof(snapshot));
        if (preparedItem.OperationId != operationId || preparedItem.PlanId != planId
            || preparedItem.ItemId != itemId || snapshot.OperationId != operationId
            || snapshot.ItemId != itemId || preparedItem.DispatchStartedAt is null
            || preparedItem.VendorRequestId is null)
            throw new ArgumentException("Prepared dispatch identity and boundary are incomplete.");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireDispatchLocksAsync(
            connection, transaction, tenantId, planId, policyId, cancellationToken)
            .ConfigureAwait(false);

        const string stateSql = """
            SELECT operation.attempt = @expected_attempt
                       AND operation.lease_owner = @lease_owner
                       AND operation.status = 'Running'
                       AND operation.lease_expires_at > clock_timestamp() AS lease_valid,
                   policy.enabled
                       AND policy.version = @policy_version
                       AND policy.definition_sha256 = @policy_definition_sha256
                       AND policy.version = (
                           SELECT max(latest.version)
                           FROM entitysync.sync_policies latest
                           WHERE latest.tenant_id = policy.tenant_id
                             AND latest.policy_id = policy.policy_id) AS policy_valid,
                   source_connection.enabled
                       AND source_connection.generation = operation.source_connection_generation
                       AND target_connection.enabled
                       AND target_connection.generation = operation.target_connection_generation
                       AS connections_valid,
                   item.dispatch_started_at IS NOT NULL AS already_started,
                   item.outcome,
                   EXISTS (
                       SELECT 1
                       FROM entitysync.entity_exclusions exclusion
                       WHERE item.action = 'Create'
                         AND exclusion.tenant_id = item.tenant_id
                         AND exclusion.source_vendor = item.source_vendor
                         AND exclusion.source_connection_id = item.source_connection_id
                         AND exclusion.source_entity_type = item.source_entity_type
                         AND exclusion.target_vendor = item.target_vendor
                         AND exclusion.target_connection_id = item.target_connection_id
                         AND exclusion.target_entity_type = item.target_entity_type
                         AND exclusion.source_entity_key = lower(item.source_entity_id)
                         AND exclusion.revoked_at IS NULL) AS excluded
            FROM entitysync.sync_operations operation
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = operation.tenant_id AND plan.plan_id = operation.plan_id
            JOIN entitysync.sync_policies policy
              ON policy.tenant_id = plan.tenant_id AND policy.policy_id = plan.policy_id
             AND policy.version = plan.policy_version
            JOIN entitysync.connection_definitions source_connection
              ON source_connection.tenant_id = operation.tenant_id
             AND source_connection.connection_id = plan.source_connection_id
            JOIN entitysync.connection_definitions target_connection
              ON target_connection.tenant_id = operation.tenant_id
             AND target_connection.connection_id = plan.target_connection_id
            JOIN entitysync.sync_operation_items item
              ON item.tenant_id = operation.tenant_id
             AND item.operation_id = operation.operation_id
             AND item.item_id = @item_id
            WHERE operation.tenant_id = @tenant_id
              AND operation.operation_id = @operation_id
              AND operation.plan_id = @plan_id
              AND plan.policy_id = @policy_id
            FOR UPDATE OF operation, item
            """;
        bool leaseValid;
        bool policyValid;
        bool connectionsValid;
        bool alreadyStarted;
        bool excluded;
        EntitySyncItemOutcome outcome;
        await using (var state = new NpgsqlCommand(stateSql, connection, transaction))
        {
            AddDispatchIdentity(
                state, tenantId, operationId, planId, itemId, expectedOperationAttempt,
                leaseOwner, policyId, policyVersion, policyDefinitionSha256);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DispatchPreparationResult(DispatchPreparationOutcome.NotFound, null);
            }
            leaseValid = reader.GetBoolean(0);
            policyValid = reader.GetBoolean(1);
            connectionsValid = reader.GetBoolean(2);
            alreadyStarted = reader.GetBoolean(3);
            outcome = PostgresControlPersistence.ParseEnum<EntitySyncItemOutcome>(
                reader.GetString(4));
            excluded = reader.GetBoolean(5);
        }
        var rejection = !leaseValid
            ? DispatchPreparationOutcome.StaleLease
            : !policyValid
                ? DispatchPreparationOutcome.PolicyChanged
                : !connectionsValid
                    ? DispatchPreparationOutcome.ConnectionChanged
                    : alreadyStarted
                        ? DispatchPreparationOutcome.AlreadyDispatchStarted
                        : excluded
                            ? DispatchPreparationOutcome.Excluded
                            : outcome != EntitySyncItemOutcome.Pending
                                ? DispatchPreparationOutcome.NotFound
                                : DispatchPreparationOutcome.Prepared;
        if (rejection != DispatchPreparationOutcome.Prepared)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DispatchPreparationResult(rejection, null);
        }
        await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken)
            .ConfigureAwait(false);
        const string updateSql = """
            UPDATE entitysync.sync_operation_items
            SET redacted_before = @redacted_before,
                before_payload_sha256 = @before_payload_sha256,
                vendor_request_id = @vendor_request_id,
                dispatch_started_at = @dispatch_started_at,
                started_at = @started_at
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
              AND plan_id = @plan_id AND item_id = @item_id
              AND outcome = 'Pending' AND dispatch_started_at IS NULL
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddItemMutable(update, preparedItem);
            PostgresControlPersistence.Add(update, "tenant_id", NpgsqlDbType.Text, tenantId);
            PostgresControlPersistence.Add(update, "operation_id", NpgsqlDbType.Uuid, operationId);
            PostgresControlPersistence.Add(update, "plan_id", NpgsqlDbType.Uuid, planId);
            PostgresControlPersistence.Add(update, "item_id", NpgsqlDbType.Uuid, itemId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DispatchPreparationResult(
                    DispatchPreparationOutcome.AlreadyDispatchStarted, null);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DispatchPreparationResult(DispatchPreparationOutcome.Prepared, preparedItem);
    }

    public async Task<bool> TryRecordItemAsync(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedOperationAttempt,
        string leaseOwner,
        EntitySyncItemOutcome expectedOutcome,
        EntitySyncOperationItem replacement,
        EntitySyncOperationItemSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is not null)
            await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken)
                .ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.sync_operation_items item
            SET redacted_before = @redacted_before,
                before_payload_sha256 = @before_payload_sha256,
                after_payload_sha256 = @after_payload_sha256,
                vendor_request_id = @vendor_request_id,
                dispatch_started_at = @dispatch_started_at,
                vendor_target_entity_id = @vendor_target_entity_id,
                safe_write_code = @safe_write_code,
                outcome = @outcome,
                error_code = @error_code,
                error_message = @error_message,
                started_at = @started_at,
                completed_at = @completed_at
            FROM entitysync.sync_operations operation
            WHERE item.tenant_id = @tenant_id AND item.operation_id = @operation_id
              AND item.plan_id = @plan_id AND item.item_id = @item_id
              AND item.outcome = @expected_outcome
              AND operation.tenant_id = item.tenant_id
              AND operation.operation_id = item.operation_id
              AND operation.attempt = @expected_attempt
              AND operation.lease_owner = @lease_owner
              AND operation.status = 'Running'
              AND operation.lease_expires_at > clock_timestamp()
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddItemMutable(command, replacement);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, operationId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, planId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(
            command, "expected_outcome", NpgsqlDbType.Text, expectedOutcome.ToString());
        PostgresControlPersistence.Add(
            command, "expected_attempt", NpgsqlDbType.Integer, expectedOperationAttempt);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
        if (updated)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public Task<EntitySyncOperation?> TryFinalizeAttemptAsync(
        string tenantId,
        Guid operationId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        FinishAttemptAsync(
            tenantId, operationId, expectedOperationAttempt, leaseOwner, completedAt,
            cancel: false, cancellationToken);

    public Task<EntitySyncOperation?> TryCancelAttemptAsync(
        string tenantId,
        Guid operationId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken) =>
        FinishAttemptAsync(
            tenantId, operationId, expectedOperationAttempt, leaseOwner, completedAt,
            cancel: true, cancellationToken);

    public async Task<UnknownItemLease?> TryLeaseUnknownItemAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.sync_operation_items item
            SET reconcile_lease_owner = @lease_owner,
                reconcile_lease_expires_at = @lease_expires_at,
                reconcile_attempt = item.reconcile_attempt + 1
            WHERE item.tenant_id = @tenant_id
              AND item.operation_id = @operation_id
              AND item.item_id = @item_id
              AND item.outcome = 'Unknown'
              AND (item.reconcile_lease_owner IS NULL
                   OR item.reconcile_lease_expires_at <= clock_timestamp())
            RETURNING item.tenant_id, item.operation_id, item.plan_id, item.item_id,
                      item.source_vendor, item.source_connection_id,
                      item.source_entity_type, item.source_entity_key,
                      item.source_entity_id, item.target_vendor,
                      item.target_connection_id, item.target_entity_type,
                      item.target_entity_id, item.action, item.redacted_before::text,
                      item.redacted_desired::text, item.before_payload_sha256,
                      item.desired_payload_sha256, item.after_payload_sha256,
                      item.snapshots_expires_at, item.vendor_request_id, item.outcome,
                      item.error_code, item.error_message, item.started_at,
                      item.completed_at, item.dispatch_started_at,
                      item.vendor_target_entity_id, item.safe_write_code,
                      item.reconcile_attempt
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner);
        PostgresControlPersistence.Add(
            command, "lease_expires_at", NpgsqlDbType.TimestampTz, leaseExpiresAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new UnknownItemLease(
            ReadItem(reader), reader.GetInt32(29), leaseOwner, leaseExpiresAt);
    }

    public async Task<bool> TryRecordReconciliationEvidenceAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string leaseOwner,
        EntitySyncSha256 afterPayloadSha256,
        string? vendorTargetEntityId,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(
            tenantId, snapshot.TenantId, nameof(snapshot));
        if (snapshot.OperationId != operationId || snapshot.ItemId != itemId
            || snapshot.EncryptedAfterCiphertext is null)
            throw new ArgumentException(
                "Reconciliation evidence must bind the leased item and include an after snapshot.",
                nameof(snapshot));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken)
            .ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.sync_operation_items
            SET after_payload_sha256 = @after_payload_sha256,
                vendor_target_entity_id = COALESCE(
                    @vendor_target_entity_id, vendor_target_entity_id)
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
              AND item_id = @item_id AND outcome = 'Unknown'
              AND reconcile_attempt = @expected_attempt
              AND reconcile_lease_owner = @lease_owner
              AND reconcile_lease_expires_at > clock_timestamp()
              AND (after_payload_sha256 IS NULL
                   OR after_payload_sha256 = @after_payload_sha256)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(
            command, "expected_attempt", NpgsqlDbType.Integer,
            expectedReconciliationAttempt);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner);
        PostgresControlPersistence.Add(
            command, "after_payload_sha256", NpgsqlDbType.Char, afterPayloadSha256.Value);
        PostgresControlPersistence.Add(
            command, "vendor_target_entity_id", NpgsqlDbType.Text, vendorTargetEntityId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryCompleteReconciliationAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        int expectedReconciliationAttempt,
        string leaseOwner,
        EntitySyncOperationItem replacement,
        EntitySyncOperationItemSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is not null)
            await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken)
                .ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.sync_operation_items
            SET after_payload_sha256 = @after_payload_sha256,
                vendor_target_entity_id = @vendor_target_entity_id,
                safe_write_code = @safe_write_code,
                outcome = @outcome,
                error_code = @error_code,
                error_message = @error_message,
                completed_at = @completed_at,
                reconcile_lease_owner = NULL,
                reconcile_lease_expires_at = NULL
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
              AND item_id = @item_id AND outcome = 'Unknown'
              AND reconcile_attempt = @expected_attempt
              AND reconcile_lease_owner = @lease_owner
              AND reconcile_lease_expires_at > clock_timestamp()
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddItemMutable(command, replacement);
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(
            command, "expected_attempt", NpgsqlDbType.Integer, expectedReconciliationAttempt);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await RefreshTerminalOperationAsync(
            connection, transaction, tenantId, operationId, replacement.CompletedAt
                ?? DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }


    public async Task InsertSnapshotAsync(
        string tenantId,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, snapshot.TenantId, nameof(snapshot));
        const string sql = """
            INSERT INTO entitysync.sync_operation_item_snapshots (
                tenant_id, operation_id, item_id, encrypted_before_ciphertext,
                encrypted_after_ciphertext, expires_at)
            VALUES (
                @tenant_id, @operation_id, @item_id, @encrypted_before_ciphertext,
                @encrypted_after_ciphertext, @expires_at)
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddSnapshot(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntitySyncOperationItemSnapshot?> GetSnapshotAsync(
        string tenantId,
        Guid operationId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, operation_id, item_id, encrypted_before_ciphertext,
                   encrypted_after_ciphertext, expires_at
            FROM entitysync.sync_operation_item_snapshots
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id AND item_id = @item_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, operationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new EntitySyncOperationItemSnapshot(
                reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2),
                PostgresControlPersistence.NullableString(reader, 3),
                PostgresControlPersistence.NullableString(reader, 4),
                reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    public async Task<int> DeleteExpiredSnapshotsAsync(
        string tenantId,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            WITH expired AS (
                SELECT tenant_id, operation_id, item_id
                FROM entitysync.sync_operation_item_snapshots
                WHERE tenant_id = @tenant_id AND expires_at <= @now
                ORDER BY expires_at, operation_id, item_id
                LIMIT @maximum_rows
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM entitysync.sync_operation_item_snapshots snapshot
            USING expired
            WHERE snapshot.tenant_id = expired.tenant_id
              AND snapshot.operation_id = expired.operation_id
              AND snapshot.item_id = expired.item_id
              AND snapshot.tenant_id = @tenant_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "now", NpgsqlDbType.TimestampTz, now);
        PostgresControlPersistence.Add(command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<EntitySyncOperation?> FinishAttemptAsync(
        string tenantId,
        Guid operationId,
        int expectedOperationAttempt,
        string leaseOwner,
        DateTimeOffset completedAt,
        bool cancel,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string stateSql = """
            SELECT count(*)::integer,
                   count(*) FILTER (WHERE outcome = 'Pending')::integer,
                   count(*) FILTER (WHERE outcome = 'Succeeded')::integer,
                   count(*) FILTER (WHERE outcome = 'Failed')::integer,
                   count(*) FILTER (WHERE outcome = 'Skipped')::integer,
                   count(*) FILTER (WHERE outcome = 'Unknown')::integer,
                   count(*) FILTER (WHERE dispatch_started_at IS NOT NULL)::integer
            FROM entitysync.sync_operation_items
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
              AND EXISTS (
                  SELECT 1 FROM entitysync.sync_operations operation
                  WHERE operation.tenant_id = @tenant_id
                    AND operation.operation_id = @operation_id
                    AND operation.attempt = @expected_attempt
                    AND operation.lease_owner = @lease_owner
                    AND operation.status = 'Running'
                    AND operation.lease_expires_at > clock_timestamp())
            """;
        int total;
        int pending;
        int succeeded;
        int failed;
        int skipped;
        int unknown;
        int dispatched;
        await using (var state = new NpgsqlCommand(stateSql, connection, transaction))
        {
            AddOperationKey(state, tenantId, operationId);
            PostgresControlPersistence.Add(
                state, "expected_attempt", NpgsqlDbType.Integer, expectedOperationAttempt);
            PostgresControlPersistence.Add(state, "lease_owner", NpgsqlDbType.Text, leaseOwner);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            total = reader.GetInt32(0);
            pending = reader.GetInt32(1);
            succeeded = reader.GetInt32(2);
            failed = reader.GetInt32(3);
            skipped = reader.GetInt32(4);
            unknown = reader.GetInt32(5);
            dispatched = reader.GetInt32(6);
        }
        if (total == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (cancel && dispatched != 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var status = cancel
            ? EntitySyncOperationStatus.Cancelled
            : pending > 0
                ? EntitySyncOperationStatus.Queued
                : succeeded + skipped == total
                    ? EntitySyncOperationStatus.Succeeded
                    : succeeded + skipped > 0
                        ? EntitySyncOperationStatus.Partial
                        : EntitySyncOperationStatus.Failed;
        const string updateSql = """
            UPDATE entitysync.sync_operations
            SET status = @status,
                lease_owner = NULL,
                lease_expires_at = NULL,
                completed_at = CASE WHEN @status IN (
                    'Succeeded','Partial','Failed','Cancelled') THEN @completed_at ELSE NULL END,
                total_count = @total_count,
                succeeded_count = @succeeded_count,
                failed_count = @failed_count,
                skipped_count = @skipped_count,
                unknown_count = @unknown_count
            WHERE tenant_id = @tenant_id AND operation_id = @operation_id
              AND attempt = @expected_attempt AND lease_owner = @lease_owner
              AND status = 'Running' AND lease_expires_at > clock_timestamp()
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddOperationKey(update, tenantId, operationId);
            PostgresControlPersistence.Add(
                update, "expected_attempt", NpgsqlDbType.Integer, expectedOperationAttempt);
            PostgresControlPersistence.Add(update, "lease_owner", NpgsqlDbType.Text, leaseOwner);
            PostgresControlPersistence.Add(update, "status", NpgsqlDbType.Text, status.ToString());
            PostgresControlPersistence.Add(
                update, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
            AddCounts(update, total, succeeded, failed, skipped, unknown);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(tenantId, operationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcquireDispatchLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid planId,
        Guid policyId,
        CancellationToken cancellationToken)
    {
        await using (var policyLock = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 1))",
                         connection, transaction))
        {
            PostgresControlPersistence.Add(
                policyLock, "identity", NpgsqlDbType.Text, $"{tenantId}:{policyId:N}");
            await policyLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        const string connectionsSql = """
            SELECT definition.connection_id
            FROM entitysync.connection_definitions definition
            JOIN entitysync.sync_plans plan
              ON plan.tenant_id = definition.tenant_id
             AND definition.connection_id IN (
                 plan.source_connection_id, plan.target_connection_id)
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
            ORDER BY definition.connection_id
            FOR KEY SHARE OF definition
            """;
        await using (var connectionLocks = new NpgsqlCommand(
                         connectionsSql, connection, transaction))
        {
            PostgresControlPersistence.Add(
                connectionLocks, "tenant_id", NpgsqlDbType.Text, tenantId);
            PostgresControlPersistence.Add(connectionLocks, "plan_id", NpgsqlDbType.Uuid, planId);
            await connectionLocks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        const string routeSql = """
            SELECT pg_advisory_xact_lock(entitysync.entity_route_lock_key(
                plan.tenant_id, plan.source_connection_id, plan.target_connection_id))
            FROM entitysync.sync_plans plan
            WHERE plan.tenant_id = @tenant_id AND plan.plan_id = @plan_id
            """;
        await using var routeLock = new NpgsqlCommand(routeSql, connection, transaction);
        PostgresControlPersistence.Add(routeLock, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(routeLock, "plan_id", NpgsqlDbType.Uuid, planId);
        await routeLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySyncOperationItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO entitysync.sync_operation_item_snapshots (
                tenant_id, operation_id, item_id, encrypted_before_ciphertext,
                encrypted_after_ciphertext, expires_at)
            VALUES (
                @tenant_id, @operation_id, @item_id, @encrypted_before_ciphertext,
                @encrypted_after_ciphertext, @expires_at)
            ON CONFLICT (tenant_id, operation_id, item_id)
            DO UPDATE SET
                encrypted_before_ciphertext = COALESCE(
                    entitysync.sync_operation_item_snapshots.encrypted_before_ciphertext,
                    EXCLUDED.encrypted_before_ciphertext),
                encrypted_after_ciphertext = COALESCE(
                    EXCLUDED.encrypted_after_ciphertext,
                    entitysync.sync_operation_item_snapshots.encrypted_after_ciphertext)
            WHERE entitysync.sync_operation_item_snapshots.expires_at = EXCLUDED.expires_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddSnapshot(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RefreshTerminalOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid operationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH counts AS (
                SELECT count(*)::integer AS total_count,
                       count(*) FILTER (WHERE outcome = 'Succeeded')::integer
                           AS succeeded_count,
                       count(*) FILTER (WHERE outcome = 'Failed')::integer AS failed_count,
                       count(*) FILTER (WHERE outcome = 'Skipped')::integer AS skipped_count,
                       count(*) FILTER (WHERE outcome = 'Unknown')::integer AS unknown_count,
                       count(*) FILTER (WHERE outcome = 'Pending')::integer AS pending_count
                FROM entitysync.sync_operation_items
                WHERE tenant_id = @tenant_id AND operation_id = @operation_id
            )
            UPDATE entitysync.sync_operations operation
            SET status = CASE
                    WHEN counts.succeeded_count + counts.skipped_count = counts.total_count
                        THEN 'Succeeded'
                    WHEN counts.succeeded_count + counts.skipped_count > 0 THEN 'Partial'
                    ELSE 'Failed'
                END,
                completed_at = @completed_at,
                total_count = counts.total_count,
                succeeded_count = counts.succeeded_count,
                failed_count = counts.failed_count,
                skipped_count = counts.skipped_count,
                unknown_count = counts.unknown_count
            FROM counts
            WHERE operation.tenant_id = @tenant_id
              AND operation.operation_id = @operation_id
              AND counts.pending_count = 0
              AND operation.status IN ('Succeeded','Partial','Failed')
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(
            command, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddDispatchIdentity(
        NpgsqlCommand command,
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
        int expectedAttempt,
        string leaseOwner,
        Guid policyId,
        int policyVersion,
        EntitySyncSha256 policyDefinitionSha256)
    {
        AddOperationKey(command, tenantId, operationId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, planId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, itemId);
        PostgresControlPersistence.Add(
            command, "expected_attempt", NpgsqlDbType.Integer, expectedAttempt);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, leaseOwner);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, policyId);
        PostgresControlPersistence.Add(
            command, "policy_version", NpgsqlDbType.Integer, policyVersion);
        PostgresControlPersistence.Add(
            command, "policy_definition_sha256", NpgsqlDbType.Char,
            policyDefinitionSha256.Value);
    }

    private static void AddCounts(
        NpgsqlCommand command,
        int total,
        int succeeded,
        int failed,
        int skipped,
        int unknown)
    {
        PostgresControlPersistence.Add(command, "total_count", NpgsqlDbType.Integer, total);
        PostgresControlPersistence.Add(
            command, "succeeded_count", NpgsqlDbType.Integer, succeeded);
        PostgresControlPersistence.Add(command, "failed_count", NpgsqlDbType.Integer, failed);
        PostgresControlPersistence.Add(command, "skipped_count", NpgsqlDbType.Integer, skipped);
        PostgresControlPersistence.Add(command, "unknown_count", NpgsqlDbType.Integer, unknown);
    }

    internal static async Task InsertGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items,
        CancellationToken cancellationToken)
    {
        const string operationSql = """
            INSERT INTO entitysync.sync_operations (
                tenant_id, operation_id, plan_id, approval_id, route_scope,
                source_connection_generation, target_connection_generation, mode, status,
                idempotency_key, lease_owner, lease_expires_at, attempt, created_at,
                queued_at, started_at, completed_at, request_sha256, total_count,
                succeeded_count, failed_count, skipped_count, unknown_count)
            SELECT @tenant_id, @operation_id, @plan_id, @approval_id, @route_scope,
                   @source_connection_generation, @target_connection_generation, @mode, @status,
                   @idempotency_key, @lease_owner, @lease_expires_at, @attempt, @created_at,
                   @queued_at, @started_at, @completed_at, @request_sha256, @total_count,
                   @succeeded_count, @failed_count, @skipped_count, @unknown_count
            FROM entitysync.sync_plans plan
            WHERE plan.tenant_id = @tenant_id
              AND plan.plan_id = @plan_id
              AND plan.source_connection_id = @source_connection_id
              AND plan.source_connection_generation = @source_connection_generation
              AND plan.target_connection_id = @target_connection_id
              AND plan.target_connection_generation = @target_connection_generation
            """;
        await using (var command = new NpgsqlCommand(operationSql, connection, transaction))
        {
            AddOperation(command, operation);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The operation does not bind the exact plan connection identity.");
        }

        const string itemSql = """
            INSERT INTO entitysync.sync_operation_items (
                tenant_id, operation_id, plan_id, item_id, source_vendor,
                source_connection_id, source_entity_type, source_entity_key, source_entity_id,
                target_vendor, target_connection_id, target_entity_type, target_entity_id, action,
                redacted_before, redacted_desired, before_payload_sha256,
                desired_payload_sha256, after_payload_sha256, snapshots_expires_at,
                vendor_request_id, outcome, error_code, error_message, started_at,
                completed_at, dispatch_started_at, vendor_target_entity_id, safe_write_code)
            VALUES (
                @tenant_id, @operation_id, @plan_id, @item_id, @source_vendor,
                @source_connection_id, @source_entity_type, @source_entity_key, @source_entity_id,
                @target_vendor, @target_connection_id, @target_entity_type, @target_entity_id, @action,
                @redacted_before, @redacted_desired, @before_payload_sha256,
                @desired_payload_sha256, @after_payload_sha256, @snapshots_expires_at,
                @vendor_request_id, @outcome, @error_code, @error_message, @started_at,
                @completed_at, @dispatch_started_at, @vendor_target_entity_id, @safe_write_code)
            """;
        foreach (var item in items)
        {
            await using var command = new NpgsqlCommand(itemSql, connection, transaction);
            AddItem(command, item);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void ValidateGraph(
        string tenantId,
        EntitySyncOperation operation,
        IReadOnlyList<EntitySyncOperationItem> items)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(items);
        PostgresControlPersistence.RequireTenant(tenantId, operation.TenantId, nameof(operation));
        if (operation.Status != EntitySyncOperationStatus.Queued
            || operation.Attempt != 0
            || operation.LeaseOwner is not null
            || operation.LeaseExpiresAt is not null
            || operation.StartedAt is not null
            || operation.CompletedAt is not null)
            throw new ArgumentException(
                "New operation graphs must contain a fresh queued attempt-zero operation.",
                nameof(operation));
        foreach (var item in items)
        {
            if (item is null || item.TenantId != tenantId || item.OperationId != operation.OperationId || item.PlanId != operation.PlanId)
                throw new ArgumentException("Every operation item must belong to the operation tenant, operation, and plan.", nameof(items));
            if (item.Outcome != EntitySyncItemOutcome.Pending
                || item.AfterPayloadSha256 is not null
                || item.VendorRequestId is not null
                || item.ErrorCode is not null
                || item.ErrorMessage is not null
                || item.StartedAt is not null
                || item.CompletedAt is not null)
                throw new ArgumentException(
                    "New operation graphs must contain only fresh pending items.",
                    nameof(items));
        }
        if (items.Select(item => item.ItemId).Distinct().Count() != items.Count)
            throw new ArgumentException("Operation item IDs must be unique.", nameof(items));
    }

    private static void ValidateImmutableIdentity(
        EntitySyncOperation current,
        EntitySyncOperation replacement)
    {
        if (replacement.TenantId != current.TenantId
            || replacement.OperationId != current.OperationId
            || replacement.PlanId != current.PlanId
            || replacement.ApprovalId != current.ApprovalId
            || replacement.RouteScope != current.RouteScope
            || replacement.SourceConnectionId != current.SourceConnectionId
            || replacement.SourceConnectionGeneration != current.SourceConnectionGeneration
            || replacement.TargetConnectionId != current.TargetConnectionId
            || replacement.TargetConnectionGeneration != current.TargetConnectionGeneration
            || replacement.Mode != current.Mode
            || replacement.IdempotencyKey != current.IdempotencyKey
            || replacement.CreatedAt != current.CreatedAt
            || replacement.QueuedAt != current.QueuedAt)
            throw new ArgumentException(
                "Operation replacement cannot change immutable identity fields.",
                nameof(replacement));
    }

    private static void ValidateTransition(
        EntitySyncOperation current,
        EntitySyncOperation replacement)
    {
        var legal = (current.Status, replacement.Status) switch
        {
            (EntitySyncOperationStatus.Queued, EntitySyncOperationStatus.Leased) =>
                replacement.Attempt == checked(current.Attempt + 1)
                && replacement.LeaseOwner is not null
                && replacement.LeaseExpiresAt is not null
                && replacement.StartedAt is null
                && replacement.CompletedAt is null,
            (EntitySyncOperationStatus.Leased, EntitySyncOperationStatus.Running) =>
                replacement.Attempt == current.Attempt
                && replacement.LeaseOwner == current.LeaseOwner
                && replacement.LeaseExpiresAt == current.LeaseExpiresAt
                && replacement.StartedAt is not null
                && replacement.CompletedAt is null,
            (EntitySyncOperationStatus.Running,
                EntitySyncOperationStatus.Succeeded
                    or EntitySyncOperationStatus.Partial
                    or EntitySyncOperationStatus.Failed) =>
                replacement.Attempt == current.Attempt
                && replacement.LeaseOwner is null
                && replacement.LeaseExpiresAt is null
                && replacement.StartedAt == current.StartedAt
                && replacement.CompletedAt is not null,
            (EntitySyncOperationStatus.Queued
                    or EntitySyncOperationStatus.Leased
                    or EntitySyncOperationStatus.Running,
                EntitySyncOperationStatus.Cancelled) =>
                replacement.Attempt == current.Attempt
                && replacement.LeaseOwner is null
                && replacement.LeaseExpiresAt is null
                && replacement.StartedAt == current.StartedAt
                && replacement.CompletedAt is not null,
            _ => false
        };
        if (!legal)
            throw new InvalidOperationException(
                $"Illegal operation transition from {current.Status} to {replacement.Status}.");
    }

    private static void AddOperationKey(NpgsqlCommand command, string tenantId, Guid operationId)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, operationId);
    }

    private static void AddOperation(NpgsqlCommand command, EntitySyncOperation operation)
    {
        AddOperationKey(command, operation.TenantId, operation.OperationId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, operation.PlanId);
        PostgresControlPersistence.Add(command, "approval_id", NpgsqlDbType.Uuid, operation.ApprovalId);
        PostgresControlPersistence.Add(command, "route_scope", NpgsqlDbType.Text, operation.RouteScope);
        PostgresControlPersistence.Add(command, "source_connection_id", NpgsqlDbType.Text, operation.SourceConnectionId);
        PostgresControlPersistence.Add(command, "source_connection_generation", NpgsqlDbType.Bigint, operation.SourceConnectionGeneration);
        PostgresControlPersistence.Add(command, "target_connection_generation", NpgsqlDbType.Bigint, operation.TargetConnectionGeneration);
        PostgresControlPersistence.Add(command, "target_connection_id", NpgsqlDbType.Text, operation.TargetConnectionId);
        PostgresControlPersistence.Add(command, "mode", NpgsqlDbType.Text, operation.Mode.ToString());
        PostgresControlPersistence.Add(command, "status", NpgsqlDbType.Text, operation.Status.ToString());
        PostgresControlPersistence.Add(command, "idempotency_key", NpgsqlDbType.Text, operation.IdempotencyKey);
        PostgresControlPersistence.Add(command, "lease_owner", NpgsqlDbType.Text, operation.LeaseOwner);
        PostgresControlPersistence.Add(command, "lease_expires_at", NpgsqlDbType.TimestampTz, operation.LeaseExpiresAt);
        PostgresControlPersistence.Add(command, "attempt", NpgsqlDbType.Integer, operation.Attempt);
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, operation.CreatedAt);
        PostgresControlPersistence.Add(command, "queued_at", NpgsqlDbType.TimestampTz, operation.QueuedAt);
        PostgresControlPersistence.Add(command, "started_at", NpgsqlDbType.TimestampTz, operation.StartedAt);
        PostgresControlPersistence.Add(command, "completed_at", NpgsqlDbType.TimestampTz, operation.CompletedAt);
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, operation.RequestSha256?.Value);
        AddCounts(
            command, operation.TotalCount, operation.SucceededCount, operation.FailedCount,
            operation.SkippedCount, operation.UnknownCount);
    }

    private static void AddItem(NpgsqlCommand command, EntitySyncOperationItem item)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, item.TenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, item.OperationId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, item.PlanId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, item.ItemId);
        PostgresControlPersistence.Add(command, "source_vendor", NpgsqlDbType.Text, item.SourceVendor);
        PostgresControlPersistence.Add(command, "source_connection_id", NpgsqlDbType.Text, item.SourceConnectionId);
        PostgresControlPersistence.Add(command, "source_entity_type", NpgsqlDbType.Text, item.SourceEntityType);
        PostgresControlPersistence.Add(command, "source_entity_key", NpgsqlDbType.Text, item.SourceEntityKey);
        PostgresControlPersistence.Add(command, "source_entity_id", NpgsqlDbType.Text, item.SourceEntityId);
        PostgresControlPersistence.Add(command, "target_vendor", NpgsqlDbType.Text, item.TargetVendor);
        PostgresControlPersistence.Add(command, "target_connection_id", NpgsqlDbType.Text, item.TargetConnectionId);
        PostgresControlPersistence.Add(command, "target_entity_type", NpgsqlDbType.Text, item.TargetEntityType);
        PostgresControlPersistence.Add(command, "target_entity_id", NpgsqlDbType.Text, item.TargetEntityId);
        PostgresControlPersistence.Add(command, "action", NpgsqlDbType.Text, item.Action);
        PostgresControlPersistence.Add(command, "redacted_desired", NpgsqlDbType.Jsonb, item.RedactedDesired.Json);
        PostgresControlPersistence.Add(command, "desired_payload_sha256", NpgsqlDbType.Char, item.DesiredPayloadSha256.Value);
        PostgresControlPersistence.Add(command, "snapshots_expires_at", NpgsqlDbType.TimestampTz, item.SnapshotsExpireAt);
        AddItemMutable(command, item);
    }

    private static void AddItemMutable(NpgsqlCommand command, EntitySyncOperationItem item)
    {
        PostgresControlPersistence.Add(
            command, "redacted_before", NpgsqlDbType.Jsonb, item.RedactedBefore.Json);
        PostgresControlPersistence.Add(
            command, "before_payload_sha256", NpgsqlDbType.Char,
            item.BeforePayloadSha256?.Value);
        PostgresControlPersistence.Add(command, "after_payload_sha256", NpgsqlDbType.Char, item.AfterPayloadSha256?.Value);
        PostgresControlPersistence.Add(command, "vendor_request_id", NpgsqlDbType.Text, item.VendorRequestId);
        PostgresControlPersistence.Add(
            command, "dispatch_started_at", NpgsqlDbType.TimestampTz,
            item.DispatchStartedAt);
        PostgresControlPersistence.Add(
            command, "vendor_target_entity_id", NpgsqlDbType.Text,
            item.VendorTargetEntityId);
        PostgresControlPersistence.Add(
            command, "safe_write_code", NpgsqlDbType.Text, item.SafeWriteCode);
        PostgresControlPersistence.Add(command, "outcome", NpgsqlDbType.Text, item.Outcome.ToString());
        PostgresControlPersistence.Add(command, "error_code", NpgsqlDbType.Text, item.ErrorCode);
        PostgresControlPersistence.Add(command, "error_message", NpgsqlDbType.Text, item.ErrorMessage);
        PostgresControlPersistence.Add(command, "started_at", NpgsqlDbType.TimestampTz, item.StartedAt);
        PostgresControlPersistence.Add(command, "completed_at", NpgsqlDbType.TimestampTz, item.CompletedAt);
    }

    private static void AddSnapshot(NpgsqlCommand command, EntitySyncOperationItemSnapshot snapshot)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, snapshot.TenantId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, snapshot.OperationId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, snapshot.ItemId);
        PostgresControlPersistence.Add(command, "encrypted_before_ciphertext", NpgsqlDbType.Text, snapshot.EncryptedBeforeCiphertext);
        PostgresControlPersistence.Add(command, "encrypted_after_ciphertext", NpgsqlDbType.Text, snapshot.EncryptedAfterCiphertext);
        PostgresControlPersistence.Add(command, "expires_at", NpgsqlDbType.TimestampTz, snapshot.ExpiresAt);
    }

    private static EntitySyncOperation ReadOperation(NpgsqlDataReader reader)
    {
        var operation = EntitySyncOperation.Rehydrate(
            reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2),
            PostgresControlPersistence.NullableGuid(reader, 3), reader.GetString(4),
            reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8),
            PostgresControlPersistence.ParseEnum<EntitySyncOperationMode>(reader.GetString(9)),
            PostgresControlPersistence.ParseEnum<EntitySyncOperationStatus>(reader.GetString(10)),
            reader.GetString(11), PostgresControlPersistence.NullableString(reader, 12),
            PostgresControlPersistence.NullableTime(reader, 13), reader.GetInt32(14),
            reader.GetFieldValue<DateTimeOffset>(15), reader.GetFieldValue<DateTimeOffset>(16),
            PostgresControlPersistence.NullableTime(reader, 17),
            PostgresControlPersistence.NullableTime(reader, 18));
        return operation with
        {
            RequestSha256 = PostgresControlPersistence.NullableHash(reader, 19),
            TotalCount = reader.GetInt32(20),
            SucceededCount = reader.GetInt32(21),
            FailedCount = reader.GetInt32(22),
            SkippedCount = reader.GetInt32(23),
            UnknownCount = reader.GetInt32(24)
        };
    }

    private static EntitySyncOperationItem ReadItem(NpgsqlDataReader reader)
    {
        var item = EntitySyncOperationItem.Rehydrate(
            reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            PostgresControlPersistence.NullableString(reader, 12), reader.GetString(13),
            new EntitySyncJsonValue(reader.GetString(14)), new EntitySyncJsonValue(reader.GetString(15)),
            PostgresControlPersistence.NullableHash(reader, 16), new EntitySyncSha256(reader.GetString(17)),
            PostgresControlPersistence.NullableHash(reader, 18), reader.GetFieldValue<DateTimeOffset>(19),
            PostgresControlPersistence.NullableString(reader, 20),
            PostgresControlPersistence.ParseEnum<EntitySyncItemOutcome>(reader.GetString(21)),
            PostgresControlPersistence.NullableString(reader, 22),
            PostgresControlPersistence.NullableString(reader, 23),
            PostgresControlPersistence.NullableTime(reader, 24),
            PostgresControlPersistence.NullableTime(reader, 25));
        return item with
        {
            DispatchStartedAt = PostgresControlPersistence.NullableTime(reader, 26),
            VendorTargetEntityId = PostgresControlPersistence.NullableString(reader, 27),
            SafeWriteCode = PostgresControlPersistence.NullableString(reader, 28)
        };
    }
}

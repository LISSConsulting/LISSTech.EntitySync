using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresEntityRefreshStateRepository(
    NpgsqlDataSource dataSource) : IEntityRefreshStateRepository
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public async Task<IReadOnlyList<EntityRefreshStateSnapshot>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("Connection ID is required.", nameof(connectionId));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT s.tenant_id, s.connection_id, s.vendor, s.connection_generation, s.entity_type,
                   s.status, s.mode, s.last_attempt_at, s.last_successful_at,
                   s.next_scheduled_at, s.observed_count, s.cursor, s.source_updated_at,
                   s.error_code, s.snapshot_started_at, s.snapshot_completed_at, s.is_stale,
                   s.lease_owner, s.lease_expires_at
              FROM entitysync.entity_refresh_state s
             WHERE s.tenant_id = @tenant_id
               AND s.connection_id = @connection_id
               AND (@entity_type IS NULL OR s.entity_type = @entity_type)
             ORDER BY s.entity_type
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("connection_id", connectionId);
        AddOptionalText(command, "entity_type", entityType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var states = new List<EntityRefreshStateSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            states.Add(ReadState(tenantId, reader));
        return states;
    }

    public async Task<IReadOnlyList<EntityRefreshDueWork>> LeaseDueAsync(
        string tenantId,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (maximumRows is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));

        const string leaseSql = """
            WITH due AS (
                SELECT s.tenant_id, s.connection_id, s.entity_type, s.mode,
                       cd.generation AS current_generation
                  FROM entitysync.entity_refresh_state s
                  JOIN entitysync.connection_definitions cd
                    ON cd.tenant_id = s.tenant_id
                   AND cd.connection_id = s.connection_id
                 WHERE s.tenant_id = @tenant_id
                   AND s.next_scheduled_at <= @now
                   AND s.status IN ('Pending','Failed','Succeeded')
                   AND (s.lease_owner IS NULL OR s.lease_expires_at <= @now)
                 ORDER BY s.next_scheduled_at
                 LIMIT @max
                FOR UPDATE SKIP LOCKED
            )
            UPDATE entitysync.entity_refresh_state s
               SET status = 'Running',
                   lease_owner = @owner,
                   lease_expires_at = @expires,
                   -- Refresh generation from connection_definitions so a queued row
                   -- picks up a rotation performed between discovery and lease.
                   connection_generation = due.current_generation,
                   -- Preserve Manual mode (queued explicit refresh); default Scheduled
                   -- for Succeeded/Failed rows which are simply recurring.
                   mode = CASE
                       WHEN due.mode = 'Manual' THEN 'Manual'
                       ELSE 'Scheduled'
                   END,
                   -- The actual snapshot start is recorded here so the trailing
                   -- release can persist authoritative boundaries.
                   snapshot_started_at = @now,
                   snapshot_completed_at = NULL,
                   last_attempt_at = COALESCE(s.last_attempt_at, @now),
                   refreshed_at = @now
              FROM due
             WHERE s.tenant_id = due.tenant_id
               AND s.connection_id = due.connection_id
               AND s.entity_type = due.entity_type
            RETURNING s.tenant_id, s.connection_id, s.vendor, s.connection_generation,
                      s.entity_type, s.status, s.mode, s.last_attempt_at,
                      s.last_successful_at, s.next_scheduled_at, s.observed_count,
                      s.cursor, s.source_updated_at, s.error_code,
                      s.snapshot_started_at, s.snapshot_completed_at, s.is_stale,
                      s.lease_owner, s.lease_expires_at
            """;
        await using var command = dataSource.CreateCommand(leaseSql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now.Add(leaseDuration));
        command.Parameters.AddWithValue("max", maximumRows);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<EntityRefreshDueWork>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snap = ReadState(tenantId, reader);
            rows.Add(new EntityRefreshDueWork(snap, snap.ConnectionGeneration));
        }
        return rows;
    }

    public async Task<EntityRefreshStateSnapshot> UpsertOnQueueAsync(
        string tenantId,
        EntityRefreshStateSnapshot state,
        DateTimeOffset nextScheduledAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (state is null) throw new ArgumentNullException(nameof(state));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO entitysync.entity_refresh_state (
                tenant_id, connection_id, vendor, connection_generation, entity_type,
                status, mode, next_scheduled_at, refreshed_at)
            VALUES (
                @tenant_id, @connection_id, @vendor, @generation, @entity_type,
                'Pending', @mode, @next_scheduled_at, clock_timestamp())
            ON CONFLICT (tenant_id, connection_id, entity_type)
            DO UPDATE SET
                vendor = EXCLUDED.vendor,
                connection_generation = EXCLUDED.connection_generation,
                mode = EXCLUDED.mode,
                next_scheduled_at = EXCLUDED.next_scheduled_at,
                -- Manual queue should always mark the row Pending unless a worker
                -- already holds an active lease — Fresh/Succeeded/Failed rows otherwise
                -- looked up-to-date until picked.
                status = CASE
                    WHEN lease_owner IS NOT NULL AND (lease_expires_at IS NULL
                        OR lease_expires_at > clock_timestamp()) THEN status
                    ELSE 'Pending'
                END,
                is_stale = false,
                refreshed_at = clock_timestamp()
            RETURNING tenant_id, connection_id, vendor, connection_generation, entity_type,
                      status, mode, last_attempt_at, last_successful_at, next_scheduled_at,
                      observed_count, cursor, source_updated_at, error_code,
                      snapshot_started_at, snapshot_completed_at, is_stale,
                      lease_owner, lease_expires_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("connection_id", state.Key.ConnectionId);
        command.Parameters.AddWithValue("vendor", state.Vendor);
        command.Parameters.AddWithValue("generation", state.ConnectionGeneration);
        command.Parameters.AddWithValue("entity_type", state.Key.EntityType);
        command.Parameters.AddWithValue("mode", state.Mode.ToString());
        command.Parameters.AddWithValue("next_scheduled_at", nextScheduledAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadState(tenantId, reader);
    }

    public async Task<EntityRefreshStateSnapshot?> EnsureScheduledAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string insertSql = """
            INSERT INTO entitysync.entity_refresh_state (
                tenant_id, connection_id, vendor, connection_generation, entity_type,
                status, mode, next_scheduled_at, refreshed_at)
            VALUES (
                @tenant_id, @connection_id, @vendor, @generation, @entity_type,
                'Pending', 'Scheduled', @due_at, clock_timestamp())
            ON CONFLICT (tenant_id, connection_id, entity_type) DO NOTHING
            RETURNING tenant_id, connection_id, vendor, connection_generation, entity_type,
                      status, mode, last_attempt_at, last_successful_at, next_scheduled_at,
                      observed_count, cursor, source_updated_at, error_code,
                      snapshot_started_at, snapshot_completed_at, is_stale,
                      lease_owner, lease_expires_at
            """;
        await using (var command = dataSource.CreateCommand(insertSql))
        {
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("connection_id", definition.ConnectionId);
            command.Parameters.AddWithValue("vendor", definition.Vendor);
            command.Parameters.AddWithValue("generation", definition.Generation);
            command.Parameters.AddWithValue("entity_type", entityType);
            command.Parameters.AddWithValue("due_at", dueAt);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return ReadState(tenantId, reader);
        }
        var existing = await ListByConnectionAsync(
            tenantId, definition.ConnectionId, entityType, cancellationToken)
            .ConfigureAwait(false);
        return existing.Count == 0 ? null : existing[0];
    }

    public async Task<EntityRefreshStateSnapshot?> UpsertIncrementalAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        string entityType,
        DateTimeOffset receivedAt,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        CancellationToken cancellationToken)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO entitysync.entity_refresh_state (
                tenant_id, connection_id, vendor, connection_generation, entity_type,
                status, mode, last_attempt_at, next_scheduled_at, observed_count,
                cursor, source_updated_at, refreshed_at)
            VALUES (
                @tenant_id, @connection_id, @vendor, @generation, @entity_type,
                'Succeeded', 'Incremental', @received_at,
                @next_scheduled, 1, @cursor, @source_updated, clock_timestamp())
            ON CONFLICT (tenant_id, connection_id, entity_type)
            DO UPDATE SET
                status = 'Succeeded',
                mode = 'Incremental',
                last_attempt_at = GREATEST(entity_refresh_state.last_attempt_at, EXCLUDED.last_attempt_at),
                cursor = COALESCE(EXCLUDED.cursor, entity_refresh_state.cursor),
                source_updated_at = GREATEST(entity_refresh_state.source_updated_at,
                    EXCLUDED.source_updated_at),
                observed_count = entity_refresh_state.observed_count + 1,
                refreshed_at = clock_timestamp()
            RETURNING tenant_id, connection_id, vendor, connection_generation, entity_type,
                      status, mode, last_attempt_at, last_successful_at, next_scheduled_at,
                      observed_count, cursor, source_updated_at, error_code,
                      snapshot_started_at, snapshot_completed_at, is_stale,
                      lease_owner, lease_expires_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("connection_id", definition.ConnectionId);
        command.Parameters.AddWithValue("vendor", definition.Vendor);
        command.Parameters.AddWithValue("generation", definition.Generation);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("received_at", receivedAt);
        command.Parameters.AddWithValue("next_scheduled",
            receivedAt + EntityRefreshConstants.DefaultRefreshInterval);
        AddOptionalString(command, "cursor", cursor);
        AddOptionalTimestamp(command, "source_updated", sourceUpdatedAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadState(tenantId, reader)
            : null;
    }

    public async Task<EntityRefreshStateSnapshot?> TryAcquireLeaseAsync(
        EntityRefreshStateKey key,
        long expectedGeneration,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Owner is required.", nameof(owner));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.entity_refresh_state
               SET status = 'Running',
                   lease_owner = @owner,
                   lease_expires_at = @expires,
                   snapshot_started_at = @started_at,
                   snapshot_completed_at = NULL,
                   last_attempt_at = @started_at,
                   refreshed_at = clock_timestamp()
             WHERE tenant_id = @tenant_id
               AND connection_id = @connection_id
               AND entity_type = @entity_type
               AND connection_generation = @generation
               AND (lease_owner IS NULL OR lease_owner = @owner OR lease_expires_at <= @started_at)
            RETURNING tenant_id, connection_id, vendor, connection_generation, entity_type,
                      status, mode, last_attempt_at, last_successful_at, next_scheduled_at,
                      observed_count, cursor, source_updated_at, error_code,
                      snapshot_started_at, snapshot_completed_at, is_stale,
                      lease_owner, lease_expires_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", key.TenantId);
        command.Parameters.AddWithValue("connection_id", key.ConnectionId);
        command.Parameters.AddWithValue("entity_type", key.EntityType);
        command.Parameters.AddWithValue("generation", expectedGeneration);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("expires", startedAt.Add(leaseDuration));
        command.Parameters.AddWithValue("started_at", startedAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadState(key.TenantId, reader);
    }

    public async Task<bool> TryRenewLeaseAsync(
        EntityRefreshStateKey key,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.entity_refresh_state
               SET lease_expires_at = @expires,
                   refreshed_at = clock_timestamp()
             WHERE tenant_id = @tenant_id
               AND connection_id = @connection_id
               AND entity_type = @entity_type
               AND lease_owner = @owner
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", key.TenantId);
        command.Parameters.AddWithValue("connection_id", key.ConnectionId);
        command.Parameters.AddWithValue("entity_type", key.EntityType);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("expires", now.Add(leaseDuration));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<EntityRefreshStateSnapshot?> TryReleaseLeaseAsync(
        EntityRefreshStateKey key,
        string owner,
        EntityRefreshStatus status,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset? lastSuccessfulAt,
        DateTimeOffset nextScheduledAt,
        long observedCount,
        string? cursor,
        DateTimeOffset? sourceUpdatedAt,
        string? errorCode,
        DateTimeOffset? snapshotStartedAt,
        DateTimeOffset? snapshotCompletedAt,
        CancellationToken cancellationToken)
    {
        if (observedCount < 0)
            throw new ArgumentOutOfRangeException(nameof(observedCount));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.entity_refresh_state
               SET status = @status,
                   -- Preserve the row's current mode (Manual/Scheduled/Incremental);
                   -- the caller selects the mode via lease acquisition, not here.
                   mode = mode,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   last_attempt_at = @last_attempt,
                   last_successful_at = COALESCE(@last_success, last_successful_at),
                   next_scheduled_at = @next_scheduled,
                   observed_count = @observed,
                   cursor = COALESCE(@cursor, cursor),
                   source_updated_at = COALESCE(@source_updated, source_updated_at),
                   error_code = @error_code,
                   snapshot_started_at = COALESCE(@snapshot_started, snapshot_started_at),
                   snapshot_completed_at = COALESCE(@snapshot_completed, snapshot_completed_at),
                   is_stale = CASE WHEN @status = 'Failed' THEN true WHEN @status = 'Succeeded' THEN false ELSE is_stale END,
                   refreshed_at = clock_timestamp()
             WHERE tenant_id = @tenant_id
               AND connection_id = @connection_id
               AND entity_type = @entity_type
               AND lease_owner = @owner
            RETURNING tenant_id, connection_id, vendor, connection_generation, entity_type,
                      status, mode, last_attempt_at, last_successful_at, next_scheduled_at,
                      observed_count, cursor, source_updated_at, error_code,
                      snapshot_started_at, snapshot_completed_at, is_stale,
                      lease_owner, lease_expires_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", key.TenantId);
        command.Parameters.AddWithValue("connection_id", key.ConnectionId);
        command.Parameters.AddWithValue("entity_type", key.EntityType);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("last_attempt", lastAttemptAt);
        AddOptionalTimestamp(command, "last_success", lastSuccessfulAt);
        command.Parameters.AddWithValue("next_scheduled", nextScheduledAt);
        command.Parameters.AddWithValue("observed", observedCount);
        AddOptionalString(command, "cursor", cursor);
        AddOptionalTimestamp(command, "source_updated", sourceUpdatedAt);
        AddOptionalString(command, "error_code", errorCode);
        AddOptionalTimestamp(command, "snapshot_started", snapshotStartedAt);
        AddOptionalTimestamp(command, "snapshot_completed", snapshotCompletedAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadState(key.TenantId, reader);
    }

    public async Task<bool> MarkStaleAsync(
        EntityRefreshStateKey key,
        long observedGeneration,
        bool isStale,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            UPDATE entitysync.entity_refresh_state
               SET is_stale = @stale,
                   refreshed_at = clock_timestamp()
             WHERE tenant_id = @tenant_id
               AND connection_id = @connection_id
               AND entity_type = @entity_type
               AND connection_generation = @generation
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", key.TenantId);
        command.Parameters.AddWithValue("connection_id", key.ConnectionId);
        command.Parameters.AddWithValue("entity_type", key.EntityType);
        command.Parameters.AddWithValue("generation", observedGeneration);
        command.Parameters.AddWithValue("stale", isStale);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) >= 1;
    }

    private static EntityRefreshStateSnapshot ReadState(
        string tenantId,
        NpgsqlDataReader reader)
    {
        var connectionId = reader.GetString(1);
        var vendor = reader.GetString(2);
        var generation = reader.GetInt64(3);
        var entityType = reader.GetString(4);
        var status = Enum.Parse<EntityRefreshStatus>(reader.GetString(5));
        var mode = Enum.Parse<EntityRefreshMode>(reader.GetString(6));
        var lastAttempt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
        var lastSuccess = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
        var next = reader.GetFieldValue<DateTimeOffset>(9);
        var observed = reader.GetInt64(10);
        var cursor = reader.IsDBNull(11) ? null : reader.GetString(11);
        var sourceUpdated = reader.IsDBNull(12) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(12);
        var errorCode = reader.IsDBNull(13) ? null : reader.GetString(13);
        var snapStart = reader.IsDBNull(14) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(14);
        var snapComplete = reader.IsDBNull(15) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(15);
        var stale = reader.GetBoolean(16);
        var leaseOwner = reader.IsDBNull(17) ? null : reader.GetString(17);
        var leaseExpiry = reader.IsDBNull(18) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(18);
        return new EntityRefreshStateSnapshot
        {
            Key = new EntityRefreshStateKey(tenantId, connectionId, entityType),
            Vendor = vendor,
            ConnectionGeneration = generation,
            Status = status,
            Mode = mode,
            LastAttemptAt = lastAttempt,
            LastSuccessfulAt = lastSuccess,
            NextScheduledAt = next,
            ObservedCount = observed,
            Cursor = cursor,
            SourceUpdatedAt = sourceUpdated,
            ErrorCode = errorCode,
            SnapshotStartedAt = snapStart,
            SnapshotCompletedAt = snapComplete,
            IsStale = stale,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiry
        };
    }

    private static void AddOptionalTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value ?? (object)DBNull.Value
        });
    }

    private static void AddOptionalString(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrEmpty(value) ? DBNull.Value : value
        });
    }

    private static void AddOptionalText(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await EntitySyncDatabaseMigrator.ApplyAsync(dataSource, cancellationToken)
                .ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }
}

public sealed class PostgresEntityRefreshEventRepository(
    NpgsqlDataSource dataSource) : IEntityRefreshEventRepository
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public async Task<IReadOnlyList<EntityRefreshEvent>> ListAsync(
        string tenantId,
        string connectionId,
        string? entityType,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (maximumRows is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT event_id, connection_id, vendor, entity_type, mode, operation, status,
                   snapshot_started_at, snapshot_completed_at, observed_count,
                   source_cursor, source_updated_at, error_code, received_at
              FROM entitysync.entity_refresh_events
             WHERE tenant_id = @tenant_id
               AND connection_id = @connection_id
               AND (@entity_type IS NULL OR entity_type = @entity_type)
             ORDER BY received_at DESC
             LIMIT @max
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("connection_id", connectionId);
        AddOptional(command, "entity_type", entityType);
        command.Parameters.AddWithValue("max", maximumRows);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var events = new List<EntityRefreshEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(ReadEvent(tenantId, reader));
        return events;
    }

    public async Task AppendAsync(
        EntityRefreshEvent eventRecord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventRecord);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            INSERT INTO entitysync.entity_refresh_events (
                tenant_id, event_id, connection_id, vendor, entity_type,
                mode, operation, status, snapshot_started_at, snapshot_completed_at,
                observed_count, source_cursor, source_updated_at, error_code, received_at)
            VALUES (
                @tenant_id, @event_id, @connection_id, @vendor, @entity_type,
                @mode, @operation, @status, @snapshot_started, @snapshot_completed,
                @observed, @source_cursor, @source_updated, @error_code, @received)
            ON CONFLICT (tenant_id, event_id) DO NOTHING
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", eventRecord.Key.TenantId);
        command.Parameters.AddWithValue("event_id", eventRecord.EventId);
        command.Parameters.AddWithValue("connection_id", eventRecord.Key.ConnectionId);
        command.Parameters.AddWithValue("vendor", eventRecord.Vendor);
        command.Parameters.AddWithValue("entity_type", eventRecord.Key.EntityType);
        command.Parameters.AddWithValue("mode", eventRecord.Mode.ToString());
        command.Parameters.AddWithValue("operation", eventRecord.Operation.ToString());
        command.Parameters.AddWithValue("status", eventRecord.Status.ToString());
        AddOptional(command, "snapshot_started", eventRecord.SnapshotStartedAt);
        AddOptional(command, "snapshot_completed", eventRecord.SnapshotCompletedAt);
        AddOptional(command, "observed", eventRecord.ObservedCount);
        AddOptional(command, "source_cursor", eventRecord.SourceCursor);
        AddOptional(command, "source_updated", eventRecord.SourceUpdatedAt);
        AddOptional(command, "error_code", eventRecord.ErrorCode);
        command.Parameters.AddWithValue("received", eventRecord.ReceivedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EntityRefreshEvent ReadEvent(
        string tenantId,
        NpgsqlDataReader reader)
    {
        var eventId = reader.GetGuid(0);
        var connectionId = reader.GetString(1);
        var vendor = reader.GetString(2);
        var entityType = reader.GetString(3);
        var mode = Enum.Parse<EntityRefreshMode>(reader.GetString(4));
        var operation = Enum.Parse<EntityRefreshEventOperation>(reader.GetString(5));
        var status = Enum.Parse<EntityRefreshStatus>(reader.GetString(6));
        var snapStart = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
        var snapComplete = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
        long? observed = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        var cursor = reader.IsDBNull(10) ? null : reader.GetString(10);
        var sourceUpdated = reader.IsDBNull(11) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(11);
        var errorCode = reader.IsDBNull(12) ? null : reader.GetString(12);
        var receivedAt = reader.GetFieldValue<DateTimeOffset>(13);
        return new EntityRefreshEvent(
            eventId,
            new EntityRefreshStateKey(tenantId, connectionId, entityType),
            vendor,
            mode,
            operation,
            status,
            snapStart,
            snapComplete,
            observed,
            cursor,
            sourceUpdated,
            errorCode,
            receivedAt);
    }

    private static void AddOptional(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value ?? (object)DBNull.Value
        });
    }

    private static void AddOptional(NpgsqlCommand command, string name, long? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint)
        {
            Value = value ?? (object)DBNull.Value
        });
    }

    private static void AddOptional(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrEmpty(value) ? DBNull.Value : value
        });
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await EntitySyncDatabaseMigrator.ApplyAsync(dataSource, cancellationToken)
                .ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }
}

public sealed class PostgresEntityRefreshCapabilityRepository(
    NpgsqlDataSource dataSource) : IEntityRefreshCapabilityRepository
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public async Task ReplaceAsync(
        string tenantId,
        string connectionId,
        IReadOnlyList<EntityRefreshCapability> capabilities,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("Connection ID is required.", nameof(connectionId));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var delete = new NpgsqlCommand(
                "DELETE FROM entitysync.connection_refresh_capabilities WHERE tenant_id = @tenant_id AND connection_id = @connection_id",
                connection, transaction))
            {
                delete.Parameters.AddWithValue("tenant_id", tenantId);
                delete.Parameters.AddWithValue("connection_id", connectionId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var capability in capabilities)
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO entitysync.connection_refresh_capabilities (
                        tenant_id, connection_id, vendor, entity_type,
                        supports_refresh, last_discovered_at)
                    VALUES (
                        @tenant_id, @connection_id, @vendor, @entity_type,
                        @supports, @discovered)
                    ON CONFLICT (tenant_id, connection_id, entity_type)
                    DO UPDATE SET
                        vendor = EXCLUDED.vendor,
                        supports_refresh = EXCLUDED.supports_refresh,
                        last_discovered_at = EXCLUDED.last_discovered_at
                    """,
                    connection, transaction);
                insert.Parameters.AddWithValue("tenant_id", tenantId);
                insert.Parameters.AddWithValue("connection_id", connectionId);
                insert.Parameters.AddWithValue("vendor", capability.Vendor);
                insert.Parameters.AddWithValue("entity_type", capability.EntityType);
                insert.Parameters.AddWithValue("supports", capability.SupportsRefresh);
                insert.Parameters.AddWithValue("discovered", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<EntityRefreshCapability>> ListByConnectionAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT vendor, entity_type, supports_refresh, last_discovered_at
              FROM entitysync.connection_refresh_capabilities
             WHERE tenant_id = @tenant_id AND connection_id = @connection_id
             ORDER BY entity_type
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("connection_id", connectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<EntityRefreshCapability>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EntityRefreshCapability(
                tenantId,
                connectionId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<EntityRefreshCapability>> ListRefreshableAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT connection_id, vendor, entity_type, supports_refresh, last_discovered_at
              FROM entitysync.connection_refresh_capabilities
             WHERE tenant_id = @tenant_id AND supports_refresh
             ORDER BY connection_id, entity_type
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<EntityRefreshCapability>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EntityRefreshCapability(
                tenantId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await EntitySyncDatabaseMigrator.ApplyAsync(dataSource, cancellationToken)
                .ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }
}

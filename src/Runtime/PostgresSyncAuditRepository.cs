using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresSyncAuditRepository(NpgsqlDataSource dataSource) : ISyncAuditRepository
{
    public async Task AppendAsync(string tenantId, EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? fullValues, CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, auditEvent.TenantId, nameof(auditEvent));
        if ((auditEvent.FullValuesSha256 is null) != (fullValues is null))
            throw new ArgumentException("Audit metadata and full values must be supplied together.", nameof(fullValues));
        if (fullValues is not null)
        {
            PostgresControlPersistence.RequireTenant(tenantId, fullValues.TenantId, nameof(fullValues));
            if (fullValues.AuditEventId != auditEvent.AuditEventId
                || fullValues.ExpiresAt != auditEvent.FullValuesExpiresAt)
                throw new ArgumentException("Full values must match the audit event identity and expiry.", nameof(fullValues));
        }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string eventSql = """
            INSERT INTO entitysync.audit_events (
                tenant_id, audit_event_id, occurred_at, event_type, actor_id,
                operation_id, run_id, plan_id, item_id, correlation_id, redacted_values,
                redacted_values_sha256, full_values_sha256, full_values_expires_at)
            VALUES (@tenant_id, @audit_event_id, @occurred_at, @event_type, @actor_id,
                @operation_id, @run_id, @plan_id, @item_id, @correlation_id, @redacted_values,
                @redacted_values_sha256, @full_values_sha256, @full_values_expires_at)
            """;
        await using (var command = new NpgsqlCommand(eventSql, connection, transaction))
        {
            AddEvent(command, auditEvent);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (fullValues is not null)
        {
            const string fullSql = """
                INSERT INTO entitysync.audit_event_full_values (
                    tenant_id, audit_event_id, full_values_ciphertext, expires_at)
                VALUES (@tenant_id, @audit_event_id, @full_values_ciphertext, @expires_at)
                """;
            await using var command = new NpgsqlCommand(fullSql, connection, transaction);
            PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, fullValues.TenantId);
            PostgresControlPersistence.Add(command, "audit_event_id", NpgsqlDbType.Uuid, fullValues.AuditEventId);
            PostgresControlPersistence.Add(command, "full_values_ciphertext", NpgsqlDbType.Text, fullValues.FullValuesCiphertext);
            PostgresControlPersistence.Add(command, "expires_at", NpgsqlDbType.TimestampTz, fullValues.ExpiresAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<bool> TryAppendAsync(
        string tenantId,
        EntitySyncAuditEvent auditEvent,
        EntitySyncAuditEventFullValues? fullValues,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendAsync(tenantId, auditEvent, fullValues, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            const string sql = """
                SELECT event.event_type = @event_type
                       AND event.actor_id = @actor_id
                       AND event.operation_id IS NOT DISTINCT FROM @operation_id
                       AND event.plan_id IS NOT DISTINCT FROM @plan_id
                       AND event.item_id IS NOT DISTINCT FROM @item_id
                       AND event.correlation_id = @correlation_id
                       AND event.redacted_values_sha256 = @redacted_values_sha256
                       AND event.full_values_sha256 IS NOT DISTINCT FROM @full_values_sha256
                       AND ((@has_full_values = false)
                            OR EXISTS (
                                SELECT 1
                                FROM entitysync.audit_event_full_values values
                                WHERE values.tenant_id = event.tenant_id
                                  AND values.audit_event_id = event.audit_event_id))
                FROM entitysync.audit_events event
                WHERE event.tenant_id = @tenant_id
                  AND event.audit_event_id = @audit_event_id
                """;
            await using var command = dataSource.CreateCommand(sql);
            AddEvent(command, auditEvent);
            PostgresControlPersistence.Add(
                command, "has_full_values", NpgsqlDbType.Boolean, fullValues is not null);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is true;
        }
    }


    public async Task<EntitySyncAuditPage> ListAsync(string tenantId,
        DateTimeOffset? continuationOccurredAt, Guid? continuationEventId, int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if ((continuationOccurredAt is null) != (continuationEventId is null))
            throw new ArgumentException("Both continuation values must be supplied together.", nameof(continuationEventId));
        const string sql = """
            SELECT tenant_id, audit_event_id, occurred_at, event_type, actor_id,
                   operation_id, run_id, plan_id, item_id, correlation_id,
                   redacted_values::text, redacted_values_sha256,
                   full_values_sha256, full_values_expires_at
            FROM entitysync.audit_events
            WHERE tenant_id = @tenant_id
              AND (@continuation_occurred_at IS NULL
                   OR (occurred_at, audit_event_id) < (@continuation_occurred_at, @continuation_event_id))
            ORDER BY occurred_at DESC, audit_event_id DESC
            LIMIT @row_limit
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "continuation_occurred_at", NpgsqlDbType.TimestampTz, continuationOccurredAt);
        PostgresControlPersistence.Add(command, "continuation_event_id", NpgsqlDbType.Uuid, continuationEventId);
        PostgresControlPersistence.Add(command, "row_limit", NpgsqlDbType.Integer, checked(pageSize + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<EntitySyncAuditEvent>(pageSize + 1);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) events.Add(ReadEvent(reader));
        DateTimeOffset? nextTime = null;
        Guid? nextId = null;
        if (events.Count > pageSize)
        {
            events.RemoveAt(events.Count - 1);
            nextTime = events[^1].OccurredAt;
            nextId = events[^1].AuditEventId;
        }
        return new EntitySyncAuditPage(tenantId, nextTime, nextId, events);
    }

    public async Task<EntitySyncAuditEventFullValues?> GetFullValuesAsync(
        string tenantId, Guid auditEventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, audit_event_id, full_values_ciphertext, expires_at
            FROM entitysync.audit_event_full_values
            WHERE tenant_id = @tenant_id AND audit_event_id = @audit_event_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "audit_event_id", NpgsqlDbType.Uuid, auditEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new EntitySyncAuditEventFullValues(reader.GetString(0), reader.GetGuid(1),
                reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    public async Task<int> DeleteExpiredFullValuesAsync(
        string tenantId, DateTimeOffset now, int maximumRows, CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            WITH expired AS (
                SELECT tenant_id, audit_event_id
                FROM entitysync.audit_event_full_values
                WHERE tenant_id = @tenant_id
                  AND expires_at <= clock_timestamp()
                  AND values_redacted_at IS NULL
                ORDER BY expires_at, audit_event_id
                LIMIT @maximum_rows
                FOR UPDATE SKIP LOCKED
            ), scrubbed AS (
                UPDATE entitysync.audit_event_full_values value
                SET full_values_ciphertext = NULL,
                    values_redacted_at = clock_timestamp()
                FROM expired
                WHERE value.tenant_id = expired.tenant_id
                  AND value.audit_event_id = expired.audit_event_id
                RETURNING value.tenant_id, value.audit_event_id,
                          value.values_redacted_at
            ), marked AS (
                UPDATE entitysync.audit_events event
                SET values_redacted_at = scrubbed.values_redacted_at
                FROM scrubbed
                WHERE event.tenant_id = scrubbed.tenant_id
                  AND event.audit_event_id = scrubbed.audit_event_id
                RETURNING event.audit_event_id
            )
            SELECT count(*)::integer FROM marked
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static void AddEvent(NpgsqlCommand command, EntitySyncAuditEvent auditEvent)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, auditEvent.TenantId);
        PostgresControlPersistence.Add(command, "audit_event_id", NpgsqlDbType.Uuid, auditEvent.AuditEventId);
        PostgresControlPersistence.Add(command, "occurred_at", NpgsqlDbType.TimestampTz, auditEvent.OccurredAt);
        PostgresControlPersistence.Add(command, "event_type", NpgsqlDbType.Text, auditEvent.EventType);
        PostgresControlPersistence.Add(command, "actor_id", NpgsqlDbType.Text, auditEvent.Actor.ActorId);
        PostgresControlPersistence.Add(command, "operation_id", NpgsqlDbType.Uuid, auditEvent.OperationId);
        PostgresControlPersistence.Add(command, "run_id", NpgsqlDbType.Uuid, auditEvent.RunId);
        PostgresControlPersistence.Add(command, "plan_id", NpgsqlDbType.Uuid, auditEvent.PlanId);
        PostgresControlPersistence.Add(command, "item_id", NpgsqlDbType.Uuid, auditEvent.ItemId);
        PostgresControlPersistence.Add(command, "correlation_id", NpgsqlDbType.Text, auditEvent.CorrelationId);
        PostgresControlPersistence.Add(command, "redacted_values", NpgsqlDbType.Jsonb, auditEvent.RedactedValues.Json);
        PostgresControlPersistence.Add(command, "redacted_values_sha256", NpgsqlDbType.Char, auditEvent.RedactedValuesSha256.Value);
        PostgresControlPersistence.Add(command, "full_values_sha256", NpgsqlDbType.Char, auditEvent.FullValuesSha256?.Value);
        PostgresControlPersistence.Add(command, "full_values_expires_at", NpgsqlDbType.TimestampTz, auditEvent.FullValuesExpiresAt);
    }

    private static EntitySyncAuditEvent ReadEvent(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
        reader.GetString(3), new EntitySyncActor(reader.GetString(4)),
        PostgresControlPersistence.NullableGuid(reader, 5),
        PostgresControlPersistence.NullableGuid(reader, 6),
        PostgresControlPersistence.NullableGuid(reader, 7),
        PostgresControlPersistence.NullableGuid(reader, 8), reader.GetString(9),
        new EntitySyncJsonValue(reader.GetString(10)), new EntitySyncSha256(reader.GetString(11)),
        PostgresControlPersistence.NullableHash(reader, 12),
        PostgresControlPersistence.NullableTime(reader, 13));
}

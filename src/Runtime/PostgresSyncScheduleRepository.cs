using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresSyncScheduleRepository(NpgsqlDataSource dataSource) : ISyncScheduleRepository
{
    public async Task InsertVersionAsync(string tenantId, EntitySyncSchedule schedule, CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, schedule.TenantId, nameof(schedule));
        const string sql = """
            INSERT INTO entitysync.sync_schedules (
                tenant_id, schedule_id, version, name, policy_id, policy_version,
                cron_expression, time_zone, enabled, next_run_at, last_run_at,
                created_at, created_by)
            VALUES (@tenant_id, @schedule_id, @version, @name, @policy_id, @policy_version,
                @cron_expression, @time_zone, @enabled, @next_run_at, @last_run_at,
                @created_at, @created_by)
            """;
        await using var lease = await PostgresControlTransaction
            .AcquireAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection, lease.Transaction);
        AddSchedule(command, schedule);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<EntitySyncSchedule?> GetAsync(string tenantId, Guid scheduleId, int version, CancellationToken cancellationToken) =>
        GetOneAsync("""
            SELECT tenant_id, schedule_id, version, name, policy_id, policy_version,
                   cron_expression, time_zone, enabled, next_run_at, last_run_at,
                   created_at, created_by
            FROM entitysync.sync_schedules
            WHERE tenant_id = @tenant_id AND schedule_id = @schedule_id AND version = @version
            """, tenantId, scheduleId, version, cancellationToken);

    public Task<EntitySyncSchedule?> GetLatestAsync(string tenantId, Guid scheduleId, CancellationToken cancellationToken) =>
        GetOneAsync("""
            SELECT tenant_id, schedule_id, version, name, policy_id, policy_version,
                   cron_expression, time_zone, enabled, next_run_at, last_run_at,
                   created_at, created_by
            FROM entitysync.sync_schedules
            WHERE tenant_id = @tenant_id AND schedule_id = @schedule_id
            ORDER BY version DESC LIMIT 1
            """, tenantId, scheduleId, null, cancellationToken);

    public async Task<IReadOnlyList<EntitySyncSchedule>> ListLatestAsync(
        string tenantId,
        int offset,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumRows is <= 0 or > 101)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            SELECT latest.tenant_id, latest.schedule_id, latest.version, latest.name,
                   latest.policy_id, latest.policy_version, latest.cron_expression,
                   latest.time_zone, latest.enabled, latest.next_run_at,
                   latest.last_run_at, latest.created_at, latest.created_by
            FROM (
                SELECT DISTINCT ON (schedule_id)
                       tenant_id, schedule_id, version, name, policy_id, policy_version,
                       cron_expression, time_zone, enabled, next_run_at, last_run_at,
                       created_at, created_by
                FROM entitysync.sync_schedules
                WHERE tenant_id = @tenant_id
                ORDER BY schedule_id, version DESC
            ) latest
            WHERE latest.tenant_id = @tenant_id
            ORDER BY latest.schedule_id
            LIMIT @maximum_rows OFFSET @offset
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(
            command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        PostgresControlPersistence.Add(command, "offset", NpgsqlDbType.Integer, offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<EntitySyncSchedule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadSchedule(reader));
        return result;
    }

    public async Task<IReadOnlyList<EntitySyncSchedule>> ListDueAsync(
        string tenantId, DateTimeOffset dueAt, int maximumRows, CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            SELECT schedule.tenant_id, schedule.schedule_id, schedule.version, schedule.name,
                   schedule.policy_id, schedule.policy_version, schedule.cron_expression,
                   schedule.time_zone, schedule.enabled, schedule.next_run_at,
                   schedule.last_run_at, schedule.created_at, schedule.created_by
            FROM entitysync.sync_schedules schedule
            WHERE schedule.tenant_id = @tenant_id AND schedule.enabled
              AND schedule.next_run_at IS NOT NULL AND schedule.next_run_at <= @due_at
              AND NOT EXISTS (
                    SELECT 1 FROM entitysync.sync_schedules newer
                    WHERE newer.tenant_id = @tenant_id
                      AND newer.schedule_id = schedule.schedule_id
                      AND newer.version > schedule.version)
            ORDER BY schedule.next_run_at, schedule.schedule_id
            LIMIT @maximum_rows
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "due_at", NpgsqlDbType.TimestampTz, dueAt);
        PostgresControlPersistence.Add(command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EntitySyncSchedule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadSchedule(reader));
        return result;
    }

    public async Task InsertChangeEventAsync(
        string tenantId, EntitySyncCanonicalChangeEvent changeEvent, CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, changeEvent.TenantId, nameof(changeEvent));
        const string sql = """
            INSERT INTO entitysync.canonical_change_events (
                tenant_id, event_id, canonical_entity_type, canonical_entity_id,
                canonical_version, changed_fields, occurred_at, received_at, status)
            VALUES (@tenant_id, @event_id, @canonical_entity_type, @canonical_entity_id,
                @canonical_version, @changed_fields, @occurred_at, @received_at, @status)
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddChangeEvent(command, changeEvent);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EntitySyncCanonicalChangeEvent>> ListPendingChangeEventsAsync(
        string tenantId, int maximumRows, CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            SELECT tenant_id, event_id, canonical_entity_type, canonical_entity_id,
                   canonical_version, changed_fields::text, occurred_at, received_at, status
            FROM entitysync.canonical_change_events
            WHERE tenant_id = @tenant_id AND status = 'Pending'
            ORDER BY received_at, event_id
            LIMIT @maximum_rows
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<EntitySyncCanonicalChangeEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadChangeEvent(reader));
        return result;
    }

    public async Task<bool> TrySetChangeEventStatusAsync(
        string tenantId, Guid eventId, EntitySyncCanonicalChangeStatus expectedStatus,
        EntitySyncCanonicalChangeStatus status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.canonical_change_events SET status = @status
            WHERE tenant_id = @tenant_id AND event_id = @event_id AND status = @expected_status
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "event_id", NpgsqlDbType.Uuid, eventId);
        PostgresControlPersistence.Add(command, "expected_status", NpgsqlDbType.Text, expectedStatus.ToString());
        PostgresControlPersistence.Add(command, "status", NpgsqlDbType.Text, status.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<EntitySyncSchedule?> GetOneAsync(string sql, string tenantId, Guid scheduleId,
        int? version, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "schedule_id", NpgsqlDbType.Uuid, scheduleId);
        if (version is not null)
            PostgresControlPersistence.Add(command, "version", NpgsqlDbType.Integer, version.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSchedule(reader) : null;
    }

    private static void AddSchedule(NpgsqlCommand command, EntitySyncSchedule schedule)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, schedule.TenantId);
        PostgresControlPersistence.Add(command, "schedule_id", NpgsqlDbType.Uuid, schedule.ScheduleId);
        PostgresControlPersistence.Add(command, "version", NpgsqlDbType.Integer, schedule.Version);
        PostgresControlPersistence.Add(command, "name", NpgsqlDbType.Text, schedule.Name);
        PostgresControlPersistence.Add(command, "policy_id", NpgsqlDbType.Uuid, schedule.PolicyId);
        PostgresControlPersistence.Add(command, "policy_version", NpgsqlDbType.Integer, schedule.PolicyVersion);
        PostgresControlPersistence.Add(command, "cron_expression", NpgsqlDbType.Text, schedule.CronExpression);
        PostgresControlPersistence.Add(command, "time_zone", NpgsqlDbType.Text, schedule.TimeZone);
        PostgresControlPersistence.Add(command, "enabled", NpgsqlDbType.Boolean, schedule.Enabled);
        PostgresControlPersistence.Add(command, "next_run_at", NpgsqlDbType.TimestampTz, schedule.NextRunAt);
        PostgresControlPersistence.Add(command, "last_run_at", NpgsqlDbType.TimestampTz, schedule.LastRunAt);
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, schedule.CreatedAt);
        PostgresControlPersistence.Add(command, "created_by", NpgsqlDbType.Text, schedule.CreatedBy.ActorId);
    }

    private static void AddChangeEvent(NpgsqlCommand command, EntitySyncCanonicalChangeEvent changeEvent)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, changeEvent.TenantId);
        PostgresControlPersistence.Add(command, "event_id", NpgsqlDbType.Uuid, changeEvent.EventId);
        PostgresControlPersistence.Add(command, "canonical_entity_type", NpgsqlDbType.Text, changeEvent.CanonicalEntityType);
        PostgresControlPersistence.Add(command, "canonical_entity_id", NpgsqlDbType.Text, changeEvent.CanonicalEntityId);
        PostgresControlPersistence.Add(command, "canonical_version", NpgsqlDbType.Bigint, changeEvent.CanonicalVersion);
        PostgresControlPersistence.Add(command, "changed_fields", NpgsqlDbType.Jsonb, changeEvent.ChangedFields.Json);
        PostgresControlPersistence.Add(command, "occurred_at", NpgsqlDbType.TimestampTz, changeEvent.OccurredAt);
        PostgresControlPersistence.Add(command, "received_at", NpgsqlDbType.TimestampTz, changeEvent.ReceivedAt);
        PostgresControlPersistence.Add(command, "status", NpgsqlDbType.Text, changeEvent.Status.ToString());
    }

    private static EntitySyncSchedule ReadSchedule(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
        reader.GetBoolean(8), PostgresControlPersistence.NullableTime(reader, 9),
        PostgresControlPersistence.NullableTime(reader, 10), reader.GetFieldValue<DateTimeOffset>(11),
        new EntitySyncActor(reader.GetString(12)));

    private static EntitySyncCanonicalChangeEvent ReadChangeEvent(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4), new EntitySyncJsonValue(reader.GetString(5)),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
        PostgresControlPersistence.ParseEnum<EntitySyncCanonicalChangeStatus>(reader.GetString(8)));
}

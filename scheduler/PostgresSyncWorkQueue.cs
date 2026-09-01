using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Scheduler;

public enum SyncControlWorkKind { Schedule, CanonicalChange }
public enum SyncControlWorkState { Queued, Leased, Planning, Held, Completed }

public sealed record SyncControlWork(
    string TenantId, Guid WorkId, SyncControlWorkKind Kind, SyncControlWorkState State,
    Guid PolicyId, int PolicyVersion, string RouteScope,
    Guid? ScheduleId, int? ScheduleVersion, DateTimeOffset? ScheduledFor,
    Guid? CanonicalEventId, string? CanonicalEntityType, Guid? CanonicalEntityId,
    long? CanonicalVersion, IReadOnlyList<string> ChangedFields,
    EntitySyncSha256? PayloadSha256, string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt, int Attempt, Guid? PlanId,
    EntitySyncSha256? PlanDigestSha256, Guid? ApprovalId, Guid? OperationId,
    string? HoldReason);

public sealed class PostgresSyncWorkQueue(NpgsqlDataSource dataSource)
    : ICanonicalChangeRepository, IEntitySyncWorkSignal
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<CanonicalChangeReceipt> AcceptAsync(
        CanonicalChangeRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var eventId = StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            Namespace = "entitysync-canonical-receipt-v1",
            request.TenantId,
            request.OutboxEventId
        }));
        var fieldsJson = JsonSerializer.Serialize(request.ChangedFields);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var eventInserted = false;
        const string insertEventSql = """
            INSERT INTO entitysync.canonical_change_events (
                tenant_id, event_id, receipt_id, om_event_id, canonical_entity_type,
                canonical_entity_id, canonical_version, changed_fields, payload_sha256,
                occurred_at, received_at, status)
            VALUES (@tenant, @event, @receipt, @outbox, @entity_type, @entity_id,
                    @version, @fields, @hash, @occurred, clock_timestamp(), 'Pending')
            ON CONFLICT (tenant_id, om_event_id) DO NOTHING
            """;
        await using (var insert = new NpgsqlCommand(insertEventSql, connection, transaction))
        {
            Add(insert, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(insert, "event", NpgsqlDbType.Uuid, eventId);
            Add(insert, "receipt", NpgsqlDbType.Uuid, eventId);
            Add(insert, "outbox", NpgsqlDbType.Text, request.OutboxEventId);
            Add(insert, "entity_type", NpgsqlDbType.Text, request.CanonicalEntityType);
            Add(insert, "entity_id", NpgsqlDbType.Text, request.CanonicalEntityId.ToString("D"));
            Add(insert, "version", NpgsqlDbType.Bigint, request.CanonicalVersion);
            Add(insert, "fields", NpgsqlDbType.Jsonb, fieldsJson);
            Add(insert, "hash", NpgsqlDbType.Char, request.PayloadSha256.Value);
            Add(insert, "occurred", NpgsqlDbType.TimestampTz, request.OccurredAt);
            eventInserted = await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) == 1;
        }

        const string readEventSql = """
            SELECT event_id, receipt_id, canonical_entity_type, canonical_entity_id,
                   canonical_version, changed_fields::text, payload_sha256, received_at
            FROM entitysync.canonical_change_events
            WHERE tenant_id = @tenant AND om_event_id = @outbox
            FOR UPDATE
            """;
        Guid storedEventId;
        DateTimeOffset storedReceivedAt;
        await using (var read = new NpgsqlCommand(readEventSql, connection, transaction))
        {
            Add(read, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(read, "outbox", NpgsqlDbType.Text, request.OutboxEventId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Canonical receipt disappeared during intake.");
            storedEventId = reader.GetGuid(0);
            var storedFields = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [];
            var identical = reader.GetString(2).Equals(
                                request.CanonicalEntityType, StringComparison.OrdinalIgnoreCase)
                            && Guid.TryParse(reader.GetString(3), out var storedEntityId)
                            && storedEntityId == request.CanonicalEntityId
                            && reader.GetInt64(4) == request.CanonicalVersion
                            && reader.GetString(6).Equals(
                                request.PayloadSha256.Value, StringComparison.Ordinal)
                            && storedFields.SequenceEqual(
                                request.ChangedFields, StringComparer.OrdinalIgnoreCase);
            if (!identical)
                throw new CanonicalChangeConflictException(request.OutboxEventId);
            storedReceivedAt = reader.GetFieldValue<DateTimeOffset>(7);
        }

        const string createWorkSql = """
            WITH latest AS (
                SELECT DISTINCT ON (policy_id)
                       policy_id, version, route_scope, definition, enabled
                FROM entitysync.sync_policies
                WHERE tenant_id = @tenant
                ORDER BY policy_id, version DESC
            )
            INSERT INTO entitysync.sync_control_work (
                tenant_id, work_id, work_kind, state, policy_id, policy_version,
                route_scope, canonical_event_id, canonical_entity_type,
                canonical_entity_id, canonical_version, changed_fields,
                payload_sha256, created_at, updated_at)
            SELECT @tenant,
                   md5(@tenant || ':' || @event::text || ':' || latest.policy_id::text
                       || ':' || latest.version::text)::uuid,
                   'CanonicalChange', 'Queued', latest.policy_id, latest.version,
                   latest.route_scope, @event, @entity_type, @entity_id, @version,
                   @fields, @hash, clock_timestamp(), clock_timestamp()
            FROM latest
            WHERE latest.enabled
              AND (latest.definition->>'SourceVendor') = 'OrchestraMSP'
              AND lower(latest.definition->>'SourceEntityType') = lower(@entity_type)
            ON CONFLICT (tenant_id, canonical_event_id, policy_id, policy_version)
                WHERE work_kind = 'CanonicalChange' DO NOTHING
            """;
        await using (var work = new NpgsqlCommand(createWorkSql, connection, transaction))
        {
            Add(work, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(work, "event", NpgsqlDbType.Uuid, storedEventId);
            Add(work, "entity_type", NpgsqlDbType.Text, request.CanonicalEntityType);
            Add(work, "entity_id", NpgsqlDbType.Uuid, request.CanonicalEntityId);
            Add(work, "version", NpgsqlDbType.Bigint, request.CanonicalVersion);
            Add(work, "fields", NpgsqlDbType.Jsonb, fieldsJson);
            Add(work, "hash", NpgsqlDbType.Char, request.PayloadSha256.Value);
            if (eventInserted)
                await work.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var workIds = await ReadCanonicalWorkIdsAsync(
            connection, transaction, request.TenantId, storedEventId, cancellationToken)
            .ConfigureAwait(false);
        const string statusSql = """
            UPDATE entitysync.canonical_change_events
            SET status = @status
            WHERE tenant_id = @tenant AND event_id = @event AND status = 'Pending'
            """;
        await using (var status = new NpgsqlCommand(statusSql, connection, transaction))
        {
            Add(status, "status", NpgsqlDbType.Text,
                workIds.Count == 0 ? "Ignored" : "Planned");
            Add(status, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(status, "event", NpgsqlDbType.Uuid, storedEventId);
            if (eventInserted)
                await status.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (eventInserted)
            await NotifyInTransactionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CanonicalChangeReceipt(
            eventId, request.TenantId, request.OutboxEventId,
            request.CanonicalEntityId, request.CanonicalVersion,
            request.PayloadSha256, workIds, storedReceivedAt);
    }

    public async Task<int> EnqueueDueAsync(
        string tenantId, int maximumRows, CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string dueSql = """
            SELECT schedule_id, version, policy_id, policy_version, cron_expression,
                   time_zone, next_run_at, runtime_revision
            FROM entitysync.sync_schedules schedule
            WHERE tenant_id = @tenant AND enabled
              AND next_run_at <= clock_timestamp()
              AND NOT EXISTS (
                  SELECT 1 FROM entitysync.sync_schedules newer
                  WHERE newer.tenant_id = schedule.tenant_id
                    AND newer.schedule_id = schedule.schedule_id
                    AND newer.version > schedule.version)
            ORDER BY next_run_at, schedule_id
            FOR UPDATE SKIP LOCKED
            LIMIT @limit
            """;
        var due = new List<(Guid Id, int Version, Guid PolicyId, int PolicyVersion,
            string Cron, string Zone, DateTimeOffset ScheduledFor, long Revision)>();
        await using (var select = new NpgsqlCommand(dueSql, connection, transaction))
        {
            Add(select, "tenant", NpgsqlDbType.Text, tenantId);
            Add(select, "limit", NpgsqlDbType.Integer, maximumRows);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                due.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7)));
        }
        foreach (var item in due)
        {
            var next = SyncScheduleService.GetNextRun(
                item.Cron, TimeZoneInfo.FindSystemTimeZoneById(item.Zone), item.ScheduledFor);
            var workId = CreateScheduleWorkId(
                tenantId, item.Id, item.Version, item.ScheduledFor);
            const string workSql = """
                INSERT INTO entitysync.sync_control_work (
                    tenant_id, work_id, work_kind, state, policy_id, policy_version,
                    route_scope, schedule_id, schedule_version, scheduled_for,
                    created_at, updated_at)
                SELECT @tenant, @work, 'Schedule', 'Queued', policy.policy_id,
                       policy.version, policy.route_scope, @schedule, @version,
                       @scheduled_for, clock_timestamp(), clock_timestamp()
                FROM entitysync.sync_policies policy
                WHERE policy.tenant_id = @tenant
                  AND policy.policy_id = @policy
                  AND policy.version = @policy_version
                ON CONFLICT (tenant_id, schedule_id, schedule_version, scheduled_for)
                    WHERE work_kind = 'Schedule' DO NOTHING
                """;
            await using (var insert = new NpgsqlCommand(workSql, connection, transaction))
            {
                Add(insert, "tenant", NpgsqlDbType.Text, tenantId);
                Add(insert, "work", NpgsqlDbType.Uuid, workId);
                Add(insert, "schedule", NpgsqlDbType.Uuid, item.Id);
                Add(insert, "version", NpgsqlDbType.Integer, item.Version);
                Add(insert, "scheduled_for", NpgsqlDbType.TimestampTz, item.ScheduledFor);
                Add(insert, "policy", NpgsqlDbType.Uuid, item.PolicyId);
                Add(insert, "policy_version", NpgsqlDbType.Integer, item.PolicyVersion);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            const string advanceSql = """
                UPDATE entitysync.sync_schedules
                SET last_run_at = @scheduled_for, next_run_at = @next,
                    runtime_revision = runtime_revision + 1
                WHERE tenant_id = @tenant AND schedule_id = @schedule
                  AND version = @version AND runtime_revision = @revision
                  AND next_run_at = @scheduled_for
                """;
            await using var advance = new NpgsqlCommand(advanceSql, connection, transaction);
            Add(advance, "tenant", NpgsqlDbType.Text, tenantId);
            Add(advance, "schedule", NpgsqlDbType.Uuid, item.Id);
            Add(advance, "version", NpgsqlDbType.Integer, item.Version);
            Add(advance, "revision", NpgsqlDbType.Bigint, item.Revision);
            Add(advance, "scheduled_for", NpgsqlDbType.TimestampTz, item.ScheduledFor);
            Add(advance, "next", NpgsqlDbType.TimestampTz, next);
            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The due schedule lost its revision fence.");
        }
        if (due.Count > 0)
            await NotifyInTransactionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return due.Count;
    }

    public async Task<SyncControlWork?> TryLeaseNextAsync(
        string tenantId, string leaseOwner, TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwner));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        const string sql = """
            WITH candidate AS (
                SELECT tenant_id, work_id FROM entitysync.sync_control_work
                WHERE tenant_id = @tenant AND not_before <= clock_timestamp()
                  AND (state = 'Queued' OR (state IN ('Leased','Planning')
                       AND lease_expires_at <= clock_timestamp()))
                ORDER BY created_at, work_id FOR UPDATE SKIP LOCKED LIMIT 1
            ), leased AS (
                UPDATE entitysync.sync_control_work work
                SET state = 'Leased', lease_owner = @owner,
                    lease_expires_at = clock_timestamp() + @duration,
                    attempt = work.attempt + 1, updated_at = clock_timestamp()
                FROM candidate
                WHERE work.tenant_id = candidate.tenant_id
                  AND work.work_id = candidate.work_id
                RETURNING work.*
            )
            SELECT tenant_id, work_id, work_kind, state, policy_id, policy_version,
                   route_scope, schedule_id, schedule_version, scheduled_for,
                   canonical_event_id, canonical_entity_type, canonical_entity_id,
                   canonical_version, changed_fields::text, payload_sha256,
                   lease_owner, lease_expires_at, attempt, plan_id,
                   plan_digest_sha256, approval_id, operation_id, hold_reason
            FROM leased
            """;
        await using var command = dataSource.CreateCommand(sql);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId);
        Add(command, "owner", NpgsqlDbType.Text, leaseOwner.Trim());
        Add(command, "duration", NpgsqlDbType.Interval, leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadWork(reader) : null;
    }

    public Task<bool> TryStartPlanningAsync(SyncControlWork work, CancellationToken cancellationToken) =>
        FencedUpdateAsync(work, "state = 'Planning', updated_at = clock_timestamp()",
            "state = 'Leased'", null, cancellationToken);

    public async Task<bool> TryRenewAsync(
        SyncControlWork work,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        const string sql = """
            UPDATE entitysync.sync_control_work
            SET lease_expires_at = clock_timestamp() + @duration,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND work_id = @work
              AND attempt = @attempt AND lease_owner = @owner
              AND lease_expires_at > clock_timestamp()
              AND state = 'Planning'
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddFence(command, work);
        Add(command, "duration", NpgsqlDbType.Interval, leaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public Task<bool> TryReleaseAsync(
        SyncControlWork work,
        CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            work,
            "state = 'Queued', lease_owner = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()",
            "state = 'Leased'",
            null,
            cancellationToken);

    public Task<bool> TryDeferAsync(
        SyncControlWork work,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        return FencedUpdateAsync(
            work,
            "state = 'Queued', not_before = clock_timestamp() + @delay, lease_owner = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()",
            "state = 'Leased'",
            null,
            cancellationToken,
            ("delay", NpgsqlDbType.Interval, delay));
    }

    public Task<bool> TryCheckpointPlanAsync(
        SyncControlWork work,
        Guid planId,
        EntitySyncSha256 digest,
        CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            work,
            "checkpoint = 'Planned', plan_id = @plan, plan_digest_sha256 = @digest, updated_at = clock_timestamp()",
            "state = 'Planning' AND checkpoint = 'Pending' AND plan_id IS NULL",
            null,
            cancellationToken,
            ("plan", NpgsqlDbType.Uuid, planId),
            ("digest", NpgsqlDbType.Char, digest.Value));

    public Task<bool> TryCheckpointApprovalAsync(
        SyncControlWork work,
        Guid approvalId,
        CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            work,
            "checkpoint = 'Approved', approval_id = @approval, updated_at = clock_timestamp()",
            "state = 'Planning' AND checkpoint = 'Planned' AND approval_id IS NULL",
            null,
            cancellationToken,
            ("approval", NpgsqlDbType.Uuid, approvalId));

    public Task<bool> TryCheckpointOperationAsync(
        SyncControlWork work,
        Guid operationId,
        CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            work,
            "checkpoint = 'OperationQueued', operation_id = @operation, updated_at = clock_timestamp()",
            "state = 'Planning' AND checkpoint = 'Approved' AND operation_id IS NULL",
            null,
            cancellationToken,
            ("operation", NpgsqlDbType.Uuid, operationId));

    public Task<bool> TryHoldAsync(SyncControlWork work, string reason, CancellationToken cancellationToken) =>
        FencedUpdateAsync(work,
            "state = 'Held', hold_reason = @reason, lease_owner = NULL, lease_expires_at = NULL, updated_at = clock_timestamp()",
            "state IN ('Leased','Planning')", reason, cancellationToken);

    public async Task<bool> TryCompleteAsync(
        SyncControlWork work, Guid planId, Guid approvalId, Guid operationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.sync_control_work
            SET state = 'Completed', hold_reason = NULL,
                lease_owner = NULL, lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND work_id = @work
              AND attempt = @attempt AND lease_owner = @owner
              AND lease_expires_at > clock_timestamp() AND state = 'Planning'
              AND checkpoint = 'OperationQueued'
              AND plan_id = @plan AND approval_id = @approval AND operation_id = @operation
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddFence(command, work);
        Add(command, "plan", NpgsqlDbType.Uuid, planId);
        Add(command, "approval", NpgsqlDbType.Uuid, approvalId);
        Add(command, "operation", NpgsqlDbType.Uuid, operationId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await NotifyAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task NotifyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT pg_notify('entitysync_work', '')");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var listen = connection.CreateCommand())
        {
            listen.CommandText = "LISTEN entitysync_work";
            await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Guid CreateScheduleWorkId(
        string tenantId,
        Guid scheduleId,
        int scheduleVersion,
        DateTimeOffset scheduledFor) =>
        StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            Namespace = "entitysync-schedule-work-v1",
            TenantId = tenantId,
            ScheduleId = scheduleId,
            Version = scheduleVersion,
            ScheduledFor = scheduledFor
        }));

    public static Guid CreateControlApprovalId(Guid workId) =>
        StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            Namespace = "entitysync-control-approval-v1",
            WorkId = workId
        }));

    public static bool CanQueueDue(
        EntitySyncSchedule schedule,
        EntitySyncPolicy policy,
        int latestScheduleVersion,
        DateTimeOffset now) =>
        schedule.Enabled
        && schedule.Version == latestScheduleVersion
        && schedule.NextRunAt is not null
        && schedule.NextRunAt <= now
        && policy.PolicyId == schedule.PolicyId
        && policy.Version == schedule.PolicyVersion;

    public static bool IsSafeSubset(
        EntitySyncPolicy policy, IReadOnlyList<EntitySyncDurablePlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(items);
        if (!policy.Enabled || !policy.Definition.ScheduledApplySafeSubset) return false;
        foreach (var item in items)
        {
            if (item.Action.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.Action.Equals("Update", StringComparison.OrdinalIgnoreCase)
                || !item.MatchEvidence.MatchType.Equals("Linked", StringComparison.OrdinalIgnoreCase)
                || item.TargetEntityId is null || item.FieldDiffs.Count == 0
                || item.FieldDiffs.Any(diff => policy.Definition.BlockedFields.Contains(diff.Field)
                    || !policy.Definition.AllowedFields.Contains(diff.Field)))
                return false;
        }
        return items.Count > 0;
    }

    public static bool CanRetryExpiredOperation(IReadOnlyList<EntitySyncOperationItem> items) =>
        items.All(item => item.DispatchStartedAt is null
                          && item.Outcome != EntitySyncItemOutcome.Unknown);

    private async Task<bool> FencedUpdateAsync(
        SyncControlWork work, string setClause, string stateClause, string? reason,
        CancellationToken cancellationToken,
        params (string Name, NpgsqlDbType Type, object? Value)[] values)
    {
        var sql = $"""
            UPDATE entitysync.sync_control_work SET {setClause}
            WHERE tenant_id = @tenant AND work_id = @work
              AND attempt = @attempt AND lease_owner = @owner
              AND lease_expires_at > clock_timestamp() AND {stateClause}
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddFence(command, work);
        if (reason is not null) Add(command, "reason", NpgsqlDbType.Text, reason);
        foreach (var value in values)
            Add(command, value.Name, value.Type, value.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<Guid>> ReadCanonicalWorkIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string tenantId, Guid eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT work_id FROM entitysync.sync_control_work
            WHERE tenant_id = @tenant AND canonical_event_id = @event
            ORDER BY policy_id, policy_version
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Add(command, "tenant", NpgsqlDbType.Text, tenantId);
        Add(command, "event", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(reader.GetGuid(0));
        return result;
    }

    private static async Task NotifyInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_notify('entitysync_work', '')", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SyncControlWork ReadWork(NpgsqlDataReader reader)
    {
        IReadOnlyList<string> fields = reader.IsDBNull(14) ? []
            : JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? [];
        return new SyncControlWork(
            reader.GetString(0), reader.GetGuid(1),
            Enum.Parse<SyncControlWorkKind>(reader.GetString(2)),
            Enum.Parse<SyncControlWorkState>(reader.GetString(3)),
            reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13), fields,
            reader.IsDBNull(15) ? null : new EntitySyncSha256(reader.GetString(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
            reader.GetInt32(18), reader.IsDBNull(19) ? null : reader.GetGuid(19),
            reader.IsDBNull(20) ? null : new EntitySyncSha256(reader.GetString(20)),
            reader.IsDBNull(21) ? null : reader.GetGuid(21),
            reader.IsDBNull(22) ? null : reader.GetGuid(22),
            reader.IsDBNull(23) ? null : reader.GetString(23));
    }

    private static void AddFence(NpgsqlCommand command, SyncControlWork work)
    {
        Add(command, "tenant", NpgsqlDbType.Text, work.TenantId);
        Add(command, "work", NpgsqlDbType.Uuid, work.WorkId);
        Add(command, "attempt", NpgsqlDbType.Integer, work.Attempt);
        Add(command, "owner", NpgsqlDbType.Text, work.LeaseOwner);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static Guid StableGuid(EntitySyncSha256 digest) =>
        new(Convert.FromHexString(digest.Value).AsSpan(0, 16));
}

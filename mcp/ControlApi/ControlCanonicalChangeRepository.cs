using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed class ControlCanonicalChangeRepository(NpgsqlDataSource dataSource)
    : ICanonicalChangeRepository, IEntitySyncWorkSignal
{
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
        const string insertEventSql = """
            INSERT INTO entitysync.canonical_change_events (
                tenant_id, event_id, receipt_id, om_event_id, canonical_entity_type,
                canonical_entity_id, canonical_version, changed_fields, payload_sha256,
                occurred_at, received_at, status)
            VALUES (@tenant, @event, @receipt, @outbox, @entity_type, @entity_id,
                    @version, @fields, @hash, @occurred, clock_timestamp(), 'Pending')
            ON CONFLICT (tenant_id, om_event_id) DO NOTHING
            """;
        var inserted = false;
        await using (var insert = new NpgsqlCommand(insertEventSql, connection, transaction))
        {
            AddRequest(insert, request, eventId, fieldsJson);
            Add(insert, "receipt", NpgsqlDbType.Uuid, eventId);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken)
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
        Guid receiptId;
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
            receiptId = reader.GetGuid(1);
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

        if (inserted)
        {
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
            await using var work = new NpgsqlCommand(createWorkSql, connection, transaction);
            Add(work, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(work, "event", NpgsqlDbType.Uuid, storedEventId);
            Add(work, "entity_type", NpgsqlDbType.Text, request.CanonicalEntityType);
            Add(work, "entity_id", NpgsqlDbType.Uuid, request.CanonicalEntityId);
            Add(work, "version", NpgsqlDbType.Bigint, request.CanonicalVersion);
            Add(work, "fields", NpgsqlDbType.Jsonb, fieldsJson);
            Add(work, "hash", NpgsqlDbType.Char, request.PayloadSha256.Value);
            await work.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var workIds = await ReadWorkIdsAsync(
            connection, transaction, request.TenantId, storedEventId, cancellationToken)
            .ConfigureAwait(false);
        if (inserted)
        {
            const string statusSql = """
                UPDATE entitysync.canonical_change_events
                SET status = @status
                WHERE tenant_id = @tenant AND event_id = @event AND status = 'Pending'
                """;
            await using var status = new NpgsqlCommand(statusSql, connection, transaction);
            Add(status, "status", NpgsqlDbType.Text,
                workIds.Count == 0 ? "Ignored" : "Planned");
            Add(status, "tenant", NpgsqlDbType.Text, request.TenantId);
            Add(status, "event", NpgsqlDbType.Uuid, storedEventId);
            await status.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var notify = new NpgsqlCommand(
                "SELECT pg_notify('entitysync_work', '')", connection, transaction);
            await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CanonicalChangeReceipt(
            receiptId, request.TenantId, request.OutboxEventId,
            request.CanonicalEntityId, request.CanonicalVersion,
            request.PayloadSha256, workIds, storedReceivedAt);
    }

    public async Task NotifyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT pg_notify('entitysync_work', '')");
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

    private static void AddRequest(
        NpgsqlCommand command,
        CanonicalChangeRequest request,
        Guid eventId,
        string fieldsJson)
    {
        Add(command, "tenant", NpgsqlDbType.Text, request.TenantId);
        Add(command, "event", NpgsqlDbType.Uuid, eventId);
        Add(command, "outbox", NpgsqlDbType.Text, request.OutboxEventId);
        Add(command, "entity_type", NpgsqlDbType.Text, request.CanonicalEntityType);
        Add(command, "entity_id", NpgsqlDbType.Text, request.CanonicalEntityId.ToString("D"));
        Add(command, "version", NpgsqlDbType.Bigint, request.CanonicalVersion);
        Add(command, "fields", NpgsqlDbType.Jsonb, fieldsJson);
        Add(command, "hash", NpgsqlDbType.Char, request.PayloadSha256.Value);
        Add(command, "occurred", NpgsqlDbType.TimestampTz, request.OccurredAt);
    }

    private static async Task<IReadOnlyList<Guid>> ReadWorkIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
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

    private static Guid StableGuid(EntitySyncSha256 digest) =>
        new(Convert.FromHexString(digest.Value).AsSpan(0, 16));

    private static void Add(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
}

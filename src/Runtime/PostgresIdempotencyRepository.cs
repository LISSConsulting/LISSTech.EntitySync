using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed class PostgresIdempotencyRepository(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    : IIdempotencyRepository, IIdempotentCommandExecutor
{
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(24);

    public async Task<bool> TryInsertAsync(
        string tenantId, EntitySyncIdempotencyReceipt receipt, CancellationToken cancellationToken)
    {
        PostgresControlPersistence.RequireTenant(tenantId, receipt.TenantId, nameof(receipt));
        const string sql = """
            INSERT INTO entitysync.api_idempotency_records (
                tenant_id, idempotency_key, request_sha256, response_status_code,
                response_body, created_at, completed_at, expires_at)
            VALUES (@tenant_id, @idempotency_key, @request_sha256, @response_status_code,
                @response_body, @created_at, @completed_at, @expires_at)
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddReceipt(command, receipt);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<EntitySyncIdempotencyReceipt?> GetAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, idempotency_key, request_sha256, response_status_code,
                   response_body::text, created_at, completed_at, expires_at
            FROM entitysync.api_idempotency_records
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddKey(command, tenantId, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadReceipt(reader) : null;
    }

    public async Task<bool> TryCompleteAsync(string tenantId, string idempotencyKey,
        EntitySyncSha256 requestSha256, int responseStatusCode, EntitySyncJsonValue responseBody,
        DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.api_idempotency_records
            SET response_status_code = @response_status_code,
                response_body = @response_body,
                completed_at = @completed_at
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
              AND request_sha256 = @request_sha256
              AND response_status_code IS NULL AND response_body IS NULL AND completed_at IS NULL
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddKey(command, tenantId, idempotencyKey);
        PostgresControlPersistence.Add(command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(command, "response_status_code", NpgsqlDbType.Integer, responseStatusCode);
        PostgresControlPersistence.Add(command, "response_body", NpgsqlDbType.Jsonb, responseBody.Json);
        PostgresControlPersistence.Add(command, "completed_at", NpgsqlDbType.TimestampTz, completedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> DeleteExpiredAsync(
        string tenantId, DateTimeOffset now, int maximumRows, CancellationToken cancellationToken)
    {
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        const string sql = """
            WITH expired AS (
                SELECT tenant_id, idempotency_key
                FROM entitysync.api_idempotency_records
                WHERE tenant_id = @tenant_id AND expires_at <= @now
                ORDER BY expires_at, idempotency_key
                LIMIT @maximum_rows
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM entitysync.api_idempotency_records receipt
            USING expired
            WHERE receipt.tenant_id = expired.tenant_id
              AND receipt.idempotency_key = expired.idempotency_key
              AND receipt.tenant_id = @tenant_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "now", NpgsqlDbType.TimestampTz, now);
        PostgresControlPersistence.Add(command, "maximum_rows", NpgsqlDbType.Integer, maximumRows);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdempotentResponse> ExecuteAsync(
        string tenantId, string key, string requestHash,
        Func<CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestSha256 = new EntitySyncSha256(requestHash);
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Idempotency key is required.", nameof(key));
        tenantId = tenantId.Trim();
        key = key.Trim();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        const string lockSql = """
            SELECT pg_advisory_xact_lock(
                hashtextextended(@tenant_id || chr(31) || @idempotency_key, 0))
            """;
        await using (var advisoryLock = new NpgsqlCommand(lockSql, connection, transaction))
        {
            AddKey(advisoryLock, tenantId, key);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var existing = await GetForUpdateAsync(connection, transaction, tenantId, key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.ExpiresAt <= now)
        {
            const string deleteSql = """
                DELETE FROM entitysync.api_idempotency_records
                WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
                  AND expires_at <= @now
                """;
            await using var delete = new NpgsqlCommand(deleteSql, connection, transaction);
            AddKey(delete, tenantId, key);
            PostgresControlPersistence.Add(delete, "now", NpgsqlDbType.TimestampTz, now);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            existing = null;
        }
        if (existing is not null)
        {
            if (existing.RequestSha256 != requestSha256)
                throw new IdempotencyConflictException("The idempotency key is already bound to a different request hash.");
            if (existing.ResponseStatusCode is not null && existing.ResponseBody is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new IdempotentResponse(existing.ResponseStatusCode.Value, existing.ResponseBody);
            }
        }

        var response = await command(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The idempotent command returned no response.");
        if (existing is null)
        {
            var receipt = new EntitySyncIdempotencyReceipt(
                tenantId, key, requestSha256, response.StatusCode, response.ResponseBody,
                now, now, now + ReceiptLifetime);
            const string insertSql = """
                INSERT INTO entitysync.api_idempotency_records (
                    tenant_id, idempotency_key, request_sha256, response_status_code,
                    response_body, created_at, completed_at, expires_at)
                VALUES (@tenant_id, @idempotency_key, @request_sha256, @response_status_code,
                    @response_body, @created_at, @completed_at, @expires_at)
                """;
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            AddReceipt(insert, receipt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            const string completeSql = """
                UPDATE entitysync.api_idempotency_records
                SET response_status_code = @response_status_code,
                    response_body = @response_body,
                    completed_at = @completed_at
                WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
                  AND request_sha256 = @request_sha256
                  AND response_status_code IS NULL AND response_body IS NULL
                """;
            await using var complete = new NpgsqlCommand(completeSql, connection, transaction);
            AddKey(complete, tenantId, key);
            PostgresControlPersistence.Add(complete, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
            PostgresControlPersistence.Add(complete, "response_status_code", NpgsqlDbType.Integer, response.StatusCode);
            PostgresControlPersistence.Add(complete, "response_body", NpgsqlDbType.Jsonb, response.ResponseBody.Json);
            PostgresControlPersistence.Add(complete, "completed_at", NpgsqlDbType.TimestampTz, now);
            if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The incomplete idempotency receipt could not be completed.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task<EntitySyncIdempotencyReceipt?> GetForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, string key,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tenant_id, idempotency_key, request_sha256, response_status_code,
                   response_body::text, created_at, completed_at, expires_at
            FROM entitysync.api_idempotency_records
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            FOR UPDATE
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddKey(command, tenantId, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadReceipt(reader) : null;
    }

    private static void AddKey(NpgsqlCommand command, string tenantId, string idempotencyKey)
    {
        PostgresControlPersistence.Add(command, "tenant_id", NpgsqlDbType.Text, tenantId);
        PostgresControlPersistence.Add(command, "idempotency_key", NpgsqlDbType.Text, idempotencyKey);
    }

    private static void AddReceipt(NpgsqlCommand command, EntitySyncIdempotencyReceipt receipt)
    {
        AddKey(command, receipt.TenantId, receipt.IdempotencyKey);
        PostgresControlPersistence.Add(command, "request_sha256", NpgsqlDbType.Char, receipt.RequestSha256.Value);
        PostgresControlPersistence.Add(command, "response_status_code", NpgsqlDbType.Integer, receipt.ResponseStatusCode);
        PostgresControlPersistence.Add(command, "response_body", NpgsqlDbType.Jsonb, receipt.ResponseBody?.Json);
        PostgresControlPersistence.Add(command, "created_at", NpgsqlDbType.TimestampTz, receipt.CreatedAt);
        PostgresControlPersistence.Add(command, "completed_at", NpgsqlDbType.TimestampTz, receipt.CompletedAt);
        PostgresControlPersistence.Add(command, "expires_at", NpgsqlDbType.TimestampTz, receipt.ExpiresAt);
    }

    private static EntitySyncIdempotencyReceipt ReadReceipt(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), new EntitySyncSha256(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.IsDBNull(4) ? null : new EntitySyncJsonValue(reader.GetString(4)),
        reader.GetFieldValue<DateTimeOffset>(5), PostgresControlPersistence.NullableTime(reader, 6),
        reader.GetFieldValue<DateTimeOffset>(7));
}

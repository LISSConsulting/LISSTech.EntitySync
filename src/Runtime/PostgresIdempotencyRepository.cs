using System.Security.Cryptography;
using System.Text;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Runtime;

public sealed record PostgresIdempotencyExecutionOptions
{
    public static PostgresIdempotencyExecutionOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(50));

    public PostgresIdempotencyExecutionOptions(
        TimeSpan leaseDuration,
        TimeSpan heartbeatInterval,
        TimeSpan pollInterval)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (heartbeatInterval <= TimeSpan.Zero || heartbeatInterval >= leaseDuration)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        LeaseDuration = leaseDuration;
        HeartbeatInterval = heartbeatInterval;
        PollInterval = pollInterval;
    }

    public TimeSpan LeaseDuration { get; }
    public TimeSpan HeartbeatInterval { get; }
    public TimeSpan PollInterval { get; }
}

public sealed class PostgresIdempotencyRepository
    : IIdempotencyRepository, IIdempotentCommandExecutor
{
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(24);
    private readonly NpgsqlDataSource dataSource;
    private readonly TimeSpan executionLeaseDuration;
    private readonly TimeSpan executionHeartbeatInterval;
    private readonly TimeSpan leasePollInterval;

    public PostgresIdempotencyRepository(
        NpgsqlDataSource dataSource,
        PostgresIdempotencyExecutionOptions? options = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        var selected = options ?? PostgresIdempotencyExecutionOptions.Default;
        executionLeaseDuration = selected.LeaseDuration;
        executionHeartbeatInterval = selected.HeartbeatInterval;
        leasePollInterval = selected.PollInterval;
    }

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
              AND execution_owner IS NULL AND execution_lease_expires_at IS NULL
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddKey(command, tenantId, idempotencyKey);
        PostgresControlPersistence.Add(command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(command, "response_status_code", NpgsqlDbType.Integer, responseStatusCode);
        PostgresControlPersistence.Add(command, "response_body", NpgsqlDbType.Text, responseBody.Json);
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
        string tenantId,
        string key,
        string requestHash,
        IdempotencyExecutionMode mode,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestSha256 = new EntitySyncSha256(requestHash);
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key is required.", nameof(key));
        tenantId = tenantId.Trim();
        key = key.Trim();
        var owner = Guid.NewGuid();

        while (true)
        {
            var claim = await TryClaimExecutionAsync(
                tenantId, key, requestSha256, owner, cancellationToken).ConfigureAwait(false);
            if (claim.Response is not null) return claim.Response;
            if (!claim.Acquired)
            {
                await Task.Delay(leasePollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var context = new IdempotencyExecutionContext(
                tenantId,
                key,
                CreateStableToken(tenantId, key, requestSha256),
                claim.IsRecovery);
            return mode == IdempotencyExecutionMode.AtomicDatabase
                ? await ExecuteAtomicOwnedAsync(
                    tenantId,
                    key,
                    requestSha256,
                    owner,
                    claim.Attempt,
                    context,
                    command,
                    cancellationToken).ConfigureAwait(false)
                : await ExecuteRecoverableOwnedAsync(
                    tenantId,
                    key,
                    requestSha256,
                    owner,
                    claim.Attempt,
                    context,
                    command,
                    cancellationToken).ConfigureAwait(false);
        }
    }


    private async Task<ExecutionClaim> TryClaimExecutionAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await GetExecutionForUpdateAsync(
            connection, transaction, tenantId, key, cancellationToken).ConfigureAwait(false);
        if (record is not null && record.ExpiresAt <= record.DatabaseNow)
        {
            const string deleteSql = """
                DELETE FROM entitysync.api_idempotency_records
                WHERE tenant_id = @tenant_id
                  AND idempotency_key = @idempotency_key
                  AND expires_at <= clock_timestamp()
                """;
            await using var delete = new NpgsqlCommand(deleteSql, connection, transaction);
            AddKey(delete, tenantId, key);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            record = null;
        }

        if (record is not null && record.RequestSha256 != requestSha256)
            throw new IdempotencyConflictException(
                "The idempotency key is already bound to a different request hash.");
        if (record?.ResponseStatusCode is { } statusCode
            && record.ResponseBody is { } responseBody)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ExecutionClaim.Replay(new IdempotentResponse(statusCode, responseBody));
        }
        if (record?.Owner is not null
            && record.LeaseExpiresAt is { } leaseExpiresAt
            && leaseExpiresAt > record.DatabaseNow)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ExecutionClaim.Waiting;
        }

        long attempt;
        var recovering = record is not null;
        if (record is null)
        {
            attempt = 1;
            const string insertSql = """
                INSERT INTO entitysync.api_idempotency_records (
                    tenant_id, idempotency_key, request_sha256,
                    response_status_code, response_body, created_at,
                    completed_at, expires_at, execution_owner,
                    execution_attempt, execution_lease_expires_at)
                VALUES (
                    @tenant_id, @idempotency_key, @request_sha256,
                    NULL, NULL, clock_timestamp(), NULL,
                    clock_timestamp() + @receipt_lifetime,
                    @execution_owner, @execution_attempt,
                    clock_timestamp() + @lease_duration)
                ON CONFLICT (tenant_id, idempotency_key) DO NOTHING
                """;
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            AddExecutionIdentity(insert, tenantId, key, requestSha256, owner, attempt);
            PostgresControlPersistence.Add(
                insert, "receipt_lifetime", NpgsqlDbType.Interval, ReceiptLifetime);
            PostgresControlPersistence.Add(
                insert, "lease_duration", NpgsqlDbType.Interval, executionLeaseDuration);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ExecutionClaim.Waiting;
            }
        }
        else
        {
            attempt = checked(record.Attempt + 1);
            const string takeoverSql = """
                UPDATE entitysync.api_idempotency_records
                SET execution_owner = @execution_owner,
                    execution_attempt = @execution_attempt,
                    execution_lease_expires_at = clock_timestamp() + @lease_duration
                WHERE tenant_id = @tenant_id
                  AND idempotency_key = @idempotency_key
                  AND request_sha256 = @request_sha256
                  AND response_status_code IS NULL
                  AND response_body IS NULL
                  AND completed_at IS NULL
                  AND (
                      execution_owner IS NULL
                      OR execution_lease_expires_at <= clock_timestamp())
                """;
            await using var takeover = new NpgsqlCommand(
                takeoverSql, connection, transaction);
            AddExecutionIdentity(takeover, tenantId, key, requestSha256, owner, attempt);
            PostgresControlPersistence.Add(
                takeover, "lease_duration", NpgsqlDbType.Interval, executionLeaseDuration);
            if (await takeover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ExecutionClaim.Waiting;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExecutionClaim(true, recovering, attempt, null);
    }

    private async Task<IdempotentResponse> ExecuteRecoverableOwnedAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt,
        IdempotencyExecutionContext context,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await RunOwnedCommandAsync(
                tenantId, key, requestSha256, owner, attempt, context, command,
                cancellationToken).ConfigureAwait(false);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var completed = await CompleteOwnedAsync(
                connection, transaction, tenantId, key, requestSha256, owner, attempt,
                response, allowCompletedReceiptAdoption: true, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch
        {
            await ReleaseOwnedAsync(
                tenantId, key, requestSha256, owner, attempt).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IdempotentResponse> ExecuteAtomicOwnedAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt,
        IdempotencyExecutionContext context,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            using var scope = PostgresControlTransaction.Enter(connection, transaction);
            var response = await RunOwnedCommandAsync(
                tenantId, key, requestSha256, owner, attempt, context, command,
                cancellationToken).ConfigureAwait(false);
            var completed = await CompleteOwnedAsync(
                connection, transaction, tenantId, key, requestSha256, owner, attempt,
                response, allowCompletedReceiptAdoption: false, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch
        {
            await ReleaseOwnedAsync(
                tenantId, key, requestSha256, owner, attempt).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IdempotentResponse> RunOwnedCommandAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt,
        IdempotencyExecutionContext context,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var callback = InvokeCommandAsync(command, context, execution.Token);
        var heartbeat = MaintainExecutionLeaseAsync(
            tenantId, key, requestSha256, owner, attempt, execution.Token);
        if (await Task.WhenAny(callback, heartbeat).ConfigureAwait(false) == heartbeat)
        {
            Exception ownershipFailure;
            try
            {
                await heartbeat.ConfigureAwait(false);
                ownershipFailure = new IdempotencyExecutionLeaseLostException();
            }
            catch (Exception exception)
            {
                ownershipFailure = exception;
            }
            execution.Cancel();
            try
            {
                await callback.ConfigureAwait(false);
            }
            catch
            {
                // Ownership loss is authoritative; observe callback termination before returning.
            }
            throw ownershipFailure;
        }

        IdempotentResponse response;
        try
        {
            response = await callback.ConfigureAwait(false);
        }
        catch
        {
            execution.Cancel();
            await ObserveHeartbeatTerminationAsync(heartbeat).ConfigureAwait(false);
            throw;
        }
        execution.Cancel();
        await ObserveHeartbeatTerminationAsync(heartbeat).ConfigureAwait(false);
        return response;
    }

    private static async Task<IdempotentResponse> InvokeCommandAsync(
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        IdempotencyExecutionContext context,
        CancellationToken cancellationToken) =>
        await command(context, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The idempotent command returned no response.");

    private async Task MaintainExecutionLeaseAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(executionHeartbeatInterval, cancellationToken)
                .ConfigureAwait(false);
            await using var command = dataSource.CreateCommand(
                """
                UPDATE entitysync.api_idempotency_records
                SET execution_lease_expires_at = clock_timestamp() + @lease_duration
                WHERE tenant_id = @tenant_id
                  AND idempotency_key = @idempotency_key
                  AND request_sha256 = @request_sha256
                  AND execution_owner = @execution_owner
                  AND execution_attempt = @execution_attempt
                  AND execution_lease_expires_at > clock_timestamp()
                  AND response_status_code IS NULL
                  AND response_body IS NULL
                  AND completed_at IS NULL
                """);
            AddExecutionIdentity(command, tenantId, key, requestSha256, owner, attempt);
            PostgresControlPersistence.Add(
                command, "lease_duration", NpgsqlDbType.Interval, executionLeaseDuration);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new IdempotencyExecutionLeaseLostException();
        }
    }

    private static async Task ObserveHeartbeatTerminationAsync(Task heartbeat)
    {
        try
        {
            await heartbeat.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReleaseOwnedAsync(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt)
    {
        const string sql = """
            UPDATE entitysync.api_idempotency_records
            SET execution_owner = NULL,
                execution_lease_expires_at = NULL
            WHERE tenant_id = @tenant_id
              AND idempotency_key = @idempotency_key
              AND request_sha256 = @request_sha256
              AND execution_owner = @execution_owner
              AND execution_attempt = @execution_attempt
              AND response_status_code IS NULL
              AND response_body IS NULL
              AND completed_at IS NULL
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddExecutionIdentity(command, tenantId, key, requestSha256, owner, attempt);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IdempotentResponse> CompleteOwnedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt,
        IdempotentResponse response,
        bool allowCompletedReceiptAdoption,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE entitysync.api_idempotency_records
            SET response_status_code = @response_status_code,
                response_body = @response_body,
                completed_at = clock_timestamp(),
                execution_owner = NULL,
                execution_lease_expires_at = NULL
            WHERE tenant_id = @tenant_id
              AND idempotency_key = @idempotency_key
              AND request_sha256 = @request_sha256
              AND execution_owner = @execution_owner
              AND execution_attempt = @execution_attempt
              AND execution_lease_expires_at > clock_timestamp()
              AND response_status_code IS NULL
              AND response_body IS NULL
              AND completed_at IS NULL
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddExecutionIdentity(command, tenantId, key, requestSha256, owner, attempt);
        PostgresControlPersistence.Add(
            command, "response_status_code", NpgsqlDbType.Integer, response.StatusCode);
        PostgresControlPersistence.Add(
            command, "response_body", NpgsqlDbType.Text, response.ResponseBody.Json);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            return response;

        if (!allowCompletedReceiptAdoption)
            throw new IdempotencyExecutionLeaseLostException();

        var completed = await GetExecutionForUpdateAsync(
            connection, transaction, tenantId, key, cancellationToken).ConfigureAwait(false);
        if (completed?.RequestSha256 == requestSha256
            && completed.ResponseStatusCode is { } statusCode
            && completed.ResponseBody is { } responseBody)
            return new IdempotentResponse(statusCode, responseBody);
        throw new IdempotencyExecutionLeaseLostException();
    }

    private static async Task<ExecutionRecord?> GetExecutionForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_sha256, response_status_code, response_body::text,
                   expires_at, execution_owner, execution_attempt,
                   execution_lease_expires_at, clock_timestamp()
            FROM entitysync.api_idempotency_records
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            FOR UPDATE
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddKey(command, tenantId, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ExecutionRecord(
            new EntitySyncSha256(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : new EntitySyncJsonValue(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetInt64(5),
            PostgresControlPersistence.NullableTime(reader, 6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static void AddExecutionIdentity(
        NpgsqlCommand command,
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256,
        Guid owner,
        long attempt)
    {
        AddKey(command, tenantId, key);
        PostgresControlPersistence.Add(
            command, "request_sha256", NpgsqlDbType.Char, requestSha256.Value);
        PostgresControlPersistence.Add(
            command, "execution_owner", NpgsqlDbType.Uuid, owner);
        PostgresControlPersistence.Add(
            command, "execution_attempt", NpgsqlDbType.Bigint, attempt);
    }

    private static string CreateStableToken(
        string tenantId,
        string key,
        EntitySyncSha256 requestSha256)
    {
        var material = $"{tenantId.Length}:{tenantId}{key.Length}:{key}{requestSha256.Value}";
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private sealed record ExecutionRecord(
        EntitySyncSha256 RequestSha256,
        int? ResponseStatusCode,
        EntitySyncJsonValue? ResponseBody,
        DateTimeOffset ExpiresAt,
        Guid? Owner,
        long Attempt,
        DateTimeOffset? LeaseExpiresAt,
        DateTimeOffset DatabaseNow);

    private sealed record ExecutionClaim(
        bool Acquired,
        bool IsRecovery,
        long Attempt,
        IdempotentResponse? Response)
    {
        public static ExecutionClaim Waiting { get; } = new(false, false, 0, null);
        public static ExecutionClaim Replay(IdempotentResponse response) =>
            new(false, false, 0, response);
    }

    private sealed class IdempotencyExecutionLeaseLostException()
        : InvalidOperationException("The durable idempotency execution lease was lost.");

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
        PostgresControlPersistence.Add(command, "response_body", NpgsqlDbType.Text, receipt.ResponseBody?.Json);
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

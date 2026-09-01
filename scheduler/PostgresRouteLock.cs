using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using Npgsql;
using NpgsqlTypes;

namespace LISSTech.EntitySync.Scheduler;

public interface IEntitySyncRouteLease : IEntitySyncOperationRouteLease
{
}

public interface IEntitySyncRouteLock
{
    Task<IEntitySyncRouteLease?> TryAcquireAsync(
        string tenantId,
        string routeScope,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}

public sealed class PostgresRouteLock(NpgsqlDataSource dataSource)
    : IEntitySyncRouteLock, IEntitySyncOperationRouteLock
{
    public static bool CanTakeLease(
        DateTimeOffset existingLeaseExpiresAt,
        DateTimeOffset databaseNow) =>
        existingLeaseExpiresAt <= databaseNow;

    async Task<IEntitySyncOperationRouteLease?>
        IEntitySyncOperationRouteLock.TryAcquireAsync(
            EntitySyncOperation operation,
            string owner,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
        await TryAcquireAsync(
            operation.TenantId, operation.RouteScope, owner, leaseDuration,
            cancellationToken).ConfigureAwait(false);

    public async Task<IEntitySyncRouteLease?> TryAcquireAsync(
        string tenantId,
        string routeScope,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        routeScope = Require(routeScope, nameof(routeScope));
        owner = Require(owner, nameof(owner));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        var token = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        const string advisorySql = """
            SELECT pg_try_advisory_xact_lock(
                hashtextextended(@tenant || chr(31) || @route, 17))
            """;
        await using (var advisory = new NpgsqlCommand(advisorySql, connection, transaction))
        {
            advisory.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId);
            advisory.Parameters.AddWithValue("route", NpgsqlDbType.Text, routeScope);
            if (await advisory.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                is not true)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        const string leaseSql = """
            INSERT INTO entitysync.sync_route_leases (
                tenant_id, route_scope, lease_owner, lease_token,
                lease_expires_at, attempt)
            VALUES (@tenant, @route, @owner, @token,
                    clock_timestamp() + @duration, 1)
            ON CONFLICT (tenant_id, route_scope) DO UPDATE
            SET lease_owner = EXCLUDED.lease_owner,
                lease_token = EXCLUDED.lease_token,
                lease_expires_at = EXCLUDED.lease_expires_at,
                attempt = entitysync.sync_route_leases.attempt + 1
            WHERE entitysync.sync_route_leases.lease_expires_at <= clock_timestamp()
            RETURNING attempt
            """;
        long? attempt;
        await using (var lease = new NpgsqlCommand(leaseSql, connection, transaction))
        {
            lease.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId);
            lease.Parameters.AddWithValue("route", NpgsqlDbType.Text, routeScope);
            lease.Parameters.AddWithValue("owner", NpgsqlDbType.Text, owner);
            lease.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, token);
            lease.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, leaseDuration);
            attempt = await lease.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                as long?;
        }
        if (attempt is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RouteLease(dataSource, tenantId, routeScope, owner, token, attempt.Value);
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();

    private sealed class RouteLease(
        NpgsqlDataSource dataSource,
        string tenantId,
        string routeScope,
        string owner,
        Guid token,
        long attempt) : IEntitySyncRouteLease
    {
        private int disposed;
        public async Task<bool> TryRenewAsync(
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            if (leaseDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            const string sql = """
                UPDATE entitysync.sync_route_leases
                SET lease_expires_at = clock_timestamp() + @duration
                WHERE tenant_id = @tenant AND route_scope = @route
                  AND lease_owner = @owner AND lease_token = @token
                  AND attempt = @attempt
                  AND lease_expires_at > clock_timestamp()
                """;
            await using var command = CreateCommand(sql);
            command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, leaseDuration);
            return await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) == 1;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            const string sql = """
                DELETE FROM entitysync.sync_route_leases
                WHERE tenant_id = @tenant AND route_scope = @route
                  AND lease_owner = @owner AND lease_token = @token
                  AND attempt = @attempt
                """;
            await using var command = CreateCommand(sql);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private NpgsqlCommand CreateCommand(string sql)
        {
            var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId);
            command.Parameters.AddWithValue("route", NpgsqlDbType.Text, routeScope);
            command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, owner);
            command.Parameters.AddWithValue("token", NpgsqlDbType.Uuid, token);
            command.Parameters.AddWithValue("attempt", NpgsqlDbType.Bigint, attempt);
            return command;
        }
    }
}

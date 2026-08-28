using Npgsql;

namespace LISSTech.EntitySync.Scheduler;

public sealed class PostgresEntitySyncSchedulerRunLock(NpgsqlDataSource dataSource) : IEntitySyncSchedulerRunLock
{
    private const string AcquireSql = "SELECT pg_try_advisory_lock(hashtextextended(@route_key, 0))";
    private const string ReleaseSql = "SELECT pg_advisory_unlock(hashtextextended(@route_key, 0))";

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string routeKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
            throw new ArgumentException("Route key is required.", nameof(routeKey));

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = AcquireSql;
            command.Parameters.AddWithValue("route_key", routeKey);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not true)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new AdvisoryLockLease(connection, routeKey);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class AdvisoryLockLease(
        NpgsqlConnection ownedConnection,
        string routeKey) : IAsyncDisposable
    {
        private NpgsqlConnection? connection = ownedConnection;

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref connection, null);
            if (owned is null) return;

            try
            {
                await using var command = owned.CreateCommand();
                command.CommandText = ReleaseSql;
                command.Parameters.AddWithValue("route_key", routeKey);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

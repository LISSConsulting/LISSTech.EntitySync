using System.Threading;
using Npgsql;

namespace LISSTech.EntitySync.Runtime;

internal static class PostgresControlTransaction
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static Scope Enter(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        if (CurrentState.Value is not null)
            throw new InvalidOperationException("A control transaction is already active.");
        CurrentState.Value = new State(connection, transaction);
        return new Scope();
    }

    public static async Task<Lease> AcquireAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (CurrentState.Value is { } current)
            return new Lease(current.Connection, current.Transaction, ownsResources: false);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            return new Lease(connection, transaction, ownsResources: true);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record State(NpgsqlConnection Connection, NpgsqlTransaction Transaction);

    internal sealed class Scope : IDisposable
    {
        public void Dispose() => CurrentState.Value = null;
    }

    internal sealed class Lease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool ownsResources) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) =>
            ownsResources ? Transaction.CommitAsync(cancellationToken) : Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) =>
            ownsResources ? Transaction.RollbackAsync(cancellationToken) : Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (!ownsResources) return;
            await Transaction.DisposeAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

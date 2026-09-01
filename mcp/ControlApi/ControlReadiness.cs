using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record ControlReadinessResult(
    bool DatabaseMigrations,
    bool KeyRing,
    bool WorkerHeartbeat)
{
    public bool Ready => DatabaseMigrations && KeyRing && WorkerHeartbeat;
}

public interface IControlReadinessProbe
{
    Task<ControlReadinessResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed class ControlReadinessProbe(
    NpgsqlDataSource dataSource,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider,
    EntitySyncWorkerSettings workerSettings) : IControlReadinessProbe
{

    public async Task<ControlReadinessResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        var keyRing = CheckKeyRing();
        var migrations = false;
        var heartbeat = false;
        try
        {
            await using var connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var migrationCommand = new NpgsqlCommand(
                             "SELECT version FROM entitysync.schema_migrations",
                             connection))
            await using (var reader = await migrationCommand
                             .ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                var appliedVersions = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    appliedVersions.Add(reader.GetString(0));
                migrations =
                    appliedVersions.Count == EntitySyncDatabaseMigrator.ExpectedVersions.Count
                    && EntitySyncDatabaseMigrator.ExpectedVersions.All(
                        appliedVersions.Contains);
            }

            await using var heartbeatCommand = new NpgsqlCommand(
                "SELECT COALESCE(max(observed_at) >= @minimum_heartbeat, false) " +
                "FROM entitysync.control_worker_heartbeats",
                connection);
            heartbeatCommand.Parameters.AddWithValue(
                "minimum_heartbeat",
                timeProvider.GetUtcNow() - workerSettings.MaximumHeartbeatAge);
            heartbeat = (bool)(await heartbeatCommand
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            migrations = false;
            heartbeat = false;
        }

        return new ControlReadinessResult(migrations, keyRing, heartbeat);
    }

    private bool CheckKeyRing()
    {
        try
        {
            var protector = dataProtection.CreateProtector(
                "LISSTech.EntitySync.ControlApi.Readiness.v1");
            var plaintext = Guid.NewGuid().ToString("N");
            var protectedValue = protector.Protect(plaintext);
            return protector.Unprotect(protectedValue) == plaintext;
        }
        catch
        {
            return false;
        }
    }
}

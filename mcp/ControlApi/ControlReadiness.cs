using LISSTech.EntitySync.Hosting;
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
            await using var command = dataSource.CreateCommand(
                "SELECT " +
                "EXISTS (SELECT 1 FROM entitysync.schema_migrations WHERE version = '018_snapshot_evidence_enrichment'), " +
                "COALESCE(max(observed_at) >= @minimum_heartbeat, false) " +
                "FROM entitysync.control_worker_heartbeats");
            command.Parameters.AddWithValue(
                "minimum_heartbeat",
                timeProvider.GetUtcNow() - workerSettings.MaximumHeartbeatAge);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                migrations = reader.GetBoolean(0);
                heartbeat = reader.GetBoolean(1);
            }
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

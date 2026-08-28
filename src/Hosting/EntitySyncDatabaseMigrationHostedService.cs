using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace LISSTech.EntitySync.Hosting;

public sealed class EntitySyncDatabaseMigrationHostedService(NpgsqlDataSource dataSource) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        EntitySyncDatabaseMigrator.ApplyAsync(dataSource, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using LISSTech.EntitySync.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LISSTech.EntitySync.Scheduler;

public sealed class AuditRetentionWorker : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly ISyncAuditRepository audits;
    private readonly ISyncOperationRepository operations;
    private readonly IReadOnlyList<string> tenantIds;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AuditRetentionWorker> logger;

    public AuditRetentionWorker(
        ISyncAuditRepository audits,
        ISyncOperationRepository operations,
        IReadOnlyList<string> tenantIds,
        TimeProvider? timeProvider = null,
        ILogger<AuditRetentionWorker>? logger = null)
    {
        this.audits = audits ?? throw new ArgumentNullException(nameof(audits));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        ArgumentNullException.ThrowIfNull(tenantIds);
        this.tenantIds = tenantIds
            .Select(value => string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Tenant IDs cannot contain blanks.", nameof(tenantIds))
                : value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<AuditRetentionWorker>.Instance;
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var tenantId in tenantIds)
        {
            // Repositories deliberately use clock_timestamp(); the supplied value exists only
            // for the stable port shared with older callers and is never authoritative.
            var observed = timeProvider.GetUtcNow();
            total += await audits.DeleteExpiredFullValuesAsync(
                tenantId, observed, BatchSize, cancellationToken).ConfigureAwait(false);
            total += await operations.DeleteExpiredSnapshotsAsync(
                tenantId, observed, BatchSize, cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "EntitySync audit retention failed without exposing retained values.");
            }
            await Task.Delay(Interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}

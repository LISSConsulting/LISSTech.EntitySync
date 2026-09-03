using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed class SyncAuditService(
    ISyncAuditRepository audits,
    IEntitySyncDataProtector protector,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FullValueRetention = TimeSpan.FromDays(365);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<bool> AppendAsync(
        string tenantId,
        string eventType,
        EntitySyncActor actor,
        Guid operationId,
        Guid planId,
        Guid? itemId,
        string correlationId,
        object redactedValues,
        object? fullValues,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(
            tenantId, eventType, actor, operationId, planId, itemId,
            correlationId, redactedValues, fullValues);
        return await audits.TryAppendAsync(
            tenantId, prepared.Event, prepared.FullValues, cancellationToken)
            .ConfigureAwait(false);
    }

    public PreparedSyncAudit Prepare(
        string tenantId,
        string eventType,
        EntitySyncActor actor,
        Guid operationId,
        Guid planId,
        Guid? itemId,
        string correlationId,
        object redactedValues,
        object? fullValues)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(redactedValues);
        var occurredAt = clock.GetUtcNow();
        var redactedElement = JsonSerializer.SerializeToElement(redactedValues);
        var redacted = new EntitySyncJsonValue(redactedElement.GetRawText());
        var redactedHash = EntitySyncCanonicalDigest.Compute(redactedElement);
        EntitySyncSha256? fullHash = null;
        DateTimeOffset? expiresAt = null;
        EntitySyncAuditEventFullValues? retained = null;
        var eventId = StableGuid(EntitySyncCanonicalDigest.Compute(new
        {
            TenantId = tenantId,
            EventType = eventType,
            OperationId = operationId,
            PlanId = planId,
            ItemId = itemId,
            CorrelationId = correlationId
        }));
        if (fullValues is not null)
        {
            var fullElement = JsonSerializer.SerializeToElement(fullValues);
            fullHash = EntitySyncCanonicalDigest.Compute(fullElement);
            expiresAt = occurredAt + FullValueRetention;
            retained = new EntitySyncAuditEventFullValues(
                tenantId,
                eventId,
                protector.Protect(
                    EntitySyncDataProtectionPurpose.AuditValue,
                    fullElement.GetRawText()),
                expiresAt.Value);
        }
        var auditEvent = new EntitySyncAuditEvent(
            tenantId,
            eventId,
            occurredAt,
            eventType,
            actor,
            operationId,
            null,
            planId,
            itemId,
            correlationId,
            redacted,
            redactedHash,
            fullHash,
            expiresAt);
        return new PreparedSyncAudit(auditEvent, retained);
    }

    private static Guid StableGuid(EntitySyncSha256 digest)
    {
        Span<byte> bytes = stackalloc byte[16];
        Convert.FromHexString(digest.Value).AsSpan(0, bytes.Length).CopyTo(bytes);
        return new Guid(bytes);
    }
}

public sealed record PreparedSyncAudit(
    EntitySyncAuditEvent Event,
    EntitySyncAuditEventFullValues? FullValues);

namespace LISSTech.EntitySync.Core;

public sealed record EntitySyncAuditEvent
{
    public EntitySyncAuditEvent(
        string tenantId,
        Guid auditEventId,
        DateTimeOffset occurredAt,
        string eventType,
        EntitySyncActor actor,
        Guid? operationId,
        Guid? runId,
        Guid? planId,
        Guid? itemId,
        string correlationId,
        EntitySyncJsonValue redactedValues,
        EntitySyncSha256 redactedValuesSha256,
        EntitySyncSha256? fullValuesSha256,
        DateTimeOffset? fullValuesExpiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        AuditEventId = ControlModelGuard.NonEmpty(auditEventId, nameof(auditEventId));
        OccurredAt = occurredAt;
        EventType = ControlModelGuard.Required(eventType, nameof(eventType));
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        OperationId = OptionalId(operationId, nameof(operationId));
        RunId = OptionalId(runId, nameof(runId));
        PlanId = OptionalId(planId, nameof(planId));
        ItemId = OptionalId(itemId, nameof(itemId));
        CorrelationId = ControlModelGuard.Required(correlationId, nameof(correlationId));
        RedactedValues = redactedValues ?? throw new ArgumentNullException(nameof(redactedValues));
        RedactedValuesSha256 = redactedValuesSha256 ?? throw new ArgumentNullException(nameof(redactedValuesSha256));
        if ((fullValuesSha256 is null) != (fullValuesExpiresAt is null))
            throw new ArgumentException("Full-value hash and expiry must be supplied together.", nameof(fullValuesExpiresAt));
        if (fullValuesExpiresAt > occurredAt.AddDays(365))
            throw new ArgumentOutOfRangeException(nameof(fullValuesExpiresAt), fullValuesExpiresAt, "Full audit values cannot be retained for more than 365 days.");
        FullValuesSha256 = fullValuesSha256;
        FullValuesExpiresAt = fullValuesExpiresAt;
    }

    public string TenantId { get; }
    public Guid AuditEventId { get; }
    public DateTimeOffset OccurredAt { get; }
    public string EventType { get; }
    public EntitySyncActor Actor { get; }
    public Guid? OperationId { get; }
    public Guid? RunId { get; }
    public Guid? PlanId { get; }
    public Guid? ItemId { get; }
    public string CorrelationId { get; }
    public EntitySyncJsonValue RedactedValues { get; }
    public EntitySyncSha256 RedactedValuesSha256 { get; }
    public EntitySyncSha256? FullValuesSha256 { get; }
    public DateTimeOffset? FullValuesExpiresAt { get; }

    private static Guid? OptionalId(Guid? value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException($"{parameterName} cannot be empty.", parameterName);
        return value;
    }
}

public sealed record EntitySyncAuditEventFullValues
{
    public EntitySyncAuditEventFullValues(
        string tenantId,
        Guid auditEventId,
        string fullValuesCiphertext,
        DateTimeOffset expiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        AuditEventId = ControlModelGuard.NonEmpty(auditEventId, nameof(auditEventId));
        FullValuesCiphertext = ControlModelGuard.Required(fullValuesCiphertext, nameof(fullValuesCiphertext));
        ExpiresAt = expiresAt;
    }

    public string TenantId { get; }
    public Guid AuditEventId { get; }
    public string FullValuesCiphertext { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed record EntitySyncAuditPage
{
    public EntitySyncAuditPage(
        string tenantId,
        DateTimeOffset? continuationOccurredAt,
        Guid? continuationEventId,
        IEnumerable<EntitySyncAuditEvent> events)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        ContinuationOccurredAt = continuationOccurredAt;
        if (continuationEventId == Guid.Empty) throw new ArgumentException("Continuation event ID cannot be empty.", nameof(continuationEventId));
        ContinuationEventId = continuationEventId;
        if ((continuationOccurredAt is null) != (continuationEventId is null))
            throw new ArgumentException("Continuation time and event ID must be supplied together.", nameof(continuationEventId));
        Events = ControlModelGuard.ReadOnlyCopy(events, nameof(events));
        if (Events.Any(auditEvent => auditEvent.TenantId != TenantId))
            throw new ArgumentException("Every audit event must belong to the page tenant.", nameof(events));
    }

    public string TenantId { get; }
    public DateTimeOffset? ContinuationOccurredAt { get; }
    public Guid? ContinuationEventId { get; }
    public IReadOnlyList<EntitySyncAuditEvent> Events { get; }
}

using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record AuditEventResponse(
    Guid AuditEventId,
    DateTimeOffset OccurredAt,
    string EventType,
    string ActorId,
    Guid? OperationId,
    Guid? RunId,
    Guid? PlanId,
    Guid? ItemId,
    string CorrelationId,
    string RedactedValuesJson,
    string RedactedValuesSha256,
    bool FullValuesAvailable,
    DateTimeOffset? FullValuesExpiresAt)
{
    public static AuditEventResponse From(
        EntitySyncAuditEvent value,
        DateTimeOffset now) => new(
            value.AuditEventId,
            value.OccurredAt,
            value.EventType,
            value.Actor.ActorId,
            value.OperationId,
            value.RunId,
            value.PlanId,
            value.ItemId,
            value.CorrelationId,
            value.RedactedValues.Json,
            value.RedactedValuesSha256.Value,
            value.FullValuesSha256 is not null
                && value.FullValuesExpiresAt is { } expiresAt
                && expiresAt > now,
            value.FullValuesExpiresAt);
}

public sealed record AuditValuesResponse(
    Guid AuditEventId,
    string ValuesJson,
    DateTimeOffset ExpiresAt);

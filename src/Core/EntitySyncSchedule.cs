namespace LISSTech.EntitySync.Core;

public enum EntitySyncCanonicalChangeStatus
{
    Pending,
    Planned,
    Ignored,
    Failed
}

public sealed record EntitySyncSchedule
{
    public EntitySyncSchedule(
        string tenantId,
        Guid scheduleId,
        int version,
        string name,
        Guid policyId,
        int policyVersion,
        string cronExpression,
        string timeZone,
        bool enabled,
        DateTimeOffset? nextRunAt,
        DateTimeOffset? lastRunAt,
        DateTimeOffset createdAt,
        EntitySyncActor createdBy)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        ScheduleId = ControlModelGuard.NonEmpty(scheduleId, nameof(scheduleId));
        Version = ControlModelGuard.Positive(version, nameof(version));
        Name = ControlModelGuard.Required(name, nameof(name));
        PolicyId = ControlModelGuard.NonEmpty(policyId, nameof(policyId));
        PolicyVersion = ControlModelGuard.Positive(policyVersion, nameof(policyVersion));
        CronExpression = ControlModelGuard.Required(cronExpression, nameof(cronExpression));
        TimeZone = ControlModelGuard.Required(timeZone, nameof(timeZone));
        Enabled = enabled;
        NextRunAt = nextRunAt;
        LastRunAt = lastRunAt;
        CreatedAt = createdAt;
        CreatedBy = createdBy ?? throw new ArgumentNullException(nameof(createdBy));
    }

    public string TenantId { get; }
    public Guid ScheduleId { get; }
    public int Version { get; }
    public string Name { get; }
    public Guid PolicyId { get; }
    public int PolicyVersion { get; }
    public string CronExpression { get; }
    public string TimeZone { get; }
    public bool Enabled { get; }
    public DateTimeOffset? NextRunAt { get; }
    public DateTimeOffset? LastRunAt { get; }
    public DateTimeOffset CreatedAt { get; }
    public EntitySyncActor CreatedBy { get; }

    public EntitySyncSchedule NextVersion(
        string cronExpression,
        string timeZone,
        bool enabled,
        DateTimeOffset? nextRunAt,
        EntitySyncActor actor,
        DateTimeOffset now,
        Guid? policyId = null,
        int? policyVersion = null) =>
        new(
            TenantId,
            ScheduleId,
            checked(Version + 1),
            Name,
            policyId ?? PolicyId,
            policyVersion ?? PolicyVersion,
            cronExpression,
            timeZone,
            enabled,
            nextRunAt,
            LastRunAt,
            now,
            actor);
}

public sealed record EntitySyncCanonicalChangeEvent
{
    public EntitySyncCanonicalChangeEvent(
        string tenantId,
        Guid eventId,
        string canonicalEntityType,
        string canonicalEntityId,
        long canonicalVersion,
        EntitySyncJsonValue changedFields,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        EntitySyncCanonicalChangeStatus status)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        EventId = ControlModelGuard.NonEmpty(eventId, nameof(eventId));
        CanonicalEntityType = ControlModelGuard.Required(canonicalEntityType, nameof(canonicalEntityType));
        CanonicalEntityId = ControlModelGuard.Required(canonicalEntityId, nameof(canonicalEntityId));
        CanonicalVersion = ControlModelGuard.Positive(canonicalVersion, nameof(canonicalVersion));
        ChangedFields = changedFields ?? throw new ArgumentNullException(nameof(changedFields));
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Status = ControlModelGuard.Defined(status, nameof(status));
    }

    public string TenantId { get; }
    public Guid EventId { get; }
    public string CanonicalEntityType { get; }
    public string CanonicalEntityId { get; }
    public long CanonicalVersion { get; }
    public EntitySyncJsonValue ChangedFields { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset ReceivedAt { get; }
    public EntitySyncCanonicalChangeStatus Status { get; }
}

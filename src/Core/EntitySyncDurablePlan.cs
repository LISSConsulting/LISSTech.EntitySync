namespace LISSTech.EntitySync.Core;

public enum EntitySyncDurablePlanStatus
{
    Draft,
    Approved,
    Consumed,
    Expired
}

public enum EntitySyncInspectionStatus
{
    Open,
    Completed
}

public sealed record EntitySyncSelectionBounds
{
    public EntitySyncSelectionBounds(string? sourceSearch, int? sourceCount, string? sourceEntityId)
    {
        SourceSearch = ControlModelGuard.Optional(sourceSearch, nameof(sourceSearch));
        if (sourceCount is <= 0) throw new ArgumentOutOfRangeException(nameof(sourceCount), sourceCount, "Source count must be positive when supplied.");
        SourceCount = sourceCount;
        SourceEntityId = ControlModelGuard.Optional(sourceEntityId, nameof(sourceEntityId));
    }

    public string? SourceSearch { get; }
    public int? SourceCount { get; }
    public string? SourceEntityId { get; }
}

public sealed record EntitySyncMatchEvidence
{
    public EntitySyncMatchEvidence(int score, string matchType, IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(score), score, "Match score must be between 0 and 100.");
        Score = score;
        MatchType = ControlModelGuard.Required(matchType, nameof(matchType));
        Reasons = ControlModelGuard.ReadOnlyCopy(
            reasons.Select(reason => ControlModelGuard.Required(reason, nameof(reasons))),
            nameof(reasons));
    }

    public int Score { get; }
    public string MatchType { get; }
    public IReadOnlyList<string> Reasons { get; }
}

public sealed record EntitySyncFieldDiff
{
    public EntitySyncFieldDiff(string fieldName, EntitySyncJsonValue before, EntitySyncJsonValue desired)
    {
        FieldName = ControlModelGuard.Required(fieldName, nameof(fieldName));
        Before = before ?? throw new ArgumentNullException(nameof(before));
        Desired = desired ?? throw new ArgumentNullException(nameof(desired));
    }

    public string FieldName { get; }
    public EntitySyncJsonValue Before { get; }
    public EntitySyncJsonValue Desired { get; }
}

public sealed record EntitySyncDurablePlanItem
{
    public EntitySyncDurablePlanItem(
        string tenantId,
        Guid planId,
        Guid itemId,
        int itemOrdinal,
        string sourceVendor,
        string sourceConnectionId,
        string sourceEntityType,
        string sourceEntityKey,
        string sourceEntityId,
        string targetVendor,
        string targetConnectionId,
        string targetEntityType,
        string? targetEntityId,
        string action,
        EntitySyncMatchEvidence matchEvidence,
        EntitySyncJsonValue redactedBefore,
        EntitySyncJsonValue redactedDesired,
        EntitySyncSha256? beforePayloadSha256,
        EntitySyncSha256 desiredPayloadSha256,
        IEnumerable<EntitySyncFieldDiff> fieldDiffs)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        ItemId = ControlModelGuard.NonEmpty(itemId, nameof(itemId));
        ItemOrdinal = ControlModelGuard.NonNegative(itemOrdinal, nameof(itemOrdinal));
        ArgumentNullException.ThrowIfNull(fieldDiffs);
        SourceVendor = EntitySyncVendors.Normalize(ControlModelGuard.Required(sourceVendor, nameof(sourceVendor)));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceEntityType = ControlModelGuard.Required(sourceEntityType, nameof(sourceEntityType));
        SourceEntityKey = ControlModelGuard.Required(sourceEntityKey, nameof(sourceEntityKey));
        SourceEntityId = ControlModelGuard.Required(sourceEntityId, nameof(sourceEntityId));
        TargetVendor = EntitySyncVendors.Normalize(ControlModelGuard.Required(targetVendor, nameof(targetVendor)));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetEntityType = ControlModelGuard.Required(targetEntityType, nameof(targetEntityType));
        TargetEntityId = ControlModelGuard.Optional(targetEntityId, nameof(targetEntityId));
        Action = ControlModelGuard.Required(action, nameof(action));
        MatchEvidence = matchEvidence ?? throw new ArgumentNullException(nameof(matchEvidence));
        RedactedBefore = redactedBefore ?? throw new ArgumentNullException(nameof(redactedBefore));
        RedactedDesired = redactedDesired ?? throw new ArgumentNullException(nameof(redactedDesired));
        BeforePayloadSha256 = beforePayloadSha256;
        DesiredPayloadSha256 = desiredPayloadSha256 ?? throw new ArgumentNullException(nameof(desiredPayloadSha256));
        FieldDiffs = ControlModelGuard.ReadOnlyCopy(fieldDiffs, nameof(fieldDiffs));
        if (FieldDiffs.Any(diff => diff is null)) throw new ArgumentException("Field diffs cannot contain null entries.", nameof(fieldDiffs));
        if (FieldDiffs.Select(diff => diff.FieldName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != FieldDiffs.Count)
            throw new ArgumentException("Field diffs must have unique field names.", nameof(fieldDiffs));
    }

    public string TenantId { get; }
    public Guid PlanId { get; }
    public Guid ItemId { get; }
    public int ItemOrdinal { get; }
    public string SourceVendor { get; }
    public string SourceConnectionId { get; }
    public string SourceEntityType { get; }
    public string SourceEntityKey { get; }
    public string SourceEntityId { get; }
    public string TargetVendor { get; }
    public string TargetConnectionId { get; }
    public string TargetEntityType { get; }
    public string? TargetEntityId { get; }
    public string Action { get; }
    public EntitySyncMatchEvidence MatchEvidence { get; }
    public EntitySyncJsonValue RedactedBefore { get; }
    public EntitySyncJsonValue RedactedDesired { get; }
    public EntitySyncSha256? BeforePayloadSha256 { get; }
    public EntitySyncSha256 DesiredPayloadSha256 { get; }
    public IReadOnlyList<EntitySyncFieldDiff> FieldDiffs { get; }
}

public sealed record EntitySyncDurablePlan
{
    public EntitySyncDurablePlan(
        string tenantId,
        Guid planId,
        Guid policyId,
        int policyVersion,
        EntitySyncSha256 policyDefinitionSha256,
        string routeScope,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncSha256 planDigestSha256,
        EntitySyncDurablePlanStatus status,
        EntitySyncSelectionBounds selectionBounds,
        int itemCount,
        DateTimeOffset createdAt,
        EntitySyncActor createdBy,
        DateTimeOffset expiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        PolicyId = ControlModelGuard.NonEmpty(policyId, nameof(policyId));
        PolicyVersion = ControlModelGuard.Positive(policyVersion, nameof(policyVersion));
        PolicyDefinitionSha256 = policyDefinitionSha256 ?? throw new ArgumentNullException(nameof(policyDefinitionSha256));
        RouteScope = ControlModelGuard.Required(routeScope, nameof(routeScope));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceConnectionGeneration = ControlModelGuard.Positive(sourceConnectionGeneration, nameof(sourceConnectionGeneration));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetConnectionGeneration = ControlModelGuard.Positive(targetConnectionGeneration, nameof(targetConnectionGeneration));
        PlanDigestSha256 = planDigestSha256 ?? throw new ArgumentNullException(nameof(planDigestSha256));
        Status = ControlModelGuard.Defined(status, nameof(status));
        SelectionBounds = selectionBounds ?? throw new ArgumentNullException(nameof(selectionBounds));
        ItemCount = ControlModelGuard.NonNegative(itemCount, nameof(itemCount));
        CreatedAt = createdAt;
        CreatedBy = createdBy ?? throw new ArgumentNullException(nameof(createdBy));
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Plan expiry must follow creation.");
        ExpiresAt = expiresAt;
    }

    public string TenantId { get; }
    public Guid PlanId { get; }
    public Guid PolicyId { get; }
    public int PolicyVersion { get; }
    public EntitySyncSha256 PolicyDefinitionSha256 { get; }
    public string RouteScope { get; }
    public string SourceConnectionId { get; }
    public long SourceConnectionGeneration { get; }
    public string TargetConnectionId { get; }
    public long TargetConnectionGeneration { get; }
    public EntitySyncSha256 PlanDigestSha256 { get; }
    public EntitySyncDurablePlanStatus Status { get; }
    public EntitySyncSelectionBounds SelectionBounds { get; }
    public int ItemCount { get; }
    public DateTimeOffset CreatedAt { get; }
    public EntitySyncActor CreatedBy { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal EntitySyncDurablePlan BindManifest(EntitySyncSha256 digest, int itemCount) =>
        new(
            TenantId, PlanId, PolicyId, PolicyVersion, PolicyDefinitionSha256, RouteScope,
            SourceConnectionId, SourceConnectionGeneration, TargetConnectionId, TargetConnectionGeneration,
            digest, Status, SelectionBounds, itemCount, CreatedAt, CreatedBy, ExpiresAt);

    public EntitySyncDurablePlan TransitionTo(EntitySyncDurablePlanStatus status)
    {
        ControlModelGuard.Defined(status, nameof(status));
        var legal = (Status, status) switch
        {
            (EntitySyncDurablePlanStatus.Draft, EntitySyncDurablePlanStatus.Approved) => true,
            (EntitySyncDurablePlanStatus.Draft, EntitySyncDurablePlanStatus.Expired) => true,
            (EntitySyncDurablePlanStatus.Approved, EntitySyncDurablePlanStatus.Consumed) => true,
            (EntitySyncDurablePlanStatus.Approved, EntitySyncDurablePlanStatus.Expired) => true,
            _ => false
        };
        if (!legal) throw new InvalidOperationException($"A plan cannot transition from {Status} to {status}.");
        return new EntitySyncDurablePlan(
            TenantId, PlanId, PolicyId, PolicyVersion, PolicyDefinitionSha256, RouteScope,
            SourceConnectionId, SourceConnectionGeneration, TargetConnectionId, TargetConnectionGeneration,
            PlanDigestSha256, status, SelectionBounds, ItemCount, CreatedAt, CreatedBy, ExpiresAt);
    }
}

public sealed record EntitySyncDurablePlanManifest
{
    private EntitySyncDurablePlanManifest(
        EntitySyncDurablePlan plan,
        IReadOnlyList<EntitySyncDurablePlanItem> items)
    {
        Plan = plan;
        Items = items;
    }

    public EntitySyncDurablePlan Plan { get; }
    public IReadOnlyList<EntitySyncDurablePlanItem> Items { get; }

    public static EntitySyncDurablePlanManifest Create(
        EntitySyncDurablePlan plan,
        IEnumerable<EntitySyncDurablePlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != EntitySyncDurablePlanStatus.Draft)
            throw new ArgumentException("A new durable manifest must be in Draft status.", nameof(plan));
        var copiedItems = ControlModelGuard.ReadOnlyCopy(items, nameof(items));
        var itemIds = new HashSet<Guid>(copiedItems.Count);
        for (var ordinal = 0; ordinal < copiedItems.Count; ordinal++)
        {
            var item = copiedItems[ordinal];
            if (item is null)
                throw new ArgumentException("A durable manifest cannot contain null items.", nameof(items));
            if (item.TenantId != plan.TenantId || item.PlanId != plan.PlanId)
                throw new ArgumentException("Every manifest item must belong to the plan tenant and ID.", nameof(items));
            if (item.ItemOrdinal != ordinal)
                throw new ArgumentException("Manifest item ordinals must be contiguous from zero.", nameof(items));
            if (!itemIds.Add(item.ItemId))
                throw new ArgumentException("Manifest item IDs must be unique.", nameof(items));
            if (item.SourceConnectionId != plan.SourceConnectionId
                || item.TargetConnectionId != plan.TargetConnectionId)
                throw new ArgumentException("Every manifest item must use the plan connection IDs.", nameof(items));
        }

        var digest = ComputeDigest(plan, copiedItems);
        return new EntitySyncDurablePlanManifest(
            plan.BindManifest(digest, copiedItems.Count),
            copiedItems);
    }

    private static EntitySyncSha256 ComputeDigest(
        EntitySyncDurablePlan plan,
        IReadOnlyList<EntitySyncDurablePlanItem> items)
    {
        var canonical = new
        {
            plan.TenantId,
            plan.PlanId,
            plan.PolicyId,
            plan.PolicyVersion,
            PolicyDefinitionSha256 = plan.PolicyDefinitionSha256.Value,
            plan.RouteScope,
            plan.SourceConnectionId,
            plan.SourceConnectionGeneration,
            plan.TargetConnectionId,
            plan.TargetConnectionGeneration,
            SelectionBounds = new
            {
                plan.SelectionBounds.SourceSearch,
                plan.SelectionBounds.SourceCount,
                plan.SelectionBounds.SourceEntityId
            },
            ItemCount = items.Count,
            plan.CreatedAt,
            CreatedBy = plan.CreatedBy.ActorId,
            plan.ExpiresAt,
            Items = items.Select(item => new
            {
                item.TenantId,
                item.PlanId,
                item.ItemId,
                item.ItemOrdinal,
                item.SourceVendor,
                item.SourceConnectionId,
                item.SourceEntityType,
                item.SourceEntityKey,
                item.SourceEntityId,
                item.TargetVendor,
                item.TargetConnectionId,
                item.TargetEntityType,
                item.TargetEntityId,
                item.Action,
                MatchEvidence = new
                {
                    item.MatchEvidence.Score,
                    item.MatchEvidence.MatchType,
                    Reasons = item.MatchEvidence.Reasons.ToArray()
                },
                RedactedBefore = item.RedactedBefore.Json,
                RedactedDesired = item.RedactedDesired.Json,
                BeforePayloadSha256 = item.BeforePayloadSha256?.Value,
                DesiredPayloadSha256 = item.DesiredPayloadSha256.Value,
                FieldDiffs = item.FieldDiffs.Select(diff => new
                {
                    diff.FieldName,
                    Before = diff.Before.Json,
                    Desired = diff.Desired.Json
                }).ToArray()
            }).ToArray()
        };
        return EntitySyncCanonicalDigest.Compute(canonical);
    }
}

public sealed record EntitySyncDurablePlanPage
{
    public EntitySyncDurablePlanPage(
        string tenantId,
        Guid planId,
        int page,
        int pageSize,
        int totalItems,
        IEnumerable<EntitySyncDurablePlanItem> items)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        Page = ControlModelGuard.Positive(page, nameof(page));
        PageSize = ControlModelGuard.Positive(pageSize, nameof(pageSize));
        TotalItems = ControlModelGuard.NonNegative(totalItems, nameof(totalItems));
        Items = ControlModelGuard.ReadOnlyCopy(items, nameof(items));
        if (Items.Count > PageSize) throw new ArgumentException("A page cannot contain more than page size items.", nameof(items));
        var expectedOrdinal = checked((Page - 1) * PageSize);
        foreach (var item in Items)
        {
            if (item.TenantId != TenantId || item.PlanId != PlanId)
                throw new ArgumentException("Every page item must belong to the page tenant and plan.", nameof(items));
            if (item.ItemOrdinal != expectedOrdinal++)
                throw new ArgumentException("Page items must be ordered and contiguous by item ordinal.", nameof(items));
        }
    }

    public string TenantId { get; }
    public Guid PlanId { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalItems { get; }
    public IReadOnlyList<EntitySyncDurablePlanItem> Items { get; }
}

public sealed record EntitySyncInspectionSession
{
    public EntitySyncInspectionSession(
        string tenantId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncInspectionStatus status,
        DateTimeOffset inspectedAt,
        EntitySyncActor inspectedBy,
        DateTimeOffset? completedAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        InspectionId = ControlModelGuard.NonEmpty(inspectionId, nameof(inspectionId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        PlanDigestSha256 = planDigestSha256 ?? throw new ArgumentNullException(nameof(planDigestSha256));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceConnectionGeneration = ControlModelGuard.Positive(sourceConnectionGeneration, nameof(sourceConnectionGeneration));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetConnectionGeneration = ControlModelGuard.Positive(targetConnectionGeneration, nameof(targetConnectionGeneration));
        Status = ControlModelGuard.Defined(status, nameof(status));
        InspectedAt = inspectedAt;
        InspectedBy = inspectedBy ?? throw new ArgumentNullException(nameof(inspectedBy));
        CompletedAt = completedAt;
        if ((status == EntitySyncInspectionStatus.Open) != (completedAt is null))
            throw new ArgumentException("Open inspections cannot be completed and completed inspections require a completion time.", nameof(completedAt));
        if (completedAt < inspectedAt) throw new ArgumentOutOfRangeException(nameof(completedAt), completedAt, "Completion cannot precede inspection.");
    }

    public string TenantId { get; }
    public Guid InspectionId { get; }
    public Guid PlanId { get; }
    public EntitySyncSha256 PlanDigestSha256 { get; }
    public string SourceConnectionId { get; }
    public long SourceConnectionGeneration { get; }
    public string TargetConnectionId { get; }
    public long TargetConnectionGeneration { get; }
    public EntitySyncInspectionStatus Status { get; }
    public DateTimeOffset InspectedAt { get; }
    public EntitySyncActor InspectedBy { get; }
    public DateTimeOffset? CompletedAt { get; }

    public EntitySyncInspectionSession Complete(DateTimeOffset now)
    {
        if (Status != EntitySyncInspectionStatus.Open) throw new InvalidOperationException("Only an open inspection can be completed.");
        return new EntitySyncInspectionSession(
            TenantId, InspectionId, PlanId, PlanDigestSha256, SourceConnectionId,
            SourceConnectionGeneration, TargetConnectionId, TargetConnectionGeneration,
            EntitySyncInspectionStatus.Completed, InspectedAt, InspectedBy, now);
    }
}

public sealed record EntitySyncInspectionRange
{
    public EntitySyncInspectionRange(
        string tenantId,
        Guid inspectionId,
        Guid rangeId,
        int rangeStart,
        int rangeEnd,
        DateTimeOffset inspectedAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        InspectionId = ControlModelGuard.NonEmpty(inspectionId, nameof(inspectionId));
        RangeId = ControlModelGuard.NonEmpty(rangeId, nameof(rangeId));
        RangeStart = ControlModelGuard.NonNegative(rangeStart, nameof(rangeStart));
        if (rangeEnd < rangeStart)
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), rangeEnd, "Range end cannot precede range start.");
        RangeEnd = rangeEnd;
        InspectedAt = inspectedAt;
    }

    public string TenantId { get; }
    public Guid InspectionId { get; }
    public Guid RangeId { get; }
    public int RangeStart { get; }
    public int RangeEnd { get; }
    public DateTimeOffset InspectedAt { get; }
}

public sealed record EntitySyncApproval
{
    public EntitySyncApproval(
        string tenantId,
        Guid approvalId,
        Guid inspectionId,
        Guid planId,
        EntitySyncSha256 planDigestSha256,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        DateTimeOffset approvedAt,
        EntitySyncActor approvedBy,
        DateTimeOffset? expiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        ApprovalId = ControlModelGuard.NonEmpty(approvalId, nameof(approvalId));
        InspectionId = ControlModelGuard.NonEmpty(inspectionId, nameof(inspectionId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        PlanDigestSha256 = planDigestSha256 ?? throw new ArgumentNullException(nameof(planDigestSha256));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceConnectionGeneration = ControlModelGuard.Positive(sourceConnectionGeneration, nameof(sourceConnectionGeneration));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetConnectionGeneration = ControlModelGuard.Positive(targetConnectionGeneration, nameof(targetConnectionGeneration));
        ApprovedAt = approvedAt;
        ApprovedBy = approvedBy ?? throw new ArgumentNullException(nameof(approvedBy));
        if (expiresAt <= approvedAt) throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Approval expiry must follow approval.");
        ExpiresAt = expiresAt;
    }

    public string TenantId { get; }
    public Guid ApprovalId { get; }
    public Guid InspectionId { get; }
    public Guid PlanId { get; }
    public EntitySyncSha256 PlanDigestSha256 { get; }
    public string SourceConnectionId { get; }
    public long SourceConnectionGeneration { get; }
    public string TargetConnectionId { get; }
    public long TargetConnectionGeneration { get; }
    public DateTimeOffset ApprovedAt { get; }
    public EntitySyncActor ApprovedBy { get; }
    public DateTimeOffset? ExpiresAt { get; }
}

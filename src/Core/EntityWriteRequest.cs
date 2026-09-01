namespace LISSTech.EntitySync.Core;

public sealed class EntityWriteRequest
{
    public string Vendor { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public string? ParentEntityType { get; set; }
    public string? ParentClientId { get; set; }
    public long? ExpectedVersion { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PrimarySiteId { get; set; }
    public string? VendorRequestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public EntityAddress? Address { get; set; }
    public EntityWriteParent? ResolvedParent { get; set; }
    public EntityWriteCorrelation? Correlation { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EntityWriteCorrelation
{
    public EntityWriteCorrelation(
        Guid operationId,
        Guid planId,
        Guid runId,
        int itemIndex,
        Guid correlationId)
    {
        OperationId = ControlModelGuard.NonEmpty(operationId, nameof(operationId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        RunId = ControlModelGuard.NonEmpty(runId, nameof(runId));
        ItemIndex = ControlModelGuard.NonNegative(itemIndex, nameof(itemIndex));
        CorrelationId = ControlModelGuard.NonEmpty(correlationId, nameof(correlationId));
        if (new[] { OperationId, PlanId, RunId, CorrelationId }.Distinct().Count() != 4)
            throw new ArgumentException(
                "Operation, plan, run, and correlation IDs must be distinct.");
    }

    public Guid OperationId { get; }
    public Guid PlanId { get; }
    public Guid RunId { get; }
    public int ItemIndex { get; }
    public Guid CorrelationId { get; }
}

public sealed record EntityWriteParent(
    Guid ClientId,
    Guid? SiteId,
    string ParentEntityType,
    string SourcePlatformInstanceId = "",
    string MatchedLinkExternalId = "",
    string MatchedLinkStatus = "",
    string MatchedLinkToken = "",
    long ObservedOwnerVersion = 0);

namespace LISSTech.EntitySync.Core;

public enum EntitySyncOperationMode
{
    DryRun,
    Apply
}

public enum EntitySyncOperationStatus
{
    Queued,
    Leased,
    Running,
    Succeeded,
    Partial,
    Failed,
    Cancelled
}

public enum EntitySyncItemOutcome
{
    Pending,
    Succeeded,
    Failed,
    Skipped,
    Unknown
}

public sealed record EntitySyncOperation
{
    public EntitySyncOperation(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid? approvalId,
        string routeScope,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncOperationMode mode,
        EntitySyncOperationStatus status,
        string idempotencyKey,
        string? leaseOwner,
        DateTimeOffset? leaseExpiresAt,
        int attempt,
        DateTimeOffset createdAt,
        DateTimeOffset queuedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        OperationId = ControlModelGuard.NonEmpty(operationId, nameof(operationId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        if (approvalId == Guid.Empty) throw new ArgumentException("Approval ID cannot be empty.", nameof(approvalId));
        ApprovalId = approvalId;
        RouteScope = ControlModelGuard.Required(routeScope, nameof(routeScope));
        SourceConnectionId = ControlModelGuard.Required(sourceConnectionId, nameof(sourceConnectionId));
        SourceConnectionGeneration = ControlModelGuard.Positive(sourceConnectionGeneration, nameof(sourceConnectionGeneration));
        TargetConnectionId = ControlModelGuard.Required(targetConnectionId, nameof(targetConnectionId));
        TargetConnectionGeneration = ControlModelGuard.Positive(targetConnectionGeneration, nameof(targetConnectionGeneration));
        Mode = ControlModelGuard.Defined(mode, nameof(mode));
        Status = ControlModelGuard.Defined(status, nameof(status));
        if (mode == EntitySyncOperationMode.Apply && approvalId is null)
            throw new ArgumentException("Apply operations require an approval.", nameof(approvalId));
        if (mode == EntitySyncOperationMode.DryRun && approvalId is not null)
            throw new ArgumentException("Dry-run operations cannot consume an approval.", nameof(approvalId));
        IdempotencyKey = ControlModelGuard.Required(idempotencyKey, nameof(idempotencyKey));
        LeaseOwner = ControlModelGuard.Optional(leaseOwner, nameof(leaseOwner));
        LeaseExpiresAt = leaseExpiresAt;
        if ((LeaseOwner is null) != (leaseExpiresAt is null))
            throw new ArgumentException("Lease owner and expiry must be supplied together.", nameof(leaseExpiresAt));
        if (status is EntitySyncOperationStatus.Leased or EntitySyncOperationStatus.Running && LeaseOwner is null)
            throw new ArgumentException("Leased and running operations require lease information.", nameof(leaseOwner));
        if (IsTerminal(status) && LeaseOwner is not null)
            throw new ArgumentException("Terminal operations cannot retain a lease.", nameof(leaseOwner));
        Attempt = ControlModelGuard.NonNegative(attempt, nameof(attempt));
        CreatedAt = createdAt;
        if (queuedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(queuedAt), queuedAt, "Queue time cannot precede creation.");
        QueuedAt = queuedAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        if (status == EntitySyncOperationStatus.Running && startedAt is null)
            throw new ArgumentException("Running operations require a start time.", nameof(startedAt));
        if (startedAt < queuedAt) throw new ArgumentOutOfRangeException(nameof(startedAt), startedAt, "Start time cannot precede queue time.");
        if (IsTerminal(status) != (completedAt is not null))
            throw new ArgumentException("Only terminal operations have a completion time.", nameof(completedAt));
        if (completedAt < startedAt || completedAt < queuedAt)
            throw new ArgumentOutOfRangeException(nameof(completedAt), completedAt, "Completion cannot precede operation work.");
    }

    public string TenantId { get; }
    public Guid OperationId { get; }
    public Guid PlanId { get; }
    public Guid? ApprovalId { get; }
    public string RouteScope { get; }
    public string SourceConnectionId { get; }
    public long SourceConnectionGeneration { get; }
    public string TargetConnectionId { get; }
    public long TargetConnectionGeneration { get; }
    public EntitySyncOperationMode Mode { get; }
    public EntitySyncOperationStatus Status { get; }
    public string IdempotencyKey { get; }
    public string? LeaseOwner { get; }
    public DateTimeOffset? LeaseExpiresAt { get; }
    public int Attempt { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset QueuedAt { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }

    public static EntitySyncOperation QueueDryRun(
        string tenantId,
        Guid operationId,
        Guid planId,
        string idempotencyKey,
        string routeScope,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        DateTimeOffset now) =>
        Queue(
            tenantId, operationId, planId, null, idempotencyKey, routeScope,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
            targetConnectionGeneration, EntitySyncOperationMode.DryRun, now);

    public static EntitySyncOperation QueueApply(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid? approvalId,
        string idempotencyKey,
        string routeScope,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        DateTimeOffset now) =>
        Queue(
            tenantId, operationId, planId, approvalId, idempotencyKey, routeScope,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
            targetConnectionGeneration, EntitySyncOperationMode.Apply, now);

    public EntitySyncOperation Lease(string leaseOwner, DateTimeOffset leaseExpiresAt)
    {
        if (Status != EntitySyncOperationStatus.Queued) throw new InvalidOperationException("Only queued operations can be leased.");
        if (leaseExpiresAt <= QueuedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt), leaseExpiresAt, "Lease expiry must follow queue time.");
        return Copy(
            EntitySyncOperationStatus.Leased,
            ControlModelGuard.Required(leaseOwner, nameof(leaseOwner)),
            leaseExpiresAt,
            checked(Attempt + 1),
            StartedAt,
            null);
    }

    public EntitySyncOperation Start(DateTimeOffset now)
    {
        if (Status != EntitySyncOperationStatus.Leased) throw new InvalidOperationException("Only leased operations can start.");
        return Copy(EntitySyncOperationStatus.Running, LeaseOwner, LeaseExpiresAt, Attempt, now, null);
    }

    public EntitySyncOperation Complete(EntitySyncOperationStatus status, DateTimeOffset now)
    {
        if (Status != EntitySyncOperationStatus.Running) throw new InvalidOperationException("Only running operations can complete.");
        if (status is not (EntitySyncOperationStatus.Succeeded or EntitySyncOperationStatus.Partial or EntitySyncOperationStatus.Failed))
            throw new ArgumentException("Completion status must be Succeeded, Partial, or Failed.", nameof(status));
        return Copy(status, null, null, Attempt, StartedAt, now);
    }

    public EntitySyncOperation Cancel(DateTimeOffset now)
    {
        if (IsTerminal(Status)) throw new InvalidOperationException("A terminal operation cannot be cancelled.");
        return Copy(EntitySyncOperationStatus.Cancelled, null, null, Attempt, StartedAt, now);
    }

    private static EntitySyncOperation Queue(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid? approvalId,
        string idempotencyKey,
        string routeScope,
        string sourceConnectionId,
        long sourceConnectionGeneration,
        string targetConnectionId,
        long targetConnectionGeneration,
        EntitySyncOperationMode mode,
        DateTimeOffset now) =>
        new(
            tenantId, operationId, planId, approvalId, routeScope,
            sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
            targetConnectionGeneration, mode, EntitySyncOperationStatus.Queued,
            idempotencyKey, null, null, 0, now, now, null, null);

    private EntitySyncOperation Copy(
        EntitySyncOperationStatus status,
        string? leaseOwner,
        DateTimeOffset? leaseExpiresAt,
        int attempt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt) =>
        new(
            TenantId, OperationId, PlanId, ApprovalId, RouteScope,
            SourceConnectionId, SourceConnectionGeneration, TargetConnectionId,
            TargetConnectionGeneration, Mode, status, IdempotencyKey,
            leaseOwner, leaseExpiresAt, attempt, CreatedAt, QueuedAt, startedAt, completedAt);

    private static bool IsTerminal(EntitySyncOperationStatus status) =>
        status is EntitySyncOperationStatus.Succeeded
            or EntitySyncOperationStatus.Partial
            or EntitySyncOperationStatus.Failed
            or EntitySyncOperationStatus.Cancelled;
}

public sealed record EntitySyncOperationItem
{
    public EntitySyncOperationItem(
        string tenantId,
        Guid operationId,
        Guid planId,
        Guid itemId,
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
        EntitySyncJsonValue redactedBefore,
        EntitySyncJsonValue redactedDesired,
        EntitySyncSha256? beforePayloadSha256,
        EntitySyncSha256 desiredPayloadSha256,
        EntitySyncSha256? afterPayloadSha256,
        DateTimeOffset snapshotsExpireAt,
        string? vendorRequestId,
        EntitySyncItemOutcome outcome,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        OperationId = ControlModelGuard.NonEmpty(operationId, nameof(operationId));
        PlanId = ControlModelGuard.NonEmpty(planId, nameof(planId));
        ItemId = ControlModelGuard.NonEmpty(itemId, nameof(itemId));
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
        RedactedBefore = redactedBefore ?? throw new ArgumentNullException(nameof(redactedBefore));
        RedactedDesired = redactedDesired ?? throw new ArgumentNullException(nameof(redactedDesired));
        BeforePayloadSha256 = beforePayloadSha256;
        DesiredPayloadSha256 = desiredPayloadSha256 ?? throw new ArgumentNullException(nameof(desiredPayloadSha256));
        AfterPayloadSha256 = afterPayloadSha256;
        SnapshotsExpireAt = snapshotsExpireAt;
        VendorRequestId = ControlModelGuard.Optional(vendorRequestId, nameof(vendorRequestId));
        Outcome = ControlModelGuard.Defined(outcome, nameof(outcome));
        ErrorCode = ControlModelGuard.Optional(errorCode, nameof(errorCode));
        ErrorMessage = ControlModelGuard.Optional(errorMessage, nameof(errorMessage));
        StartedAt = startedAt;
        CompletedAt = completedAt;
        if (outcome == EntitySyncItemOutcome.Pending && (completedAt is not null || ErrorCode is not null || ErrorMessage is not null))
            throw new ArgumentException("Pending items cannot contain completion or error details.", nameof(outcome));
        if (outcome == EntitySyncItemOutcome.Failed && (ErrorCode is null || ErrorMessage is null))
            throw new ArgumentException("Failed items require an error code and message.", nameof(outcome));
        if (outcome != EntitySyncItemOutcome.Failed && (ErrorCode is not null || ErrorMessage is not null))
            throw new ArgumentException("Only failed items can contain error details.", nameof(outcome));
        if (outcome != EntitySyncItemOutcome.Pending && completedAt is null)
            throw new ArgumentException("Completed item outcomes require a completion time.", nameof(completedAt));
        if (completedAt < startedAt) throw new ArgumentOutOfRangeException(nameof(completedAt), completedAt, "Completion cannot precede item start.");
    }

    public string TenantId { get; }
    public Guid OperationId { get; }
    public Guid PlanId { get; }
    public Guid ItemId { get; }
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
    public EntitySyncJsonValue RedactedBefore { get; }
    public EntitySyncJsonValue RedactedDesired { get; }
    public EntitySyncSha256? BeforePayloadSha256 { get; }
    public EntitySyncSha256 DesiredPayloadSha256 { get; }
    public EntitySyncSha256? AfterPayloadSha256 { get; }
    public DateTimeOffset SnapshotsExpireAt { get; }
    public string? VendorRequestId { get; }
    public EntitySyncItemOutcome Outcome { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
}

public sealed record EntitySyncOperationItemSnapshot
{
    public EntitySyncOperationItemSnapshot(
        string tenantId,
        Guid operationId,
        Guid itemId,
        string? encryptedBeforeCiphertext,
        string? encryptedAfterCiphertext,
        DateTimeOffset expiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        OperationId = ControlModelGuard.NonEmpty(operationId, nameof(operationId));
        ItemId = ControlModelGuard.NonEmpty(itemId, nameof(itemId));
        EncryptedBeforeCiphertext = ControlModelGuard.Optional(encryptedBeforeCiphertext, nameof(encryptedBeforeCiphertext));
        EncryptedAfterCiphertext = ControlModelGuard.Optional(encryptedAfterCiphertext, nameof(encryptedAfterCiphertext));
        if (EncryptedBeforeCiphertext is null && EncryptedAfterCiphertext is null)
            throw new ArgumentException("At least one encrypted snapshot value is required.", nameof(encryptedBeforeCiphertext));
        ExpiresAt = expiresAt;
    }

    public string TenantId { get; }
    public Guid OperationId { get; }
    public Guid ItemId { get; }
    public string? EncryptedBeforeCiphertext { get; }
    public string? EncryptedAfterCiphertext { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed record EntitySyncIdempotencyReceipt
{
    public EntitySyncIdempotencyReceipt(
        string tenantId,
        string idempotencyKey,
        EntitySyncSha256 requestSha256,
        int? responseStatusCode,
        EntitySyncJsonValue? responseBody,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt,
        DateTimeOffset expiresAt)
    {
        TenantId = ControlModelGuard.Required(tenantId, nameof(tenantId));
        IdempotencyKey = ControlModelGuard.Required(idempotencyKey, nameof(idempotencyKey));
        RequestSha256 = requestSha256 ?? throw new ArgumentNullException(nameof(requestSha256));
        if ((responseStatusCode is null) != (responseBody is null))
            throw new ArgumentException("Response status and body must be supplied together.", nameof(responseBody));
        ResponseStatusCode = responseStatusCode;
        ResponseBody = responseBody;
        CreatedAt = createdAt;
        CompletedAt = completedAt;
        if (completedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(completedAt), completedAt, "Completion cannot precede creation.");
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Expiry must follow creation.");
        ExpiresAt = expiresAt;
    }

    public string TenantId { get; }
    public string IdempotencyKey { get; }
    public EntitySyncSha256 RequestSha256 { get; }
    public int? ResponseStatusCode { get; }
    public EntitySyncJsonValue? ResponseBody { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
}

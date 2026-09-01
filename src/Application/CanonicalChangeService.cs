using System.Collections.ObjectModel;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Application;

public sealed record CanonicalChangeRequest
{
    public CanonicalChangeRequest(
        string tenantId,
        string outboxEventId,
        string canonicalEntityType,
        Guid canonicalEntityId,
        long canonicalVersion,
        IEnumerable<string> changedFields,
        EntitySyncSha256 payloadSha256,
        DateTimeOffset occurredAt)
    {
        TenantId = Require(tenantId, nameof(tenantId));
        OutboxEventId = Require(outboxEventId, nameof(outboxEventId));
        CanonicalEntityType = Require(canonicalEntityType, nameof(canonicalEntityType));
        if (canonicalEntityId == Guid.Empty)
            throw new ArgumentException("Canonical entity ID is required.", nameof(canonicalEntityId));
        if (canonicalVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(canonicalVersion));
        ArgumentNullException.ThrowIfNull(changedFields);
        var fields = changedFields
            .Select(field => Require(field, nameof(changedFields)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (fields.Length == 0)
            throw new ArgumentException("At least one changed field is required.", nameof(changedFields));
        ChangedFields = new ReadOnlyCollection<string>(fields);
        PayloadSha256 = payloadSha256 ?? throw new ArgumentNullException(nameof(payloadSha256));
        CanonicalEntityId = canonicalEntityId;
        CanonicalVersion = canonicalVersion;
        OccurredAt = occurredAt;
    }

    public string TenantId { get; }
    public string OutboxEventId { get; }
    public string CanonicalEntityType { get; }
    public Guid CanonicalEntityId { get; }
    public long CanonicalVersion { get; }
    public IReadOnlyList<string> ChangedFields { get; }
    public EntitySyncSha256 PayloadSha256 { get; }
    public DateTimeOffset OccurredAt { get; }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}

public sealed record CanonicalChangeReceipt(
    Guid ReceiptId,
    string TenantId,
    string OutboxEventId,
    Guid CanonicalEntityId,
    long CanonicalVersion,
    EntitySyncSha256 PayloadSha256,
    IReadOnlyList<Guid> WorkIds,
    DateTimeOffset ReceivedAt);

public interface ICanonicalChangeRepository
{
    Task<CanonicalChangeReceipt> AcceptAsync(
        CanonicalChangeRequest request,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken);
}

public interface IEntitySyncWorkSignal
{
    Task NotifyAsync(CancellationToken cancellationToken);
    Task WaitAsync(CancellationToken cancellationToken);
}

public sealed class CanonicalChangeConflictException(string outboxEventId)
    : InvalidOperationException(
        $"Canonical outbox event '{outboxEventId}' is already bound to different content.")
{
    public int StatusCode => 409;
}

public sealed record CanonicalEntityVersion(
    Guid CanonicalEntityId,
    long CanonicalVersion,
    ExternalEntity Entity);

public interface ICanonicalEntityVersionAdapter
{
    Task<CanonicalEntityVersion?> ReadCanonicalAsync(
        string entityType,
        Guid canonicalEntityId,
        long assertedVersion,
        CancellationToken cancellationToken);
}

public enum CanonicalVersionReadStatus
{
    Exact,
    NotFound,
    IdentityMismatch,
    StaleVersion
}

public sealed record CanonicalVersionReadResult(
    CanonicalVersionReadStatus Status,
    ExternalEntity? Entity);

public sealed class CanonicalChangeService(
    ICanonicalChangeRepository changes,
    IEntitySyncWorkSignal signal,
    TimeProvider timeProvider)
{
    public async Task<CanonicalChangeReceipt> AcceptAsync(
        CanonicalChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await changes.AcceptAsync(
            request, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await signal.NotifyAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public static async Task<CanonicalVersionReadResult> ReadAssertedVersionAsync(
        ICanonicalEntityVersionAdapter adapter,
        CanonicalChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(request);
        var read = await adapter.ReadCanonicalAsync(
            request.CanonicalEntityType,
            request.CanonicalEntityId,
            request.CanonicalVersion,
            cancellationToken).ConfigureAwait(false);
        if (read is null)
            return new CanonicalVersionReadResult(CanonicalVersionReadStatus.NotFound, null);
        if (read.CanonicalEntityId != request.CanonicalEntityId)
            return new CanonicalVersionReadResult(
                CanonicalVersionReadStatus.IdentityMismatch, null);
        if (read.CanonicalVersion != request.CanonicalVersion)
            return new CanonicalVersionReadResult(
                CanonicalVersionReadStatus.StaleVersion, null);
        return new CanonicalVersionReadResult(CanonicalVersionReadStatus.Exact, read.Entity);
    }
}

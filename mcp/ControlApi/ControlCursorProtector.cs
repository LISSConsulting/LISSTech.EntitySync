using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed class InvalidControlCursorException : ArgumentException
{
    public InvalidControlCursorException() : base("The cursor is invalid for this resource and tenant.")
    {
    }
}

public sealed class ControlCursorProtector
{
    private const int Version = 1;
    private const int MaximumCursorLength = 2048;
    private const int MaximumPayloadLength = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly IDataProtector protector;

    public ControlCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("LISSTech.EntitySync.ControlApi.Cursor.v1");
    }

    public string ProtectOffset(string resource, string tenantId, int offset) =>
        Protect(new CursorPayload(Version, resource, tenantId, offset, null, null));

    public int UnprotectOffset(string cursor, string resource, string tenantId)
    {
        var payload = Unprotect(cursor, resource, tenantId);
        if (payload.Offset is null || payload.Offset < 0
            || payload.OccurredAt is not null || payload.EventId is not null)
            throw new InvalidControlCursorException();
        return payload.Offset.Value;
    }

    public string ProtectAudit(
        string resource,
        string tenantId,
        DateTimeOffset occurredAt,
        Guid eventId) =>
        Protect(new CursorPayload(
            Version, resource, tenantId, null, occurredAt, eventId));

    public (DateTimeOffset OccurredAt, Guid EventId) UnprotectAudit(
        string cursor,
        string resource,
        string tenantId)
    {
        var payload = Unprotect(cursor, resource, tenantId);
        if (payload.Offset is not null || payload.OccurredAt is null
            || payload.EventId is null || payload.EventId == Guid.Empty)
            throw new InvalidControlCursorException();
        return (payload.OccurredAt.Value, payload.EventId.Value);
    }

    public string ProtectRunStart(
        string resource,
        string tenantId,
        DateTimeOffset highWater,
        int pageSize)
    {
        if (highWater.Offset != TimeSpan.Zero)
            throw new ArgumentException("The run cursor high-water is invalid.");
        ValidateRunPageSize(pageSize);
        return Protect(new RunCursorPayload(
            Version,
            resource,
            tenantId,
            highWater.ToString("O", CultureInfo.InvariantCulture),
            null,
            null,
            pageSize));
    }

    public string ProtectRun(
        string resource,
        string tenantId,
        DateTimeOffset highWater,
        DateTimeOffset lastQueuedAt,
        Guid lastOperationId,
        int pageSize)
    {
        if (highWater.Offset != TimeSpan.Zero
            || lastQueuedAt.Offset != TimeSpan.Zero
            || lastQueuedAt > highWater
            || lastOperationId == Guid.Empty)
            throw new ArgumentException("The run cursor position is invalid.");
        ValidateRunPageSize(pageSize);
        return Protect(new RunCursorPayload(
            Version,
            resource,
            tenantId,
            highWater.ToString("O", CultureInfo.InvariantCulture),
            lastQueuedAt.ToString("O", CultureInfo.InvariantCulture),
            lastOperationId.ToString("D"),
            pageSize));
    }

    public (
        DateTimeOffset HighWater,
        DateTimeOffset? LastQueuedAt,
        Guid? LastOperationId,
        int PageSize) UnprotectRun(
            string cursor,
            string resource,
            string tenantId)
    {
        var payload = UnprotectRunPayload(cursor);
        if (payload.Version != Version
            || !string.Equals(payload.Resource, resource, StringComparison.Ordinal)
            || !string.Equals(payload.TenantId, tenantId, StringComparison.Ordinal)
            || (payload.LastQueuedAt is null) != (payload.LastOperationId is null)
            || payload.PageSize is <= 0 or > 100)
            throw new InvalidControlCursorException();
        var highWater = ParseCanonicalTime(payload.HighWater);
        if (payload.LastQueuedAt is null)
            return (highWater, null, null, payload.PageSize);
        var lastQueuedAt = ParseCanonicalTime(payload.LastQueuedAt);
        var lastOperationId = ParseCanonicalGuid(payload.LastOperationId);
        if (lastQueuedAt > highWater) throw new InvalidControlCursorException();
        return (highWater, lastQueuedAt, lastOperationId, payload.PageSize);
    }

    private RunCursorPayload UnprotectRunPayload(string cursor)
    {
        ValidateCursorLength(cursor);
        try
        {
            var json = protector.Unprotect(cursor);
            if (json.Length > MaximumPayloadLength)
                throw new InvalidControlCursorException();
            return JsonSerializer.Deserialize<RunCursorPayload>(json, JsonOptions)
                ?? throw new InvalidControlCursorException();
        }
        catch (InvalidControlCursorException)
        {
            throw;
        }
        catch
        {
            throw new InvalidControlCursorException();
        }
    }

    private static DateTimeOffset ParseCanonicalTime(string? value)
    {
        if (value is null
            || !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(
                value,
                parsed.ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw new InvalidControlCursorException();
        return parsed;
    }

    private static Guid ParseCanonicalGuid(string? value)
    {
        if (value is null
            || !Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
            throw new InvalidControlCursorException();
        return parsed;
    }

    private string Protect<T>(T payload) =>
        protector.Protect(JsonSerializer.Serialize(payload, JsonOptions));

    private CursorPayload Unprotect(string cursor, string resource, string tenantId)
    {
        ValidateCursorLength(cursor);
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                protector.Unprotect(cursor), JsonOptions)
                ?? throw new InvalidControlCursorException();
            if (payload.Version != Version
                || !payload.Resource.Equals(resource, StringComparison.Ordinal)
                || !payload.TenantId.Equals(tenantId, StringComparison.Ordinal))
                throw new InvalidControlCursorException();
            return payload;
        }
        catch (InvalidControlCursorException)
        {
            throw;
        }
        catch
        {
            throw new InvalidControlCursorException();
        }
    }

    private static void ValidateCursorLength(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorLength)
            throw new InvalidControlCursorException();
    }

    private static void ValidateRunPageSize(int pageSize)
    {
        if (pageSize is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    private sealed record CursorPayload(
        int Version,
        string Resource,
        string TenantId,
        int? Offset,
        DateTimeOffset? OccurredAt,
        Guid? EventId);

    private sealed record RunCursorPayload(
        int Version,
        string Resource,
        string TenantId,
        string HighWater,
        string? LastQueuedAt,
        string? LastOperationId,
        int PageSize);
}

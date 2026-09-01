using System.Text.Json;
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

    private string Protect(CursorPayload payload) =>
        protector.Protect(JsonSerializer.Serialize(payload));

    private CursorPayload Unprotect(string cursor, string resource, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidControlCursorException();
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                protector.Unprotect(cursor)) ?? throw new InvalidControlCursorException();
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

    private sealed record CursorPayload(
        int Version,
        string Resource,
        string TenantId,
        int? Offset,
        DateTimeOffset? OccurredAt,
        Guid? EventId);
}

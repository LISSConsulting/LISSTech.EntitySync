using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record CreateConnectionRequest(
    string Vendor,
    string? ConnectionId,
    string DisplayName,
    Guid? PlatformInstanceId = null);

public sealed record UpdateConnectionRequest(
    string DisplayName,
    long ExpectedGeneration,
    Guid? PlatformInstanceId = null);

public sealed record DeleteConnectionRequest(long ExpectedGeneration);

public sealed record TestConnectionRequest(long ExpectedGeneration);

public sealed record ConnectionResponse(
    string ConnectionId,
    string Vendor,
    string DisplayName,
    long Generation,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    Guid? PlatformInstanceId)
{
    public static ConnectionResponse From(EntitySyncConnectionDefinition value) =>
        new(
            value.ConnectionId,
            value.Vendor,
            value.DisplayName,
            value.Generation,
            value.Enabled,
            value.CreatedAt,
            value.CreatedBy.ActorId,
            value.UpdatedAt,
            value.UpdatedBy.ActorId,
            value.PlatformInstanceId);
}

public sealed record ConnectionTestResponse(
    string ConnectionId,
    long Generation,
    bool Connected,
    string CorrelationId);

public sealed record ConnectionDeleteResponse(
    string ConnectionId,
    string Outcome,
    long? Generation);

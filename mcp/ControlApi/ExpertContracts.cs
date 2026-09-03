using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record SuiteQlRequest(
    string ConnectionId,
    string Query,
    [property: Range(1, 1000), DefaultValue(100)] int MaximumRows = 100);

public sealed record SuiteQlResponse(
    string ConnectionId,
    int RowCount,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Truncated,
    string CorrelationId);

public sealed record CustomPropertyRequest(
    string ConnectionId,
    string EntityId,
    string Name,
    string Value);

public sealed record CustomPropertyResponse(
    string ConnectionId,
    string EntityId,
    string Name,
    bool Accepted,
    string? SafeCode,
    string CorrelationId);

public sealed record CapabilityEntityResponse(
    string EntityType,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Fields);

public sealed record CapabilityResponse(
    string ConnectionId,
    string Vendor,
    IReadOnlyList<CapabilityEntityResponse> Entities);

public sealed record EntityQueryResponse(
    string Vendor,
    string EntityType,
    string Id,
    string Name,
    string? Email,
    string? Phone,
    string? Website,
    bool? IsActive);

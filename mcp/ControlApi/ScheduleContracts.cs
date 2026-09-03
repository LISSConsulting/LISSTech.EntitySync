using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Mcp.ControlApi;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewScheduleRequest(
    [property: Required, JsonPropertyName("cron_expression")] string CronExpression,
    [property: Required, JsonPropertyName("time_zone")] string TimeZone);

public sealed record PreviewScheduleResponse(
    [property: Required, JsonPropertyName("occurrences")]
    IReadOnlyList<DateTimeOffset> Occurrences);

public sealed record CreateScheduleRequest(
    Guid? ScheduleId,
    string Name,
    Guid PolicyId,
    int PolicyVersion,
    string CronExpression,
    string TimeZone,
    bool Enabled);

public sealed record CreateScheduleVersionRequest(
    int ExpectedVersion,
    string Name,
    Guid PolicyId,
    int PolicyVersion,
    string CronExpression,
    string TimeZone,
    bool Enabled);

public sealed record QueueScheduleRunRequest(int ExpectedVersion);

public sealed record QueuedScheduleRunResponse(
    Guid WorkId,
    Guid ScheduleId,
    int ScheduleVersion,
    DateTimeOffset QueuedAt,
    string Status)
{
    public static QueuedScheduleRunResponse From(SyncScheduleRunReceipt value) => new(
        value.WorkId,
        value.ScheduleId,
        value.ScheduleVersion,
        value.QueuedAt,
        "Queued");
}

public sealed record ScheduleResponse(
    Guid ScheduleId,
    int Version,
    string Name,
    Guid PolicyId,
    int PolicyVersion,
    string CronExpression,
    string TimeZone,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public static ScheduleResponse From(EntitySyncSchedule value) => new(
        value.ScheduleId,
        value.Version,
        value.Name,
        value.PolicyId,
        value.PolicyVersion,
        value.CronExpression,
        value.TimeZone,
        value.Enabled,
        value.NextRunAt,
        value.LastRunAt,
        value.CreatedAt,
        value.CreatedBy.ActorId);
}

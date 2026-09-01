using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

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

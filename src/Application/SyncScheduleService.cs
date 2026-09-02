using Cronos;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Application;

public sealed record SyncScheduleRequest(
    string Name,
    Guid PolicyId,
    int PolicyVersion,
    string CronExpression,
    string TimeZone,
    bool Enabled);

public sealed class SyncScheduleVersionConflictException(Guid scheduleId, int expectedVersion)
    : InvalidOperationException(
        $"Schedule '{scheduleId}' is no longer at expected version {expectedVersion}.");

public sealed class SyncScheduleService(
    ISyncScheduleRepository schedules,
    ISyncPolicyRepository policies,
    TimeProvider timeProvider)
{
    public async Task<EntitySyncSchedule> CreateAsync(
        string tenantId,
        Guid scheduleId,
        SyncScheduleRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        if (scheduleId == Guid.Empty)
            throw new ArgumentException("Schedule ID is required.", nameof(scheduleId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (await schedules.GetLatestAsync(tenantId, scheduleId, cancellationToken)
                .ConfigureAwait(false) is not null)
            throw new SyncScheduleVersionConflictException(scheduleId, 0);

        var now = timeProvider.GetUtcNow();
        var policy = await RequireSafePolicyAsync(tenantId, request, cancellationToken)
            .ConfigureAwait(false);
        var zone = ResolveTimeZone(request.TimeZone);
        var expression = Parse(request.CronExpression);
        var schedule = new EntitySyncSchedule(
            tenantId,
            scheduleId,
            1,
            Require(request.Name, nameof(request.Name)),
            policy.PolicyId,
            policy.Version,
            request.CronExpression.Trim(),
            zone.Id,
            request.Enabled,
            request.Enabled ? GetNextRun(expression, zone, now) : null,
            null,
            now,
            actor);
        await schedules.InsertVersionAsync(tenantId, schedule, cancellationToken)
            .ConfigureAwait(false);
        return schedule;
    }

    public async Task<EntitySyncSchedule> CreateNextVersionAsync(
        string tenantId,
        Guid scheduleId,
        int expectedVersion,
        SyncScheduleRequest request,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        if (expectedVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var latest = await schedules.GetLatestAsync(tenantId, scheduleId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Schedule '{scheduleId}' was not found for tenant '{tenantId}'.");
        if (latest.Version != expectedVersion)
            throw new SyncScheduleVersionConflictException(scheduleId, expectedVersion);
        if (!latest.Name.Equals(Require(request.Name, nameof(request.Name)), StringComparison.Ordinal))
            throw new ArgumentException(
                "A schedule name is immutable across versions.", nameof(request));

        var now = timeProvider.GetUtcNow();
        var policy = await RequireSafePolicyAsync(tenantId, request, cancellationToken)
            .ConfigureAwait(false);
        var zone = ResolveTimeZone(request.TimeZone);
        var expression = Parse(request.CronExpression);
        var next = latest.NextVersion(
            request.CronExpression.Trim(),
            zone.Id,
            request.Enabled,
            request.Enabled ? GetNextRun(expression, zone, now) : null,
            actor,
            now,
            policy.PolicyId,
            policy.Version);
        await schedules.InsertVersionAsync(tenantId, next, cancellationToken)
            .ConfigureAwait(false);
        return next;
    }

    public async Task<EntitySyncSchedule> DisableAsync(
        string tenantId,
        Guid scheduleId,
        int expectedVersion,
        EntitySyncActor actor,
        CancellationToken cancellationToken)
    {
        tenantId = Require(tenantId, nameof(tenantId));
        var latest = await schedules.GetLatestAsync(tenantId, scheduleId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Schedule '{scheduleId}' was not found for tenant '{tenantId}'.");
        if (latest.Version != expectedVersion)
            throw new SyncScheduleVersionConflictException(scheduleId, expectedVersion);
        var next = latest.NextVersion(
            latest.CronExpression,
            latest.TimeZone,
            false,
            null,
            actor ?? throw new ArgumentNullException(nameof(actor)),
            timeProvider.GetUtcNow());
        await schedules.InsertVersionAsync(tenantId, next, cancellationToken)
            .ConfigureAwait(false);
        return next;
    }

    public IReadOnlyList<DateTimeOffset> PreviewOccurrences(
        string cronExpression,
        string timeZone,
        DateTimeOffset after)
    {
        var zone = ResolveTimeZone(timeZone);
        var expression = Parse(cronExpression);
        var occurrences = new DateTimeOffset[3];
        var previous = after;
        for (var index = 0; index < occurrences.Length; index++)
        {
            previous = GetNextRun(expression, zone, previous);
            occurrences[index] = previous;
        }
        return occurrences;
    }

    public static DateTimeOffset GetNextRun(
        string cronExpression,
        TimeZoneInfo timeZone,
        DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return GetNextRun(Parse(cronExpression), timeZone, after);
    }

    private static DateTimeOffset GetNextRun(
        CronExpression expression,
        TimeZoneInfo timeZone,
        DateTimeOffset after)
    {
        var occurrence = expression.GetNextOccurrence(
            after.UtcDateTime,
            timeZone,
            inclusive: false);
        if (occurrence is null)
            throw new InvalidOperationException(
                "The cron expression has no future occurrence.");
        return new DateTimeOffset(
            DateTime.SpecifyKind(occurrence.Value, DateTimeKind.Utc));
    }

    private async Task<EntitySyncPolicy> RequireSafePolicyAsync(
        string tenantId,
        SyncScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PolicyId == Guid.Empty)
            throw new ArgumentException("Policy ID is required.", nameof(request));
        if (request.PolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        var exact = await policies.GetAsync(
            tenantId, request.PolicyId, request.PolicyVersion, cancellationToken)
            .ConfigureAwait(false);
        var latest = await policies.GetLatestAsync(
            tenantId, request.PolicyId, cancellationToken).ConfigureAwait(false);
        if (exact is null
            || latest is null
            || latest.Version != exact.Version
            || !exact.Enabled)
            throw new InvalidOperationException(
                "The exact latest enabled policy version is required for a schedule.");
        if (!exact.Definition.ScheduledApplySafeSubset)
            throw new InvalidOperationException(
                "Scheduled execution requires a policy marked ScheduledApplySafeSubset.");
        return exact;
    }

    private static CronExpression Parse(string expression) =>
        CronExpression.Parse(Require(expression, nameof(expression)), CronFormat.Standard);

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        var required = Require(id, nameof(id));
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(required, out _))
            throw new TimeZoneNotFoundException("An IANA time zone is required.");
        return TimeZoneInfo.FindSystemTimeZoneById(required);
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();
}

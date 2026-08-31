using System.Reflection;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class ControlModelTests
{
    private static readonly DateTimeOffset Instant = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('A', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public void Control_state_names_match_the_database_contract()
    {
        Assert.Equal(["Draft", "Approved", "Consumed", "Expired"], Enum.GetNames<EntitySyncDurablePlanStatus>());
        Assert.Equal(["Open", "Completed"], Enum.GetNames<EntitySyncInspectionStatus>());
        Assert.Equal(["DryRun", "Apply"], Enum.GetNames<EntitySyncOperationMode>());
        Assert.Equal(
            ["Queued", "Leased", "Running", "Succeeded", "Partial", "Failed", "Cancelled"],
            Enum.GetNames<EntitySyncOperationStatus>());
        Assert.Equal(
            ["Pending", "Succeeded", "Failed", "Skipped", "Unknown"],
            Enum.GetNames<EntitySyncItemOutcome>());
        Assert.Equal(["Pending", "Planned", "Ignored", "Failed"], Enum.GetNames<EntitySyncCanonicalChangeStatus>());
    }

    [Fact]
    public void Sha256_values_are_normalized_and_invalid_values_are_rejected()
    {
        Assert.Equal(new string('a', 64), new EntitySyncSha256($"  {HashA}  ").Value);
        Assert.Throws<ArgumentException>(() => new EntitySyncSha256("not-a-hash"));
    }

    [Fact]
    public void Connection_definition_requires_stable_identity_and_positive_generation()
    {
        Assert.Throws<ArgumentException>(() => Connection(tenantId: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Connection(generation: 0));
        Assert.Throws<ArgumentException>(() => Connection(connectionId: " "));
    }

    [Fact]
    public void Policy_definition_enforces_scores_and_disjoint_field_rules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(autoLinkScore: 101));
        Assert.Throws<ArgumentException>(() => Definition(autoLinkScore: 70, reviewScore: 80));
        Assert.Throws<ArgumentException>(() => Definition(allowedFields: ["name"], blockedFields: ["NAME"]));
    }

    [Fact]
    public void Policy_next_version_preserves_identity_increments_version_and_rehashes_definition()
    {
        var policy = Policy();
        var next = policy.NextVersion(
            actor: new EntitySyncActor("operator"),
            definition: Definition(createMissing: true),
            now: Instant.AddMinutes(1));

        Assert.Equal(policy.TenantId, next.TenantId);
        Assert.Equal(policy.PolicyId, next.PolicyId);
        Assert.Equal(policy.Version + 1, next.Version);
        Assert.NotEqual(policy.DefinitionSha256, next.DefinitionSha256);
    }

    [Fact]
    public void Policy_field_collections_do_not_leak_mutability()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" };
        var policy = Definition(allowedFields: allowed);

        allowed.Add("phone");

        Assert.Equal(["name"], policy.AllowedFields.Order(StringComparer.Ordinal));
        Assert.False(policy.AllowedFields is ISet<string> mutable && !mutable.IsReadOnly);
    }

    [Fact]
    public void Selection_bounds_preserve_count_only_and_exact_id_assertions()
    {
        var countOnly = new EntitySyncSelectionBounds(null, 25, null);
        var assertedSearch = new EntitySyncSelectionBounds("acme", 100, "company-42");

        Assert.Null(countOnly.SourceSearch);
        Assert.Equal(25, countOnly.SourceCount);
        Assert.Null(countOnly.SourceEntityId);
        Assert.Equal("acme", assertedSearch.SourceSearch);
        Assert.Equal(100, assertedSearch.SourceCount);
        Assert.Equal("company-42", assertedSearch.SourceEntityId);
    }

    [Fact]
    public void Plan_item_copies_ordered_match_reasons_and_field_diffs()
    {
        var reasons = new List<string> { "external id", "name" };
        var diffs = new List<EntitySyncFieldDiff>
        {
            Diff("name", "\"Old\"", "\"Acme\""),
            Diff("phone", "\"1\"", "\"2\"")
        };
        var item = PlanItem(0, new EntitySyncMatchEvidence(95, "Linked", reasons), diffs);

        reasons.Clear();
        diffs.Clear();

        Assert.Equal(["external id", "name"], item.MatchEvidence.Reasons);
        Assert.Equal(["name", "phone"], item.FieldDiffs.Select(diff => diff.FieldName));
    }

    [Fact]
    public void Durable_plan_page_copies_and_requires_ordered_bound_items()
    {
        var items = new List<EntitySyncDurablePlanItem> { PlanItem(0), PlanItem(1) };
        var page = new EntitySyncDurablePlanPage("tenant", PlanId, 1, 50, 2, items);

        items.Clear();

        Assert.Equal([0, 1], page.Items.Select(item => item.ItemOrdinal));
        Assert.Throws<ArgumentException>(() =>
            new EntitySyncDurablePlanPage("tenant", PlanId, 1, 50, 2, [PlanItem(1), PlanItem(0)]));
    }

    [Fact]
    public void Durable_plan_allows_only_legal_state_transitions()
    {
        var draft = Plan();
        var approved = draft.TransitionTo(EntitySyncDurablePlanStatus.Approved);
        var consumed = approved.TransitionTo(EntitySyncDurablePlanStatus.Consumed);

        Assert.Equal(EntitySyncDurablePlanStatus.Consumed, consumed.Status);
        Assert.Throws<InvalidOperationException>(() => draft.TransitionTo(EntitySyncDurablePlanStatus.Consumed));
        Assert.Throws<InvalidOperationException>(() => consumed.TransitionTo(EntitySyncDurablePlanStatus.Approved));
    }

    [Fact]
    public void Inspection_session_completes_once_and_ranges_validate_bounds()
    {
        var session = Inspection();
        var completed = session.Complete(Instant.AddMinutes(1));

        Assert.Equal(EntitySyncInspectionStatus.Completed, completed.Status);
        Assert.Throws<InvalidOperationException>(() => completed.Complete(Instant.AddMinutes(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntitySyncInspectionRange(
            "tenant", session.InspectionId, Guid.NewGuid(), 5, 4, Instant));
    }

    [Fact]
    public void Apply_operation_requires_an_approval()
    {
        Assert.Throws<ArgumentException>(() => EntitySyncOperation.QueueApply(
            tenantId: "tenant",
            operationId: Guid.NewGuid(),
            planId: PlanId,
            approvalId: null,
            idempotencyKey: "apply-1",
            routeScope: "om-to-halo",
            sourceConnectionId: "source-1",
            sourceConnectionGeneration: 7,
            targetConnectionId: "target-1",
            targetConnectionGeneration: 11,
            now: Instant));
    }

    [Fact]
    public void Operation_allows_only_legal_state_transitions()
    {
        var queued = EntitySyncOperation.QueueDryRun(
            "tenant", Guid.NewGuid(), PlanId, "dry-1", "route", "source-1", 7, "target-1", 11, Instant);
        var leased = queued.Lease("worker-1", Instant.AddMinutes(1));
        var running = leased.Start(Instant.AddSeconds(1));
        var succeeded = running.Complete(EntitySyncOperationStatus.Succeeded, Instant.AddSeconds(2));

        Assert.Equal(1, leased.Attempt);
        Assert.Equal(EntitySyncOperationStatus.Succeeded, succeeded.Status);
        Assert.Throws<InvalidOperationException>(() => queued.Start(Instant));
        Assert.Throws<InvalidOperationException>(() => succeeded.Lease("worker-2", Instant.AddMinutes(2)));
    }

    [Fact]
    public void Schedule_next_version_preserves_identity_and_increments_version()
    {
        var schedule = new EntitySyncSchedule(
            "tenant", Guid.NewGuid(), 1, "nightly", PolicyId, 2, "0 2 * * *", "UTC", true,
            Instant.AddDays(1), null, Instant, new EntitySyncActor("operator"));

        var next = schedule.NextVersion("0 3 * * *", "UTC", false, null, new EntitySyncActor("operator-2"), Instant.AddMinutes(1));

        Assert.Equal(schedule.ScheduleId, next.ScheduleId);
        Assert.Equal(2, next.Version);
        Assert.Equal("0 3 * * *", next.CronExpression);
    }

    [Fact]
    public void Inspection_repository_contract_models_session_ranges_completion_and_single_use_approval()
    {
        var methods = typeof(IDurableSyncPlanRepository).GetMethods().ToDictionary(method => method.Name);

        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.OpenInspectionAsync)]);
        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.RecordInspectionRangeAsync)]);
        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.CompleteInspectionAsync)]);
        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.HasCompleteInspectionAsync)]);
        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.ApproveInspectionAsync)]);
        AssertTenantFirstAsync(methods[nameof(IDurableSyncPlanRepository.TryConsumeApprovalAsync)]);

        var recordParameters = methods[nameof(IDurableSyncPlanRepository.RecordInspectionRangeAsync)].GetParameters();
        Assert.Contains(recordParameters, parameter => parameter.Name == "inspectionId");
        Assert.Contains(recordParameters, parameter => parameter.Name == "rangeStart");
        Assert.Contains(recordParameters, parameter => parameter.Name == "rangeEnd");

        var approveParameters = methods[nameof(IDurableSyncPlanRepository.ApproveInspectionAsync)].GetParameters();
        Assert.Contains(approveParameters, parameter => parameter.Name == "inspectionId");
        Assert.Contains(approveParameters, parameter => parameter.Name == "planDigestSha256");

        var consumeParameters = methods[nameof(IDurableSyncPlanRepository.TryConsumeApprovalAsync)].GetParameters();
        Assert.Contains(consumeParameters, parameter =>
            parameter.Name == "applyOperation" && parameter.ParameterType == typeof(EntitySyncOperation));
        Assert.Contains(consumeParameters, parameter =>
            parameter.Name == "operationItems"
            && parameter.ParameterType == typeof(IReadOnlyList<EntitySyncOperationItem>));
    }

    [Fact]
    public void Every_repository_method_is_tenant_first_and_async()
    {
        var repositoryTypes = new[]
        {
            typeof(IConnectionDefinitionRepository), typeof(ISyncPolicyRepository),
            typeof(IDurableSyncPlanRepository), typeof(ISyncOperationRepository),
            typeof(ISyncScheduleRepository), typeof(ISyncAuditRepository), typeof(IIdempotencyRepository)
        };

        foreach (var method in repositoryTypes.SelectMany(type => type.GetMethods()))
            AssertTenantFirstAsync(method);
    }

    private static readonly Guid PolicyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid PlanId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    private static EntitySyncConnectionDefinition Connection(
        string tenantId = "tenant",
        string connectionId = "source-1",
        long generation = 1) =>
        new(
            tenantId, connectionId, "halo", "Halo", generation, true,
            new EntitySyncJsonValue("{}"), "ciphertext", Instant, new EntitySyncActor("creator"),
            Instant, new EntitySyncActor("creator"));

    private static EntitySyncPolicyDefinition Definition(
        bool createMissing = false,
        int autoLinkScore = 90,
        int reviewScore = 70,
        IEnumerable<string>? allowedFields = null,
        IEnumerable<string>? blockedFields = null) =>
        new(
            "halo", "source-1", "Company", "netsuite", "target-1", "Customer",
            false, createMissing, autoLinkScore, reviewScore, "id", "externalId",
            EntitySyncUpdatePolicy.Standard, allowedFields ?? ["name"], blockedFields ?? ["secret"], false);

    private static EntitySyncPolicy Policy()
    {
        var definition = Definition();
        return EntitySyncPolicy.Create(
            "tenant", PolicyId, "default", "halo-to-netsuite", definition, true, Instant,
            new EntitySyncActor("creator"));
    }

    private static EntitySyncDurablePlan Plan() =>
        new(
            "tenant", PlanId, PolicyId, 1, Policy().DefinitionSha256, "halo-to-netsuite",
            "source-1", 7, "target-1", 11, new EntitySyncSha256(HashA),
            EntitySyncDurablePlanStatus.Draft, new EntitySyncSelectionBounds(null, 100, null),
            2, Instant, new EntitySyncActor("creator"), Instant.AddHours(1));

    private static EntitySyncDurablePlanItem PlanItem(
        int ordinal,
        EntitySyncMatchEvidence? evidence = null,
        IEnumerable<EntitySyncFieldDiff>? fieldDiffs = null) =>
        new(
            "tenant", PlanId, Guid.NewGuid(), ordinal,
            "halo", "source-1", "Company", $"key-{ordinal}", $"source-{ordinal}",
            "netsuite", "target-1", "Customer", $"target-{ordinal}", "Update",
            evidence ?? new EntitySyncMatchEvidence(95, "Linked", ["external id"]),
            new EntitySyncJsonValue("{}"), new EntitySyncJsonValue("{\"name\":\"Acme\"}"),
            null, new EntitySyncSha256(HashB),
            fieldDiffs ?? [Diff("name", "\"Old\"", "\"Acme\"")]);

    private static EntitySyncFieldDiff Diff(string fieldName, string before, string desired) =>
        new(fieldName, new EntitySyncJsonValue(before), new EntitySyncJsonValue(desired));

    private static EntitySyncInspectionSession Inspection() =>
        new(
            "tenant", Guid.NewGuid(), PlanId, new EntitySyncSha256(HashA),
            "source-1", 7, "target-1", 11, EntitySyncInspectionStatus.Open,
            Instant, new EntitySyncActor("inspector"), null);

    private static void AssertTenantFirstAsync(MethodInfo method)
    {
        var parameters = method.GetParameters();
        Assert.NotEmpty(parameters);
        Assert.Equal("tenantId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.True(
            method.ReturnType == typeof(Task)
            || method.ReturnType == typeof(ValueTask)
            || method.ReturnType.IsGenericType
                && (method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                    || method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)),
            $"{method.DeclaringType?.Name}.{method.Name} must be asynchronous.");
    }
}

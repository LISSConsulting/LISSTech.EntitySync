using System.Collections.Concurrent;
using System.Text.Json;
using System.IO.Compression;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Commands;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class DurablePlanServiceTests
{
    [Fact]
    public async Task Inspection_of_684_items_is_durable_repeatable_out_of_order_and_required_for_approval()
    {
        using var fixture = new Fixture(684);
        var plan = await fixture.Service.CreatePlanAsync(fixture.Request(), fixture.Reviewer, default);

        Assert.Equal(684, plan.ItemCount);
        Assert.Equal(7, plan.PageCount(100));
        Assert.Equal(1, plan.PolicyVersion);
        Assert.Equal(1, plan.SourceConnectionGeneration);
        Assert.Equal(1, plan.TargetConnectionGeneration);

        var first = await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 1, 100, fixture.Reviewer, default);
        await Assert.ThrowsAsync<PlanInspectionIncompleteException>(() =>
            fixture.Service.ApproveAsync(
                Fixture.Tenant, plan.PlanId, plan.Digest, fixture.Reviewer, default));
        var repeated = await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 1, 100, fixture.Reviewer, default);
        Assert.Equal(first.InspectionId, repeated.InspectionId);

        DurablePlanInspectionPage? last = null;
        foreach (var page in new[] { 7, 3, 2, 6, 4, 5 })
        {
            last = await fixture.Service.GetPageAsync(
                Fixture.Tenant, plan.PlanId, page, 100, fixture.Reviewer, default);
        }

        Assert.NotNull(last);
        Assert.True(last!.InspectionComplete);
        Assert.Equal(684, last.CoveredItemCount);
        Assert.Equal([new DurableInspectionRange(0, 684)], last.Coverage);
        Assert.Equal(7, fixture.DurableRepository.RangeCount);
        Assert.Equal(Enumerable.Range(0, 684), fixture.DurableRepository.Manifest!.Items.Select(item => item.ItemOrdinal));
        Assert.Equal(684, fixture.DurableRepository.Manifest.Items.Select(item => item.ItemId).Distinct().Count());
        Assert.All(fixture.DurableRepository.Manifest.Items, item =>
        {
            Assert.Equal($"source-{item.ItemOrdinal}", item.SourceEntityKey);
            Assert.Equal($"source-{item.ItemOrdinal}", item.SourceEntityId);
        });
        var completedRepeat = await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 3, 100, fixture.Reviewer, default);
        Assert.True(completedRepeat.InspectionComplete);
        Assert.Equal(7, fixture.DurableRepository.RangeCount);


        var approval = await fixture.Service.ApproveAsync(
            Fixture.Tenant, plan.PlanId, plan.Digest, fixture.Reviewer, default);
        Assert.Equal(plan.Digest, approval.Digest);
        Assert.Equal(1, fixture.DurableRepository.AuditCount);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(8, 100)]
    public async Task Page_bounds_are_one_based_and_bounded(int page, int pageSize)
    {
        using var fixture = new Fixture(684);
        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request(), fixture.Reviewer, default);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.GetPageAsync(
                Fixture.Tenant, plan.PlanId, page, pageSize, fixture.Reviewer, default));
        Assert.Equal(0, fixture.DurableRepository.SessionCount);
    }

    [Fact]
    public async Task Mixed_page_sizes_complete_the_overlapping_inspection_union()
    {
        using var fixture = new Fixture(684);
        var plan = await fixture.Service.CreatePlanAsync(
            fixture.Request("mixed-pages"), fixture.Reviewer, default);
        await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 1, 100, fixture.Reviewer, default);
        await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 2, 50, fixture.Reviewer, default);

        DurablePlanInspectionPage? last = null;
        for (var page = 3; page <= 14; page++)
        {
            last = await fixture.Service.GetPageAsync(
                Fixture.Tenant, plan.PlanId, page, 50, fixture.Reviewer, default);
        }

        Assert.NotNull(last);
        Assert.True(last!.InspectionComplete);
        Assert.Equal([new DurableInspectionRange(0, 684)], last.Coverage);
    }

    [Fact]
    public async Task Manifest_digest_is_stable_for_identical_inputs_and_sensitive_to_payload_changes()
    {
        using var fixture = new Fixture(1);
        var output = await fixture.CreatePlannerSnapshotAsync();
        var planId = Guid.Parse("5a5ea5af-c361-4e68-ab13-43fb68647ef2");
        var selection = new EntitySyncSelectionBounds(null, null, null);
        var first = fixture.ManifestBuilder.Build(
            output, fixture.Policy, planId, fixture.Reviewer, fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow().AddHours(4), selection,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var second = fixture.ManifestBuilder.Build(
            output, fixture.Policy, planId, fixture.Reviewer, fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow().AddHours(4), selection,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(first.Plan.PlanDigestSha256, second.Plan.PlanDigestSha256);
        Assert.Equal(
            PlanManifestBuilder.ComputeItemDigest(first.Items[0]),
            PlanManifestBuilder.ComputeItemDigest(second.Items[0]));

        fixture.Sources[0].Name = "Changed";
        var changed = fixture.ManifestBuilder.Build(
            output, fixture.Policy, planId, fixture.Reviewer, fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow().AddHours(4), selection,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.NotEqual(first.Plan.PlanDigestSha256, changed.Plan.PlanDigestSha256);
        Assert.NotEqual(first.Items[0].DesiredPayloadSha256, changed.Items[0].DesiredPayloadSha256);
    }

    [Fact]
    public async Task Pinned_shadow_plan_uses_real_mapper_without_source_or_vendor_writes()
    {
        var canonicalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var sourceEntity = new ExternalEntity
        {
            Vendor = "OrchestraMSP",
            EntityType = "Client",
            Id = canonicalId.ToString("D"),
            Name = "Mapped Client",
            Email = "new@example.test",
            ExternalIds = { ["OrchestraClientId"] = canonicalId.ToString("D") }
        };
        var targetEntity = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "target-1",
            Name = "Mapped Client",
            Email = "old@example.test",
            CustomFields = { ["CFOrchestraClientID"] = canonicalId.ToString("D") }
        };
        var instant = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var creator = new EntitySyncActor("creator");
        EntitySyncConnectionDefinition Definition(string id, string vendor) => new(
            Fixture.Tenant,
            id,
            vendor,
            id,
            1,
            true,
            new EntitySyncJsonValue("{}"),
            "ciphertext",
            instant,
            creator,
            instant,
            creator);
        var sourceAdapter = new TestAdapter("OrchestraMSP", []);
        using var connections = new DefinitionAndRuntimeRepository(
            Definition(Fixture.SourceConnectionId, "OrchestraMSP"),
            sourceAdapter,
            Definition(Fixture.TargetConnectionId, "HaloPSA"),
            new TestAdapter("HaloPSA", [targetEntity]));
        var definition = new EntitySyncPolicyDefinition(
            "OrchestraMSP", Fixture.SourceConnectionId, "Client",
            "HaloPSA", Fixture.TargetConnectionId, "Client",
            false, false, 90, 70, "OrchestraClientId", "CFOrchestraClientID",
            EntitySyncUpdatePolicy.Standard, ["name", "contactemail"], [], false);
        var policy = EntitySyncPolicy.Create(
            Fixture.Tenant,
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            "shadow", "shadow-route", definition, true, instant, creator);
        var policies = new MemoryPolicyRepository(policy);
        var exclusions = new RecordingExclusionRepository();
        var time = new ManualTimeProvider(instant);
        var repository = new MemoryDurableRepository(time, policies, connections);
        var mapper = new DefaultEntityMapper();
        var planner = new EntitySyncPlanner(
            connections, new TestEntitySyncPlanRepository(), exclusions,
            new WeightedEntityMatcher(), mapper,
            new InMemoryEntitySyncChangeStateRepository());
        var service = new DurablePlanService(
            planner, new PlanManifestBuilder(mapper), policies, connections,
            connections, exclusions, repository, time);

        var result = await service.CreatePlanAsync(new CreateDurablePlanRequest
        {
            TenantId = Fixture.Tenant,
            IdempotencyKey = "shadow-mapped",
            PolicyId = policy.PolicyId,
            PolicyVersion = policy.Version,
            PinnedCanonicalSources =
            [
                new CanonicalEntityVersion(canonicalId, 7, sourceEntity)
            ],
            PlanLifetime = TimeSpan.FromHours(1)
        }, new EntitySyncActor("operator"), default);

        Assert.Equal(EntitySyncDurablePlanStatus.Draft, repository.Manifest!.Plan.Status);
        Assert.Equal(1, result.ItemCount);
        var desired = repository.Manifest.Items[0].RedactedDesired.Json;
        Assert.Contains("\"contactemail\":\"new@example.test\"", desired);
        Assert.DoesNotContain("\"email\":", desired);

        var emptyShadow = await service.CreatePlanAsync(new CreateDurablePlanRequest
        {
            TenantId = Fixture.Tenant,
            IdempotencyKey = "shadow-empty",
            PolicyId = policy.PolicyId,
            PolicyVersion = policy.Version,
            PinnedCanonicalOnly = true,
            PinnedCanonicalSources = [],
            PlanLifetime = TimeSpan.FromHours(1)
        }, new EntitySyncActor("operator"), default);
        Assert.Equal(0, emptyShadow.ItemCount);
        Assert.Equal(EntitySyncDurablePlanStatus.Draft, repository.Manifest!.Plan.Status);
        Assert.Empty(repository.Manifest.Items);
        Assert.Equal(0, sourceAdapter.GetEntitiesCalls);
        var emptyInspection = await service.GetPageAsync(
            Fixture.Tenant,
            emptyShadow.PlanId,
            page: 1,
            pageSize: 100,
            new EntitySyncActor("operator"),
            default);
        Assert.Empty(emptyInspection.Items);
        Assert.Equal(0, emptyInspection.CoveredItemCount);
        Assert.True(emptyInspection.InspectionComplete);

        await Assert.ThrowsAsync<DurablePlanIdempotencyConflictException>(() =>
            service.CreatePlanAsync(new CreateDurablePlanRequest
            {
                TenantId = Fixture.Tenant,
                IdempotencyKey = "shadow-empty",
                PolicyId = policy.PolicyId,
                PolicyVersion = policy.Version,
                PlanLifetime = TimeSpan.FromHours(1)
            }, new EntitySyncActor("operator"), default));
        Assert.Equal(0, sourceAdapter.GetEntitiesCalls);

        var normal = await service.CreatePlanAsync(new CreateDurablePlanRequest
        {
            TenantId = Fixture.Tenant,
            IdempotencyKey = "normal-empty",
            PolicyId = policy.PolicyId,
            PolicyVersion = policy.Version,
            PlanLifetime = TimeSpan.FromHours(1)
        }, new EntitySyncActor("operator"), default);
        Assert.Equal(0, normal.ItemCount);
        Assert.Equal(1, sourceAdapter.GetEntitiesCalls);
    }

    [Fact]
    public async Task Concurrent_lost_response_retry_plans_once_and_changed_body_conflicts()
    {
        using var fixture = new Fixture(2);
        fixture.SourceAdapter.BlockNextRead();
        var firstTask = fixture.Service.CreatePlanAsync(
            fixture.Request("stable-key"), fixture.Reviewer, default);
        await fixture.SourceAdapter.WaitForReadAsync();
        var retryTask = fixture.Service.CreatePlanAsync(
            fixture.Request("stable-key"), fixture.Reviewer, default);
        await Task.Delay(50);
        Assert.False(retryTask.IsCompleted);
        fixture.SourceAdapter.ReleaseRead();

        var results = await Task.WhenAll(firstTask, retryTask);
        Assert.Equal(results[0].PlanId, results[1].PlanId);
        Assert.Equal(results[0].Digest, results[1].Digest);
        Assert.Equal(1, fixture.SourceAdapter.GetEntitiesCalls);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var laterRetry = await fixture.Service.CreatePlanAsync(
            fixture.Request("stable-key"), fixture.Reviewer, default);
        Assert.Equal(results[0].PlanId, laterRetry.PlanId);
        Assert.Equal(1, fixture.SourceAdapter.GetEntitiesCalls);
        await Assert.ThrowsAsync<DurablePlanIdempotencyConflictException>(() =>
            fixture.Service.CreatePlanAsync(
                fixture.Request("stable-key", sourceCount: 1),
                fixture.Reviewer,
                default));
        await Assert.ThrowsAsync<DurablePlanIdempotencyConflictException>(() =>
            fixture.Service.CreatePlanAsync(
                fixture.Request("stable-key", omitPolicyVersion: true),
                fixture.Reviewer,
                default));
        Assert.Equal(1, fixture.SourceAdapter.GetEntitiesCalls);
    }

    [Fact]
    public async Task Planning_beyond_the_initial_lease_renews_and_contenders_do_not_replan()
    {
        using var fixture = new Fixture(2);
        var service = fixture.CreateService(new DurablePlanCreationOptions(
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(10)));
        fixture.SourceAdapter.BlockNextRead();
        var firstTask = service.CreatePlanAsync(
            fixture.Request("renewed-key"), fixture.Reviewer, default);
        await fixture.SourceAdapter.WaitForReadAsync();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(150));
        await Task.Delay(80);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(150));
        await Task.Delay(80);
        var retryTask = service.CreatePlanAsync(
            fixture.Request("renewed-key"), fixture.Reviewer, default);
        await Task.Delay(40);
        Assert.False(retryTask.IsCompleted);

        fixture.SourceAdapter.ReleaseRead();
        var results = await Task.WhenAll(firstTask, retryTask);

        Assert.Equal(results[0].PlanId, results[1].PlanId);
        Assert.Equal(1, fixture.SourceAdapter.GetEntitiesCalls);
        Assert.True(fixture.DurableRepository.RenewalCount >= 2);
    }

    [Fact]
    public async Task Lost_renewal_ownership_cancels_planning_and_rejects_stale_persistence()
    {
        using var fixture = new Fixture(1);
        var service = fixture.CreateService(new DurablePlanCreationOptions(
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(10)));
        fixture.DurableRepository.LoseOwnershipOnRenewal = true;
        fixture.SourceAdapter.BlockNextRead();

        var creation = service.CreatePlanAsync(
            fixture.Request("lost-owner-key"), fixture.Reviewer, default);
        await fixture.SourceAdapter.WaitForReadAsync();

        await Assert.ThrowsAsync<DurablePlanCreationConflictException>(() => creation);
        Assert.Null(fixture.DurableRepository.Manifest);
        Assert.True(fixture.DurableRepository.RenewalCount >= 1);
    }

    [Fact]
    public async Task Caller_cancellation_releases_the_creation_claim_for_a_retry()
    {
        using var fixture = new Fixture(1);
        fixture.SourceAdapter.BlockNextRead();
        using var cancelled = new CancellationTokenSource();
        var creation = fixture.Service.CreatePlanAsync(
            fixture.Request("cancelled-key"), fixture.Reviewer, cancelled.Token);
        await fixture.SourceAdapter.WaitForReadAsync();

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.Null(fixture.DurableRepository.Manifest);

        fixture.SourceAdapter.ReleaseRead();
        var retried = await fixture.Service.CreatePlanAsync(
            fixture.Request("cancelled-key"), fixture.Reviewer, default);
        Assert.NotEqual(Guid.Empty, retried.PlanId);
        Assert.Equal(2, fixture.SourceAdapter.GetEntitiesCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Planner_adapter_read_failures_are_safe_and_release_creation_claim(
        bool sourceFails)
    {
        using var fixture = new Fixture(1);
        var adapter = sourceFails ? fixture.SourceAdapter : fixture.TargetAdapter;
        adapter.ReadFailure = new InvalidOperationException(
            "vendor-secret-response");

        var failure = await Assert.ThrowsAsync<EntitySyncDependencyUnavailableException>(
            () => fixture.Service.CreatePlanAsync(
                fixture.Request("dependency-key"), fixture.Reviewer, default));

        Assert.Equal("The entity adapter is unavailable.", failure.Message);
        Assert.Null(fixture.DurableRepository.Manifest);
        adapter.ReadFailure = null;
        var retry = await fixture.Service.CreatePlanAsync(
            fixture.Request("dependency-key"), fixture.Reviewer, default);
        Assert.NotEqual(Guid.Empty, retry.PlanId);
    }

    [Fact]
    public async Task Changed_exclusion_reclassifies_create_before_persistence()
    {
        using var fixture = new Fixture(1);
        fixture.Exclusions.ReturnExclusionOnSecondRead = true;

        await fixture.Service.CreatePlanAsync(fixture.Request(), fixture.Reviewer, default);

        var item = Assert.Single(fixture.DurableRepository.Manifest!.Items);
        Assert.Equal("None", item.Action);
        Assert.Equal("PersistentExclusion", item.MatchEvidence.MatchType);
        Assert.Equal(2, fixture.Exclusions.ListCalls);
    }

    [Fact]
    public async Task Allowed_blocked_and_sensitive_fields_are_filtered_hashed_and_redacted()
    {
        using var fixture = new Fixture(1);

        await fixture.Service.CreatePlanAsync(fixture.Request(), fixture.Reviewer, default);

        var item = Assert.Single(fixture.DurableRepository.Manifest!.Items);
        Assert.DoesNotContain("blockedField", item.RedactedDesired.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Fixture.SecretValue, item.RedactedDesired.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.NestedSecretValue, item.RedactedDesired.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ArraySecretValue, item.RedactedDesired.Json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", item.RedactedDesired.Json, StringComparison.Ordinal);
        Assert.Equal(
            ["allowedField", "apiSecret", "name", "nested"],
            item.FieldDiffs.Select(change => change.Field));
        var sensitive = Assert.Single(item.FieldDiffs, change => change.Field == "apiSecret");
        Assert.True(sensitive.Sensitive);
        Assert.DoesNotContain(Fixture.SecretValue, sensitive.Desired.Json, StringComparison.Ordinal);
        var nested = Assert.Single(item.FieldDiffs, change => change.Field == "nested");
        Assert.True(nested.Sensitive);
        Assert.Contains("visible", nested.Desired.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.NestedSecretValue, nested.Desired.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ArraySecretValue, nested.Desired.Json, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", sensitive.DesiredSha256.Value);
        var persisted = JsonSerializer.Serialize(fixture.DurableRepository.Manifest);
        Assert.DoesNotContain(Fixture.SecretValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.NestedSecretValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.CredentialSecretValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.NestedBlockedValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ArraySecretValue, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_rejects_wrong_actor_wrong_digest_and_expiry()
    {
        using var fixture = new Fixture(1);
        var plan = await fixture.Service.CreatePlanAsync(fixture.Request(), fixture.Reviewer, default);
        await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 1, 100, fixture.Reviewer, default);

        await Assert.ThrowsAsync<DurablePlanDigestMismatchException>(() =>
            fixture.Service.ApproveAsync(
                Fixture.Tenant, plan.PlanId, new string('f', 64), fixture.Reviewer, default));
        await Assert.ThrowsAsync<PlanInspectionIncompleteException>(() =>
            fixture.Service.ApproveAsync(
                Fixture.Tenant, plan.PlanId, plan.Digest, new EntitySyncActor("other"), default));
        fixture.Time.Advance(TimeSpan.FromHours(5));
        await Assert.ThrowsAsync<DurablePlanExpiredException>(() =>
            fixture.Service.ApproveAsync(
                Fixture.Tenant, plan.PlanId, plan.Digest, fixture.Reviewer, default));
        Assert.Equal(0, fixture.DurableRepository.AuditCount);
    }

    [Fact]
    public async Task Approval_rejects_connection_generation_and_policy_rotation()
    {
        using var generationFixture = new Fixture(1);
        var generationPlan = await generationFixture.Service.CreatePlanAsync(
            generationFixture.Request(), generationFixture.Reviewer, default);
        await generationFixture.Service.GetPageAsync(
            Fixture.Tenant, generationPlan.PlanId, 1, 100, generationFixture.Reviewer, default);
        generationFixture.Connections.Rotate(Fixture.SourceConnectionId);
        await Assert.ThrowsAsync<DurablePlanConnectionChangedException>(() =>
            generationFixture.Service.ApproveAsync(
                Fixture.Tenant,
                generationPlan.PlanId,
                generationPlan.Digest,
                generationFixture.Reviewer,
                default));

        using var policyFixture = new Fixture(1);
        var policyPlan = await policyFixture.Service.CreatePlanAsync(
            policyFixture.Request(), policyFixture.Reviewer, default);
        await policyFixture.Service.GetPageAsync(
            Fixture.Tenant, policyPlan.PlanId, 1, 100, policyFixture.Reviewer, default);
        policyFixture.Policies.Rotate(policyFixture.Policy);
        await Assert.ThrowsAsync<DurablePlanPolicyChangedException>(() =>
            policyFixture.Service.ApproveAsync(
                Fixture.Tenant,
                policyPlan.PlanId,
                policyPlan.Digest,
                policyFixture.Reviewer,
                default));
    }

    [Fact]
    public async Task Concurrent_approvals_create_one_result_and_one_atomic_audit_event()
    {
        using var fixture = new Fixture(1);
        var plan = await fixture.Service.CreatePlanAsync(fixture.Request(), fixture.Reviewer, default);
        await fixture.Service.GetPageAsync(
            Fixture.Tenant, plan.PlanId, 1, 100, fixture.Reviewer, default);
        using var start = new ManualResetEventSlim(false);
        async Task<object> ApproveAsync()
        {
            start.Wait();
            try
            {
                return await fixture.Service.ApproveAsync(
                    Fixture.Tenant, plan.PlanId, plan.Digest, fixture.Reviewer, default);
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
        var attempts = new[] { Task.Run(ApproveAsync), Task.Run(ApproveAsync) };
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result is DurablePlanApprovalResult);
        Assert.Single(results, result => result is DurablePlanApprovalConflictException);
        Assert.Equal(1, fixture.DurableRepository.ApprovalCount);
        Assert.Equal(1, fixture.DurableRepository.AuditCount);
    }
    [Fact]
    public async Task Durable_import_validates_exact_context_replays_and_conflicts()
    {
        using var source = new Fixture(1);
        await source.Service.CreatePlanAsync(
            source.Request("import-source"), source.Reviewer, default);
        var manifest = source.DurableRepository.Manifest!;

        using var target = new Fixture(1);
        var imported = await target.Service.ImportManifestAsync(
            Fixture.Tenant, manifest, "import-key", target.Reviewer, default);
        var replay = await target.Service.ImportManifestAsync(
            Fixture.Tenant, manifest, "import-key", target.Reviewer, default);
        Assert.Equal(imported.PlanId, replay.PlanId);
        Assert.Equal(manifest.Plan.PlanDigestSha256, replay.PlanDigestSha256);

        var changedPlan = new EntitySyncDurablePlan(
            manifest.Plan.TenantId,
            manifest.Plan.PlanId,
            manifest.Plan.PolicyId,
            manifest.Plan.PolicyVersion,
            manifest.Plan.PolicyDefinitionSha256,
            manifest.Plan.RouteScope,
            manifest.Plan.SourceConnectionId,
            manifest.Plan.SourceConnectionGeneration,
            manifest.Plan.TargetConnectionId,
            manifest.Plan.TargetConnectionGeneration,
            manifest.Plan.PlanDigestSha256,
            EntitySyncDurablePlanStatus.Draft,
            manifest.Plan.SelectionBounds,
            manifest.Plan.ItemCount,
            manifest.Plan.CreatedAt,
            manifest.Plan.CreatedBy,
            manifest.Plan.ExpiresAt.AddMinutes(1));
        var conflicting = EntitySyncDurablePlanManifest.Create(
            changedPlan, manifest.Items);
        await Assert.ThrowsAsync<DurablePlanIdempotencyConflictException>(() =>
            target.Service.ImportManifestAsync(
                Fixture.Tenant, conflicting, "import-key", target.Reviewer, default));

        var approved = manifest.Plan.TransitionTo(EntitySyncDurablePlanStatus.Approved);
        var approvedManifest = EntitySyncDurablePlanManifest.LoadPersisted(
            approved, manifest.Items);
        Assert.Equal(EntitySyncDurablePlanStatus.Approved, approvedManifest.Plan.Status);
        using var statusTarget = new Fixture(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            statusTarget.Service.ImportManifestAsync(
                Fixture.Tenant, approvedManifest, "import-key", target.Reviewer, default));
    }

    [Fact]
    public async Task Durable_import_rejects_changed_policy_and_rotated_connections()
    {
        using var source = new Fixture(1);
        await source.Service.CreatePlanAsync(
            source.Request("import-guards"), source.Reviewer, default);
        var manifest = source.DurableRepository.Manifest!;

        using var policyTarget = new Fixture(1);
        await policyTarget.Policies.InsertAsync(
            Fixture.Tenant,
            policyTarget.Policy.NextVersion(
                policyTarget.Reviewer,
                policyTarget.Policy.Definition,
                policyTarget.Time.GetUtcNow().AddMinutes(1)),
            default);
        await Assert.ThrowsAsync<DurablePlanPolicyChangedException>(() =>
            policyTarget.Service.ImportManifestAsync(
                Fixture.Tenant, manifest, "policy-guard",
                policyTarget.Reviewer, default));

        using var generationTarget = new Fixture(1);
        generationTarget.Connections.Rotate(Fixture.SourceConnectionId);
        await Assert.ThrowsAsync<DurablePlanConnectionChangedException>(() =>
            generationTarget.Service.ImportManifestAsync(
                Fixture.Tenant, manifest, "generation-guard",
                generationTarget.Reviewer, default));
    }

    [Fact]
    public async Task Durable_workbook_is_authenticated_and_preserves_persisted_status()
    {
        using var fixture = new Fixture(1);
        await fixture.Service.CreatePlanAsync(
            fixture.Request("workbook-crypto"), fixture.Reviewer, default);
        var draft = fixture.DurableRepository.Manifest!;
        var approved = EntitySyncDurablePlanManifest.LoadPersisted(
            draft.Plan.TransitionTo(EntitySyncDurablePlanStatus.Approved),
            draft.Items);
        var root = Path.Combine(Path.GetTempPath(), $"entitysync-workbook-{Guid.NewGuid():N}");
        var firstKeys = Directory.CreateDirectory(Path.Combine(root, "first"));
        var secondKeys = Directory.CreateDirectory(Path.Combine(root, "second"));
        var path = Path.Combine(root, "plan.xlsx");
        try
        {
            var first = new EntitySyncDataProtector(
                DataProtectionProvider.Create(firstKeys));
            var second = new EntitySyncDataProtector(
                DataProtectionProvider.Create(secondKeys));
            PowerShellDurablePlanWorkbook.Write(approved, path, first);
            var loaded = PowerShellDurablePlanWorkbook.Read(
                path, Fixture.Tenant, first);
            Assert.Equal(EntitySyncDurablePlanStatus.Approved, loaded.Plan.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
                PowerShellDurablePlanWorkbook.Read(path, "other-tenant", first)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
                PowerShellDurablePlanWorkbook.Read(path, Fixture.Tenant, second)));

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("entitysync/durable-plan.json")!;
                string json;
                using (var reader = new StreamReader(entry.Open()))
                    json = reader.ReadToEnd();
                using var document = JsonDocument.Parse(json);
                var envelope = document.RootElement;
                var protectedPayload = envelope.GetProperty(
                    "protectedPayload").GetString()!;
                entry.Delete();
                var replacement = archive.CreateEntry("entitysync/durable-plan.json");
                using var writer = new StreamWriter(replacement.Open());
                writer.Write(JsonSerializer.Serialize(new
                {
                    tenantId = envelope.GetProperty("tenantId").GetString(),
                    planId = envelope.GetProperty("planId").GetGuid(),
                    protectedPayload =
                        (protectedPayload[0] == 'A' ? "B" : "A")
                        + protectedPayload[1..]
                }));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
                PowerShellDurablePlanWorkbook.Read(path, Fixture.Tenant, first)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }


    private sealed class Fixture : IDisposable
    {
        internal const string Tenant = "tenant";
        internal const string SourceConnectionId = "source";
        internal const string TargetConnectionId = "target";
        internal const string SecretValue = "super-secret-value";
        internal const string NestedSecretValue = "nested-secret-value";
        internal const string ArraySecretValue = "array-secret-value";
        internal const string CredentialSecretValue = "credential-secret-value";
        internal const string NestedBlockedValue = "nested-blocked-value";
        private static readonly DateTimeOffset Instant =
            new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

        public Fixture(int sourceCount)
        {
            Sources = Enumerable.Range(0, sourceCount).Select(index => new ExternalEntity
            {
                Vendor = "NetSuite",
                EntityType = "Customer",
                Id = $"source-{index}",
                Name = $"Source {index}",
                ExternalIds = { ["MutableExternalReference"] = $"external-{index}" }
            }).ToList();
            SourceAdapter = new TestAdapter("NetSuite", Sources);
            TargetAdapter = new TestAdapter("HaloPSA", []);
            Connections = new DefinitionAndRuntimeRepository(
                Definition(SourceConnectionId, "NetSuite"),
                SourceAdapter,
                Definition(TargetConnectionId, "HaloPSA"),
                TargetAdapter);
            var definition = new EntitySyncPolicyDefinition(
                "NetSuite",
                SourceConnectionId,
                "Customer",
                "HaloPSA",
                TargetConnectionId,
                "Client",
                false,
                true,
                90,
                70,
                "NetSuiteInternalId",
                "CFNetSuiteCustomerID",
                EntitySyncUpdatePolicy.Standard,
                ["name", "allowedField", "apiSecret", "nested"],
                ["blockedField"],
                false);
            Policy = EntitySyncPolicy.Create(
                Tenant,
                Guid.Parse("12c4d916-a759-4d70-bec8-98389451b99c"),
                "policy",
                "route",
                definition,
                true,
                Instant,
                new EntitySyncActor("creator"));
            Policies = new MemoryPolicyRepository(Policy);
            Exclusions = new RecordingExclusionRepository();
            Mapper = new TestMapper();
            ManifestBuilder = new PlanManifestBuilder(Mapper);
            Time = new ManualTimeProvider(Instant);
            DurableRepository = new MemoryDurableRepository(
                Time, Policies, Connections);
            Planner = new EntitySyncPlanner(
                Connections,
                new TestEntitySyncPlanRepository(),
                Exclusions,
                new WeightedEntityMatcher(),
                Mapper,
                new InMemoryEntitySyncChangeStateRepository());
            Service = CreateService();
        }

        public List<ExternalEntity> Sources { get; }
        public TestAdapter SourceAdapter { get; }
        public TestAdapter TargetAdapter { get; }
        public DefinitionAndRuntimeRepository Connections { get; }
        public MemoryPolicyRepository Policies { get; }
        public RecordingExclusionRepository Exclusions { get; }
        public MemoryDurableRepository DurableRepository { get; }
        public TestMapper Mapper { get; }
        public PlanManifestBuilder ManifestBuilder { get; }
        public ManualTimeProvider Time { get; }
        public EntitySyncPlanner Planner { get; }
        public DurablePlanService Service { get; }
        public EntitySyncPolicy Policy { get; }
        public EntitySyncActor Reviewer { get; } = new("reviewer");
        public CreateDurablePlanRequest Request(
            string idempotencyKey = "plan-key",
            int? sourceCount = null,
            bool omitPolicyVersion = false) => new()
        {
            TenantId = Tenant,
            IdempotencyKey = idempotencyKey,
            PolicyId = Policy.PolicyId,
            PolicyVersion = omitPolicyVersion ? null : Policy.Version,
            SourceCount = sourceCount,
            PlanLifetime = TimeSpan.FromHours(4)
        };
        public DurablePlanService CreateService(DurablePlanCreationOptions? options = null) =>
            new(
                Planner,
                ManifestBuilder,
                Policies,
                Connections,
                Connections,
                Exclusions,
                DurableRepository,
                Time,
                options);


        public async Task<EntitySyncPlan> CreatePlannerSnapshotAsync()
        {
            await using var source = await Connections.AcquireAsync(
                Tenant, SourceConnectionId, 1, default);
            await using var target = await Connections.AcquireAsync(
                Tenant, TargetConnectionId, 1, default);
            return await Planner.CreateSnapshotAsync(
                new CreateEntitySyncPlanRequest
                {
                    TenantId = Tenant,
                    SourceVendor = "NetSuite",
                    SourceConnectionId = SourceConnectionId,
                    SourceEntityType = "Customer",
                    TargetVendor = "HaloPSA",
                    TargetConnectionId = TargetConnectionId,
                    TargetEntityType = "Client",
                    CreateMissing = true,
                    AutoLinkScore = 90,
                    ReviewScore = 70,
                    SourceExternalIdName = "NetSuiteInternalId",
                    TargetCustomFieldName = "CFNetSuiteCustomerID"
                },
                source,
                target,
                default);
        }

        public void Dispose() => Connections.Dispose();

        private static EntitySyncConnectionDefinition Definition(string id, string vendor) =>
            new(
                Tenant,
                id,
                vendor,
                id,
                1,
                true,
                new EntitySyncJsonValue("{}"),
                "ciphertext",
                Instant,
                new EntitySyncActor("creator"),
                Instant,
                new EntitySyncActor("creator"));
    }

    private sealed class TestMapper : IEntityMapper
    {
        public EntityWriteRequest MapCreate(
            ExternalEntity source,
            string targetVendor,
            string targetEntityType,
            MatchOptions options) => Request(source, targetVendor, targetEntityType, null);

        public EntityWriteRequest MapUpdate(
            ExternalEntity source,
            ExternalEntity target,
            MatchOptions options) => Request(source, target.Vendor, target.EntityType, target.Id);

        private static EntityWriteRequest Request(
            ExternalEntity source,
            string vendor,
            string type,
            string? id)
        {
            var request = new EntityWriteRequest
            {
                Vendor = vendor,
                EntityType = type,
                Id = id,
                Name = source.Name
            };
            request.Fields["blockedField"] = "must-not-persist";
            request.Fields["allowedField"] = new Dictionary<string, object?>
            {
                ["z"] = 2,
                ["a"] = null
            };
            request.Fields["apiSecret"] = Fixture.SecretValue;
            request.Fields["nested"] = new Dictionary<string, object?>
            {
                ["visible"] = "retained",
                ["authentication"] = new Dictionary<string, object?>
                {
                    ["apiToken"] = Fixture.NestedSecretValue
                },
                ["credentials"] = Fixture.CredentialSecretValue,
                ["blockedField"] = Fixture.NestedBlockedValue,
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["password"] = Fixture.ArraySecretValue,
                        ["label"] = "retained"
                    }
                }
            };
            return request;
        }
    }
    private sealed class TestAdapter(string vendor, IReadOnlyList<ExternalEntity> entities)
        : IEntityAdapter
    {
        private TaskCompletionSource? readStarted;
        private TaskCompletionSource? releaseRead;

        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int GetEntitiesCalls { get; private set; }
        public Exception? ReadFailure { get; set; }

        public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken)
        {
            GetEntitiesCalls++;
            if (ReadFailure is not null) throw ReadFailure;
            readStarted?.TrySetResult();
            if (releaseRead is not null)
                await releaseRead.Task.WaitAsync(cancellationToken);
            return entities;
        }

        public void BlockNextRead()
        {
            readStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitForReadAsync() => readStarted?.Task
            ?? throw new InvalidOperationException("The read was not blocked.");

        public void ReleaseRead()
        {
            releaseRead?.TrySetResult();
            releaseRead = null;
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class DefinitionAndRuntimeRepository :
        IConnectionDefinitionRepository,
        IConnectionRuntimeFactory,
        IDisposable
    {
        private readonly ConcurrentDictionary<string, EntitySyncConnectionDefinition> definitions = new();
        private readonly IReadOnlyDictionary<string, IEntityAdapter> adapters;

        public DefinitionAndRuntimeRepository(
            EntitySyncConnectionDefinition source,
            IEntityAdapter sourceAdapter,
            EntitySyncConnectionDefinition target,
            IEntityAdapter targetAdapter)
        {
            definitions[source.ConnectionId] = source;
            definitions[target.ConnectionId] = target;
            adapters = new Dictionary<string, IEntityAdapter>
            {
                [source.ConnectionId] = sourceAdapter,
                [target.ConnectionId] = targetAdapter
            };
        }

        public Task<EntitySyncConnectionDefinition> InsertAsync(
            string tenantId,
            EntitySyncConnectionDefinition definition,
            CancellationToken cancellationToken)
        {
            definitions[definition.ConnectionId] = definition;
            return Task.FromResult(definition);
        }

        public Task<EntitySyncConnectionDefinition?> GetAsync(
            string tenantId,
            string connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(definitions.TryGetValue(connectionId, out var value) ? value : null);

        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
            string tenantId,
            string? vendor,
            bool? enabled,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncConnectionDefinition>>(
                definitions.Values.Where(definition =>
                    (vendor is null || definition.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                    && (enabled is null || definition.Enabled == enabled)).ToArray());

        public Task<EntitySyncConnectionDefinition?> TryReplaceAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            EntitySyncConnectionDefinition nextGeneration,
            CancellationToken cancellationToken)
        {
            definitions[connectionId] = nextGeneration;
            return Task.FromResult<EntitySyncConnectionDefinition?>(nextGeneration);
        }

        public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(ConnectionDefinitionDeleteResult.Referenced);

        public Task<IConnectionRuntimeLease> AcquireAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken)
        {
            var definition = definitions[connectionId];
            if (!definition.Enabled || definition.Generation != expectedGeneration)
                throw new StaleConnectionGenerationException(
                    connectionId, expectedGeneration, definition.Generation);
            return Task.FromResult<IConnectionRuntimeLease>(
                new Lease(definition, adapters[connectionId]));
        }

        public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken)
        {
            var definition = connectionId is null
                ? definitions.Values.Single(value => value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                : definitions[connectionId];
            return Task.FromResult<IConnectionRuntimeLease>(
                new Lease(definition, adapters[definition.ConnectionId]));
        }

        public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
            string tenantId,
            string vendor,
            string? connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(connectionId is null
                ? definitions.Values.Single(value => value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                : definitions[connectionId]);

        public void Rotate(string connectionId)
        {
            var current = definitions[connectionId];
            definitions[connectionId] = current.NextGeneration(
                current.DisplayName,
                true,
                current.PublicConfiguration,
                current.SecretCiphertext,
                new EntitySyncActor("rotator"),
                current.UpdatedAt.AddMinutes(1));
        }

        public void Dispose()
        {
        }

        private sealed class Lease(
            EntitySyncConnectionDefinition definition,
            IEntityAdapter adapter) : IConnectionRuntimeLease
        {
            public EntitySyncConnectionDefinition Definition { get; } = definition;
            public IEntityAdapter Adapter { get; } = adapter;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryPolicyRepository(EntitySyncPolicy initial) : ISyncPolicyRepository
    {
        private readonly List<EntitySyncPolicy> values = [initial];
        private readonly Dictionary<(Guid PolicyId, string Token), EntitySyncPolicy> byToken = [];

        public Task InsertAsync(
            string tenantId,
            EntitySyncPolicy policy,
            CancellationToken cancellationToken)
        {
            values.Add(policy);
            return Task.CompletedTask;
        }

        public async Task<bool> TryInsertValidatedAsync(
            string tenantId,
            EntitySyncPolicy policy,
            string sourceConnectionId,
            long sourceGeneration,
            string targetConnectionId,
            long targetGeneration,
            CancellationToken cancellationToken)
        {
            await InsertAsync(tenantId, policy, cancellationToken);
            return true;
        }

        public async Task<bool> TryInsertValidatedWithTokenAsync(
            string tenantId,
            EntitySyncPolicy policy,
            string sourceConnectionId,
            long sourceGeneration,
            string targetConnectionId,
            long targetGeneration,
            string idempotencyToken,
            CancellationToken cancellationToken)
        {
            if (!await TryInsertValidatedAsync(
                    tenantId, policy, sourceConnectionId, sourceGeneration,
                    targetConnectionId, targetGeneration, cancellationToken))
                return false;
            byToken.Add((policy.PolicyId, idempotencyToken), policy);
            return true;
        }

        public Task<EntitySyncPolicy?> GetByIdempotencyTokenAsync(
            string tenantId,
            Guid policyId,
            string idempotencyToken,
            CancellationToken cancellationToken) =>
            Task.FromResult(byToken.GetValueOrDefault((policyId, idempotencyToken)));

        public Task<EntitySyncPolicy?> GetAsync(
            string tenantId,
            Guid policyId,
            int version,
            CancellationToken cancellationToken) =>
            Task.FromResult(values.SingleOrDefault(value =>
                value.PolicyId == policyId && value.Version == version));

        public Task<EntitySyncPolicy?> GetLatestAsync(
            string tenantId,
            Guid policyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(values.Where(value => value.PolicyId == policyId)
                .OrderByDescending(value => value.Version).FirstOrDefault());

        public Task<IReadOnlyList<EntitySyncPolicy>> ListLatestAsync(
            string tenantId,
            string? routeScope,
            bool? enabled,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncPolicy>>(
                values.GroupBy(value => value.PolicyId)
                    .Select(group => group.OrderByDescending(value => value.Version).First())
                    .ToArray());

        public void Rotate(EntitySyncPolicy policy) =>
            values.Add(policy.NextVersion(
                new EntitySyncActor("rotator"),
                policy.Definition,
                policy.CreatedAt.AddMinutes(1)));
    }

    private sealed class RecordingExclusionRepository : IEntityExclusionRepository
    {
        public int ListCalls { get; private set; }
        public bool ReturnExclusionOnSecondRead { get; set; }

        public Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(
            EntityExclusionRoute route,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            if (!ReturnExclusionOnSecondRead || ListCalls < 2)
                return Task.FromResult<IReadOnlyList<EntityExclusion>>([]);
            return Task.FromResult<IReadOnlyList<EntityExclusion>>(
                [new EntityExclusion(
                    Guid.NewGuid(), route, "source-0", "Source 0", "excluded", "actor",
                    DateTimeOffset.UtcNow)]);
        }

        public Task<EntityExclusion> AddAsync(
            EntityExclusionRoute route,
            string sourceEntityId,
            string sourceName,
            string reason,
            string actor,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(
            EntityExclusionRoute route,
            string sourceEntityId,
            string actor,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryDurableRepository(
        TimeProvider timeProvider,
        ISyncPolicyRepository policies,
        IConnectionDefinitionRepository connections)
        : IDurableSyncPlanRepository
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<(string TenantId, Guid PlanId), MemoryCreationClaim>
            creationClaims = [];
        private readonly Dictionary<Guid, EntitySyncInspectionSession> sessions = [];
        private readonly Dictionary<Guid, Dictionary<Guid, EntitySyncInspectionRange>> ranges = [];
        private readonly Dictionary<
            (string TenantId, string CallerKey),
            (EntitySyncSha256 RequestSha256, Guid PlanId,
                EntitySyncSha256 PlanDigestSha256)>
            importReceipts = [];
        private EntitySyncDurablePlan? currentPlan;

        public EntitySyncDurablePlanManifest? Manifest { get; private set; }
        public int SessionCount => sessions.Count;
        public int RangeCount => ranges.Values.Sum(value => value.Count);
        public int ApprovalCount { get; private set; }
        public int AuditCount { get; private set; }
        public int RenewalCount { get; private set; }
        public bool LoseOwnershipOnRenewal { get; set; }

        public async Task<DurablePlanCreationClaim> TryClaimCreationAsync(
            string tenantId,
            Guid planId,
            EntitySyncSha256 requestSha256,
            Guid proposedOwnerToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var now = timeProvider.GetUtcNow();
                var key = (tenantId, planId);
                if (!creationClaims.TryGetValue(key, out var stored))
                {
                    stored = new MemoryCreationClaim(
                        requestSha256,
                        proposedOwnerToken,
                        now.Add(leaseDuration),
                        false,
                        null,
                        null);
                    creationClaims.Add(key, stored);
                    return ToClaim(DurablePlanCreationClaimState.Owner, stored);
                }
                if (stored.RequestSha256 != requestSha256)
                    return new DurablePlanCreationClaim(
                        DurablePlanCreationClaimState.Conflict,
                        null,
                        null,
                        null,
                        null);
                if (stored.Completed)
                    return ToClaim(DurablePlanCreationClaimState.Completed, stored);
                if (stored.OwnerToken != proposedOwnerToken && stored.LeaseExpiresAt > now)
                    return ToClaim(DurablePlanCreationClaimState.Waiting, stored);
                stored = stored with
                {
                    OwnerToken = proposedOwnerToken,
                    LeaseExpiresAt = now.Add(leaseDuration)
                };
                creationClaims[key] = stored;
                return ToClaim(DurablePlanCreationClaimState.Owner, stored);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<bool> RenewCreationAsync(
            string tenantId,
            Guid planId,
            EntitySyncSha256 requestSha256,
            Guid ownerToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                RenewalCount++;
                var now = timeProvider.GetUtcNow();
                var key = (tenantId, planId);
                if (!creationClaims.TryGetValue(key, out var stored)
                    || stored.RequestSha256 != requestSha256
                    || stored.OwnerToken != ownerToken
                    || stored.Completed
                    || stored.LeaseExpiresAt <= now)
                    return false;
                if (LoseOwnershipOnRenewal)
                {
                    creationClaims[key] = stored with
                    {
                        OwnerToken = Guid.NewGuid(),
                        LeaseExpiresAt = now.Add(leaseDuration)
                    };
                    return false;
                }
                creationClaims[key] = stored with
                {
                    LeaseExpiresAt = now.Add(leaseDuration)
                };
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task ReleaseCreationAsync(
            string tenantId,
            Guid planId,
            EntitySyncSha256 requestSha256,
            Guid ownerToken,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var key = (tenantId, planId);
                if (creationClaims.TryGetValue(key, out var stored)
                    && stored.RequestSha256 == requestSha256
                    && stored.OwnerToken == ownerToken
                    && !stored.Completed)
                    creationClaims[key] = stored with
                    {
                        LeaseExpiresAt = timeProvider.GetUtcNow()
                    };
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task InsertClaimedAsync(
            string tenantId,
            EntitySyncDurablePlanManifest manifest,
            EntitySyncSha256 requestSha256,
            Guid ownerToken,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var now = timeProvider.GetUtcNow();
                var key = (tenantId, manifest.Plan.PlanId);
                if (!creationClaims.TryGetValue(key, out var stored)
                    || stored.RequestSha256 != requestSha256
                    || stored.OwnerToken != ownerToken
                    || stored.Completed
                    || stored.LeaseExpiresAt <= now
                    || currentPlan?.PlanId == manifest.Plan.PlanId)
                    throw new InvalidOperationException(
                        "Ownership of the durable creation claim was lost before persistence.");
                Manifest = manifest;
                currentPlan = manifest.Plan;
                creationClaims[key] = stored with
                {
                    LeaseExpiresAt = now,
                    Completed = true,
                    ResultPlanId = manifest.Plan.PlanId,
                    ResultPlanDigestSha256 = manifest.Plan.PlanDigestSha256
                };
            }
            finally
            {
                gate.Release();
            }
        }

        public Task InsertAsync(
            string tenantId,
            EntitySyncDurablePlanManifest manifest,
            CancellationToken cancellationToken)
        {
            Manifest = manifest;
            currentPlan = manifest.Plan;
            return Task.CompletedTask;
        }

        public async Task<DurablePlanImportPersistenceResult> ImportAsync(
            string tenantId,
            EntitySyncDurablePlanManifest manifest,
            string callerKey,
            EntitySyncActor actor,
            CancellationToken cancellationToken)
        {
            var requestSha256 = EntitySyncCanonicalDigest.Compute(new
            {
                TenantId = tenantId,
                CallerKey = callerKey,
                PlanId = manifest.Plan.PlanId,
                PlanDigestSha256 = manifest.Plan.PlanDigestSha256.Value,
                ActorId = actor.ActorId
            });
            await gate.WaitAsync(cancellationToken);
            try
            {
                var key = (tenantId, callerKey);
                if (importReceipts.TryGetValue(key, out var receipt))
                {
                    if (receipt.RequestSha256 != requestSha256)
                        return new(
                            DurablePlanImportPersistenceState.Conflict,
                            null);
                    if (currentPlan is null
                        || currentPlan.PlanId != receipt.PlanId
                        || currentPlan.PlanDigestSha256
                            != receipt.PlanDigestSha256)
                        throw new InvalidOperationException(
                            "The durable plan import receipt references a missing or mismatched plan.");
                    return new(
                        DurablePlanImportPersistenceState.Replayed,
                        currentPlan);
                }
                var plan = manifest.Plan;
                if (plan.ExpiresAt <= timeProvider.GetUtcNow())
                    return new(
                        DurablePlanImportPersistenceState.Expired,
                        null);
                var policy = await policies.GetLatestAsync(
                    tenantId, plan.PolicyId, cancellationToken);
                if (policy is null
                    || !policy.Enabled
                    || policy.Version != plan.PolicyVersion
                    || policy.DefinitionSha256
                        != plan.PolicyDefinitionSha256
                    || policy.RouteScope != plan.RouteScope
                    || policy.Definition.SourceConnectionId
                        != plan.SourceConnectionId
                    || policy.Definition.TargetConnectionId
                        != plan.TargetConnectionId)
                    return new(
                        DurablePlanImportPersistenceState.PolicyChanged,
                        null);
                var source = await connections.GetAsync(
                    tenantId, plan.SourceConnectionId, cancellationToken);
                var target = await connections.GetAsync(
                    tenantId, plan.TargetConnectionId, cancellationToken);
                if (source is null
                    || !source.Enabled
                    || source.Generation
                        != plan.SourceConnectionGeneration
                    || target is null
                    || !target.Enabled
                    || target.Generation
                        != plan.TargetConnectionGeneration)
                    return new(
                        DurablePlanImportPersistenceState.ConnectionChanged,
                        null);
                if (currentPlan?.PlanId == plan.PlanId
                    && currentPlan.PlanDigestSha256
                        != plan.PlanDigestSha256)
                    return new(
                        DurablePlanImportPersistenceState.Conflict,
                        null);
                var inserted = currentPlan?.PlanId != plan.PlanId;
                if (inserted)
                {
                    Manifest = manifest;
                    currentPlan = plan;
                }
                importReceipts.Add(
                    key,
                    (requestSha256, plan.PlanId, plan.PlanDigestSha256));
                return new(
                    inserted
                        ? DurablePlanImportPersistenceState.Inserted
                        : DurablePlanImportPersistenceState.Replayed,
                    currentPlan);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<EntitySyncDurablePlan?> GetAsync(
            string tenantId,
            Guid planId,
            CancellationToken cancellationToken) =>
            Task.FromResult(currentPlan is { PlanId: var id } && id == planId ? currentPlan : null);

        public Task<EntitySyncDurablePlanPage> GetPageAsync(
            string tenantId,
            Guid planId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Manifest!.Items.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
            return Task.FromResult(new EntitySyncDurablePlanPage(
                tenantId, planId, page, pageSize, Manifest.Items.Count, items));
        }

        public async Task<EntitySyncInspectionSession> GetOrOpenInspectionAsync(
            string tenantId,
            Guid proposedInspectionId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            string sourceConnectionId,
            long sourceConnectionGeneration,
            string targetConnectionId,
            long targetConnectionGeneration,
            EntitySyncActor actor,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var existing = sessions.Values.FirstOrDefault(session =>
                    session.PlanId == planId
                    && session.PlanDigestSha256 == planDigestSha256
                    && session.InspectedBy == actor);
                if (existing is not null) return existing;
                var created = new EntitySyncInspectionSession(
                    tenantId, proposedInspectionId, planId, planDigestSha256,
                    sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
                    targetConnectionGeneration, EntitySyncInspectionStatus.Open,
                    now, actor, null);
                sessions.Add(created.InspectionId, created);
                ranges.Add(created.InspectionId, []);
                return created;
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<EntitySyncInspectionSession?> FindInspectionAsync(
            string tenantId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            EntitySyncActor actor,
            CancellationToken cancellationToken) =>
            Task.FromResult(sessions.Values.FirstOrDefault(session =>
                session.PlanId == planId
                && session.PlanDigestSha256 == planDigestSha256
                && session.InspectedBy == actor));

        public Task<IReadOnlyList<EntitySyncInspectionRange>> ListInspectionRangesAsync(
            string tenantId,
            Guid inspectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncInspectionRange>>(
                ranges[inspectionId].Values.OrderBy(value => value.RangeStart).ToArray());

        public async Task<EntitySyncInspectionRange> RecordInspectionRangeAsync(
            string tenantId,
            Guid inspectionId,
            Guid rangeId,
            int rangeStart,
            int rangeEnd,
            DateTimeOffset inspectedAt,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var value = new EntitySyncInspectionRange(
                    tenantId, inspectionId, rangeId, rangeStart, rangeEnd, inspectedAt);
                if (ranges[inspectionId].TryGetValue(rangeId, out var existing))
                {
                    if (existing.RangeStart != rangeStart || existing.RangeEnd != rangeEnd)
                        throw new InvalidOperationException("Range identity conflict.");
                    return existing;
                }
                ranges[inspectionId].Add(rangeId, value);
                return value;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<EntitySyncInspectionSession> CompleteInspectionAsync(
            string tenantId,
            Guid inspectionId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            string sourceConnectionId,
            long sourceConnectionGeneration,
            string targetConnectionId,
            long targetConnectionGeneration,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var session = sessions[inspectionId];
                if (session.Status == EntitySyncInspectionStatus.Completed) return session;
                var next = 0;
                foreach (var range in ranges[inspectionId].Values
                             .OrderBy(value => value.RangeStart)
                             .ThenBy(value => value.RangeEnd))
                {
                    if (range.RangeStart > next) throw new InvalidOperationException("Coverage gap.");
                    next = Math.Max(next, range.RangeEnd + 1);
                }
                if (next != Manifest!.Items.Count) throw new InvalidOperationException("Coverage incomplete.");
                session = session.Complete(completedAt);
                sessions[inspectionId] = session;
                return session;
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<bool> HasCompleteInspectionAsync(
            string tenantId,
            Guid inspectionId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            string sourceConnectionId,
            long sourceConnectionGeneration,
            string targetConnectionId,
            long targetConnectionGeneration,
            CancellationToken cancellationToken) =>
            Task.FromResult(sessions.TryGetValue(inspectionId, out var session)
                && session.Status == EntitySyncInspectionStatus.Completed);

        public async Task<EntitySyncApproval> ApproveInspectionAsync(
            string tenantId,
            Guid approvalId,
            Guid inspectionId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            string sourceConnectionId,
            long sourceConnectionGeneration,
            string targetConnectionId,
            long targetConnectionGeneration,
            EntitySyncActor actor,
            DateTimeOffset approvedAt,
            DateTimeOffset? expiresAt,
            EntitySyncAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (currentPlan?.Status != EntitySyncDurablePlanStatus.Draft
                    || sessions[inspectionId].Status != EntitySyncInspectionStatus.Completed)
                    throw new InvalidOperationException("Approval conflict.");
                var approval = new EntitySyncApproval(
                    tenantId, approvalId, inspectionId, planId, planDigestSha256,
                    sourceConnectionId, sourceConnectionGeneration, targetConnectionId,
                    targetConnectionGeneration, approvedAt, actor, expiresAt);
                currentPlan = currentPlan.TransitionTo(EntitySyncDurablePlanStatus.Approved);
                ApprovalCount++;
                AuditCount++;
                return approval;
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<bool> TryConsumeApprovalAsync(
            string tenantId,
            Guid approvalId,
            Guid inspectionId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            string sourceConnectionId,
            long sourceConnectionGeneration,
            string targetConnectionId,
            long targetConnectionGeneration,
            EntitySyncOperation applyOperation,
            IReadOnlyList<EntitySyncOperationItem> operationItems,
            DateTimeOffset now,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TryExpireAsync(
            string tenantId,
            Guid planId,
            EntitySyncSha256 planDigestSha256,
            EntitySyncDurablePlanStatus expectedStatus,
            DateTimeOffset now,
            CancellationToken cancellationToken) => Task.FromResult(false);
        private static DurablePlanCreationClaim ToClaim(
            DurablePlanCreationClaimState state,
            MemoryCreationClaim claim) =>
            new(
                state,
                claim.OwnerToken,
                claim.LeaseExpiresAt,
                claim.ResultPlanId,
                claim.ResultPlanDigestSha256);

        private sealed record MemoryCreationClaim(
            EntitySyncSha256 RequestSha256,
            Guid OwnerToken,
            DateTimeOffset LeaseExpiresAt,
            bool Completed,
            Guid? ResultPlanId,
            EntitySyncSha256? ResultPlanDigestSha256);

    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset utcNow = now;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}

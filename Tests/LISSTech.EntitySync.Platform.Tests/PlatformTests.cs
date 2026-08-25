using System.Security.Claims;
using System.Net;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void ConnectionsArePartitionedByTenantAndConnectionId()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        repository.Register("tenant-a", "primary", new FakeAdapter("HaloPSA"));
        repository.Register("tenant-b", "primary", new FakeAdapter("HaloPSA"));
        repository.Register("tenant-a", "secondary", new FakeAdapter("HaloPSA"));

        Assert.Equal(2, repository.List("tenant-a").Count);
        Assert.Single(repository.List("tenant-b"));
        Assert.Equal("secondary", repository.Resolve("tenant-a", "HaloPSA", "secondary").Id);
        Assert.Throws<InvalidOperationException>(() => repository.Resolve("tenant-a", "HaloPSA"));
    }

    [Fact]
    public void ReplacingConnectionIncrementsGenerationAndDisposesOldAdapter()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var oldAdapter = new FakeAdapter("HaloPSA");
        var first = repository.Register("tenant", "halo", oldAdapter);
        var second = repository.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.True(oldAdapter.Disposed);
    }

    [Fact]
    public void ReplacingLeasedConnectionDefersDisposalUntilLeaseEnds()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var oldAdapter = new FakeAdapter("HaloPSA");
        var first = repository.Register("tenant", "halo", oldAdapter);
        using var lease = repository.Acquire("tenant", "HaloPSA", "halo", first.Generation);

        repository.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        Assert.Same(oldAdapter, lease.Connection.Adapter);
        Assert.False(oldAdapter.Disposed);
        lease.Dispose();
        Assert.True(oldAdapter.Disposed);
    }

    [Fact]
    public async Task ApprovedPlanIsAppliedOnlyOnce()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var source = new FakeAdapter("NetSuite", [Source("1", "Acme")]);
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "netsuite", source);
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections);

        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, target.CreateCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public async Task ApplyReportsAggregateProgressAfterEveryProcessedItem()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme"), Source("2", "Beta")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        InspectAllAndApprove(service, plan);
        var progress = new List<EntitySyncApplyProgress>();

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None, progress.Add);

        Assert.True(result.Success);
        Assert.Equal([1, 2], progress.Select(item => item.Processed));
        Assert.All(progress, item => Assert.Equal(2, item.Total));
        Assert.Equal(2, progress[^1].Succeeded);
        Assert.Equal(0, progress[^1].Failed);
        Assert.Equal(0, progress[^1].Skipped);
    }

    [Fact]
    public async Task ThrowingProgressCallbackDoesNotDuplicateProcessedItem()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections, plans);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        InspectAllAndApprove(service, plan);
        var progress = new List<EntitySyncApplyProgress>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None, item =>
            {
                progress.Add(item);
                throw new InvalidOperationException("Progress callback failed.");
            }));

        Assert.Equal("Progress callback failed.", error.Message);
        Assert.Equal(1, target.CreateCalls);
        Assert.Single(progress);
        Assert.Equal(1, progress[0].Processed);
        Assert.Equal(EntitySyncPlanStatuses.Failed, plans.Get("tenant", plan.Id).Status);
    }

    [Fact]
    public async Task ApplyRejectsConnectionReplacedAfterPlanning()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyKeepsUsingPinnedConnectionWhenItIsReplacedDuringWrite()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTarget = new FakeAdapter("HaloPSA", beforeCreate: async () =>
        {
            writeStarted.SetResult();
            await continueWrite.Task;
        });
        var newTarget = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", oldTarget);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        var applyTask = service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);
        await writeStarted.Task;
        connections.Register("tenant", "halo", newTarget);

        Assert.False(oldTarget.Disposed);
        continueWrite.SetResult();
        var result = await applyTask;
        Assert.True(result.Success);
        Assert.Equal(1, oldTarget.CreateCalls);
        Assert.Equal(0, newTarget.CreateCalls);
        Assert.True(oldTarget.Disposed);
    }

    [Fact]
    public async Task CancelledApplyMovesPlanToFailedTerminalState()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA", beforeCreate: () => Task.FromException(new OperationCanceledException())));
        var service = CreateService(connections, plans);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));

        Assert.Equal(EntitySyncPlanStatuses.Failed, plans.Get("tenant", plan.Id).Status);
    }

    [Fact]
    public async Task PlanInspectionIsCompleteAndPaginated()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 60).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);

        var first = service.GetPlan("tenant", plan.Id, 1, 25);
        var last = service.GetPlan("tenant", plan.Id, 3, 25);

        Assert.Equal(60, first.TotalItems);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(10, last.Items.Count);
        Assert.Equal(first.Digest, last.Digest);
    }

    [Theory]
    [InlineData("NCentral")]
    [InlineData("Bill.com")]
    public async Task ApplicationPlannerRejectsFlowsThatRequireUnavailableSourceWriteBack(string targetVendor)
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var service = CreateService(connections);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            TargetVendor = targetVendor
        }, CancellationToken.None));

        Assert.Contains("source integration-link writeback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApprovalRequiresInspectionOfEveryPlanItem()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 60).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);

        var first = service.GetPlan("tenant", plan.Id, 1, 25);
        Assert.Throws<InvalidOperationException>(() => service.ApprovePlan("tenant", plan.Id, first.Digest));
        service.GetPlan("tenant", plan.Id, 2, 25);
        service.GetPlan("tenant", plan.Id, 3, 25);

        Assert.Equal(first.Digest, service.ApprovePlan("tenant", plan.Id, first.Digest));
    }

    [Fact]
    public void PlanRepositoryReturnsSnapshotsInsteadOfStoredMutableInstances()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            Items = [new EntitySyncPlanItem { Action = "Create", Source = Source("1", "Acme") }]
        };
        repository.Add(plan);

        plan.Items.Clear();
        var firstRead = repository.Get("tenant", plan.Id);
        firstRead.Items[0].Source.Name = "Changed";
        firstRead.Status = EntitySyncPlanStatuses.Approved;
        var secondRead = repository.Get("tenant", plan.Id);

        Assert.Single(secondRead.Items);
        Assert.Equal("Acme", secondRead.Items[0].Source.Name);
        Assert.Equal(EntitySyncPlanStatuses.Draft, secondRead.Status);
    }

    [Fact]
    public void ExpiredPlansAreEvicted()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan { TenantId = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        repository.Add(plan);

        Assert.Throws<InvalidOperationException>(() => repository.Get("tenant", plan.Id));
        Assert.Throws<KeyNotFoundException>(() => repository.Get("tenant", plan.Id));
    }

    [Fact]
    public void ApplyingPlanCanReachTerminalStateAfterExpiration()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var plan = new EntitySyncPlan { TenantId = "tenant", ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(100) };
        repository.Add(plan);
        Assert.True(repository.TryTransition("tenant", plan.Id, EntitySyncPlanStatuses.Draft, EntitySyncPlanStatuses.Applying));
        Thread.Sleep(200);

        Assert.True(repository.TryTransition("tenant", plan.Id, EntitySyncPlanStatuses.Applying, EntitySyncPlanStatuses.Applied));
        Assert.Throws<InvalidOperationException>(() => repository.Get("tenant", plan.Id));
    }

    [Fact]
    public void PlanSnapshotsPreserveCaseInsensitiveEntityFields()
    {
        var repository = new InMemoryEntitySyncPlanRepository();
        var source = Source("1", "Acme");
        source.ExternalIds.Clear();
        source.ExternalIds["mixedCaseId"] = "42";
        source.CustomFields["mixedCaseField"] = "value";
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            Items = [new EntitySyncPlanItem { Action = "Create", Source = source }]
        };
        repository.Add(plan);

        var snapshot = repository.Get("tenant", plan.Id).Items[0].Source;

        Assert.Equal("42", snapshot.GetExternalId("MIXEDCASEID"));
        Assert.Equal("value", snapshot.GetCustomField("MIXEDCASEFIELD"));
    }

    [Fact]
    public async Task PlanningRejectsUnboundedEntitySets()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var sources = Enumerable.Range(1, 5001).Select(index => Source(index.ToString(), $"Customer {index}")).ToArray();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", sources));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(Request(), CancellationToken.None));

        Assert.Contains("limited to 5000", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpFocusedPlanUsesBoundedQueryAndExactSourceId()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var source = new FakeAdapter("NetSuite", [Source("1815", "Other"), Source("1816", "Degmor Enterprises Inc")]);
        connections.Register("tenant", "netsuite", source);
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections);

        var response = await SyncTools.CreateSyncPlan(
            service,
            new McpRequestContext("tenant", false),
            "NetSuite",
            "HaloPSA",
            sourceConnectionId: "netsuite",
            targetConnectionId: "halo",
            sourceEntityType: "Customer",
            targetEntityType: "Client",
            createMissing: true,
            sourceSearch: "Degmor",
            sourceCount: 10,
            sourceEntityId: "1816",
            cancellationToken: CancellationToken.None);

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("totalItems").GetInt32());
        Assert.Equal("1816", root.GetProperty("items")[0].GetProperty("sourceId").GetString());
        Assert.Equal("Degmor Enterprises Inc", root.GetProperty("items")[0].GetProperty("source").GetString());
        Assert.Equal("1816", root.GetProperty("sourceSelection").GetProperty("entityId").GetString());
        Assert.Equal("Degmor", source.LastQuery?.Search);
        Assert.Equal(10, source.LastQuery?.Count);
    }

    [Fact]
    public async Task FocusedPlanRejectsSourceIdOutsideBoundedQueryBeforeReadingTargets()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1815", "Other")]));
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreatePlanAsync(Request("Degmor", 10, "1816"), CancellationToken.None));

        Assert.Contains("Source entity ID '1816'", error.Message, StringComparison.Ordinal);
        Assert.Null(target.LastQuery);
    }

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceAdaptersRuntimeOrPowerShell()
    {
        var references = typeof(EntitySyncService).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("LISSTech.EntitySync.Adapters", references);
        Assert.DoesNotContain("LISSTech.EntitySync.Runtime", references);
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void CoreAssemblyHasNoFirstPartyOrPowerShellDependencies()
    {
        var references = typeof(EntitySyncPlan).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, reference => reference.StartsWith("LISSTech.EntitySync.", StringComparison.Ordinal));
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void ReviewedPlansRejectUnapprovedExecutableItems()
    {
        var plan = new EntitySyncPlan
        {
            ReviewRequired = true,
            Items =
            [
                new EntitySyncPlanItem
                {
                    Action = "Create",
                    Status = "Planned",
                    Source = Source("1", "Acme")
                }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => ReviewedPlanPolicy.EnsureApproved(plan));
        plan.Items[0].Status = "Accepted";
        ReviewedPlanPolicy.EnsureApproved(plan);
    }

    [Fact]
    public void ImportedExecutableStatusesMustBeReviewedAgain()
    {
        var plan = new EntitySyncPlan
        {
            Items =
            [
                new EntitySyncPlanItem
                {
                    Action = "Create",
                    Status = "Accepted",
                    Source = Source("1", "Acme")
                }
            ]
        };

        ReviewedPlanPolicy.PrepareForReview(plan);

        Assert.True(plan.ReviewRequired);
        Assert.Equal("Planned", plan.Items[0].Status);
        Assert.Throws<InvalidOperationException>(() => ReviewedPlanPolicy.EnsureApproved(plan));
    }

    [Fact]
    public void McpConnectionToolDoesNotExposeEndpointsOrSecrets()
    {
        var parameters = typeof(ConnectionTools).GetMethod(nameof(ConnectionTools.ConnectVendor))!
            .GetParameters()
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(parameters, name => name.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameters, name => name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(parameters, name => name.Contains("token", StringComparison.OrdinalIgnoreCase) && !name.Equals("cancellationToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void McpAssemblyDoesNotReferencePowerShellHost()
    {
        var references = typeof(ConnectionTools).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("LISSTech.EntitySync", references);
        Assert.DoesNotContain("System.Management.Automation", references);
    }

    [Fact]
    public void McpExposesInspectApproveAndApplyWorkflow()
    {
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.GetSyncPlan)));
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.ApproveSyncPlan)));
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.ApplySyncPlan)));
        Assert.NotNull(typeof(SyncTools).GetMethod(nameof(SyncTools.GetSyncPlanApply)));
    }

    [Fact]
    public async Task McpApplyStartsAndPollsAfterRequestCancellation()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var plans = new InMemoryEntitySyncPlanRepository();
        var writeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new FakeAdapter("HaloPSA", beforeCreate: async () =>
        {
            writeStarted.TrySetResult(true);
            await releaseWrite.Task;
        });
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", target);
        var service = CreateService(connections, plans);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        InspectAllAndApprove(service, plan);
        var coordinator = new EntitySyncApplyCoordinator(service, plans, new TestApplicationLifetime());
        var context = new McpRequestContext("tenant", true);
        using var requestCancellation = new CancellationTokenSource();

        try
        {
            var startResponse = await SyncTools.ApplySyncPlan(
                    service,
                    coordinator,
                    context,
                    plan.Id,
                    apply: true,
                    cancellationToken: requestCancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var startJson = JsonDocument.Parse(startResponse);

            Assert.True(startJson.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Applying", startJson.RootElement.GetProperty("snapshot").GetProperty("status").GetString());
            Assert.Equal(0, target.CreateCalls);

            requestCancellation.Cancel();
        }
        finally
        {
            releaseWrite.TrySetResult(true);
        }

        JsonElement snapshot;
        using var pollTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            using var pollJson = JsonDocument.Parse(SyncTools.GetSyncPlanApply(coordinator, context, plan.Id));
            Assert.True(pollJson.RootElement.GetProperty("success").GetBoolean());
            snapshot = pollJson.RootElement.GetProperty("snapshot").Clone();
            if (snapshot.GetProperty("status").GetString() is "Applied" or "Failed") break;
            await Task.Delay(10, pollTimeout.Token);
        }
        while (true);

        Assert.Equal("Applied", snapshot.GetProperty("status").GetString());
        Assert.Equal(1, snapshot.GetProperty("total").GetInt32());
        Assert.Equal(1, snapshot.GetProperty("processed").GetInt32());
        Assert.Equal(1, snapshot.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("failed").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("skipped").GetInt32());
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public void HttpMcpContextUsesAuthenticatedOAuthSubjectAsTenant()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://issuer.example.test/"),
                new Claim("sub", "oauth-subject")
            ], "Bearer"))
        };
        var context = new McpRequestContext(new HttpContextAccessor { HttpContext = httpContext });

        Assert.Equal("https://issuer.example.test::oauth-subject", context.TenantId);
        Assert.Equal("https://issuer.example.test::oauth-subject", context.Actor);
        Assert.False(context.AllowProfiles);
    }

    [Fact]
    public void HttpMcpContextRejectsMissingOAuthSubject()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Bearer"))
        };
        var context = new McpRequestContext(new HttpContextAccessor { HttpContext = httpContext });

        var exception = Assert.Throws<InvalidOperationException>(() => context.TenantId);
        Assert.Contains("'sub' claim", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthChallengeHintsExposeExplicitEndpointsAndPublicClient()
    {
        var hints = OAuthChallengeHints.Create(
            "https://mcp.example.test/mcp",
            "https://login.example.test/tenant/oauth2/v2.0/authorize",
            "https://login.example.test/tenant/oauth2/v2.0/token",
            "public-client-id",
            ["api://entitysync/mcp.tools", "offline_access"]);

        var challenges = hints!.Append(
            ["Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\""]);

        Assert.Equal(
            "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\", authorization_endpoint=\"https://login.example.test/tenant/oauth2/v2.0/authorize\", token_endpoint=\"https://login.example.test/tenant/oauth2/v2.0/token\", client_id=\"public-client-id\", scope=\"api://entitysync/mcp.tools offline_access\"",
            Assert.Single(challenges));
    }

    [Fact]
    public void OAuthChallengePinsHttpsResourceMetadataBehindReverseProxy()
    {
        var hints = OAuthChallengeHints.Create(
            "https://mcp.example.test/mcp",
            null,
            null,
            null,
            ["mcp.tools"]);

        var challenges = hints.Append(
            ["Bearer resource_metadata=\"http://mcp.example.test/.well-known/oauth-protected-resource/mcp\""]);

        Assert.Equal(
            "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(challenges));
    }

    [Fact]
    public void OAuthChallengeHintsRequireCompleteSafeConfiguration()
    {
        var hints = OAuthChallengeHints.Create("https://mcp.example.test/mcp", null, null, null, ["mcp.tools"]);
        Assert.Equal(
            "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"",
            Assert.Single(hints.Append(
                ["Bearer resource_metadata=\"http://internal/.well-known/oauth-protected-resource/mcp\""])));

        var partial = Assert.Throws<InvalidOperationException>(
            () => OAuthChallengeHints.Create(
                "https://mcp.example.test/mcp",
                "https://login.example.test/authorize",
                null,
                "public-client-id",
                ["mcp.tools"]));
        Assert.Contains("must be configured together", partial.Message, StringComparison.Ordinal);

        var unsafeClient = Assert.Throws<InvalidOperationException>(
            () => OAuthChallengeHints.Create(
                "https://mcp.example.test/mcp",
                "https://login.example.test/authorize",
                "https://login.example.test/token",
                "client\r\ninjected",
                ["mcp.tools"]));
        Assert.Contains("cannot be emitted safely", unsafeClient.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthChallengeHintsLeaveUnrelatedChallengesUnchanged()
    {
        var hints = OAuthChallengeHints.Create(
            "https://mcp.example.test/mcp",
            "https://login.example.test/authorize",
            "https://login.example.test/token",
            "public-client-id",
            ["mcp.tools"]);
        string[] original =
        [
            "Basic realm=\"legacy\"",
            "Bearer realm=\"api\""
        ];

        Assert.Equal(original, hints!.Append(original));
    }

    [Fact]
    public async Task AgentControllerProviderUsesExactClientCredentialsAndExchangeContracts()
    {
        using var handler = new RecordingHttpMessageHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"entra-access-token","expires_in":3600}"""),
            1 => JsonResponse(
                HttpStatusCode.OK,
                """{"token_type":"Bearer","access_token":"ltac-access-token","expires_in":900,"ops_base_url":"https://ops.example.test/","subject":"entitysync","role":"api_operator","customer_slugs":[],"scope":"customer_scope_sync:write"}"""),
            _ => throw new InvalidOperationException("Unexpected AgentController token request.")
        });
        using var provider = new AgentControllerTokenProvider(
            new AgentControllerProviderConfiguration(
                "https://auth.example.test/",
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                "client-secret",
                "api://agent-controller/.default",
                AgentControllerTokenProvider.DefaultInternalScope,
                AgentControllerTokenProvider.DefaultExchangePath),
            handler);

        var exchange = await provider.AcquireAsync(CancellationToken.None);

        Assert.Equal("ltac-access-token", exchange.AccessToken);
        Assert.Equal(900, exchange.ExpiresInSeconds);
        Assert.Equal("https://ops.example.test/", exchange.OpsBaseUrl.AbsoluteUri);
        Assert.Equal("customer_scope_sync:write", exchange.InternalScope);
        Assert.Equal(2, handler.Requests.Count);

        var entraRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, entraRequest.Method);
        Assert.Equal(
            "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/v2.0/token",
            entraRequest.Uri.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", entraRequest.ContentType);
        var form = ParseForm(entraRequest.Body);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("22222222-2222-2222-2222-222222222222", form["client_id"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal("api://agent-controller/.default", form["scope"]);

        var exchangeRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, exchangeRequest.Method);
        Assert.Equal(
            "https://auth.example.test/v1/operator-token/exchange",
            exchangeRequest.Uri.AbsoluteUri);
        Assert.Equal("application/json", exchangeRequest.ContentType);
        using var payload = JsonDocument.Parse(exchangeRequest.Body);
        Assert.Equal(2, payload.RootElement.EnumerateObject().Count());
        Assert.Equal(
            "entra-access-token",
            payload.RootElement.GetProperty("entra_access_token").GetString());
        Assert.Equal(
            0,
            payload.RootElement.GetProperty("requested_customer_slugs").GetArrayLength());
        Assert.False(payload.RootElement.TryGetProperty("requested_scope", out _));
    }

    [Fact]
    public async Task AgentControllerProviderErrorsDoNotDiscloseCredentialsOrTokens()
    {
        const string clientSecret = "client-secret-do-not-disclose";
        const string entraToken = "entra-token-do-not-disclose";
        using var handler = new RecordingHttpMessageHandler((_, index) => index switch
        {
            0 => JsonResponse(
                HttpStatusCode.OK,
                $$"""{"access_token":"{{entraToken}}","expires_in":3600}"""),
            1 => JsonResponse(
                HttpStatusCode.Unauthorized,
                $$"""{"message":"{{clientSecret}} {{entraToken}}"}"""),
            _ => throw new InvalidOperationException("Unexpected AgentController token request.")
        });
        using var provider = new AgentControllerTokenProvider(
            new AgentControllerProviderConfiguration(
                "https://auth.example.test/",
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                clientSecret,
                "api://agent-controller/.default",
                AgentControllerTokenProvider.DefaultInternalScope,
                AgentControllerTokenProvider.DefaultExchangePath),
            handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.AcquireAsync(CancellationToken.None));

        Assert.DoesNotContain(clientSecret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(entraToken, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(clientSecret, provider.Configuration.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":"PGRST301","message":"expired","details":null,"hint":null}""")]
    [InlineData(HttpStatusCode.OK, "false")]
    public async Task AgentControllerConnectionUsesExchangeOpsUrlAndRefreshesRejectedToken(
        HttpStatusCode firstProbeStatus,
        string firstProbeBody)
    {
        using var handler = new RecordingHttpMessageHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"entra-token-one","expires_in":3600}"""),
            1 => JsonResponse(
                HttpStatusCode.OK,
                """{"token_type":"Bearer","access_token":"ltac-token-one","expires_in":900,"ops_base_url":"https://ops.example.test/","subject":"entitysync","role":"api_operator","customer_slugs":[],"scope":"customer_scope_sync:write"}"""),
            2 => JsonResponse(firstProbeStatus, firstProbeBody),
            3 => JsonResponse(HttpStatusCode.OK, """{"access_token":"entra-token-two","expires_in":3600}"""),
            4 => JsonResponse(
                HttpStatusCode.OK,
                """{"token_type":"Bearer","access_token":"ltac-token-two","expires_in":900,"ops_base_url":"https://ops.example.test/","subject":"entitysync","role":"api_operator","customer_slugs":[],"scope":"customer_scope_sync:write"}"""),
            5 => JsonResponse(HttpStatusCode.OK, "true"),
            _ => throw new InvalidOperationException("Unexpected AgentController request.")
        });
        var environment = new Dictionary<string, string?>
        {
            ["AGENTCONTROLLER_AUTH_BASE_URL"] = "https://auth.example.test/",
            ["AGENTCONTROLLER_ENTRA_TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
            ["AGENTCONTROLLER_ENTRA_CLIENT_ID"] = "22222222-2222-2222-2222-222222222222",
            ["AGENTCONTROLLER_ENTRA_CLIENT_SECRET"] = "client-secret",
            ["AGENTCONTROLLER_ENTRA_SCOPE"] = "api://agent-controller/.default"
        };
        using var adapterHttpClient = new HttpClient(handler, disposeHandler: false);
        var adapter = await ConnectionTools.ConnectAgentControllerAsync(
            environment,
            configuration => new AgentControllerTokenProvider(configuration, handler),
            options => new LTACEntityAdapter(options, adapterHttpClient),
            CancellationToken.None);

        try
        {
            Assert.True(await adapter.TestConnectionAsync(CancellationToken.None));
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }

        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(
            "https://ops.example.test/rpc/has_scope",
            handler.Requests[2].Uri.AbsoluteUri);
        Assert.Equal(
            "https://ops.example.test/rpc/has_scope",
            handler.Requests[5].Uri.AbsoluteUri);
        Assert.Equal("Bearer", handler.Requests[2].AuthorizationScheme);
        Assert.Equal("ltac-token-one", handler.Requests[2].AuthorizationParameter);
        Assert.Equal("Bearer", handler.Requests[5].AuthorizationScheme);
        Assert.Equal("ltac-token-two", handler.Requests[5].AuthorizationParameter);
        using var hasScopePayload = JsonDocument.Parse(handler.Requests[5].Body);
        Assert.Equal(
            "customer_scope_sync:write",
            hasScopePayload.RootElement.GetProperty("p_scope").GetString());
    }

    [Fact]
    public void AgentControllerEnvironmentValidationDoesNotDiscloseSecret()
    {
        const string secret = "never-disclose-this-secret";
        var environment = new Dictionary<string, string?>
        {
            ["AGENTCONTROLLER_AUTH_BASE_URL"] = "http://auth.example.test/",
            ["AGENTCONTROLLER_ENTRA_TENANT_ID"] = "11111111-1111-1111-1111-111111111111",
            ["AGENTCONTROLLER_ENTRA_CLIENT_ID"] = "22222222-2222-2222-2222-222222222222",
            ["AGENTCONTROLLER_ENTRA_CLIENT_SECRET"] = secret,
            ["AGENTCONTROLLER_ENTRA_SCOPE"] = "api://agent-controller/.default"
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => AgentControllerTokenProvider.FromEnvironment(environment));

        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateMissingMarksPersistentlyExcludedSourcesAsNonExecutable()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme"), Source("2", "Ignored")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var exclusions = new InMemoryEntityExclusionRepository();
        var route = EntityExclusionRoute.Create("tenant", "NetSuite", "netsuite", "Customer", "HaloPSA", "halo", "Client");
        await exclusions.AddAsync(route, "2", "Ignored", "Not a managed client", "operator", CancellationToken.None);
        var service = CreateService(connections, exclusions: exclusions);

        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);

        Assert.Equal("Create", plan.Items.Single(item => item.Source.Id == "1").Action);
        var excluded = plan.Items.Single(item => item.Source.Id == "2");
        Assert.Equal("None", excluded.Action);
        Assert.Equal("PersistentExclusion", excluded.MatchType);
        Assert.Equal("Excluded", excluded.Status);
        Assert.Contains("Not a managed client", excluded.Reasons.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyExclusionPolicyAllowsCreateMissing()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));

        var plan = await CreateService(connections).CreatePlanAsync(Request(), CancellationToken.None);

        Assert.Equal("Create", Assert.Single(plan.Items).Action);
    }

    [Fact]
    public async Task CreateMissingFailsClosedWhenExclusionsCannotBeRead()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "halo", new FakeAdapter("HaloPSA"));
        var service = CreateService(connections, exclusions: new FailingEntityExclusionRepository());

        var error = await Assert.ThrowsAsync<EntityExclusionUnavailableException>(
            () => service.CreatePlanAsync(Request(), CancellationToken.None));

        Assert.Equal("Permanent exclusions could not be obtained; create-missing planning is blocked.", error.Message);
        Assert.Equal("Exclusion storage unavailable.", error.InnerException?.Message);
    }

    [Fact]
    public async Task ApplyFailsClosedWhenExclusionsCannotBeRevalidated()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "halo", target);
        var exclusions = new ToggleEntityExclusionRepository();
        var service = CreateService(connections, exclusions: exclusions);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        exclusions.FailReads = true;

        var error = await Assert.ThrowsAsync<EntityExclusionUnavailableException>(
            () => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));

        Assert.Equal("Permanent exclusions could not be obtained; create actions are blocked.", error.Message);
        Assert.Equal("Exclusion storage unavailable.", error.InnerException?.Message);
        Assert.Equal(0, target.CreateCalls);
    }

    [Fact]
    public async Task ApplyRejectsAPlanWhenSourceWasExcludedAfterPlanning()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        var target = new FakeAdapter("HaloPSA");
        connections.Register("tenant", "halo", target);
        var exclusions = new InMemoryEntityExclusionRepository();
        var service = CreateService(connections, exclusions: exclusions);
        var plan = await service.CreatePlanAsync(Request(), CancellationToken.None);
        var inspected = service.GetPlan("tenant", plan.Id);
        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
        var route = EntityExclusionRoute.Create("tenant", "NetSuite", "netsuite", "Customer", "HaloPSA", "halo", "Client");
        await exclusions.AddAsync(route, "1", "Acme", "Excluded after review", "operator", CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));

        Assert.Contains("changed after planning", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, target.CreateCalls);
    }

    [Fact]
    public async Task AgentControllerRoutesRejectPermanentExclusions()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register("tenant", "ncentral", new FakeAdapter("NCentral"));
        connections.Register("tenant", "agentcontroller", new FakeAdapter("AgentController"));
        var service = new EntityExclusionService(connections, new InMemoryEntityExclusionRepository());
        var request = new EntityExclusionRouteRequest
        {
            TenantId = "tenant",
            SourceVendor = "NCentral",
            SourceConnectionId = "ncentral",
            SourceEntityType = "CustomerScope",
            TargetVendor = "AgentController",
            TargetConnectionId = "agentcontroller",
            TargetEntityType = "Customer"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddAsync(request, "1", "Acme", "Ignore", "operator", CancellationToken.None));

        Assert.Contains("does not permit exclusions", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static IReadOnlyDictionary<string, string> ParseForm(string body)
    {
        return body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
                StringComparer.Ordinal);
    }

    private sealed record RecordedHttpRequest(
        HttpMethod Method,
        Uri Uri,
        string Body,
        string? ContentType,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    private sealed class RecordingHttpMessageHandler(
        Func<RecordedHttpRequest, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedHttpRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedHttpRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI is required."),
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter);
            Requests.Add(recorded);
            return responder(recorded, Requests.Count - 1);
        }
    }

    private static void InspectAllAndApprove(EntitySyncService service, EntitySyncPlan plan)
    {
        var page = 1;
        EntitySyncPlanPage inspected;
        do
        {
            inspected = service.GetPlan("tenant", plan.Id, page, 100);
            page++;
        }
        while ((page - 1) * 100 < inspected.TotalItems);

        service.ApprovePlan("tenant", plan.Id, inspected.Digest);
    }

    private static EntitySyncService CreateService(
        IEntityConnectionRepository connections,
        IEntitySyncPlanRepository? plans = null,
        IEntityExclusionRepository? exclusions = null)
    {
        plans ??= new InMemoryEntitySyncPlanRepository();
        exclusions ??= new InMemoryEntityExclusionRepository();
        return new EntitySyncService(
            new EntitySyncPlanner(connections, plans, exclusions, new WeightedEntityMatcher()),
            connections,
            plans,
            exclusions,
            new DefaultEntityMapper());
    }

    private static CreateEntitySyncPlanRequest Request(
        string? sourceSearch = null,
        int? sourceCount = null,
        string? sourceEntityId = null) => new()
        {
            TenantId = "tenant",
            SourceVendor = "NetSuite",
            SourceConnectionId = "netsuite",
            SourceSearch = sourceSearch,
            SourceCount = sourceCount,
            SourceEntityId = sourceEntityId,
            TargetVendor = "HaloPSA",
            TargetConnectionId = "halo",
            CreateMissing = true
        };

    private static ExternalEntity Source(string id, string name) => new()
    {
        Vendor = "NetSuite",
        EntityType = "Customer",
        Id = id,
        Name = name,
        ExternalIds = { ["NetSuiteInternalId"] = id }
    };

    private sealed class FailingEntityExclusionRepository : IEntityExclusionRepository
    {
        public Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(EntityExclusionRoute route, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<EntityExclusion>>(new InvalidOperationException("Exclusion storage unavailable."));

        public Task<EntityExclusion> AddAsync(EntityExclusionRoute route, string sourceEntityId, string sourceName, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(EntityExclusionRoute route, string sourceEntityId, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ToggleEntityExclusionRepository : IEntityExclusionRepository
    {
        public bool FailReads { get; set; }

        public Task<IReadOnlyList<EntityExclusion>> ListActiveAsync(EntityExclusionRoute route, CancellationToken cancellationToken) =>
            FailReads
                ? Task.FromException<IReadOnlyList<EntityExclusion>>(new InvalidOperationException("Exclusion storage unavailable."))
                : Task.FromResult<IReadOnlyList<EntityExclusion>>(Array.Empty<EntityExclusion>());

        public Task<EntityExclusion> AddAsync(EntityExclusionRoute route, string sourceEntityId, string sourceName, string reason, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(EntityExclusionRoute route, string sourceEntityId, string actor, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class FakeAdapter(string vendor, IReadOnlyList<ExternalEntity>? entities = null, Func<Task>? beforeCreate = null) : IEntityAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int CreateCalls { get; private set; }
        public bool Disposed { get; private set; }
        public EntityQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            return Task.FromResult(entities ?? (IReadOnlyList<ExternalEntity>)Array.Empty<ExternalEntity>());
        }

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>(Array.Empty<EntitySyncLookup>());

        public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCreate != null) await beforeCreate();
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            return new EntityWriteResult { Success = true, Id = CreateCalls.ToString(), Message = "Created." };
        }

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult { Success = true, Id = request.Id, Message = "Updated." });

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }
}

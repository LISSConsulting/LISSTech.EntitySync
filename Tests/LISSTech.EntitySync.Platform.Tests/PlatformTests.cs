using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Net;
using System.Text.Json;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Adapters.Halo;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Adapters.LTAC;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    public void Connection_registration_rejects_empty_platform_uuid_before_mutation()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var adapter = new FakeAdapter("NCentral");

        Assert.Throws<ArgumentException>(() =>
            repository.Register(
                "tenant", "ncentral-prod", adapter, Guid.Empty));

        Assert.Empty(repository.List("tenant"));
        Assert.False(adapter.Disposed);
    }

    [Fact]
    public void Platform_uuid_is_connection_specific_and_nullable_for_Orchestra_target()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var platformInstanceId =
            Guid.Parse("44444444-4444-4444-4444-444444444444");

        var source = repository.Register(
            "tenant", "ncentral-prod", new FakeAdapter("NCentral"), platformInstanceId);
        var target = repository.Register(
            "tenant", "orchestra-target", new FakeAdapter("OrchestraMSP"));

        Assert.Equal(platformInstanceId, source.PlatformInstanceId);
        Assert.Null(target.PlatformInstanceId);
        Assert.Equal(2, repository.List("tenant").Count);
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
    public void HostingFactoryBuildsNetSuiteAndHaloOptionsFromInjectedSettings()
    {
        const string netSuiteSecret = "netsuite-secret-do-not-log";
        const string haloSecret = "halo-secret-do-not-log";
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("123_sb1", "https://HALO.example.test", netSuiteSecret, haloSecret));

        var netSuite = factory.CreateNetSuiteOptions(null);
        var halo = factory.CreateHaloOptions(null, "short-lived-halo-token");

        Assert.Equal("123_sb1", netSuite.AccountId);
        Assert.Equal("consumer-key", netSuite.ConsumerKey);
        Assert.Equal(netSuiteSecret, netSuite.ConsumerSecret);
        Assert.Equal("token-id", netSuite.TokenId);
        Assert.Equal(netSuiteSecret, netSuite.TokenSecret);
        Assert.Equal("https://halo.example.test/", halo.BaseUrl);
        Assert.Equal("short-lived-halo-token", halo.AccessToken);
        Assert.Equal(1, halo.TopLevelId);
        Assert.Equal("CFNetSuiteCustomerID", halo.NetSuiteCustomerIdField);
        Assert.Equal("CFassignedtam", halo.AccountManagerField);
        Assert.DoesNotContain(netSuiteSecret, factory.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(haloSecret, factory.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FixedRouteValidationAcceptsSyntacticallyValidFakeCredentialsWithoutNetwork()
    {
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("test-account", "https://halo.example.test", "net-suite-secret", "halo-secret"));

        factory.ValidateConfiguration(["NetSuite", "HaloPSA"]);

        Assert.Equal(64, NetSuiteHaloScope(factory).Length);
    }

    [Theory]
    [InlineData("NETSUITE_ACCOUNT_ID")]
    [InlineData("NETSUITE_CONSUMER_KEY")]
    [InlineData("NETSUITE_CONSUMER_SECRET")]
    [InlineData("NETSUITE_TOKEN_ID")]
    [InlineData("NETSUITE_TOKEN_SECRET")]
    [InlineData("HALO_BASE_URL")]
    [InlineData("HALO_CLIENT_ID")]
    [InlineData("HALO_CLIENT_SECRET")]
    public void FixedRouteValidationRejectsBlankRequiredSettings(string variableName)
    {
        var settings = FactorySettings(
            "test-account",
            "https://halo.example.test",
            "net-suite-secret",
            "halo-secret");
        settings[variableName] = " ";
        var factory = new ServerManagedEntityAdapterFactory(settings);

        var error = Assert.Throws<InvalidOperationException>(
            () => factory.ValidateConfiguration(["NetSuite", "HaloPSA"]));

        Assert.Contains(variableName, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://halo.example.test")]
    public void FixedRouteValidationRejectsMalformedOrNonHttpsHaloUrl(string haloUrl)
    {
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("test-account", haloUrl, "net-suite-secret", "halo-secret"));

        var error = Assert.Throws<InvalidOperationException>(
            () => factory.ValidateConfiguration(["NetSuite", "HaloPSA"]));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostingFactoryPrefersLocalProfileSettingsOverInjectedEnvironment()
    {
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("environment-account", "https://environment-halo.example.test", "environment-secret", "environment-halo-secret"));
        var profile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NetSuiteAccountId"] = "profile-account",
            ["NetSuiteConsumerKey"] = "profile-consumer-key",
            ["NetSuiteConsumerSecret"] = "profile-consumer-secret",
            ["NetSuiteTokenId"] = "profile-token-id",
            ["NetSuiteTokenSecret"] = "profile-token-secret",
            ["HaloBaseUrl"] = "https://profile-halo.example.test",
            ["HaloClientId"] = "profile-halo-client-id",
            ["HaloClientSecret"] = "profile-halo-client-secret",
            ["HaloScope"] = "profile-scope",
            ["HaloTopLevelId"] = "42"
        };

        var netSuite = factory.CreateNetSuiteOptions(profile);
        var halo = factory.CreateHaloOptions(profile, "profile-access-token");

        Assert.Equal("profile-account", netSuite.AccountId);
        Assert.Equal("profile-consumer-key", netSuite.ConsumerKey);
        Assert.Equal("profile-consumer-secret", netSuite.ConsumerSecret);
        Assert.Equal("profile-token-id", netSuite.TokenId);
        Assert.Equal("profile-token-secret", netSuite.TokenSecret);
        Assert.Equal("https://profile-halo.example.test/", halo.BaseUrl);
        Assert.Equal("profile-access-token", halo.AccessToken);
        Assert.Equal(42, halo.TopLevelId);
    }

    [Theory]
    [InlineData("NetSuite")]
    [InlineData("NCentral")]
    [InlineData("Bill.com")]
    public async Task HostingFactoryCreatesNonInteractiveVendorAdapters(string vendor)
    {
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("account", "https://halo.example.test", "netsuite-secret", "halo-secret"));

        var adapter = await factory.CreateAsync(vendor, null, CancellationToken.None);

        try
        {
            Assert.Equal(EntitySyncVendors.Normalize(vendor), adapter.Vendor);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void RouteScopeIgnoresSecretRotationButChangesForAccountIdentity()
    {
        var first = new ServerManagedEntityAdapterFactory(
            FactorySettings(" 123 ", "https://halo.example.test", "one", "halo-one"));
        var rotated = new ServerManagedEntityAdapterFactory(
            FactorySettings("123", "https://HALO.EXAMPLE.TEST/", "two", "halo-two"));
        var movedNetSuite = new ServerManagedEntityAdapterFactory(
            FactorySettings("456", "https://halo.example.test", "two", "halo-two"));
        var movedHalo = new ServerManagedEntityAdapterFactory(
            FactorySettings("123", "https://other-halo.example.test", "two", "halo-two"));
        var lowercaseAccount = new ServerManagedEntityAdapterFactory(
            FactorySettings("account_sb1", "https://halo.example.test", "two", "halo-two"));
        var uppercaseAccount = new ServerManagedEntityAdapterFactory(
            FactorySettings(" ACCOUNT_SB1 ", "https://halo.example.test", "two", "halo-two"));

        var scope = NetSuiteHaloScope(first);

        Assert.Equal(64, scope.Length);
        Assert.Matches("^[0-9a-f]{64}$", scope);
        Assert.Equal(
            "b0e71ffa748c0fd1b34c32e9999c664e063869019d1eb3f6c526bd10d3b52f69",
            scope);
        Assert.Equal(scope, NetSuiteHaloScope(rotated));
        Assert.Equal(
            NetSuiteHaloScope(lowercaseAccount),
            NetSuiteHaloScope(uppercaseAccount));
        Assert.NotEqual(scope, NetSuiteHaloScope(movedNetSuite));
        Assert.NotEqual(scope, NetSuiteHaloScope(movedHalo));
        Assert.DoesNotContain("123", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("halo.example.test", scope, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:route-secret@halo.example.test/")]
    [InlineData("https://halo.example.test/?token=route-secret")]
    [InlineData("https://halo.example.test/#route-secret")]
    public void RouteScopeRejectsUrlComponentsThatCouldContainSecrets(string haloUrl)
    {
        var factory = new ServerManagedEntityAdapterFactory(
            FactorySettings("123", haloUrl, "netsuite-secret", "halo-secret"));

        var error = Assert.Throws<InvalidOperationException>(
            () => NetSuiteHaloScope(factory));

        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("route-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HostingCompositionRegistersSharedPlatformAndStartupMigration()
    {
        var services = new ServiceCollection();

        services.AddEntitySyncPlatform(
            "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            EntitySyncHostMode.LocalStdio);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<NpgsqlDataSource>());
        Assert.NotNull(provider.GetRequiredService<IServerManagedEntityAdapterFactory>());
        Assert.IsType<InMemoryEntityConnectionRepository>(
            provider.GetRequiredService<IEntityConnectionRepository>());
        Assert.Null(provider.GetService<IEntitySyncPlanRepository>());
        Assert.IsType<PostgresEntityExclusionRepository>(
            provider.GetRequiredService<IEntityExclusionRepository>());
        Assert.IsType<PostgresEntitySyncChangeStateRepository>(
            provider.GetRequiredService<IEntitySyncChangeStateRepository>());
        Assert.IsType<PostgresEntityGraphRepository>(
            provider.GetRequiredService<IEntityGraphRepository>());
        Assert.IsType<WeightedEntityMatcher>(provider.GetRequiredService<IEntityMatcher>());
        Assert.IsType<DefaultEntityMapper>(provider.GetRequiredService<IEntityMapper>());
        Assert.NotNull(provider.GetRequiredService<EntitySyncPlanner>());
        Assert.NotNull(provider.GetRequiredService<IEntitySyncControlCommands>());
        Assert.NotNull(provider.GetRequiredService<EntityExclusionService>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is EntitySyncDatabaseMigrationHostedService);
    }

    [Fact]
    public async Task StartupMigrationFailurePropagatesFromHostedService()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unreachable;Username=unused;Password=unused;Timeout=1");
        var service = new EntitySyncDatabaseMigrationHostedService(dataSource);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.StartAsync(CancellationToken.None));
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
        var plans = new TestEntitySyncPlanRepository();
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

        await Assert.ThrowsAsync<StaleConnectionGenerationException>(() =>
            service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None));
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
        var plans = new TestEntitySyncPlanRepository();
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
    public async Task ApplicationAppliesHaloSourceRoutesWithRequiredWriteback(string targetVendor)
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var halo = new FakeAdapter("HaloPSA",
        [
            new ExternalEntity
            {
                Vendor = "HaloPSA",
                EntityType = "Client",
                Id = "halo-1",
                Name = "Acme"
            }
        ]);
        var target = new FakeAdapter(targetVendor);
        connections.Register("tenant", "halo", halo);
        connections.Register("tenant", "target", target);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "halo",
            TargetVendor = targetVendor,
            TargetConnectionId = "target",
            CreateMissing = true
        }, CancellationToken.None);
        InspectAllAndApprove(service, plan);

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, target.CreateCalls);
        if (targetVendor.Equals("Bill.com", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(1, halo.UpdateCalls);
            Assert.Equal("1", halo.LastUpdateRequest?.CustomFields["CFBillSpendClientID"]);
            Assert.True(halo.LastUpdateRequest?.CustomFieldOnly);
        }
        else
        {
            Assert.Equal(1, halo.NCentralClientLinkCalls);
        }
    }

    [Fact]
    public async Task ChangedOnlyBillComBootstrapWritesUniqueExactNameLinkToHalo()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var haloSource = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "halo-1",
            Name = "Custom Protective Services"
        };
        var billTarget = new ExternalEntity
        {
            Vendor = EntitySyncVendors.BillCom,
            EntityType = "Client",
            Id = "2378",
            Name = "Custom Protective Services",
            IsActive = true
        };
        billTarget.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = billTarget.Id;
        var halo = new FakeAdapter("HaloPSA", [haloSource]);
        var bill = new FakeAdapter(EntitySyncVendors.BillCom, [billTarget]);
        connections.Register("tenant", "halo", halo);
        connections.Register("tenant", "bill", bill);
        var changeStates = new InMemoryEntitySyncChangeStateRepository();
        var service = CreateService(connections, changeStates: changeStates);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "halo",
            SourceEntityType = "Client",
            TargetVendor = EntitySyncVendors.BillCom,
            TargetConnectionId = "bill",
            TargetEntityType = "Client",
            BootstrapExactNameLinks = true,
            UpdatePolicy = EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
            ChangeStateScope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        }, CancellationToken.None);

        var bootstrap = Assert.Single(plan.Items);
        Assert.Equal("Link", bootstrap.Action);
        Assert.Equal("BootstrapExactName", bootstrap.MatchType);
        Assert.Equal("2378", bootstrap.Target?.Id);
        Assert.NotNull(bootstrap.DesiredStateHash);
        InspectAllAndApprove(service, plan);

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, bill.UpdateCalls);
        Assert.Equal(1, halo.UpdateCalls);
        Assert.True(halo.LastUpdateRequest?.CustomFieldOnly);
        Assert.Equal(
            "2378",
            halo.LastUpdateRequest?.CustomFields[EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName]);

        var route = EntitySyncChangeStateRoute.Create(
            "tenant",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "HaloPSA",
            "halo",
            "Client",
            EntitySyncVendors.BillCom,
            "bill",
            "Client");
        var checkpoints = await changeStates.GetBySourceIdsAsync(route, ["halo-1"], CancellationToken.None);
        var checkpoint = Assert.Single(checkpoints).Value;
        Assert.Equal("2378", checkpoint.TargetEntityId);
        Assert.Equal(bootstrap.DesiredStateHash, checkpoint.PayloadHash);
    }

    [Fact]
    public async Task ChangedOnlyBillComBootstrapRejectsDuplicateExactNames()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var haloSource = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "halo-1",
            Name = "Duplicate"
        };
        var first = new ExternalEntity
        {
            Vendor = EntitySyncVendors.BillCom,
            EntityType = "Client",
            Id = "1",
            Name = "Duplicate",
            IsActive = true
        };
        var second = new ExternalEntity
        {
            Vendor = EntitySyncVendors.BillCom,
            EntityType = "Client",
            Id = "2",
            Name = "Duplicate",
            IsActive = true
        };
        var halo = new FakeAdapter("HaloPSA", [haloSource]);
        connections.Register("tenant", "halo", halo);
        connections.Register("tenant", "bill", new FakeAdapter(EntitySyncVendors.BillCom, [first, second]));
        var service = CreateService(connections);

        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "halo",
            SourceEntityType = "Client",
            TargetVendor = EntitySyncVendors.BillCom,
            TargetConnectionId = "bill",
            TargetEntityType = "Client",
            BootstrapExactNameLinks = true,
            UpdatePolicy = EntitySyncUpdatePolicy.ChangedLinkedUpdatesOnly,
            ChangeStateScope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        }, CancellationToken.None);

        var ambiguous = Assert.Single(plan.Items);
        Assert.Equal("None", ambiguous.Action);
        Assert.Equal("Ambiguous", ambiguous.MatchType);
        Assert.Null(ambiguous.Target);
        Assert.Equal(0, halo.UpdateCalls);
    }

    [Fact]
    public async Task BillComExactListApplyReplacesRenamesAndDeletesTargetOnlyValues()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var haloSource = new ExternalEntity { Vendor = "HaloPSA", EntityType = "Client", Id = "halo-1", Name = "New Name" };
        haloSource.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "100";
        var linkedTarget = new ExternalEntity { Vendor = EntitySyncVendors.BillCom, EntityType = "Client", Id = "100", Name = "Old Name", IsActive = true };
        linkedTarget.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "100";
        var obsoleteTarget = new ExternalEntity { Vendor = EntitySyncVendors.BillCom, EntityType = "Client", Id = "200", Name = "Obsolete", IsActive = true };
        obsoleteTarget.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "200";
        var halo = new FakeAdapter("HaloPSA", [haloSource]);
        var bill = new FakeAdapter(EntitySyncVendors.BillCom, [linkedTarget, obsoleteTarget], updateResultId: "300");
        connections.Register("tenant", "halo", halo);
        connections.Register("tenant", "bill", bill);
        var service = CreateService(connections);

        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "halo",
            TargetVendor = EntitySyncVendors.BillCom,
            TargetConnectionId = "bill",
            CreateMissing = true
        }, CancellationToken.None);

        var update = Assert.Single(plan.Items, item => item.Action == "Update");
        Assert.Equal("100", update.Target?.Id);
        Assert.Contains(update.Reasons, reason => reason.Contains("irreversibly delete", StringComparison.OrdinalIgnoreCase));
        var delete = Assert.Single(plan.Items, item => item.Action == "Delete");
        Assert.Equal("200", delete.Target?.Id);
        Assert.Contains(delete.Reasons, reason => reason.Contains("irreversibly deleted", StringComparison.OrdinalIgnoreCase));
        Assert.True(halo.LastQuery?.FullObjects);
        Assert.Contains(EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName, halo.LastQuery?.RequiredCustomFieldName);

        InspectAllAndApprove(service, plan);
        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("300", halo.LastUpdateRequest?.CustomFields[EntitySyncIntegrationContracts.BillComHaloClientCustomFieldName]);
        Assert.Equal(["100", "200"], bill.DeletedIds);
    }

    [Fact]
    public async Task BillComExactListSkipsDeletesWhenHaloWritebackFails()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var haloSource = new ExternalEntity { Vendor = "HaloPSA", EntityType = "Client", Id = "halo-1", Name = "New Name" };
        haloSource.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "100";
        var linkedTarget = new ExternalEntity { Vendor = EntitySyncVendors.BillCom, EntityType = "Client", Id = "100", Name = "Old Name", IsActive = true };
        linkedTarget.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "100";
        var obsoleteTarget = new ExternalEntity { Vendor = EntitySyncVendors.BillCom, EntityType = "Client", Id = "200", Name = "Obsolete", IsActive = true };
        obsoleteTarget.ExternalIds[EntitySyncIntegrationContracts.BillComClientExternalIdName] = "200";
        var halo = new FakeAdapter("HaloPSA", [haloSource], updateSucceeds: false);
        var bill = new FakeAdapter(EntitySyncVendors.BillCom, [linkedTarget, obsoleteTarget], updateResultId: "300");
        connections.Register("tenant", "halo", halo);
        connections.Register("tenant", "bill", bill);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "HaloPSA",
            SourceConnectionId = "halo",
            TargetVendor = EntitySyncVendors.BillCom,
            TargetConnectionId = "bill",
            CreateMissing = true
        }, CancellationToken.None);
        InspectAllAndApprove(service, plan);

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(bill.DeletedIds);
    }

    [Fact]
    public async Task ApplicationAllowsSophosCentralAsPlanTarget()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var target = new FakeAdapter(EntitySyncVendors.SophosCentral);
        connections.Register("tenant", "netsuite", new FakeAdapter("NetSuite", [Source("1", "Acme")]));
        connections.Register("tenant", "sophos", target);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "NetSuite",
            SourceConnectionId = "netsuite",
            TargetVendor = EntitySyncVendors.SophosCentral,
            TargetConnectionId = "sophos",
            CreateMissing = true
        }, CancellationToken.None);
        InspectAllAndApprove(service, plan);

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, target.CreateCalls);
    }

    [Fact]
    public async Task ApplicationUsesAuthoritativeBatchAdapterForAgentController()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var customer = new ExternalEntity
        {
            Vendor = "NCentral",
            EntityType = "Customer",
            Id = "ncentral-1",
            Name = "Acme"
        };
        customer.ExternalIds["NCentralCustomerId"] = customer.Id;
        var site = new ExternalEntity
        {
            Vendor = "NCentral",
            EntityType = "Site",
            Id = "ncentral-site-1",
            Name = "Acme HQ"
        };
        site.ExternalIds["NCentralSiteId"] = site.Id;
        site.ExternalIds["NCentralCustomerId"] = customer.Id;
        var target = new FakeAdapter(EntitySyncVendors.AgentController);
        var sourceAdapter = new FakeAdapter("NCentral", [customer, site], filterByEntityType: true);
        connections.Register("tenant", "ncentral", sourceAdapter);
        connections.Register("tenant", "agent-controller", target);
        var service = CreateService(connections);
        var plan = await service.CreatePlanAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "NCentral",
            SourceConnectionId = "ncentral",
            TargetVendor = EntitySyncVendors.AgentController,
            TargetConnectionId = "agent-controller",
            CreateMissing = true
        }, CancellationToken.None);
        InspectAllAndApprove(service, plan);

        var result = await service.ApplyAsync("tenant", plan.Id, true, CancellationToken.None);

        Assert.Equal("CustomerScope", plan.SourceEntityType);
        Assert.True(result.Success);
        Assert.Equal(1, target.BatchCalls);
        Assert.Equal(0, target.CreateCalls);
        Assert.Equal(2, target.LastBatchRequests!.Count);
        Assert.Contains(target.LastBatchRequests, request => Equals(request.Fields["ncentral_customer_id"], "ncentral-1"));
        Assert.Contains(target.LastBatchRequests, request => Equals(request.Fields["ncentral_customer_id"], "ncentral-site-1"));
        Assert.Equal(new[] { "Customer", "Site" }, sourceAdapter.Queries.Select(query => query.EntityType));
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
        var repository = new TestEntitySyncPlanRepository();
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
    public void Legacy_test_plan_repository_leaves_expiration_to_the_durable_service()
    {
        var repository = new TestEntitySyncPlanRepository();
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        repository.Add(plan);

        Assert.Equal(plan.Id, repository.Get("tenant", plan.Id).Id);
        Assert.Equal(plan.Id, repository.Get("tenant", plan.Id).Id);
    }

    [Fact]
    public void Legacy_test_plan_repository_allows_terminal_transition_after_expiration()
    {
        var repository = new TestEntitySyncPlanRepository();
        var plan = new EntitySyncPlan
        {
            TenantId = "tenant",
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(100)
        };
        repository.Add(plan);
        Assert.True(repository.TryTransition(
            "tenant", plan.Id, EntitySyncPlanStatuses.Draft, EntitySyncPlanStatuses.Applying));
        Thread.Sleep(200);

        Assert.True(repository.TryTransition(
            "tenant", plan.Id, EntitySyncPlanStatuses.Applying, EntitySyncPlanStatuses.Applied));
        Assert.Equal(EntitySyncPlanStatuses.Applied, repository.Get("tenant", plan.Id).Status);
    }

    [Fact]
    public void PlanSnapshotsPreserveCaseInsensitiveEntityFields()
    {
        var repository = new TestEntitySyncPlanRepository();
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
    public async Task ConnectVendorReturnsSafeErrorsForInvalidVendorAndProfile()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        using var services = new ServiceCollection()
            .AddSingleton<IEntityConnectionRepository>(connections)
            .BuildServiceProvider();
        var factory = new RecordingServerManagedEntityAdapterFactory();

        var invalidVendor = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: new McpRequestContext("tenant", false),
            vendor: "not-a-vendor",
            cancellationToken: default);
        var invalidProfile = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: new McpRequestContext("tenant", true),
            vendor: "HaloPSA",
            profileName: $"missing-{Guid.NewGuid():N}",
            cancellationToken: default);
        var missingRemoteConfiguration = await ConnectionTools.ConnectVendor(
            services,
            new ServerManagedEntityAdapterFactory(
                new Dictionary<string, string?>()),
            definitions: null,
            context: new McpRequestContext("tenant", false),
            vendor: "HaloPSA",
            cancellationToken: default);

        using var vendorJson = JsonDocument.Parse(invalidVendor);
        using var profileJson = JsonDocument.Parse(invalidProfile);
        using var configurationJson = JsonDocument.Parse(missingRemoteConfiguration);
        Assert.False(vendorJson.RootElement.GetProperty("success").GetBoolean());
        Assert.False(profileJson.RootElement.GetProperty("success").GetBoolean());
        Assert.False(configurationJson.RootElement.GetProperty("success").GetBoolean());
        Assert.True(vendorJson.RootElement.TryGetProperty("error", out _));
        Assert.True(profileJson.RootElement.TryGetProperty("error", out _));
        Assert.True(configurationJson.RootElement.TryGetProperty("error", out _));
        Assert.DoesNotContain("secret", invalidVendor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret",
            missingRemoteConfiguration,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectVendorClearsPartialNetSuiteSecretsWhenConfigurationFails()
    {
        var secretBuffer = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var factory = new ServerManagedEntityAdapterFactory(
            new Dictionary<string, string?>
            {
                ["NETSUITE_ACCOUNT_ID"] = "account",
                ["NETSUITE_CONSUMER_KEY"] = "consumer-key",
                ["NETSUITE_CONSUMER_SECRET"] = "consumer-secret-value",
                ["NETSUITE_TOKEN_ID"] = "token-id"
            },
            () => secretBuffer);
        using var services = new ServiceCollection().BuildServiceProvider();

        var response = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: new McpRequestContext("tenant", false),
            vendor: "NetSuite",
            cancellationToken: default);

        using var json = JsonDocument.Parse(response);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.True(json.RootElement.TryGetProperty("error", out _));
        Assert.Empty(secretBuffer);
        Assert.DoesNotContain(
            "consumer-secret-value",
            response,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectVendorDelegatesToSharedFactoryAndPreservesConnectionGenerations()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var factory = new RecordingServerManagedEntityAdapterFactory();
        var platformInstanceId =
            Guid.Parse("44444444-4444-4444-4444-444444444444");
        factory.PlatformInstanceId = platformInstanceId;
        var explicitPlatformInstanceId =
            Guid.Parse("55555555-5555-5555-5555-555555555555");
        var context = new McpRequestContext("tenant", true);
        using var services = new ServiceCollection()
            .AddSingleton<IEntityConnectionRepository>(connections)
            .BuildServiceProvider();

        var firstResponse = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: context,
            vendor: "HaloPSA",
            connectionId: "primary",
            cancellationToken: CancellationToken.None);
        Assert.Equal(
            platformInstanceId,
            Assert.Single(connections.List("tenant")).PlatformInstanceId);
        var secondResponse = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: context,
            vendor: "HaloPSA",
            connectionId: "primary",
            platformInstanceId: explicitPlatformInstanceId,
            cancellationToken: CancellationToken.None);

        using var firstJson = JsonDocument.Parse(firstResponse);
        using var secondJson = JsonDocument.Parse(secondResponse);
        Assert.True(firstJson.RootElement.GetProperty("success").GetBoolean());
        Assert.True(secondJson.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, firstJson.RootElement.GetProperty("Generation").GetInt64());
        Assert.Equal(2, secondJson.RootElement.GetProperty("Generation").GetInt64());
        Assert.Equal(2, factory.Calls.Count);
        Assert.All(factory.Calls, call => Assert.Equal("HaloPSA", call.Vendor));
        Assert.All(factory.Calls, call => Assert.Null(call.ProfileSettings));
        Assert.True(factory.Adapters[0].Disposed);
        Assert.False(factory.Adapters[1].Disposed);
        Assert.Equal(
            explicitPlatformInstanceId,
            Assert.Single(connections.List("tenant")).PlatformInstanceId);
    }

    [Fact]
    public async Task Local_mcp_rejects_empty_platform_uuid_without_registering_generation()
    {
        using var connections = new InMemoryEntityConnectionRepository();
        var factory = new RecordingServerManagedEntityAdapterFactory();
        var context = new McpRequestContext("tenant", true);
        using var services = new ServiceCollection()
            .AddSingleton<IEntityConnectionRepository>(connections)
            .BuildServiceProvider();

        var response = await ConnectionTools.ConnectVendor(
            services,
            factory,
            definitions: null,
            context: context,
            vendor: "NCentral",
            connectionId: "ncentral-prod",
            platformInstanceId: Guid.Empty,
            cancellationToken: default);

        using var document = JsonDocument.Parse(response);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(connections.List("tenant"));
        Assert.Single(factory.Adapters);
        Assert.True(factory.Adapters[0].Disposed);
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
    public void McpDiscoveryMetadataRoutesNaturalLanguageEntitySyncRequests()
    {
        var options = new ModelContextProtocol.Server.McpServerOptions();
        EntitySyncMcpMetadata.Configure(options);

        Assert.Equal("lisstech-entitysync", options.ServerInfo?.Name);
        Assert.Contains("Entity Sync / ES", options.ServerInfo?.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vendor-record lookup", options.ServerInfo?.Description, StringComparison.OrdinalIgnoreCase);

        var instructions = options.ServerInstructions ?? string.Empty;
        foreach (var phrase in new[]
        {
            "EntitySync",
            "Entity Sync",
            "ES",
            "sync clients",
            "client sync",
            "customer sync",
            "account sync",
            "company sync",
            "what is the address"
        })
        {
            Assert.Contains(phrase, instructions, StringComparison.OrdinalIgnoreCase);
        }

        var connectDescription = typeof(ConnectionTools).GetMethod(nameof(ConnectionTools.ConnectVendor))!
            .GetCustomAttribute<DescriptionAttribute>()?.Description;
        var entityDescription = typeof(ConnectionTools).GetMethod(nameof(ConnectionTools.GetEntities))!
            .GetCustomAttribute<DescriptionAttribute>()?.Description;
        var planDescription = typeof(SyncTools).GetMethod(nameof(SyncTools.CreateSyncPlan))!
            .GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.Contains("client sync", connectDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("natural-language questions", entityDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client sync", planDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customer sync", planDescription, StringComparison.OrdinalIgnoreCase);
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
        var adapter = await ServerManagedEntityAdapterFactory.ConnectAgentControllerAsync(
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
        var service = new EntityExclusionService(
            connections,
            new InMemoryEntityExclusionRepository());
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
        IConnectionRuntimeFactory connections,
        IEntitySyncPlanRepository? plans = null,
        IEntityExclusionRepository? exclusions = null,
        IEntitySyncChangeStateRepository? changeStates = null)
    {
        plans ??= new TestEntitySyncPlanRepository();
        exclusions ??= new InMemoryEntityExclusionRepository();
        changeStates ??= new InMemoryEntitySyncChangeStateRepository();
        var mapper = new DefaultEntityMapper();
        var graph = new InMemoryEntityGraphRepository();
        return new EntitySyncService(
            new EntitySyncPlanner(
                connections,
                plans,
                exclusions,
                new WeightedEntityMatcher(),
                mapper,
                changeStates,
                graph),
            connections,
            plans,
            exclusions,
            mapper,
            changeStates,
            graph,
            TimeProvider.System);
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

    private sealed class TestConnectionDefinitionRepository(
        InMemoryEntityConnectionRepository connections)
        : IConnectionDefinitionRepository
    {
        public Task<EntitySyncConnectionDefinition> InsertAsync(
            string tenantId,
            EntitySyncConnectionDefinition definition,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntitySyncConnectionDefinition?> GetAsync(
            string tenantId,
            string connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                connections.List(tenantId)
                    .Where(value => value.Id == connectionId)
                    .Select(Definition)
                    .SingleOrDefault());

        public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
            string tenantId,
            string? vendor,
            bool? enabled,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<EntitySyncConnectionDefinition> result = connections
                .List(tenantId)
                .Where(value => vendor is null
                    || value.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                .Select(Definition)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<EntitySyncConnectionDefinition?> TryReplaceAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            EntitySyncConnectionDefinition nextGeneration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
            string tenantId,
            string connectionId,
            long expectedGeneration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static EntitySyncConnectionDefinition Definition(
            EntityConnectionRegistration registration) =>
            new(
                registration.TenantId,
                registration.Id,
                registration.Vendor,
                registration.Id,
                registration.Generation,
                true,
                new EntitySyncJsonValue("{}"),
                "test",
                DateTimeOffset.UnixEpoch,
                new EntitySyncActor("test"),
                DateTimeOffset.UnixEpoch,
                new EntitySyncActor("test"));
    }

    private sealed class RecordingServerManagedEntityAdapterFactory : IServerManagedEntityAdapterFactory
    {
        public List<(string Vendor, IReadOnlyDictionary<string, string>? ProfileSettings)> Calls { get; } = [];
        public List<FakeAdapter> Adapters { get; } = [];
        public Guid? PlatformInstanceId { get; set; }

        public Task<IEntityAdapter> CreateAsync(
            string vendor,
            IReadOnlyDictionary<string, string>? profileSettings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((vendor, profileSettings));
            var adapter = new FakeAdapter(vendor);
            Adapters.Add(adapter);
            return Task.FromResult<IEntityAdapter>(adapter);
        }

        public Task<IEntityAdapter> CreateDurableAsync(
            string vendor,
            IReadOnlyDictionary<string, JsonElement> publicConfiguration,
            IReadOnlyDictionary<string, string> secretConfiguration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ServerManagedConnectionConfiguration GetConnectionConfiguration(
            string vendor,
            IReadOnlyDictionary<string, string>? profileSettings) =>
            new(
                new Dictionary<string, JsonElement>(),
                new Dictionary<string, string>(),
                PlatformInstanceId);

        public void ValidateNetSuiteHaloFixedRouteConfiguration()
        {
        }

        public string GetChangeStateScope(
            string sourceVendor,
            string sourceConnectionId,
            string sourceEntityType,
            string targetVendor,
            string targetConnectionId,
            string targetEntityType) => "unused";
    }

    private sealed class FakeAdapter(
        string vendor,
        IReadOnlyList<ExternalEntity>? entities = null,
        Func<Task>? beforeCreate = null,
        bool filterByEntityType = false,
        string? updateResultId = null,
        bool updateSucceeds = true)
        : IEntityAdapter, IEntityBatchAdapter, IEntityDeleteAdapter, IHaloSourceWritebackAdapter, IDisposable
    {
        public string Vendor { get; } = vendor;
        public IReadOnlyList<string> LookupTypes => [];
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int NCentralClientLinkCalls { get; private set; }
        public int BatchCalls { get; private set; }
        public bool Disposed { get; private set; }
        public EntityQuery? LastQuery { get; private set; }
        public EntityWriteRequest? LastUpdateRequest { get; private set; }
        public List<EntityQuery> Queries { get; } = [];
        public IReadOnlyList<EntityWriteRequest>? LastBatchRequests { get; private set; }
        public List<string> DeletedIds { get; } = [];

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            Queries.Add(query);
            var result = entities ?? (IReadOnlyList<ExternalEntity>)Array.Empty<ExternalEntity>();
            if (filterByEntityType)
            {
                result = result
                    .Where(entity => entity.EntityType.Equals(query.EntityType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            return Task.FromResult(result);
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

        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdateRequest = request;
            return Task.FromResult(new EntityWriteResult { Success = updateSucceeds, Id = updateResultId ?? request.Id, Message = updateSucceeds ? "Updated." : "Update failed." });
        }

        public Task<EntityWriteResult> DeleteEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            DeletedIds.Add(request.Id ?? string.Empty);
            return Task.FromResult(new EntityWriteResult { Success = true, Id = request.Id, Action = "Delete", Message = "Deleted." });
        }

        public Task<EntityWriteResult> UpsertNCentralClientLinkAsync(
            string haloClientId,
            string haloClientName,
            string nCentralCustomerId,
            string nCentralCustomerName,
            CancellationToken cancellationToken)
        {
            NCentralClientLinkCalls++;
            return Task.FromResult(new EntityWriteResult { Success = true, Id = nCentralCustomerId, Message = "Linked." });
        }

        public Task<EntityWriteResult> UpsertNCentralSiteLinkAsync(
            string haloSiteId,
            string haloSiteName,
            string haloClientName,
            string nCentralSiteId,
            string nCentralSiteName,
            string nCentralCustomerId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EntityWriteResult { Success = true, Id = nCentralSiteId, Message = "Linked." });

        public Task<EntityWriteResult> ApplyBatchAsync(
            IReadOnlyList<EntityWriteRequest> requests,
            CancellationToken cancellationToken)
        {
            BatchCalls++;
            LastBatchRequests = requests;
            return Task.FromResult(new EntityWriteResult { Success = true, Message = "Batch applied." });
        }


        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }
    private static string NetSuiteHaloScope(IServerManagedEntityAdapterFactory factory) =>
        factory.GetChangeStateScope(
            "NetSuite",
            "netsuite",
            "Customer",
            "HaloPSA",
            "halopsa",
            "Client");

    private static Dictionary<string, string?> FactorySettings(
        string account,
        string haloUrl,
        string netSuiteSecret,
        string haloSecret) => new(StringComparer.Ordinal)
    {
        ["NETSUITE_ACCOUNT_ID"] = account,
        ["NETSUITE_CONSUMER_KEY"] = "consumer-key",
        ["NETSUITE_CONSUMER_SECRET"] = netSuiteSecret,
        ["NETSUITE_TOKEN_ID"] = "token-id",
        ["NETSUITE_TOKEN_SECRET"] = netSuiteSecret,
        ["HALO_BASE_URL"] = haloUrl,
        ["HALO_CLIENT_ID"] = "halo-client-id",
        ["HALO_CLIENT_SECRET"] = haloSecret,
        ["HALO_NCENTRAL_INTEGRATION_ID"] = "7",
        ["NCENTRAL_BASE_URL"] = "https://ncentral.example.test",
        ["NCENTRAL_USER_API_TOKEN"] = "ncentral-token",
        ["NCENTRAL_SERVICE_ORG_ID"] = "service-org",
        ["NCENTRAL_SOAP_USERNAME"] = "soap-user",
        ["NCENTRAL_SOAP_PASSWORD"] = "soap-password",
        ["BILLCOM_BASE_URL"] = "https://bill.example.test",
        ["BILLCOM_API_TOKEN"] = "bill-token",
        ["BILLCOM_CLIENT_FIELD_NAME"] = "Client",
        ["SOPHOS_CENTRAL_CLIENT_ID"] = "sophos-client-id",
        ["SOPHOS_CENTRAL_CLIENT_SECRET"] = "sophos-client-secret"
    };
}

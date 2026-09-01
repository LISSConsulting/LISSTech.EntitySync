using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.OrchestraMSP;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Matching;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class OrchestraEntityAdapterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ClientId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SiteId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AddressId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Token_uses_client_credentials_default_scope_and_refreshes_at_five_minute_skew()
    {
        var clock = new ManualTimeProvider(Now);
        using var handler = new LoopbackHttpHandler((request, index, _) =>
        {
            Assert.Equal("/tenant/oauth2/v2.0/token", request.RequestUri!.AbsolutePath);
            var form = ParseForm(request.Body);
            Assert.Equal("client_credentials", form["grant_type"]);
            Assert.Equal("client-id", form["client_id"]);
            Assert.Equal("secret-value", form["client_secret"]);
            Assert.Equal("api://orchestra/.default", form["scope"]);
            return Json(HttpStatusCode.OK,
                $$"""{"access_token":"token-{{index}}","expires_in":3600}""");
        });
        using var http = new HttpClient(handler);
        var callerSecret = Encoding.UTF8.GetBytes("secret-value");
        using var provider = new OrchestraTokenProvider(
            http, new Uri("https://login.example/"), "tenant", "client-id",
            callerSecret, "api://orchestra", 7, clock);
        callerSecret.AsSpan().Fill((byte)'x');

        Assert.Equal("token-1", await provider.GetAccessTokenAsync(default));
        clock.Advance(TimeSpan.FromMinutes(54).Add(TimeSpan.FromSeconds(59)));
        Assert.Equal("token-1", await provider.GetAccessTokenAsync(default));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("token-2", await provider.GetAccessTokenAsync(default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_token_requests_share_one_refresh_and_generation_instances_never_share_tokens()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new LoopbackHttpHandler(async (_, index, cancellationToken) =>
        {
            if (index == 1) await release.Task.WaitAsync(cancellationToken);
            return Json(HttpStatusCode.OK,
                $$"""{"access_token":"generation-token-{{index}}","expires_in":3600}""");
        });
        using var http = new HttpClient(handler);
        var secret = Encoding.UTF8.GetBytes("secret");
        using var first = new OrchestraTokenProvider(
            http, new Uri("https://login.example/"), "tenant", "client", secret,
            "api://orchestra", 1, new ManualTimeProvider(Now));
        var calls = Enumerable.Range(0, 12)
            .Select(_ => first.GetAccessTokenAsync(default)).ToArray();
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.All(await Task.WhenAll(calls), token => Assert.Equal("generation-token-1", token));
        Assert.Single(handler.Requests);

        using var second = new OrchestraTokenProvider(
            http, new Uri("https://login.example/"), "tenant", "client", secret,
            "api://orchestra", 2, new ManualTimeProvider(Now));
        Assert.Equal("generation-token-2", await second.GetAccessTokenAsync(default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Token_and_authorization_failures_expose_only_safe_codes()
    {
        const string secret = "super-secret-not-for-errors";
        const string tokenBody = "token-body-not-for-errors";
        using var tokenHandler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.Unauthorized, tokenBody));
        using var tokenHttp = new HttpClient(tokenHandler);
        using var provider = TokenProvider(tokenHttp, secret: secret);

        var tokenError = await Assert.ThrowsAsync<OrchestraDependencyException>(
            () => provider.GetAccessTokenAsync(default));
        Assert.Equal("ORCHESTRA_TOKEN_REJECTED", tokenError.SafeCode);
        Assert.DoesNotContain(secret, tokenError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(tokenBody, tokenError.ToString(), StringComparison.Ordinal);

        using var directoryHandler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.Forbidden, "vendor-body-not-for-errors"));
        using var directoryHttp = new HttpClient(directoryHandler);
        using var adapter = Adapter(directoryHttp, StaticTokenProvider("workload-token"));
        var authError = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            adapter.GetEntitiesAsync(new EntityQuery { EntityType = "Client" }, default));
        Assert.Equal("ORCHESTRA_AUTHORIZATION_FAILED", authError.SafeCode);
        Assert.DoesNotContain("vendor-body-not-for-errors", authError.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_paging_is_opaque_bounded_and_cannot_escape_configured_service()
    {
        using var handler = new LoopbackHttpHandler((request, index, _) =>
        {
            Assert.Equal("directory.example", request.RequestUri!.Host);
            Assert.Equal("/api/v1/internal/client-directory/clients", request.RequestUri.AbsolutePath);
            return index switch
            {
                1 => Json(HttpStatusCode.OK,
                    $$"""{"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":"eyJwYWdlIjoyfQ"}"""),
                2 => Json(HttpStatusCode.OK,
                    $$"""{"items":[{{ClientJson(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1, "Beta")}}],"next_cursor":null}"""),
                _ => throw new InvalidOperationException()
            };
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Client", IncludeInactive = true }, default);

        Assert.Equal(2, entities.Count);
        Assert.Contains("cursor=eyJwYWdlIjoyfQ", handler.Requests[1].RequestUri!.Query,
            StringComparison.Ordinal);
        Assert.DoesNotContain("http", handler.Requests[1].RequestUri.Query,
            StringComparison.OrdinalIgnoreCase);

        using var badHandler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.OK,
                $$"""{"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":"https://evil.example/steal"}"""));
        using var badHttp = new HttpClient(badHandler);
        using var badAdapter = Adapter(badHttp, StaticTokenProvider("token"));
        var cursorError = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            badAdapter.GetEntitiesAsync(new EntityQuery { EntityType = "Client" }, default));
        Assert.Equal("ORCHESTRA_CURSOR_INVALID", cursorError.SafeCode);

        using var endlessHandler = new LoopbackHttpHandler((_, index, _) =>
            Json(HttpStatusCode.OK,
                $$"""{"items":[],"next_cursor":"page_{{index}}"}"""));
        using var endlessHttp = new HttpClient(endlessHandler);
        using var bounded = Adapter(endlessHttp, StaticTokenProvider("token"), maximumPages: 2);
        var pageError = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            bounded.GetEntitiesAsync(new EntityQuery { EntityType = "Client" }, default));
        Assert.Equal("ORCHESTRA_PAGE_LIMIT_EXCEEDED", pageError.SafeCode);
        Assert.Equal(2, endlessHandler.Requests.Count);
    }

    [Fact]
    public async Task Client_site_and_address_mapping_preserves_identity_version_nested_data_and_links()
    {
        var payload = $$"""
            {"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":null}
            """;
        using var handler = new LoopbackHttpHandler((_, _, _) => Json(HttpStatusCode.OK, payload));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var client = Assert.Single(await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Client", IncludeInactive = true }, default));
        Assert.Equal(ClientId.ToString("D"), client.Id);
        Assert.Equal(7, client.Version);
        Assert.Equal("active", client.LifecycleStatus);
        Assert.Equal(["priority", "west"], client.Tags);
        Assert.Equal("value", client.CustomFields["nested"]);
        Assert.Equal("{\"a\":1,\"z\":2}", client.CustomFields["object"]);
        Assert.Equal(SiteId.ToString("D"), Assert.Single(client.Children).Id);
        Assert.Equal(AddressId.ToString("D"), Assert.Single(client.Children[0].Children).Id);
        var link = Assert.Single(client.PlatformLinks);
        Assert.Equal("halo-prod", link.PlatformInstanceId);
        Assert.Equal("42", link.ExternalId);

        var site = Assert.Single(await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Site", IncludeInactive = true }, default));
        Assert.Equal(3, site.Version);
        Assert.Equal(ClientId.ToString("D"), site.ParentId);
        Assert.Equal(AddressId.ToString("D"), Assert.Single(site.Children).Id);

        var address = Assert.Single(await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Address", IncludeInactive = true }, default));
        Assert.Equal(2, address.Version);
        Assert.Equal("1 Main", address.PrimaryAddress!.Line1);
        Assert.Equal(SiteId.ToString("D"), address.ParentId);
    }

    [Fact]
    public async Task Exact_reads_require_the_requested_UUID_and_version_for_every_entity_type()
    {
        using var handler = new LoopbackHttpHandler((request, _, _) =>
        {
            var json = request.RequestUri!.AbsolutePath.StartsWith(
                "/api/v1/internal/client-directory/clients/",
                StringComparison.Ordinal)
                ? ClientJson(ClientId, 7, "Acme")
                : $$"""{"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":null}""";
            return Json(HttpStatusCode.OK, json);
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var versioned = Assert.IsAssignableFrom<ICanonicalEntityVersionAdapter>(adapter);

        var client = await versioned.ReadCanonicalAsync("Client", ClientId, 7, default);
        var site = await versioned.ReadCanonicalAsync("Site", SiteId, 3, default);
        var address = await versioned.ReadCanonicalAsync("Address", AddressId, 2, default);
        var stale = await versioned.ReadCanonicalAsync("Client", ClientId, 6, default);
        var otherId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var identityMismatch = await versioned.ReadCanonicalAsync(
            "Client", otherId, 7, default);
        var staleRequest = new CanonicalChangeRequest(
            "tenant", "event-current-n-plus-one", "Client", ClientId, 6,
            ["name"], new EntitySyncSha256(new string('b', 64)), Now);
        var staleHold = await CanonicalChangeService.ReadAssertedVersionAsync(
            versioned, staleRequest, default);

        Assert.Equal(ClientId, client!.CanonicalEntityId);
        Assert.Equal(SiteId, site!.CanonicalEntityId);
        Assert.Equal(AddressId, address!.CanonicalEntityId);
        Assert.Equal(7, stale!.CanonicalVersion);
        Assert.Equal(CanonicalVersionReadStatus.StaleVersion, staleHold.Status);
        Assert.Null(staleHold.Entity);
        Assert.Equal(ClientId, identityMismatch!.CanonicalEntityId);
    }

    [Fact]
    public async Task Conflicting_duplicate_address_parent_or_version_fails_closed()
    {
        var otherClient = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var otherSite = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var payload = $$"""
            {"items":[
              {{ClientWithAddressJson(ClientId, SiteId, AddressId, 2)}},
              {{ClientWithAddressJson(otherClient, otherSite, AddressId, 3)}}
            ],"next_cursor":null}
            """;
        using var handler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.OK, payload));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var versioned = Assert.IsAssignableFrom<ICanonicalEntityVersionAdapter>(adapter);

        var error = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            versioned.ReadCanonicalAsync("Address", AddressId, 2, default));

        Assert.Equal("ORCHESTRA_IDENTITY_CONFLICT", error.SafeCode);
    }

    [Fact]
    public async Task Every_address_duplicate_across_pages_requires_exact_payload_identity()
    {
        var exact = ClientWithAddressJson(ClientId, SiteId, AddressId, 2);
        var changed = exact.Replace(
            "\"postal_code\":\"78701\"",
            "\"postal_code\":\"99999\"",
            StringComparison.Ordinal);
        var firstPage = $$"""
            {"items":[{{exact}},{{exact}}],"next_cursor":"page-two"}
            """;
        var secondPage = $$"""
            {"items":[{{exact}},{{changed}}],"next_cursor":null}
            """;
        using var handler = new LoopbackHttpHandler((request, _, _) =>
            Json(HttpStatusCode.OK,
                request.RequestUri!.Query.Contains("page-two", StringComparison.Ordinal)
                    ? secondPage
                    : firstPage));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var versioned = Assert.IsAssignableFrom<ICanonicalEntityVersionAdapter>(adapter);

        var listError = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            adapter.GetEntitiesAsync(
                new EntityQuery { EntityType = "Address", IncludeInactive = true },
                default));
        var exactError = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            versioned.ReadCanonicalAsync("Address", AddressId, 2, default));

        Assert.Equal("ORCHESTRA_IDENTITY_CONFLICT", listError.SafeCode);
        Assert.Equal("ORCHESTRA_IDENTITY_CONFLICT", exactError.SafeCode);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Deleted_and_merge_donor_entities_are_never_active_duplicates_and_survivor_is_explicit()
    {
        var donor = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var deleted = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var payload = $$"""
            {"items":[
              {{ClientJson(ClientId, 8, "Survivor", mergedFrom: [donor])}},
              {{ClientJson(donor, 5, "Donor", lifecycle: "merged", mergedInto: ClientId)}},
              {{ClientJson(deleted, 2, "Deleted", lifecycle: "deleted", deleted: true)}}
            ],"next_cursor":null}
            """;
        using var handler = new LoopbackHttpHandler((_, _, _) => Json(HttpStatusCode.OK, payload));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Client", IncludeInactive = true }, default);
        var survivor = entities.Single(entity => entity.Id == ClientId.ToString("D"));
        var donorEntity = entities.Single(entity => entity.Id == donor.ToString("D"));
        var deletedEntity = entities.Single(entity => entity.Id == deleted.ToString("D"));
        Assert.Equal([donor.ToString("D")], survivor.MergeDonorIds);
        Assert.Equal(ClientId.ToString("D"), donorEntity.MergeSurvivorId);
        Assert.False(donorEntity.IsActive);
        Assert.True(deletedEntity.IsDeleted);
        Assert.False(deletedEntity.IsActive);
    }

    [Fact]
    public async Task Platform_link_lookup_and_upsert_use_typed_contract_and_authoritative_readback()
    {
        using var handler = new LoopbackHttpHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("link-upsert-1", request.Headers.GetValues("Idempotency-Key").Single());
                Assert.Contains("\"platform_instance_id\":\"halo-prod\"", request.Body,
                    StringComparison.Ordinal);
                return Json(HttpStatusCode.OK,
                    """{"platform_instance_id":"halo-prod","platform":"HaloPSA","external_id":"42","status":"active","entity_type":"Client","entity_id":"11111111-1111-1111-1111-111111111111"}""");
            }
            return Json(HttpStatusCode.OK,
                $$"""{"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":null}""");
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var found = await adapter.LookupPlatformLinkAsync("halo-prod", "42", default);
        Assert.Equal(ClientId.ToString("D"), found!.EntityId);
        var upserted = await adapter.UpsertPlatformLinkAsync(
            new OrchestraPlatformLinkCommand(
                "halo-prod", "HaloPSA", "42", "active", "Client", ClientId),
            "link-upsert-1", default);
        Assert.Equal("42", upserted.ExternalId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Update_sends_if_match_and_stable_idempotency_key_then_reads_authoritative_result()
    {
        using var handler = new LoopbackHttpHandler((request, index, _) => index switch
        {
            1 => AssertUpdate(request),
            2 => Json(HttpStatusCode.OK, ClientJson(ClientId, 8, "Acme Updated")),
            _ => throw new InvalidOperationException()
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("workload-token"));
        var request = new EntityWriteRequest
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Client",
            Id = ClientId.ToString("D"),
            Name = "Acme Updated",
            ExpectedVersion = 7,
            IdempotencyKey = "stable-command-1",
            VendorRequestId = "request-1"
        };

        var result = await adapter.UpdateEntityAsync(request, default);

        Assert.True(result.Success);
        Assert.Equal(ClientId.ToString("D"), result.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Create_uses_stable_idempotency_and_authoritative_readback()
    {
        using var handler = new LoopbackHttpHandler((request, index, _) => index switch
        {
            1 => AssertCreate(request),
            2 => Json(HttpStatusCode.OK, ClientJson(ClientId, 1, "Created")),
            _ => throw new InvalidOperationException()
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var result = await adapter.CreateEntityAsync(new EntityWriteRequest
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Client",
            Name = "Created",
            IdempotencyKey = "create-command-1",
            VendorRequestId = "create-request-1"
        }, default);

        Assert.True(result.Success);
        Assert.Equal(ClientId.ToString("D"), result.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Mapper_to_adapter_uses_typed_site_and_address_parent_commands()
    {
        using var handler = new LoopbackHttpHandler((request, index, _) => index switch
        {
            1 => AssertSiteCommand(request, HttpMethod.Post, expectedVersion: null, "site-create"),
            2 => ClientPage(),
            3 => AssertSiteCommand(request, HttpMethod.Patch, expectedVersion: 2, "site-update"),
            4 => ClientPage(),
            5 => AssertAddressCommand(
                request, HttpMethod.Post, expectedVersion: null, "address-create"),
            6 => ClientPage(),
            7 => AssertAddressCommand(
                request, HttpMethod.Patch, expectedVersion: 1, "address-update"),
            8 => ClientPage(),
            _ => throw new InvalidOperationException()
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var mapper = new DefaultEntityMapper();
        var options = new MatchOptions();
        var siteSource = SiteEntity("Vendor", version: null);
        var siteTarget = SiteEntity(EntitySyncVendors.OrchestraMSP, version: 2);
        var addressSource = AddressEntity("Vendor", version: null);
        var addressTarget = AddressEntity(EntitySyncVendors.OrchestraMSP, version: 1);

        var siteCreate = Stamp(
            mapper.MapCreate(
                siteSource,
                EntitySyncVendors.OrchestraMSP,
                "Site",
                options,
                new EntityWriteParent(ClientId, null, "Client")),
            "site-create");
        var siteUpdate = Stamp(
            mapper.MapUpdate(siteSource, siteTarget, options), "site-update");
        var addressCreate = Stamp(
            mapper.MapCreate(
                addressSource,
                EntitySyncVendors.OrchestraMSP,
                "Address",
                options,
                new EntityWriteParent(ClientId, SiteId, "Site")),
            "address-create");
        var addressUpdate = Stamp(
            mapper.MapUpdate(addressSource, addressTarget, options), "address-update");

        Assert.True((await adapter.CreateEntityAsync(siteCreate, default)).Success);
        Assert.True((await adapter.UpdateEntityAsync(siteUpdate, default)).Success);
        Assert.True((await adapter.CreateEntityAsync(addressCreate, default)).Success);
        Assert.True((await adapter.UpdateEntityAsync(addressUpdate, default)).Success);
        Assert.Equal(8, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Site", "Client", "customer-42", false)]
    [InlineData("Address", "Client", "customer-42", false)]
    [InlineData("Address", "Site", "site-99", true)]
    public async Task Foreign_parent_links_resolve_to_typed_Orchestra_parent_before_write(
        string entityType,
        string parentType,
        string foreignParentId,
        bool expectsSite)
    {
        using var handler = new LoopbackHttpHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, ParentLinkDirectoryJson());

            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(ClientId, body.RootElement.GetProperty("client_id").GetGuid());
            if (expectsSite)
                Assert.Equal(SiteId, body.RootElement.GetProperty("site_id").GetGuid());
            else
                Assert.False(body.RootElement.TryGetProperty("site_id", out _));
            return Json(HttpStatusCode.Conflict, "stop after command proof");
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var resolver = Assert.IsAssignableFrom<IEntityWriteParentResolver>(adapter);
        var source = new ExternalEntity
        {
            Vendor = "NCentral",
            EntityType = entityType,
            Id = entityType == "Site" ? "ncentral-site-7" : "ncentral-address-8",
            ParentId = foreignParentId,
            ParentEntityType = parentType,
            Name = entityType == "Site" ? "Austin" : "billing",
            PrimaryAddress = entityType == "Address"
                ? new EntityAddress
                {
                    AddressType = "billing",
                    Line1 = "1 Main",
                    City = "Austin",
                    State = "TX",
                    PostalCode = "78701",
                    Country = "US"
                }
                : null
        };

        var resolution = await resolver.ResolveWriteParentAsync(source, default);
        Assert.Equal(EntityWriteParentResolutionStatus.Resolved, resolution.Status);
        Assert.NotNull(resolution.Parent);
        Assert.Empty(source.ExternalIds);
        var request = Stamp(
            new DefaultEntityMapper().MapCreate(
                source,
                EntitySyncVendors.OrchestraMSP,
                entityType,
                new MatchOptions(),
                resolution.Parent),
            "foreign-parent");

        Assert.Equal(
            expectsSite ? SiteId.ToString("D") : ClientId.ToString("D"),
            request.ParentId);
        Assert.Equal(expectsSite ? "Site" : "Client", request.ParentEntityType);
        await Assert.ThrowsAsync<StaleCanonicalVersionException>(() =>
            adapter.CreateEntityAsync(request, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Address_policy_payload_and_before_snapshot_use_same_typed_object()
    {
        var mapper = new DefaultEntityMapper();
        var source = AddressEntity("NCentral", version: null);
        source.ExternalIds.Clear();
        source.ParentId = "foreign-site-9";
        source.ParentEntityType = "Site";
        source.PrimaryAddress!.PostalCode = "78759";
        var target = AddressEntity(EntitySyncVendors.OrchestraMSP, version: 2);
        target.ParentEntityType = "Site";
        var request = mapper.MapUpdate(source, target, new MatchOptions());
        var definition = new EntitySyncPolicyDefinition(
            "NCentral", "source", "Address",
            EntitySyncVendors.OrchestraMSP, "target", "Address",
            false, false, 90, 70, null, null,
            EntitySyncUpdatePolicy.Standard, ["Address"], [], false);

        var desired = PlanManifestBuilder.CreateAllowedDesiredPayload(
            request, definition);
        var before = PlanManifestBuilder.BuildBeforePayload(
            target, desired.Keys, definition.BlockedFields);

        Assert.Equal("78759",
            desired["Address"].GetProperty("PostalCode").GetString());
        Assert.Equal("78701",
            before["Address"].GetProperty("PostalCode").GetString());
        Assert.Equal("physical",
            desired["Address"].GetProperty("AddressType").GetString());
    }

    [Fact]
    public async Task Update_without_expected_version_fails_before_HTTP()
    {
        using var handler = new LoopbackHttpHandler(
            (Func<RecordedRequest, int, CancellationToken, HttpResponseMessage>)
            ((_, _, _) => throw new InvalidOperationException("HTTP must not be called.")));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var request = UpdateRequest();
        request.ExpectedVersion = null;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.UpdateEntityAsync(request, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Conflict_is_stale_without_retry_and_never_exposes_vendor_body()
    {
        using var handler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.Conflict, "secret vendor conflict body"));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var request = UpdateRequest();

        var error = await Assert.ThrowsAsync<StaleCanonicalVersionException>(() =>
            adapter.UpdateEntityAsync(request, default));

        Assert.Equal("CANONICAL_VERSION_CONFLICT", error.SafeCode);
        Assert.DoesNotContain("secret vendor conflict body", error.ToString(),
            StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Request_id_lookup_never_infers_applied_from_unchanged_old_target()
    {
        using var handler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.OK, ClientJson(ClientId, 7, "Acme Updated")));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var lookup = await adapter.LookupWriteByRequestIdAsync(UpdateRequest(), default);

        Assert.Equal(VendorRequestLookupOutcome.Unsupported, lookup.RequestLookupOutcome);
        Assert.False(lookup.Success);
        Assert.Equal("REQUEST_ID_LOOKUP_UNSUPPORTED", lookup.SafeCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task No_request_landed_after_two_transport_failures_remains_unknown()
    {
        using var handler = new LoopbackHttpHandler(
            (Func<RecordedRequest, int, CancellationToken, HttpResponseMessage>)
            ((_, _, _) =>
                throw new HttpRequestException("connection dropped before receipt")));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var error = await Assert.ThrowsAsync<AmbiguousCanonicalWriteException>(() =>
            adapter.UpdateEntityAsync(UpdateRequest(), default));

        Assert.Equal("ORCHESTRA_WRITE_OUTCOME_UNKNOWN", error.SafeCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Lost_write_response_replays_only_same_receipt_key_then_reads_back_and_cancellation_does_not_replay()
    {
        using var handler = new LoopbackHttpHandler((request, index, _) => index switch
        {
            1 => throw new HttpRequestException("lost response"),
            2 => AssertReplay(request),
            3 => Json(HttpStatusCode.OK, ClientJson(ClientId, 8, "Acme Updated")),
            _ => throw new InvalidOperationException()
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));

        var result = await adapter.UpdateEntityAsync(UpdateRequest(), default);
        Assert.True(result.Success);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(
            handler.Requests[0].Headers.GetValues("Idempotency-Key").Single(),
            handler.Requests[1].Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);

        using var canceledHandler = new LoopbackHttpHandler((_, _, cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken));
        using var canceledHttp = new HttpClient(canceledHandler);
        using var canceledAdapter = Adapter(canceledHttp, StaticTokenProvider("token"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            canceledAdapter.UpdateEntityAsync(UpdateRequest(), cancellation.Token));
        Assert.Empty(canceledHandler.Requests);
    }

    [Theory]
    [InlineData("78701", true)]
    [InlineData("99999", false)]
    [InlineData(null, false)]
    public async Task Lost_address_response_requires_exact_typed_address_readback(
        string? observedPostalCode,
        bool applied)
    {
        using var handler = new LoopbackHttpHandler((_, index, _) => index switch
        {
            1 => throw new HttpRequestException("lost response"),
            2 => Json(HttpStatusCode.OK, AddressResponseJson()),
            3 => Json(HttpStatusCode.OK,
                AddressReadbackPageJson(observedPostalCode)),
            _ => throw new InvalidOperationException()
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var source = AddressEntity("NCentral", version: null);
        source.ExternalIds.Clear();
        source.ParentId = "foreign-site";
        source.ParentEntityType = "Site";
        var target = AddressEntity(EntitySyncVendors.OrchestraMSP, version: 1);
        target.ParentEntityType = "Site";
        var request = Stamp(
            new DefaultEntityMapper().MapUpdate(
                source, target, new MatchOptions()),
            "address-proof");

        if (applied)
        {
            var result = await adapter.UpdateEntityAsync(request, default);
            Assert.True(result.Success);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AmbiguousCanonicalWriteException>(
                () => adapter.UpdateEntityAsync(request, default));
            Assert.Equal("ORCHESTRA_WRITE_OUTCOME_UNKNOWN", error.SafeCode);
        }
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true, "Create")]
    [InlineData(false, "Review")]
    public async Task Planner_resolves_Orchestra_create_parent_or_holds_safely(
        bool parentLinkAvailable,
        string expectedAction)
    {
        var directoryPage = parentLinkAvailable
            ? ParentLinkDirectoryJson()
            : "{\"items\":[" + ClientJson(ClientId, 7, "Existing")
              + "],\"next_cursor\":null}";
        using var handler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.OK, directoryPage));
        using var http = new HttpClient(handler);
        using var target = Adapter(http, StaticTokenProvider("token"));
        using var connections = new InMemoryEntityConnectionRepository();
        connections.Register(
            "tenant",
            "source",
            new StaticEntityAdapter(
            [
                new ExternalEntity
                {
                    Vendor = "NCentral",
                    EntityType = "Site",
                    Id = "foreign-site",
                    ParentId = "customer-42",
                    ParentEntityType = "Client",
                    Name = "Dallas"
                }
            ]));
        connections.Register("tenant", "target", target);
        var planner = new EntitySyncPlanner(
            connections,
            new InMemoryEntitySyncPlanRepository(),
            new InMemoryEntityExclusionRepository(),
            new WeightedEntityMatcher(),
            new DefaultEntityMapper(),
            new InMemoryEntitySyncChangeStateRepository());

        var plan = await planner.CreateAsync(new CreateEntitySyncPlanRequest
        {
            TenantId = "tenant",
            SourceVendor = "NCentral",
            SourceConnectionId = "source",
            SourceEntityType = "Site",
            TargetVendor = EntitySyncVendors.OrchestraMSP,
            TargetConnectionId = "target",
            TargetEntityType = "Site",
            CreateMissing = true
        }, default);

        var item = Assert.Single(plan.Items);
        Assert.Equal(expectedAction, item.Action);
        if (parentLinkAvailable)
        {
            Assert.NotNull(item.ResolvedTargetParent);
            Assert.Equal(ClientId, item.ResolvedTargetParent.ClientId);
        }
        else
        {
            Assert.Null(item.ResolvedTargetParent);
            Assert.Equal("ParentLinkReview", item.MatchType);
            Assert.Contains(
                item.Reasons,
                reason => reason.Contains(
                    "ORCHESTRA_PARENT_LINK_MISSING",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Durable_factory_registers_first_class_generation_local_exact_version_adapter()
    {
        using var handler = new LoopbackHttpHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token",
                    StringComparison.Ordinal))
                return Json(HttpStatusCode.OK,
                    """{"access_token":"factory-token","expires_in":3600}""");
            return Json(HttpStatusCode.OK, ClientJson(ClientId, 7, "Acme"));
        });
        using var http = new HttpClient(handler);
        var factory = new ServerManagedEntityAdapterFactory(
            new Dictionary<string, string?>(), () => http);
        var publicConfiguration = new Dictionary<string, JsonElement>
        {
            ["OrchestraBaseUrl"] = JsonSerializer.SerializeToElement(
                "https://directory.example/api/v1/internal/client-directory/"),
            ["OrchestraAuthority"] = JsonSerializer.SerializeToElement("https://login.example/"),
            ["OrchestraTenantId"] = JsonSerializer.SerializeToElement("tenant"),
            ["OrchestraClientId"] = JsonSerializer.SerializeToElement("client"),
            ["OrchestraResource"] = JsonSerializer.SerializeToElement("api://orchestra"),
            ["OrchestraConnectionGeneration"] = JsonSerializer.SerializeToElement(7)
        };
        var secrets = new Dictionary<string, string>
        {
            ["OrchestraClientSecret"] = "factory-secret"
        };

        using var adapter = Assert.IsAssignableFrom<IDisposable>(
            await factory.CreateDurableAsync(
                EntitySyncVendors.OrchestraMSP, publicConfiguration, secrets, default));
        var entityAdapter = Assert.IsAssignableFrom<IEntityAdapter>(adapter);
        Assert.Equal(EntitySyncVendors.OrchestraMSP, entityAdapter.Vendor);
        var versioned = Assert.IsAssignableFrom<ICanonicalEntityVersionAdapter>(adapter);
        var request = new CanonicalChangeRequest(
            "tenant", "exact-outbox-event", "Client", ClientId, 7, ["name"],
            new EntitySyncSha256(new string('a', 64)), Now);
        var read = await CanonicalChangeService.ReadAssertedVersionAsync(
            versioned, request, default);
        Assert.Equal(CanonicalVersionReadStatus.Exact, read.Status);
        Assert.Equal(ClientId.ToString("D"), read.Entity!.Id);
    }

    [Theory]
    [InlineData("Site")]
    [InlineData("Address")]
    public async Task Missing_parent_identity_fails_safely_before_HTTP(string entityType)
    {
        using var handler = new LoopbackHttpHandler(
            (Func<RecordedRequest, int, CancellationToken, HttpResponseMessage>)
            ((_, _, _) => throw new InvalidOperationException("HTTP must not be called.")));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var source = entityType == "Site"
            ? SiteEntity("Vendor", version: null)
            : AddressEntity("Vendor", version: null);
        source.ParentId = null;
        source.ExternalIds.Clear();
        var request = Stamp(
            new DefaultEntityMapper().MapCreate(
                source, EntitySyncVendors.OrchestraMSP, entityType, new MatchOptions()),
            "missing-parent");

        var error = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            adapter.CreateEntityAsync(request, default));

        Assert.Equal("ORCHESTRA_PARENT_IDENTITY_INVALID", error.SafeCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Conflicting_parent_identity_fails_safely_before_HTTP()
    {
        using var handler = new LoopbackHttpHandler(
            (Func<RecordedRequest, int, CancellationToken, HttpResponseMessage>)
            ((_, _, _) => throw new InvalidOperationException("HTTP must not be called.")));
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var source = SiteEntity("Vendor", version: null);
        source.ExternalIds["OrchestraClientId"] =
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var request = Stamp(
            new DefaultEntityMapper().MapCreate(
                source, EntitySyncVendors.OrchestraMSP, "Site", new MatchOptions()),
            "conflicting-parent");

        var error = await Assert.ThrowsAsync<OrchestraDependencyException>(() =>
            adapter.CreateEntityAsync(request, default));

        Assert.Equal("ORCHESTRA_PARENT_IDENTITY_INVALID", error.SafeCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("active", false, null)]
    [InlineData("suspended", false, null)]
    [InlineData("deleted", true, null)]
    [InlineData("merged", false, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    public async Task Ordinary_updates_preserve_lifecycle_and_send_only_approved_fields(
        string lifecycle,
        bool deleted,
        string? mergedInto)
    {
        using var handler = new LoopbackHttpHandler((request, _, _) =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var body = document.RootElement;
            Assert.False(body.TryGetProperty("lifecycle_status", out _));
            Assert.False(body.TryGetProperty("is_deleted", out _));
            Assert.False(body.TryGetProperty("merged_into_client_id", out _));
            Assert.Equal(
                "approved",
                body.GetProperty("fields").GetProperty("sync_field").GetString());
            return Json(HttpStatusCode.Conflict, "expected stop after shape assertion");
        });
        using var http = new HttpClient(handler);
        using var adapter = Adapter(http, StaticTokenProvider("token"));
        var mapper = new DefaultEntityMapper();
        var source = new ExternalEntity
        {
            Vendor = "Vendor",
            EntityType = "Client",
            Id = ClientId.ToString("D"),
            Name = "Approved Name",
            CustomFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sync_field"] = "approved"
            }
        };
        var target = new ExternalEntity
        {
            Vendor = EntitySyncVendors.OrchestraMSP,
            EntityType = "Client",
            Id = ClientId.ToString("D"),
            Version = 7,
            Name = "Old Name",
            LifecycleStatus = lifecycle,
            IsDeleted = deleted
        };
        if (mergedInto is not null)
            target.ExternalIds["OrchestraMergedIntoClientId"] = mergedInto;
        var request = Stamp(
            mapper.MapUpdate(source, target, new MatchOptions()), "lifecycle-update");

        await Assert.ThrowsAsync<StaleCanonicalVersionException>(() =>
            adapter.UpdateEntityAsync(request, default));
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage AssertSiteCommand(
        RecordedRequest request,
        HttpMethod method,
        long? expectedVersion,
        string key)
    {
        Assert.Equal(method, request.Method);
        var expectedPath = method == HttpMethod.Post
            ? $"/api/v1/internal/client-directory/clients/{ClientId:D}/sites"
            : $"/api/v1/internal/client-directory/clients/{ClientId:D}/sites/{SiteId:D}";
        Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
        Assert.Equal(key, request.Headers.GetValues("Idempotency-Key").Single());
        AssertVersionHeader(request, expectedVersion);
        using var document = JsonDocument.Parse(request.Body);
        var body = document.RootElement;
        Assert.Equal(ClientId, body.GetProperty("client_id").GetGuid());
        Assert.Equal("Austin", body.GetProperty("name").GetString());
        Assert.Equal("ATX", body.GetProperty("fields").GetProperty("code").GetString());
        AssertLifecycleShape(body, expectedVersion);
        return Json(HttpStatusCode.OK, SiteResponseJson());
    }

    private static HttpResponseMessage AssertAddressCommand(
        RecordedRequest request,
        HttpMethod method,
        long? expectedVersion,
        string key)
    {
        Assert.Equal(method, request.Method);
        var expectedPath = method == HttpMethod.Post
            ? "/api/v1/internal/client-directory/addresses"
            : $"/api/v1/internal/client-directory/addresses/{AddressId:D}";
        Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
        Assert.Equal(key, request.Headers.GetValues("Idempotency-Key").Single());
        AssertVersionHeader(request, expectedVersion);
        using var document = JsonDocument.Parse(request.Body);
        var body = document.RootElement;
        Assert.Equal(ClientId, body.GetProperty("client_id").GetGuid());
        Assert.Equal(SiteId, body.GetProperty("site_id").GetGuid());
        Assert.Equal("physical", body.GetProperty("address_type").GetString());
        Assert.Equal("1 Main", body.GetProperty("line1").GetString());
        Assert.Equal("Austin", body.GetProperty("city").GetString());
        Assert.Equal("central", body.GetProperty("fields").GetProperty("zone").GetString());
        AssertLifecycleShape(body, expectedVersion);
        return Json(HttpStatusCode.OK, AddressResponseJson());
    }

    private static void AssertVersionHeader(
        RecordedRequest request,
        long? expectedVersion)
    {
        if (expectedVersion.HasValue)
            Assert.Equal(
                expectedVersion.Value.ToString(),
                request.Headers.GetValues("If-Match").Single());
        else
            Assert.False(request.Headers.Contains("If-Match"));
    }

    private static void AssertLifecycleShape(JsonElement body, long? expectedVersion)
    {
        if (expectedVersion.HasValue)
            Assert.False(body.TryGetProperty("lifecycle_status", out _));
        else
            Assert.Equal("active", body.GetProperty("lifecycle_status").GetString());
    }

    private static EntityWriteRequest Stamp(EntityWriteRequest request, string key)
    {
        request.IdempotencyKey = key;
        request.VendorRequestId = key + "-request";
        return request;
    }

    private static ExternalEntity SiteEntity(string vendor, long? version) => new()
    {
        Vendor = vendor,
        EntityType = "Site",
        Id = SiteId.ToString("D"),
        ParentId = ClientId.ToString("D"),
        ParentEntityType = "Client",
        Version = version,
        Name = "Austin",
        LifecycleStatus = "suspended",
        ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrchestraClientId"] = ClientId.ToString("D")
        },
        CustomFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = "ATX"
        }
    };

    private static ExternalEntity AddressEntity(string vendor, long? version) => new()
    {
        Vendor = vendor,
        EntityType = "Address",
        Id = AddressId.ToString("D"),
        ParentId = SiteId.ToString("D"),
        ParentEntityType = "Site",
        Version = version,
        Name = "physical",
        LifecycleStatus = "archived",
        ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OrchestraClientId"] = ClientId.ToString("D"),
            ["OrchestraSiteId"] = SiteId.ToString("D")
        },
        PrimaryAddress = new EntityAddress
        {
            AddressType = "physical",
            Attention = "Ops",
            Line1 = "1 Main",
            City = "Austin",
            State = "TX",
            PostalCode = "78701",
            Country = "US"
        },
        CustomFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["address_type"] = "physical",
            ["zone"] = "central"
        }
    };

    private static HttpResponseMessage ClientPage() =>
        Json(HttpStatusCode.OK,
            $$"""{"items":[{{ClientJson(ClientId, 7, "Acme")}}],"next_cursor":null}""");

    private static string SiteResponseJson() => $$"""
        {"site_id":"{{SiteId:D}}","client_id":"{{ClientId:D}}","version":3,
         "name":"Austin","lifecycle_status":"suspended","is_deleted":false,
         "fields":{"code":"ATX"},"tags":[],"addresses":[],"platform_links":[]}
        """;

    private static string AddressResponseJson() => $$"""
        {"address_id":"{{AddressId:D}}","client_id":"{{ClientId:D}}",
         "site_id":"{{SiteId:D}}","version":2,"address_type":"physical",
         "is_deleted":false,"attention":"Ops","line1":"1 Main","line2":null,
         "line3":null,"city":"Austin","state":"TX","postal_code":"78701",
         "country":"US","fields":{"zone":"central"},"tags":[],"platform_links":[]}
        """;

    private static HttpResponseMessage AssertCreate(RecordedRequest request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("create-command-1",
            request.Headers.GetValues("Idempotency-Key").Single());
        Assert.False(request.Headers.Contains("If-Match"));
        Assert.Contains("\"name\":\"Created\"", request.Body, StringComparison.Ordinal);
        return Json(HttpStatusCode.Created, ClientJson(ClientId, 1, "Created"));
    }

    private static HttpResponseMessage AssertUpdate(RecordedRequest request)
    {
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("7", request.Headers.GetValues("If-Match").Single());
        Assert.Equal("stable-command-1", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("workload-token", request.Headers.Authorization.Parameter);
        return Json(HttpStatusCode.OK, ClientJson(ClientId, 8, "Acme Updated"));
    }

    private static HttpResponseMessage AssertReplay(RecordedRequest request)
    {
        Assert.Equal("stable-command-1", request.Headers.GetValues("Idempotency-Key").Single());
        return Json(HttpStatusCode.OK, ClientJson(ClientId, 8, "Acme Updated"));
    }

    private static EntityWriteRequest UpdateRequest() => new()
    {
        Vendor = EntitySyncVendors.OrchestraMSP,
        EntityType = "Client",
        Id = ClientId.ToString("D"),
        Name = "Acme Updated",
        ExpectedVersion = 7,
        IdempotencyKey = "stable-command-1",
        VendorRequestId = "request-1"
    };

    private static OrchestraEntityAdapter Adapter(
        HttpClient http,
        OrchestraTokenProvider tokenProvider,
        int maximumPages = 100) =>
        new(new OrchestraClientDirectoryClient(
            http, tokenProvider,
            new Uri("https://directory.example/api/v1/internal/client-directory/"),
            maximumPages));

    private static string ParentLinkDirectoryJson() => $$"""
        {"items":[{
          "client_id":"{{ClientId:D}}","version":7,"name":"Acme",
          "lifecycle_status":"active","is_deleted":false,"fields":{},"tags":[],
          "sites":[{
            "site_id":"{{SiteId:D}}","client_id":"{{ClientId:D}}","version":3,
            "name":"Austin","lifecycle_status":"active","is_deleted":false,
            "fields":{},"tags":[],"addresses":[],"platform_links":[{
              "platform_instance_id":"ncentral-prod","platform":"NCentral",
              "external_id":"site-99","status":"active","entity_type":"Site",
              "entity_id":"{{SiteId:D}}"}]}],
          "addresses":[],"platform_links":[{
            "platform_instance_id":"ncentral-prod","platform":"NCentral",
            "external_id":"customer-42","status":"active","entity_type":"Client",
            "entity_id":"{{ClientId:D}}"}]
        }],"next_cursor":null}
        """;

    private static string AddressReadbackPageJson(string? postalCode)
    {
        var json = ClientWithAddressJson(ClientId, SiteId, AddressId, 2);
        return postalCode is null
            ? $$"""{"items":[{{json.Replace(
                "\"postal_code\":\"78701\"",
                "\"postal_code\":null",
                StringComparison.Ordinal)}}],"next_cursor":null}"""
            : $$"""{"items":[{{json.Replace(
                "\"postal_code\":\"78701\"",
                $"\"postal_code\":\"{postalCode}\"",
                StringComparison.Ordinal)}}],"next_cursor":null}""";
    }

    private static string ClientWithAddressJson(
        Guid clientId,
        Guid siteId,
        Guid addressId,
        long addressVersion) => $$"""
        {"client_id":"{{clientId:D}}","version":1,"name":"Client",
         "lifecycle_status":"active","is_deleted":false,"fields":{},"tags":[],
         "sites":[{"site_id":"{{siteId:D}}","client_id":"{{clientId:D}}","version":1,
           "name":"Site","lifecycle_status":"active","is_deleted":false,
           "fields":{},"tags":[],"addresses":[{
             "address_id":"{{addressId:D}}","client_id":"{{clientId:D}}",
             "site_id":"{{siteId:D}}","version":{{addressVersion}},
             "address_type":"physical","is_deleted":false,"attention":"Ops",
             "line1":"1 Main","city":"Austin","state":"TX",
             "postal_code":"78701","country":"US",
             "fields":{},"tags":[],"platform_links":[]}],"platform_links":[]}],
         "addresses":[],"platform_links":[]}
        """;

    private static OrchestraTokenProvider StaticTokenProvider(string token)
    {
        var handler = new LoopbackHttpHandler((_, _, _) =>
            Json(HttpStatusCode.OK,
                $$"""{"access_token":"{{token}}","expires_in":3600}"""));
        return new OrchestraTokenProvider(
            new HttpClient(handler, disposeHandler: true),
            new Uri("https://login.example/"), "tenant", "client",
            Encoding.UTF8.GetBytes("secret"), "api://orchestra", 1,
            new ManualTimeProvider(Now));
    }

    private static OrchestraTokenProvider TokenProvider(
        HttpClient http,
        string secret = "secret") =>
        new(http, new Uri("https://login.example/"), "tenant", "client",
            Encoding.UTF8.GetBytes(secret), "api://orchestra", 1,
            new ManualTimeProvider(Now));

    private static string ClientJson(
        Guid id,
        long version,
        string name,
        string lifecycle = "active",
        bool deleted = false,
        Guid? mergedInto = null,
        Guid[]? mergedFrom = null)
    {
        var mergedIntoJson = mergedInto is null ? "null" : $"\"{mergedInto:D}\"";
        var mergedFromJson = JsonSerializer.Serialize(
            (mergedFrom ?? []).Select(value => value.ToString("D")));
        return $$$"""
            {"client_id":"{{{id:D}}}","version":{{{version}}},"name":{{{JsonSerializer.Serialize(name)}}},
             "lifecycle_status":"{{{lifecycle}}}","is_deleted":{{{deleted.ToString().ToLowerInvariant()}}},
             "merged_into_client_id":{{{mergedIntoJson}}},"merged_from_client_ids":{{{mergedFromJson}}},
             "fields":{"nested":"value","object":{"z":2,"a":1}},"tags":["west","priority"],
             "sites":[{"site_id":"{{{SiteId:D}}}","client_id":"{{{id:D}}}","version":3,
               "name":"Austin","lifecycle_status":"active","is_deleted":false,"fields":{"code":"ATX"},
               "tags":["hq"],"addresses":[{"address_id":"{{{AddressId:D}}}","client_id":"{{{id:D}}}",
                 "site_id":"{{{SiteId:D}}}","version":2,"address_type":"physical","is_deleted":false,
                 "attention":"Ops","line1":"1 Main","line2":null,"line3":null,"city":"Austin",
                 "state":"TX","postal_code":"78701","country":"US","fields":{"zone":"central"}}],
               "platform_links":[]}],
             "addresses":[],
             "platform_links":[{"platform_instance_id":"halo-prod","platform":"HaloPSA",
               "external_id":"42","status":"active","entity_type":"Client","entity_id":"{{{id:D}}}"}]}
            """;
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]),
                pair => WebUtility.UrlDecode(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StaticEntityAdapter(
        IReadOnlyList<ExternalEntity> entities) : IEntityAdapter
    {
        public string Vendor => "NCentral";
        public IReadOnlyList<string> LookupTypes => [];

        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(
            EntityQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(entities);

        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);

        public Task<EntityWriteResult> CreateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EntityWriteResult> UpdateEntityAsync(
            EntityWriteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        System.Net.Http.Headers.HttpRequestHeaders Headers,
        string Body);

    private sealed class LoopbackHttpHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, int, CancellationToken, Task<HttpResponseMessage>>
            responder;
        private readonly HttpListener listener = new();
        private readonly HttpClient transport = new();
        private readonly CancellationTokenSource shutdown = new();
        private readonly Uri endpoint;
        private readonly Task acceptLoop;
        private int count;
        private int disposeState;

        public LoopbackHttpHandler(
            Func<RecordedRequest, int, CancellationToken, HttpResponseMessage> responder)
            : this((request, index, cancellationToken) =>
                Task.FromResult(responder(request, index, cancellationToken)))
        {
        }

        public LoopbackHttpHandler(
            Func<RecordedRequest, int, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
            endpoint = new Uri($"http://127.0.0.1:{ReservePort()}/");
            listener.Prefixes.Add(endpoint.AbsoluteUri);
            listener.Start();
            acceptLoop = AcceptAsync();
        }

        public List<RecordedRequest> Requests { get; } = [];
        public TaskCompletionSource FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var forwarded = new HttpRequestMessage(
                request.Method,
                new Uri(endpoint, request.RequestUri!.PathAndQuery));
            forwarded.Headers.TryAddWithoutValidation(
                "X-Original-Uri", request.RequestUri.AbsoluteUri);
            foreach (var header in request.Headers)
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                forwarded.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return await transport.SendAsync(
                forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        private async Task AcceptAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HttpListenerException) when (shutdown.IsCancellationRequested)
                {
                    return;
                }
                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var original = context.Request.Headers["X-Original-Uri"];
                var received = new HttpRequestMessage(
                    new HttpMethod(context.Request.HttpMethod),
                    new Uri(original!, UriKind.Absolute));
                foreach (var key in context.Request.Headers.AllKeys)
                {
                    if (key is null || key.Equals("X-Original-Uri", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    received.Headers.TryAddWithoutValidation(
                        key, context.Request.Headers.GetValues(key));
                }
                using var reader = new StreamReader(
                    context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(shutdown.Token);
                var recorded = new RecordedRequest(
                    received.Method, received.RequestUri, received.Headers, body);
                lock (Requests) Requests.Add(recorded);
                var index = Interlocked.Increment(ref count);
                FirstRequest.TrySetResult();
                using var response = await responder(recorded, index, shutdown.Token);
                context.Response.StatusCode = (int)response.StatusCode;
                if (response.Content is not null)
                {
                    var responseBody = await response.Content.ReadAsByteArrayAsync(shutdown.Token);
                    context.Response.ContentType =
                        response.Content.Headers.ContentType?.ToString()
                        ?? "application/json";
                    context.Response.ContentLength64 = responseBody.Length;
                    await context.Response.OutputStream.WriteAsync(
                        responseBody, shutdown.Token);
                }
                context.Response.Close();
            }
            catch
            {
                context.Response.Abort();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposeState, 1) == 0)
            {
                shutdown.Cancel();
                listener.Close();
                transport.Dispose();
                shutdown.Dispose();
            }
            base.Dispose(disposing);
        }

        private static int ReservePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }
}

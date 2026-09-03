extern alias mcp;

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mcp.ControlApi;
using LISSTech.EntitySync.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[CollectionDefinition(nameof(ControlApiCollection), DisableParallelization = true)]
public sealed class ControlApiCollection : ICollectionFixture<ControlApiFactory>;

[Collection(nameof(ControlApiCollection))]
public sealed class ControlApiTests(ControlApiFactory factory)
{
    private static readonly (HttpMethod Method, string Template, string Policy)[] Inventory =
    [
        (HttpMethod.Get, "/api/v1/control/connections", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/connections", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/connections/{connectionId}", ControlPolicies.Read),
        (HttpMethod.Patch, "/api/v1/control/connections/{connectionId}", ControlPolicies.Manage),
        (HttpMethod.Delete, "/api/v1/control/connections/{connectionId}", ControlPolicies.Manage),
        (HttpMethod.Post, "/api/v1/control/connections/{connectionId}/test", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/policies", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/policies", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/policies/{policyId:guid}/versions", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/policies/{policyId:guid}/versions", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/plans", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/plans", ControlPolicies.Operate),
        (HttpMethod.Post, "/api/v1/control/plans/shadow-projection", ControlPolicies.Operate),
        (HttpMethod.Get, "/api/v1/control/plans/{planId:guid}/items", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/inspections", ControlPolicies.Operate),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/approvals", ControlPolicies.Approve),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/dry-run", ControlPolicies.Operate),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/apply", ControlPolicies.Approve),
        (HttpMethod.Get, "/api/v1/control/runs", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/runs/{runId:guid}", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/runs/{runId:guid}/items", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/schedules", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/schedules/preview", ControlPolicies.Manage),
        (HttpMethod.Post, "/api/v1/control/schedules", ControlPolicies.Manage),
        (HttpMethod.Post, "/api/v1/control/schedules/{scheduleId:guid}/versions", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/audit", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/audit/{eventId:guid}/values", ControlPolicies.Audit),
        (HttpMethod.Get, "/api/v1/control/exclusions", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/exclusions", ControlPolicies.Manage),
        (HttpMethod.Delete, "/api/v1/control/exclusions", ControlPolicies.Manage),
        (HttpMethod.Get, "/api/v1/control/capabilities", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/entities", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/canonical-changes", ControlPolicies.CanonicalChanges),
        (HttpMethod.Post, "/api/v1/control/expert/suiteql", ControlPolicies.Expert),
        (HttpMethod.Post, "/api/v1/control/expert/custom-properties", ControlPolicies.Expert)
    ];

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Development", null)]
    [InlineData("Testing", "false")]
    public void Http_loopback_authority_requires_an_explicit_nonproduction_test_override(
        string environmentName,
        string? allowInsecureTestAuthority)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            mcp::McpAuthorityConfiguration.Resolve(
                "http://127.0.0.1:18082",
                environmentName,
                allowInsecureTestAuthority));

        Assert.Contains("absolute HTTPS authority", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Explicit_nonproduction_test_override_allows_only_loopback_http(string environmentName)
    {
        var authority = mcp::McpAuthorityConfiguration.Resolve(
            "http://127.0.0.1:18082",
            environmentName,
            "true");

        Assert.Equal("http://127.0.0.1:18082/", authority.Value);
        Assert.False(authority.RequireHttpsMetadata);
        Assert.Throws<InvalidOperationException>(() =>
            mcp::McpAuthorityConfiguration.Resolve(
                "http://identity.example.test",
                environmentName,
                "true"));
    }

    [Fact]
    public async Task Complete_business_route_inventory_is_OAuth_protected()
    {
        using var client = factory.CreateClient();
        foreach (var (method, template, _) in Inventory)
        {
            using var response = await client.SendAsync(
                new HttpRequestMessage(method, RoutePath(template)));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(
                response.Headers.WwwAuthenticate,
                value => value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        }
    }
    [Fact]
    public void Every_business_route_has_exact_permission_policy()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/control", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(Inventory.Length, endpoints.Length);
        foreach (var (method, template, expectedPolicy) in Inventory)
        {
            var endpoint = Assert.Single(endpoints.Where(candidate =>
                candidate.RoutePattern.RawText == template
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Contains(method.Method, StringComparer.Ordinal) == true));
            var authorization = Assert.Single(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(expectedPolicy, authorization.Policy);
        }
    }

    [Fact]
    public void State_mutations_declare_idempotency_and_schedule_preview_does_not()
    {
        var atomicRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/v1/control/connections",
            "/api/v1/control/connections/{connectionId}",
            "/api/v1/control/schedules",
            "/api/v1/control/schedules/{scheduleId:guid}/versions",
            "/api/v1/control/exclusions"
        };
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/control", StringComparison.Ordinal) == true)
            .ToArray();

        foreach (var (method, template, _) in Inventory.Where(
            item => item.Method != HttpMethod.Get))
        {
            var endpoint = Assert.Single(endpoints, candidate =>
                candidate.RoutePattern.RawText == template
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Contains(method.Method, StringComparer.Ordinal) == true);
            if (template == "/api/v1/control/schedules/preview")
            {
                Assert.Null(endpoint.Metadata.GetMetadata<IdempotencyExecutionMetadata>());
                continue;
            }

            var expected = atomicRoutes.Contains(template)
                ? IdempotencyExecutionMode.AtomicDatabase
                : IdempotencyExecutionMode.Recoverable;
            Assert.Equal(
                expected,
                endpoint.Metadata.GetMetadata<IdempotencyExecutionMetadata>()?.Mode);
        }
    }


    [Theory]
    [InlineData("scp", ControlRoles.Read, "/api/v1/control/plans", 200)]
    [InlineData("roles", ControlRoles.Read, "/api/v1/control/plans", 200)]
    [InlineData("scp", ControlRoles.Operate, "/api/v1/control/plans", 403)]
    [InlineData("roles", ControlRoles.Manage, "/api/v1/control/plans", 403)]
    public async Task Read_endpoint_requires_Read_scope_or_role(
        string claimType, string permission, string path, int status)
    {
        using var client = factory.CreateClient();
        AddClaims(client, $"tid=tenant-a;oid=user-a;{claimType}={permission}", claimType == "roles");
        using var response = await client.GetAsync(path);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("scp", ControlRoles.Operate, "/api/v1/control/plans", 400)]
    [InlineData("roles", ControlRoles.Operate, "/api/v1/control/plans", 400)]
    [InlineData("scp", ControlRoles.Approve, "/api/v1/control/plans/00000000-0000-0000-0000-000000000001/apply", 400)]
    [InlineData("roles", ControlRoles.Manage, "/api/v1/control/policies", 400)]
    [InlineData("scp", ControlRoles.Expert, "/api/v1/control/expert/suiteql", 400)]
    [InlineData("roles", ControlRoles.Expert, "/api/v1/control/expert/custom-properties", 400)]
    public async Task Mutation_permissions_are_exact_and_idempotency_is_mandatory(
        string claimType, string permission, string path, int status)
    {
        using var client = factory.CreateClient();
        AddClaims(client, $"tid=tenant-a;oid=user-a;{claimType}={permission}", claimType == "roles");
        using var response = await client.PostAsync(path, Json("{}"));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", await ProblemCode(response));
    }

    [Theory]
    [InlineData(ControlRoles.Manage, 400)]
    [InlineData(ControlRoles.Read, 403)]
    public async Task Schedule_preview_requires_Manage_without_idempotency(
        string permission,
        int status)
    {
        using var client = factory.CreateClient();
        AddClaims(client, $"tid=tenant-a;oid=user-a;scp={permission}");

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview", Json("{}"));

        Assert.Equal(status, (int)response.StatusCode);
        if (permission == ControlRoles.Manage)
            Assert.NotEqual("IDEMPOTENCY_KEY_REQUIRED", await ProblemCode(response));
    }

    [Theory]
    [InlineData("tid=tenant-a;oid=user-a;scp=EntitySync.Read;tid=tenant-a")]
    [InlineData("tid=tenant-a;oid=user-a;scp=EntitySync.Read;tid=tenant-b")]
    [InlineData("oid=user-a;scp=EntitySync.Read")]
    [InlineData("tid=tenant-a;scp=EntitySync.Read")]
    [InlineData("tid=tenant-a;oid=user-a;oid=user-b;scp=EntitySync.Read")]
    [InlineData("tid=tenant-a;oid=user-a;azp=app-a;scp=EntitySync.Read")]
    [InlineData("tid=tenant-a;azp=app-a;roles=EntitySync.Read;scp=EntitySync.Read")]
    public async Task Ambiguous_or_mixed_identity_claims_fail_closed(string claims)
    {
        using var client = factory.CreateClient();
        AddClaims(client, claims);
        using var response = await client.GetAsync("/api/v1/control/plans");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("tid=tenant-a;oid=user-a;scp=EntitySync.Operate", 403)]
    [InlineData("tid=tenant-a;azp=not-allowed;roles=EntitySync.Operate", 403)]
    [InlineData("tid=tenant-a;azp=om-workload;roles=EntitySync.Read", 403)]
    [InlineData("tid=tenant-a;azp=om-workload;roles=EntitySync.Operate", 400)]
    public async Task Canonical_change_intake_is_allowlisted_workload_only(string claims, int status)
    {
        using var client = factory.CreateClient();
        AddClaims(client, claims);
        using var response = await client.PostAsync(
            "/api/v1/control/canonical-changes", Json("{}"));
        Assert.Equal(status, (int)response.StatusCode);
    }
    [Fact]
    public async Task Idempotency_replays_exact_response_conflicts_on_hash_and_is_tenant_scoped()
    {
        var key = $"control-{Guid.NewGuid():N}";
        using var tenantA = factory.CreateClient();
        AddClaims(tenantA, "tid=tenant-a;azp=om-workload;roles=EntitySync.Operate");
        using var first = await SendCanonicalAsync(tenantA, key, """{"outboxEventId":"a"}""");
        using var replay = await SendCanonicalAsync(tenantA, key, """{"outboxEventId":"a"}""");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, replay.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await replay.Content.ReadAsStringAsync());

        using var conflict = await SendCanonicalAsync(
            tenantA, key, """{"outboxEventId":"different"}""");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ProblemCode(conflict));

        using var tenantB = factory.CreateClient();
        AddClaims(tenantB, "tid=tenant-b;azp=om-workload;roles=EntitySync.Operate");
        using var isolated = await SendCanonicalAsync(
            tenantB, key, """{"outboxEventId":"different"}""");
        Assert.Equal(HttpStatusCode.Accepted, isolated.StatusCode);
        Assert.Contains("\"tenantId\":\"tenant-b\"",
            await isolated.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }


    [Fact]
    public async Task Cursor_is_opaque_tenant_bound_and_page_size_is_bounded()
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");
        using var oversized = await client.GetAsync("/api/v1/control/plans?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal("PAGE_SIZE_OUT_OF_RANGE", await ProblemCode(oversized));

        using var malformed = await client.GetAsync("/api/v1/control/plans?pageSize=25&cursor=1");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("INVALID_CURSOR", await ProblemCode(malformed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Run_page_size_rejects_values_outside_one_through_one_hundred(
        int pageSize)
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");

        using var response = await client.GetAsync(
            $"/api/v1/control/runs?pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PAGE_SIZE_OUT_OF_RANGE", await ProblemCode(response));
    }

    [Fact]
    public async Task Run_list_rejects_offset_and_invalid_bounded_cursors_with_safe_errors()
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");
        using var offset = await client.GetAsync("/api/v1/control/runs?offset=1");
        Assert.Equal(HttpStatusCode.BadRequest, offset.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ProblemCode(offset));

        foreach (var cursor in new[] { "1", new string('A', 4097) })
        {
            using var response = await client.GetAsync(
                $"/api/v1/control/runs?cursor={Uri.EscapeDataString(cursor)}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("INVALID_CURSOR", await ProblemCode(response));
        }
    }

    [Fact]
    public async Task Run_cursor_is_strict_tamper_evident_and_restart_stable()
    {
        var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var firstCodec = new ControlCursorProtector(provider);
        var restartedCodec = new ControlCursorProtector(provider);
        var highWater = new DateTimeOffset(
            2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var lastQueuedAt = highWater.AddMinutes(-1);
        var lastOperationId =
            Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var cursor = firstCodec.ProtectRun(
            "runs", "tenant-a", highWater, lastQueuedAt, lastOperationId, 1);
        Assert.DoesNotContain("tenant-a", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain(lastOperationId.ToString("D"), cursor, StringComparison.Ordinal);
        Assert.Equal(
            (highWater, lastQueuedAt, lastOperationId, 1),
            restartedCodec.UnprotectRun(cursor, "runs", "tenant-a"));
        var pageStart = firstCodec.ProtectRunStart(
            "runs", "tenant-a", highWater, 1);
        Assert.Equal(
            (highWater, (DateTimeOffset?)null, (Guid?)null, 1),
            restartedCodec.UnprotectRun(pageStart, "runs", "tenant-a"));

        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");
        factory.LogRecorder.Clear();
        using var initial = await client.GetAsync("/api/v1/control/runs?pageSize=1");
        initial.EnsureSuccessStatusCode();
        var initialBody = await initial.Content.ReadAsStringAsync();
        using var initialJson = JsonDocument.Parse(initialBody);
        var replayCursor = initialJson.RootElement
            .GetProperty("replayCursor")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(replayCursor));
        using var replayedInitial = await client.GetAsync(
            $"/api/v1/control/runs?pageSize=1&cursor={Uri.EscapeDataString(replayCursor!)}");
        replayedInitial.EnsureSuccessStatusCode();
        Assert.Equal(initialBody, await replayedInitial.Content.ReadAsStringAsync());
        using var firstReplay = await client.GetAsync(
            $"/api/v1/control/runs?pageSize=1&cursor={Uri.EscapeDataString(cursor)}");
        using var secondReplay = await client.GetAsync(
            $"/api/v1/control/runs?pageSize=1&cursor={Uri.EscapeDataString(cursor)}");
        firstReplay.EnsureSuccessStatusCode();
        secondReplay.EnsureSuccessStatusCode();
        var firstReplayBody = await firstReplay.Content.ReadAsStringAsync();
        Assert.Equal(
            firstReplayBody,
            await secondReplay.Content.ReadAsStringAsync());
        using var firstReplayJson = JsonDocument.Parse(firstReplayBody);
        Assert.Equal(
            cursor,
            firstReplayJson.RootElement.GetProperty("replayCursor").GetString());
        Assert.DoesNotContain(
            factory.LogRecorder.Entries,
            entry => entry.Contains(cursor, StringComparison.Ordinal));

        var tamperedChars = cursor.ToCharArray();
        var tamperIndex = tamperedChars.Length / 2;
        tamperedChars[tamperIndex] = tamperedChars[tamperIndex] == 'A' ? 'B' : 'A';
        using var tampered = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(new string(tamperedChars))}");
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal("INVALID_CURSOR", await ProblemCode(tampered));
    }

    [Fact]
    public async Task Run_cursor_authenticates_page_size_for_first_and_middle_replay()
    {
        var queries = RunPagingQueryProxy.Create();
        using var pagingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IControlApiQueries>();
                services.AddSingleton(queries);
            }));
        using var client = pagingFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");

        using var first = await client.GetAsync("/api/v1/control/runs?pageSize=1");
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadAsStringAsync();
        using var firstJson = JsonDocument.Parse(firstBody);
        Assert.Single(firstJson.RootElement.GetProperty("items").EnumerateArray());
        var firstReplay = firstJson.RootElement.GetProperty("replayCursor").GetString();
        var next = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstReplay));
        Assert.False(string.IsNullOrWhiteSpace(next));

        using var replayedFirst = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(firstReplay!)}");
        replayedFirst.EnsureSuccessStatusCode();
        using var replayedFirstJson = JsonDocument.Parse(
            await replayedFirst.Content.ReadAsStringAsync());
        Assert.Equal(
            firstJson.RootElement.GetProperty("items").GetRawText(),
            replayedFirstJson.RootElement.GetProperty("items").GetRawText());
        Assert.Equal(
            firstReplay,
            replayedFirstJson.RootElement.GetProperty("replayCursor").GetString());
        using var firstMismatch = await client.GetAsync(
            $"/api/v1/control/runs?pageSize=25&cursor={Uri.EscapeDataString(firstReplay!)}");
        Assert.Equal(HttpStatusCode.BadRequest, firstMismatch.StatusCode);
        Assert.Equal("INVALID_CURSOR", await ProblemCode(firstMismatch));

        using var middle = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(next!)}");
        middle.EnsureSuccessStatusCode();
        var middleBody = await middle.Content.ReadAsStringAsync();
        using var middleJson = JsonDocument.Parse(middleBody);
        Assert.Single(middleJson.RootElement.GetProperty("items").EnumerateArray());
        var middleReplay = middleJson.RootElement.GetProperty("replayCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(middleReplay));

        using var replayedMiddle = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(middleReplay!)}");
        replayedMiddle.EnsureSuccessStatusCode();
        using var replayedMiddleJson = JsonDocument.Parse(
            await replayedMiddle.Content.ReadAsStringAsync());
        Assert.Equal(
            middleJson.RootElement.GetProperty("items").GetRawText(),
            replayedMiddleJson.RootElement.GetProperty("items").GetRawText());
        Assert.Equal(
            middleReplay,
            replayedMiddleJson.RootElement.GetProperty("replayCursor").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Run_cursor_rejects_present_blank_values(string cursor)
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");

        using var response = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_CURSOR", await ProblemCode(response));
    }

    [Theory]
    [MemberData(nameof(InvalidRunCursorPayloads))]
    public async Task Run_cursor_rejects_protected_noncanonical_payloads(string payload)
    {
        var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protectedPayload = provider
            .CreateProtector("LISSTech.EntitySync.ControlApi.Cursor.v1")
            .Protect(payload);
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");

        using var response = await client.GetAsync(
            $"/api/v1/control/runs?cursor={Uri.EscapeDataString(protectedPayload)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_CURSOR", await ProblemCode(response));
    }

    public static TheoryData<string> InvalidRunCursorPayloads => new()
    {
        """
        {"Version":2,"Resource":"runs","TenantId":"tenant-a","HighWater":"2026-09-01T12:00:00.0000000+00:00","LastQueuedAt":"2026-09-01T11:59:00.0000000+00:00","LastOperationId":"12345678-1234-1234-1234-123456789abc","PageSize":25}
        """,
        """
        {"Version":1,"Resource":"runs","TenantId":"tenant-a","HighWater":"not-a-time","LastQueuedAt":"2026-09-01T11:59:00.0000000+00:00","LastOperationId":"12345678-1234-1234-1234-123456789abc","PageSize":25}
        """,
        """
        {"Version":1,"Resource":"runs","TenantId":"tenant-a","HighWater":"2026-09-01T12:00:00.0000000+00:00","LastQueuedAt":"2026-09-01T11:59:00.0000000+00:00","LastOperationId":"12345678-1234-1234-1234-123456789abc","PageSize":25,"Unexpected":true}
        """,
        """
        {"Version":1,"Resource":"runs","TenantId":"tenant-a","HighWater":"2026-09-01T12:00:00.0000000+00:00","LastQueuedAt":"2026-09-01T11:59:00.0000000+00:00","LastOperationId":"12345678-1234-1234-1234-123456789abc"}
        """,
        """
        {"Version":1,"Resource":"runs","TenantId":"tenant-a","HighWater":"2026-09-01T12:00:00.0000000+00:00","LastQueuedAt":"2026-09-01T11:59:00.0000000+00:00","LastOperationId":"12345678-1234-1234-1234-123456789abc","PageSize":101}
        """
    };

    [Fact]
    public async Task Safe_errors_are_RFC_9457_and_do_not_expose_exception_details()
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");
        using var response = await client.GetAsync("/api/v1/control/runs/00000000-0000-0000-0000-000000000099");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"code\":\"NOT_FOUND\"", body);
        Assert.Contains("\"correlationId\"", body);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dependency_failures_are_safe_retryable_503_problems()
    {
        factory.LogRecorder.Clear();
        using var unavailableFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEntitySyncControlCommands>();
                services.AddSingleton<IEntitySyncControlCommands>(
                    DispatchProxy.Create<IEntitySyncControlCommands, DependencyFailureControlProxy>());
            }));
        using var client = unavailableFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");

        using var response = await client.GetAsync("/api/v1/control/connections");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("DEPENDENCY_UNAVAILABLE", await ProblemCode(response));
        Assert.Contains("\"correlationId\"", body);
        Assert.DoesNotContain("vendor-secret-response", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            factory.LogRecorder.Entries,
            entry => entry.Contains("vendor-secret-response", StringComparison.Ordinal));
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_connection_test_generation_is_a_safe_state_conflict()
    {
        var repository = new HttpConnectionDefinitionRepository();
        var protector = new IdentityDataProtector();
        var runtime = new ConnectionRuntimeFactory(
            repository,
            protector,
            new NeverCreatingAdapterFactory());
        var service = new ConnectionDefinitionService(
            repository,
            protector,
            runtime,
            TimeProvider.System);
        var definitionRequest = new ConnectionDefinitionRequest(
            "HaloPSA",
            "halo-main",
            "Halo primary",
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string>());
        var first = await service.CreateAsync(
            "tenant-a", definitionRequest, new EntitySyncActor("seed"), default);
        var current = await service.UpdateAsync(
            "tenant-a",
            first.ConnectionId,
            first.Generation,
            definitionRequest with { DisplayName = "Halo rotated" },
            new EntitySyncActor("rotate"),
            default);
        Assert.Equal(first.Generation + 1, current.Generation);

        using var staleFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ConnectionDefinitionService>();
                services.AddSingleton(service);
                services.RemoveAll<IIdempotentCommandExecutor>();
                services.AddSingleton<IIdempotentCommandExecutor>(
                    new PassThroughIdempotentExecutor());
            }));
        using var client = staleFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/control/connections/halo-main/test")
        {
            Content = Json($$"""{"expectedGeneration":{{first.Generation}}}""")
        };
        request.Headers.Add(IdempotencyEndpointFilter.HeaderName, "stale-test-generation");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STATE_CONFLICT", await ProblemCode(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("halo-main", body, StringComparison.Ordinal);
        Assert.DoesNotContain("generation", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schedule_preview_returns_three_server_clocked_occurrences_without_state_access()
    {
        var baseline = new DateTimeOffset(2026, 3, 7, 7, 30, 0, TimeSpan.Zero);
        using var previewFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(baseline));
            }));
        using var client = previewFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview",
            Json("""{"cron_expression":"30 2 * * *","time_zone":"America/New_York"}"""));

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("occurrences", property.Name);
        Assert.Equal(
            [
                new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 10, 6, 30, 0, TimeSpan.Zero)
            ],
            property.Value.EnumerateArray()
                .Select(value => value.GetDateTimeOffset()).ToArray());
    }

    [Fact]
    public async Task Schedule_preview_request_rejects_caller_controlled_fields()
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview",
            Json(
                """{"cron_expression":"0 * * * *","time_zone":"UTC","count":4,"baseline":"2026-01-01T00:00:00Z"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ProblemCode(response));
    }

    [Theory]
    [InlineData(
        "{\"cron_expression\":\"MALFORMED_BINDER_SECRET\"",
        "MALFORMED_BINDER_SECRET")]
    [InlineData(
        "{\"cron_expression\":{\"WRONG_TYPE_BINDER_SECRET\":true},\"time_zone\":\"UTC\"}",
        "WRONG_TYPE_BINDER_SECRET")]
    [InlineData(
        "{\"cron_expression\":\"0 * * * *\",\"time_zone\":\"UTC\",\"UNKNOWN_BINDER_SECRET\":true}",
        "UNKNOWN_BINDER_SECRET")]
    public async Task Production_binding_failures_return_safe_control_problems(
        string requestBody,
        string secretMarker)
    {
        using var productionFactory = factory.WithWebHostBuilder(
            builder => builder.UseEnvironment(Environments.Production));
        using var client = productionFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview", Json(requestBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "https://entitysync.lisstech.com/problems/invalid_request",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "Invalid request",
            problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The request is invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(
            "INVALID_REQUEST",
            problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            problem.RootElement.GetProperty("correlationId").GetString()));
        var body = problem.RootElement.GetRawText();
        Assert.DoesNotContain(secretMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("BadHttpRequestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("failed to read parameter", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be converted", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shadow_projection_rejects_null_source_as_a_safe_bad_request()
    {
        using var routeFactory = new ControlApiFactory(null, executeControlCommands: true);
        using var client = routeFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Operate");
        client.DefaultRequestHeaders.Add(
            IdempotencyEndpointFilter.HeaderName,
            $"shadow-null-{Guid.NewGuid():N}");

        using var response = await client.PostAsync(
            "/api/v1/control/plans/shadow-projection",
            Json(
                $$"""
                {
                  "policy_id": "{{Guid.NewGuid():D}}",
                  "policy_version": 1,
                  "sources": [null],
                  "lifetime_minutes": 60
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ProblemCode(response));
        Assert.DoesNotContain(
            "NullReferenceException",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONTROL_API_SECRET_CRON", "UTC")]
    [InlineData("0 * * * *", "CONTROL_API_SECRET_TIME_ZONE")]
    public async Task Schedule_preview_validation_errors_are_safe(
        string cronExpression,
        string timeZone)
    {
        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");
        var request = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["cron_expression"] = cronExpression,
            ["time_zone"] = timeZone
        });

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview", Json(request));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ProblemCode(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(cronExpression, body, StringComparison.Ordinal);
        Assert.DoesNotContain(timeZone, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuiteQl_row_limit_above_one_thousand_is_a_safe_problem()
    {
        using var routeFactory = new ControlApiFactory(null, executeControlCommands: true);
        using var client = routeFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Expert");
        client.DefaultRequestHeaders.Add(
            IdempotencyEndpointFilter.HeaderName,
            $"suiteql-{Guid.NewGuid():N}");

        using var response = await client.PostAsync(
            "/api/v1/control/expert/suiteql",
            Json(
                """
                {
                  "connectionId": "netsuite-main",
                  "query": "SELECT id FROM customer",
                  "maximumRows": 1001
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await ProblemCode(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("1001", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentOutOfRangeException", body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Schedule_preview_no_future_occurrence_is_a_safe_problem()
    {
        using var previewFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(
                    new DateTimeOffset(9999, 3, 1, 0, 0, 0, TimeSpan.Zero)));
            }));
        using var client = previewFactory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Manage");

        using var response = await client.PostAsync(
            "/api/v1/control/schedules/preview",
            Json("""{"cron_expression":"0 0 29 2 *","time_zone":"UTC"}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STATE_CONFLICT", await ProblemCode(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("no future occurrence", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_is_Read_protected_and_has_unique_stable_operations_and_schemas()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);

        using var client = factory.CreateClient();
        AddClaims(client, "tid=tenant-a;oid=user-a;scp=EntitySync.Read");
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"operationId\": \"ListControlPlans\"", document);
        Assert.Contains("\"ConnectionResponse\"", document);
        Assert.Contains("\"PlanResponse\"", document);
        Assert.Contains("\"RunResponse\"", document);
        Assert.Contains("\"RunPageResponse\"", document);
        Assert.Equal(Inventory.Length,
            CountOccurrences(document, "\"operationId\": \""));
        using var openApi = JsonDocument.Parse(document);
        var preview = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/control/schedules/preview")
            .GetProperty("post");
        Assert.Equal(
            "PreviewControlSchedule",
            preview.GetProperty("operationId").GetString());
        Assert.False(preview.TryGetProperty("parameters", out _));
        var previewRequestBody = preview.GetProperty("requestBody");
        Assert.True(previewRequestBody.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/PreviewScheduleRequest",
            previewRequestBody
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString());
        Assert.Equal(
            "#/components/schemas/PreviewScheduleResponse",
            preview.GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString());
        var schemas = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var suiteQlRequest = schemas.GetProperty("SuiteQlRequest");
        var maximumRows = suiteQlRequest
            .GetProperty("properties")
            .GetProperty("maximumRows");
        Assert.Equal(1, maximumRows.GetProperty("minimum").GetInt32());
        Assert.Equal(1000, maximumRows.GetProperty("maximum").GetInt32());
        var shadowEntity = schemas.GetProperty("CanonicalShadowEntityRequest");
        var customFieldValues = shadowEntity
            .GetProperty("properties")
            .GetProperty("customFields")
            .GetProperty("additionalProperties");
        Assert.True(customFieldValues.GetProperty("nullable").GetBoolean());
        Assert.Equal(100, maximumRows.GetProperty("default").GetInt32());
        var previewRequest = schemas.GetProperty("PreviewScheduleRequest");
        Assert.Equal(
            ["cron_expression", "time_zone"],
            previewRequest.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["cron_expression", "time_zone"],
            previewRequest.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(previewRequest.GetProperty("additionalProperties").GetBoolean());
        var previewResponse = schemas.GetProperty("PreviewScheduleResponse");
        var occurrences = Assert.Single(
            previewResponse.GetProperty("properties").EnumerateObject());
        Assert.Equal("occurrences", occurrences.Name);
        Assert.Equal("array", occurrences.Value.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            occurrences.Value.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(
            "date-time",
            occurrences.Value.GetProperty("items").GetProperty("format").GetString());
        Assert.Equal(
            ["occurrences"],
            previewResponse.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        var runList = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/control/runs")
            .GetProperty("get");
        var parameterNames = runList.GetProperty("parameters")
            .EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("cursor", parameterNames);
        Assert.Contains("pageSize", parameterNames);
        Assert.DoesNotContain("offset", parameterNames);
        var pageSizeParameter = runList.GetProperty("parameters")
            .EnumerateArray()
            .Single(value =>
                value.GetProperty("name").GetString() == "pageSize");
        Assert.False(pageSizeParameter.TryGetProperty("required", out _));
        Assert.False(
            pageSizeParameter.GetProperty("schema")
                .TryGetProperty("default", out _));
        var runSchema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("RunResponse");
        Assert.True(runSchema.GetProperty("properties").TryGetProperty("queuedAt", out _));
        var runPageSchema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("RunPageResponse");
        Assert.True(
            runPageSchema.GetProperty("properties")
                .TryGetProperty("replayCursor", out _));
        Assert.Contains(
            "replayCursor",
            runPageSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public async Task Health_is_liveness_and_readiness_uses_control_dependencies()
    {
        using var client = factory.CreateClient();
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            factory.Services.GetRequiredService<EntitySyncOperationWorkerOptions>().LeaseDuration);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("databaseMigrations", body);
        Assert.Contains("keyRing", body);
        Assert.Contains("workerHeartbeat", body);
        Assert.DoesNotContain("vendor", body, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddClaims(HttpClient client, string claims, bool workload = false)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        client.DefaultRequestHeaders.Add("X-Test-Claims", claims);
        if (workload && !claims.Contains("azp=", StringComparison.Ordinal))
        {
            client.DefaultRequestHeaders.Remove("X-Test-Claims");
            client.DefaultRequestHeaders.Add("X-Test-Claims", claims.Replace("oid=user-a", "azp=app-a", StringComparison.Ordinal));
        }
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");
    private static async Task<HttpResponseMessage> SendCanonicalAsync(
        HttpClient client,
        string key,
        string body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/control/canonical-changes")
        {
            Content = Json(body)
        };
        request.Headers.Add(IdempotencyEndpointFilter.HeaderName, key);
        return await client.SendAsync(request);
    }

    private static string RoutePath(string template) =>
        template
            .Replace("{connectionId}", "connection-a", StringComparison.Ordinal)
            .Replace("{policyId:guid}", "00000000-0000-0000-0000-000000000001",
                StringComparison.Ordinal)
            .Replace("{planId:guid}", "00000000-0000-0000-0000-000000000001",
                StringComparison.Ordinal)
            .Replace("{runId:guid}", "00000000-0000-0000-0000-000000000001",
                StringComparison.Ordinal)
            .Replace("{scheduleId:guid}", "00000000-0000-0000-0000-000000000001",
                StringComparison.Ordinal)
            .Replace("{eventId:guid}", "00000000-0000-0000-0000-000000000001",
                StringComparison.Ordinal);


    private static async Task<string?> ProblemCode(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0;
             index += marker.Length) count++;
        return count;
    }
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

}

public sealed class ControlApiFactory : WebApplicationFactory<mcp::Program>
{
    private readonly string keyPath = Path.Combine(
        Path.GetTempPath(), $"entitysync-control-api-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> original =
        new(StringComparer.Ordinal);
    private readonly IEntitySyncControlCommands? controlCommands;
    private readonly bool executeControlCommands;
    private readonly bool preserveProductionQueries;

    public SensitiveLogRecorder LogRecorder { get; } = new();

    public ControlApiFactory()
        : this(null, false, null, false)
    {
    }

    internal ControlApiFactory(
        IEntitySyncControlCommands? controlCommands,
        bool executeControlCommands)
        : this(controlCommands, executeControlCommands, null, false)
    {
    }

    internal ControlApiFactory(
        string connectionString,
        bool preserveProductionQueries)
        : this(null, false, connectionString, preserveProductionQueries)
    {
    }

    private ControlApiFactory(
        IEntitySyncControlCommands? controlCommands,
        bool executeControlCommands,
        string? connectionString,
        bool preserveProductionQueries)
    {
        this.controlCommands = controlCommands;
        this.executeControlCommands = executeControlCommands;
        this.preserveProductionQueries = preserveProductionQueries;
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Set("MCP_TRANSPORT", "http");
        Set("MCP_OAUTH_AUTHORITY", "https://login.example.test/tenant/v2.0");
        Set("MCP_OAUTH_RESOURCE", "https://entitysync.example.test");
        Set("MCP_OAUTH_AUDIENCE", "api://entitysync-test");
        Set(
            "DATABASE_URL",
            connectionString
            ?? "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        Set(
            "ORCHESTRA_BASE_URL",
            "https://directory.example.test/api/v1/internal/client-directory/");
        Set("ORCHESTRA_AUTHORITY", "https://login.example.test/tenant");
        Set("ORCHESTRA_TENANT_ID", "tenant");
        Set("ORCHESTRA_CLIENT_ID", "control-api-test");
        Set("ORCHESTRA_RESOURCE", "api://orchestra-directory");
        Set("ORCHESTRA_CLIENT_SECRET", Guid.NewGuid().ToString("N"));
        Set("ENTITYSYNC_WORKER_LEASE_SECONDS", "60");
        Set("ENTITYSYNC_WORKER_HEARTBEAT_SECONDS", "10");
        Set("ENTITYSYNC_WORKER_RETRY_SECONDS", "5");
        Set("ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyPath);
        Set("ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST", "om-workload");
        Set("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "https://logfire-us.pydantic.dev/v1/logs");
        Set("OTEL_EXPORTER_OTLP_HEADERS", $"Authorization={Guid.NewGuid():N}");
        Set("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        Set("OTEL_SERVICE_NAME", "lisstech-entitysync-mcp-test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(LogRecorder));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.Scheme, _ => { });
            if (!preserveProductionQueries)
            {
                services.RemoveAll<IControlApiQueries>();
                services.AddSingleton<IControlApiQueries>(
                    DispatchProxy.Create<IControlApiQueries, EmptyQueryProxy>());
                services.RemoveAll<IIdempotentCommandExecutor>();
                services.AddSingleton<IIdempotentCommandExecutor>(
                    executeControlCommands
                        ? new PassThroughIdempotentExecutor()
                        : new RecordingIdempotentExecutor());
            }
            if (controlCommands is not null)
            {
                services.RemoveAll<IEntitySyncControlCommands>();
                services.AddSingleton(controlCommands);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
    }

    private void Set(string name, string value)
    {
        original[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class ReadyProbe : IControlReadinessProbe
    {
        public Task<ControlReadinessResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ControlReadinessResult(true, true, true));
    }
}

public sealed class PassThroughIdempotentExecutor : IIdempotentCommandExecutor
{
    public Task<IdempotentResponse> ExecuteAsync(
        string tenantId,
        string key,
        string requestHash,
        IdempotencyExecutionMode mode,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken) =>
        command(
            new IdempotencyExecutionContext(
                tenantId, key, $"endpoint-test:{key}"),
            cancellationToken);
}

public sealed class RecordingIdempotentExecutor : IIdempotentCommandExecutor
{
    private readonly object gate = new();
    private readonly Dictionary<(string TenantId, string Key), (string Hash, IdempotentResponse Response)> entries = [];

    public Task<IdempotentResponse> ExecuteAsync(
        string tenantId,
        string key,
        string requestHash,
        IdempotencyExecutionMode mode,
        Func<IdempotencyExecutionContext, CancellationToken, Task<IdempotentResponse>> command,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (entries.TryGetValue((tenantId, key), out var existing))
            {
                if (!string.Equals(existing.Hash, requestHash, StringComparison.Ordinal))
                    throw new IdempotencyConflictException("The idempotency key is bound to another request.");
                return Task.FromResult(existing.Response);
            }

            var response = new IdempotentResponse(
                StatusCodes.Status202Accepted,
                new EntitySyncJsonValue(
                    System.Text.Json.JsonSerializer.Serialize(new { tenantId, key })));
            entries[(tenantId, key)] = (requestHash, response);
            return Task.FromResult(response);
        }
    }
}

public class RunPagingQueryProxy : DispatchProxy
{
    private readonly DateTimeOffset highWater = new(
        2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly IReadOnlyList<RunResponse> runs;

    public RunPagingQueryProxy()
    {
        var planId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        runs =
        [
            Run("00000000-0000-0000-0000-000000000001", planId, highWater.AddMinutes(-1)),
            Run("00000000-0000-0000-0000-000000000002", planId, highWater.AddMinutes(-2)),
            Run("00000000-0000-0000-0000-000000000003", planId, highWater.AddMinutes(-3))
        ];
    }

    public static IControlApiQueries Create() =>
        DispatchProxy.Create<IControlApiQueries, RunPagingQueryProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IControlApiQueries.ListRunsAsync))
            throw new NotSupportedException(targetMethod?.Name);
        var cursor = (EntitySyncOperationListCursor?)args![1];
        var maximumRows = (int)args[2]!;
        var start = cursor?.LastOperationId is { } lastOperationId
            ? runs.Select((run, index) => (run, index))
                .Single(value => value.run.RunId == lastOperationId)
                .index + 1
            : 0;
        var result = new RunQueryResult(
            cursor?.HighWater ?? highWater,
            runs.Skip(start).Take(maximumRows).ToArray());
        return Task.FromResult(result);
    }

    private static RunResponse Run(
        string runId,
        Guid planId,
        DateTimeOffset queuedAt) =>
        new(
            Guid.Parse(runId),
            planId,
            null,
            "route-a",
            "DryRun",
            "Queued",
            0,
            1,
            0,
            0,
            0,
            0,
            queuedAt,
            null,
            null);
}

public class EmptyQueryProxy : DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod!.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>))
            throw new NotSupportedException(targetMethod.Name);
        var valueType = returnType.GetGenericArguments()[0];
        var value = Empty(valueType);
        return typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(valueType)
            .Invoke(null, [value]);
    }

    private static object? Empty(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type == typeof(RunQueryResult))
            return new RunQueryResult(DateTimeOffset.UnixEpoch, []);
        if (type == typeof(bool)) return false;
        return null;
    }
}

public class DependencyFailureControlProxy : DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IEntitySyncControlCommands.ListConnectionsAsync))
            throw new NotSupportedException(targetMethod?.Name);
        return Task.FromException<IReadOnlyList<EntitySyncConnectionDefinition>>(
            new EntitySyncDependencyUnavailableException(
                "The entity adapter is unavailable.",
                new InvalidOperationException("vendor-secret-response")));
    }
}

public sealed class SensitiveLogRecorder : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    public IReadOnlyCollection<string> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Recorder(entries);

    public void Clear() => entries.Clear();

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}

public sealed class HttpConnectionDefinitionRepository : IConnectionDefinitionRepository
{
    private EntitySyncConnectionDefinition? current;

    public Task<EntitySyncConnectionDefinition> InsertAsync(
        string tenantId,
        EntitySyncConnectionDefinition definition,
        CancellationToken cancellationToken)
    {
        current = definition;
        return Task.FromResult(definition);
    }

    public Task<EntitySyncConnectionDefinition?> GetAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            current is not null
            && current.TenantId.Equals(tenantId, StringComparison.Ordinal)
            && current.ConnectionId.Equals(connectionId, StringComparison.Ordinal)
                ? current
                : null);

    public Task<IReadOnlyList<EntitySyncConnectionDefinition>> ListAsync(
        string tenantId,
        string? vendor,
        bool? enabled,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntitySyncConnectionDefinition>>(
            current is not null && current.TenantId.Equals(tenantId, StringComparison.Ordinal)
                ? [current]
                : []);

    public Task<EntitySyncConnectionDefinition?> TryReplaceAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        EntitySyncConnectionDefinition nextGeneration,
        CancellationToken cancellationToken)
    {
        if (current is null
            || !current.TenantId.Equals(tenantId, StringComparison.Ordinal)
            || !current.ConnectionId.Equals(connectionId, StringComparison.Ordinal)
            || current.Generation != expectedGeneration)
            return Task.FromResult<EntitySyncConnectionDefinition?>(null);
        current = nextGeneration;
        return Task.FromResult<EntitySyncConnectionDefinition?>(nextGeneration);
    }

    public Task<ConnectionDefinitionDeleteResult> TryDeleteAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken) =>
        Task.FromResult(ConnectionDefinitionDeleteResult.NotFound);
}


public sealed class IdentityDataProtector : IEntitySyncDataProtector
{
    public string Protect(EntitySyncDataProtectionPurpose purpose, string plaintext) => plaintext;

    public string Unprotect(EntitySyncDataProtectionPurpose purpose, string ciphertext) =>
        ciphertext;
}


public sealed class NeverCreatingAdapterFactory : IServerManagedEntityAdapterFactory
{
    public Task<IEntityAdapter> CreateAsync(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings,
        CancellationToken cancellationToken) =>
        Task.FromException<IEntityAdapter>(
            new InvalidOperationException("adapter creation was not expected"));

    public Task<IEntityAdapter> CreateDurableAsync(
        string vendor,
        IReadOnlyDictionary<string, JsonElement> publicConfiguration,
        IReadOnlyDictionary<string, string> secretConfiguration,
        CancellationToken cancellationToken) =>
        Task.FromException<IEntityAdapter>(
            new InvalidOperationException("adapter creation was not expected"));

    public ServerManagedConnectionConfiguration GetConnectionConfiguration(
        string vendor,
        IReadOnlyDictionary<string, string>? profileSettings) =>
        throw new NotSupportedException();

    public void ValidateNetSuiteHaloFixedRouteConfiguration() =>
        throw new NotSupportedException();

    public string GetNetSuiteHaloChangeStateScope() =>
        throw new NotSupportedException();
}


public sealed class ThrowingConnectionRuntimeFactory(Exception error)
    : IConnectionRuntimeFactory
{
    public Task<IConnectionRuntimeLease> AcquireAsync(
        string tenantId,
        string connectionId,
        long expectedGeneration,
        CancellationToken cancellationToken) =>
        Task.FromException<IConnectionRuntimeLease>(error);

    public Task<IConnectionRuntimeLease> AcquireCurrentAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<EntitySyncConnectionDefinition> ResolveCurrentDefinitionAsync(
        string tenantId,
        string vendor,
        string? connectionId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}


public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ControlTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Claims", out var header))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = header.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Select(pair => new Claim(pair[0], pair.Length == 2 ? pair[1] : string.Empty));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme)));
    }
}

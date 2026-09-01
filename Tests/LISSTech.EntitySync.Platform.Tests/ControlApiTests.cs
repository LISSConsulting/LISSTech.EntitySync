extern alias mcp;

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp.ControlApi;
using LISSTech.EntitySync.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
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
        (HttpMethod.Get, "/api/v1/control/plans/{planId:guid}/items", ControlPolicies.Read),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/inspections", ControlPolicies.Operate),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/approvals", ControlPolicies.Approve),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/dry-run", ControlPolicies.Operate),
        (HttpMethod.Post, "/api/v1/control/plans/{planId:guid}/apply", ControlPolicies.Approve),
        (HttpMethod.Get, "/api/v1/control/runs", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/runs/{runId:guid}", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/runs/{runId:guid}/items", ControlPolicies.Read),
        (HttpMethod.Get, "/api/v1/control/schedules", ControlPolicies.Read),
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
    public void Only_database_only_mutations_use_atomic_database_idempotency()
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
                services.RemoveAll<IControlApiQueries>();
                services.AddSingleton<IControlApiQueries>(
                    DispatchProxy.Create<IControlApiQueries, DependencyFailureQueryProxy>());
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
        Assert.Equal(Inventory.Length,
            CountOccurrences(document, "\"operationId\": \""));
    }

    [Fact]
    public async Task Health_is_liveness_and_readiness_uses_control_dependencies()
    {
        using var client = factory.CreateClient();
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
}

public sealed class ControlApiFactory : WebApplicationFactory<mcp::Program>
{
    private readonly string keyPath = Path.Combine(
        Path.GetTempPath(), $"entitysync-control-api-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> original =
        new(StringComparer.Ordinal);
    private readonly IEntitySyncControlCommands? controlCommands;
    private readonly bool executeControlCommands;

    public SensitiveLogRecorder LogRecorder { get; } = new();

    public ControlApiFactory()
        : this(null, false)
    {
    }

    internal ControlApiFactory(
        IEntitySyncControlCommands? controlCommands,
        bool executeControlCommands)
    {
        this.controlCommands = controlCommands;
        this.executeControlCommands = executeControlCommands;
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Set("MCP_TRANSPORT", "http");
        Set("MCP_OAUTH_AUTHORITY", "https://login.example.test/tenant/v2.0");
        Set("MCP_OAUTH_RESOURCE", "https://entitysync.example.test");
        Set("MCP_OAUTH_AUDIENCE", "api://entitysync-test");
        Set("DATABASE_URL", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        Set("ENTITYSYNC_DATA_PROTECTION_KEY_PATH", keyPath);
        Set("ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST", "om-workload");
        Set("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "https://logfire-us.pydantic.dev/v1/logs");
        Set("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=test-token");
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
            services.RemoveAll<IControlApiQueries>();
            services.AddSingleton<IControlApiQueries>(
                DispatchProxy.Create<IControlApiQueries, EmptyQueryProxy>());
            services.RemoveAll<IControlReadinessProbe>();
            services.AddSingleton<IControlReadinessProbe>(new ReadyProbe());
            services.RemoveAll<IIdempotentCommandExecutor>();
            services.AddSingleton<IIdempotentCommandExecutor>(
                executeControlCommands
                    ? new PassThroughIdempotentExecutor()
                    : new RecordingIdempotentExecutor());
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
        if (type == typeof(bool)) return false;
        return null;
    }
}

public class DependencyFailureQueryProxy : DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        throw new EntitySyncDependencyUnavailableException(
            "The entity adapter is unavailable.",
            new InvalidOperationException("vendor-secret-response"));
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

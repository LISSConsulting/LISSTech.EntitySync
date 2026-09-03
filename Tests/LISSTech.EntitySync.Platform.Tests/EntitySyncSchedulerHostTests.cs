using System.Reflection;
using System.Text.Json;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[Collection(nameof(SchedulerHostEnvironmentCollection))]
public sealed class EntitySyncSchedulerHostTests
{
    private static readonly string[] ExpectedStatusFields =
    [
        "applySkipped",
        "changed",
        "error",
        "failed",
        "lastCompletedAt",
        "lastStartedAt",
        "nextRunAt",
        "planId",
        "policySkipped",
        "state",
        "succeeded",
        "total",
        "unchanged"
    ];

    [Fact]
    public void SchedulerAssemblyHasExecutableEntryPoint()
    {
        Assert.NotNull(typeof(EntitySyncSchedulerWorker).Assembly.EntryPoint);
    }

    [Fact]
    public async Task SchedulerHostBuildsWithCompleteValidatedDependencyGraph()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        Assert.IsType<EntitySyncSchedulerStatus>(app.Services.GetRequiredService<EntitySyncSchedulerStatus>());
        Assert.IsType<EntitySyncSchedulerDashboardStore>(app.Services.GetRequiredService<EntitySyncSchedulerDashboardStore>());
        Assert.IsType<EntitySyncScheduledRun>(app.Services.GetRequiredService<IEntitySyncScheduledRun>());
        Assert.IsType<PostgresEntitySyncSchedulerRunLock>(app.Services.GetRequiredService<IEntitySyncSchedulerRunLock>());
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var challengeScheme = await schemes.GetDefaultChallengeSchemeAsync();
        Assert.Equal(OpenIdConnectDefaults.AuthenticationScheme, challengeScheme?.Name);
        var hostedServices = app.Services.GetServices<IHostedService>().ToArray();
        var migrationIndex = Array.FindIndex(
            hostedServices,
            service => service is EntitySyncDatabaseMigrationHostedService);
        var workerIndex = Array.FindIndex(
            hostedServices,
            service => service is EntitySyncSchedulerWorker);
        Assert.True(migrationIndex >= 0);
        Assert.True(workerIndex > migrationIndex);
    }

    [Theory]
    [InlineData("NETSUITE_TOKEN_SECRET", null, "NETSUITE_TOKEN_SECRET")]
    [InlineData("HALO_CLIENT_SECRET", " ", "HALO_CLIENT_SECRET")]
    [InlineData("HALO_BASE_URL", "not-a-url", "HTTPS")]
    [InlineData("HALO_BASE_URL", "http://halo.example.test", "HTTPS")]
    [InlineData(EntitySyncSchedulerOptions.AutomaticRunsEnabledEnvironmentVariable, "disabled", "true or false")]
    public async Task SchedulerHostBuildRejectsInvalidChainConfiguration(
        string variableName,
        string? value,
        string expectedMessage)
    {
        await using var environment = SchedulerHostEnvironment.Create(
            variableName,
            value,
            automaticRunsEnabled: true);

        var error = Assert.Throws<TargetInvocationException>(BuildSchedulerApplication);
        var configurationError = Assert.IsType<InvalidOperationException>(error.InnerException);

        Assert.Contains(expectedMessage, configurationError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("scheduler token with whitespace 0123456789abcdef")]
    public async Task SchedulerHostBuildRejectsInvalidRunToken(string? token)
    {
        await using var environment = SchedulerHostEnvironment.Create(
            EntitySyncSchedulerRunAuthorization.EnvironmentVariable,
            token);

        var error = Assert.Throws<TargetInvocationException>(BuildSchedulerApplication);
        var configurationError = Assert.IsType<InvalidOperationException>(error.InnerException);

        Assert.Contains(
            EntitySyncSchedulerRunAuthorization.EnvironmentVariable,
            configurationError.Message,
            StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.TenantIdEnvironmentVariable, null, "required")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.TenantIdEnvironmentVariable, "not-a-guid", "GUID")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.ClientIdEnvironmentVariable, null, "required")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.ClientIdEnvironmentVariable, "not-a-guid", "GUID")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.ClientSecretEnvironmentVariable, null, "required")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.PublicOriginEnvironmentVariable, "http://dashboard.example.test", "HTTPS origin")]
    [InlineData(EntitySyncSchedulerDashboardAuthentication.PublicOriginEnvironmentVariable, "https://dashboard.example.test/path", "HTTPS origin")]
    public async Task SchedulerHostBuildRejectsInvalidDashboardAuthentication(
        string variableName,
        string? value,
        string expectedMessage)
    {
        await using var environment = SchedulerHostEnvironment.Create(variableName, value);

        var error = Assert.Throws<TargetInvocationException>(BuildSchedulerApplication);
        var configurationError = Assert.IsType<InvalidOperationException>(error.InnerException);

        Assert.Contains(expectedMessage, configurationError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/dashboard/data")]
    [InlineData("/status")]
    public async Task DashboardAndTelemetryEndpointsRequireEntraAuthentication(string route)
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        var authorization = FindEndpoint(app, route)
            .Metadata
            .GetOrderedMetadata<IAuthorizeData>();

        Assert.Contains(
            authorization,
            item => item.Policy == EntitySyncSchedulerDashboardAuthentication.PolicyName);
    }

    [Fact]
    public async Task AnonymousDashboardAssetRequestIsRedirectedToConfiguredEntraTenant()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();
        var pipeline = ((IApplicationBuilder)app).Build();
        using var requestScope = app.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = Uri.UriSchemeHttps;
        context.Request.Host = new HostString("dashboard.example.test");
        context.Request.Path = "/assets/index-test.js";

        await pipeline(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.StartsWith(
            "https://login.microsoftonline.com/c62ea180-bf49-4018-a795-cdc170ead90d/oauth2/v2.0/authorize",
            context.Response.Headers.Location,
            StringComparison.Ordinal);
        Assert.Contains(
            "redirect_uri=https%3A%2F%2Fdashboard.example.test%2Fsignin-oidc",
            context.Response.Headers.Location,
            StringComparison.Ordinal);
    }


    [Fact]
    public async Task StatusEndpointReturnsOnlyBoundedAggregateAllowlist()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();
        var status = app.Services.GetRequiredService<EntitySyncSchedulerStatus>();
        status.Publish(status.Snapshot with
        {
            State = "Failed",
            LastStartedAt = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero),
            LastCompletedAt = new DateTimeOffset(2026, 8, 28, 1, 3, 4, TimeSpan.Zero),
            NextRunAt = new DateTimeOffset(2026, 8, 28, 13, 3, 4, TimeSpan.Zero),
            PlanId = "plan-42",
            Total = 10,
            Changed = 7,
            Unchanged = 3,
            PolicySkipped = 1,
            Succeeded = 5,
            Failed = 1,
            ApplySkipped = 4,
            Error = new string('x', 600)
        });

        var response = await InvokeEndpointAsync(app, "/status");
        using var json = JsonDocument.Parse(response.Body);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(ExpectedStatusFields, json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(512, json.RootElement.GetProperty("error").GetString()!.Length);
        Assert.Equal("Failed", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(10, json.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task HealthEndpointRemainsHealthyWhenLatestRunFailed()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();
        var status = app.Services.GetRequiredService<EntitySyncSchedulerStatus>();
        status.Publish(status.Snapshot with
        {
            State = "Failed",
            LastCompletedAt = DateTimeOffset.UtcNow,
            Error = "Vendor connection setup or validation failed."
        });

        var response = await InvokeEndpointAsync(app, "/health");
        using var json = JsonDocument.Parse(response.Body);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Failed", status.Snapshot.State);
    }

    [Fact]
    public async Task DashboardEndpointReturnsBuiltReactApplication()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        var response = await InvokeEndpointAsync(app, "/");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.StartsWith("text/html", response.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store, max-age=0", response.CacheControl);
        Assert.Contains("default-src 'none'", response.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("<title>EntitySync Scheduler | LISS Technologies</title>", response.Body, StringComparison.Ordinal);
        Assert.Contains("<div id=\"root\"></div>", response.Body, StringComparison.Ordinal);
        Assert.Contains("type=\"module\"", response.Body, StringComparison.Ordinal);
        Assert.Contains("/assets/index-", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(EntitySyncSchedulerRunAuthorization.EnvironmentVariable, response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardDataEndpointReturnsCurrentOperationPlansRunsAndEvents()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();
        var dashboard = app.Services.GetRequiredService<EntitySyncSchedulerDashboardStore>();
        var status = app.Services.GetRequiredService<EntitySyncSchedulerStatus>();
        var startedAt = new DateTimeOffset(2026, 9, 2, 20, 0, 0, TimeSpan.Zero);
        dashboard.BeginRun(startedAt);
        dashboard.SetOperation("Apply", EntitySyncSchedulerOptions.HaloToBillCom, "plan-42");
        dashboard.RecordPlan("plan-42", EntitySyncSchedulerOptions.HaloToBillCom);
        dashboard.RecordPlanValidation("plan-42", 10, 2, 7, 1);
        dashboard.RecordPlanProgress("plan-42", 1, 0, 0);
        var completed = status.Snapshot with
        {
            State = "Applied",
            LastStartedAt = startedAt,
            LastCompletedAt = startedAt.AddMinutes(1),
            PlanId = "plan-42",
            Total = 10,
            Changed = 2,
            Unchanged = 7,
            PolicySkipped = 1,
            Succeeded = 2
        };
        status.Publish(completed);
        dashboard.CompletePlan("plan-42", "Applied", 2, 0, 0);
        dashboard.CompleteRun(completed);

        var response = await InvokeEndpointAsync(app, "/dashboard/data");
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("no-store, max-age=0", response.CacheControl);
        Assert.Equal(
            ["current", "currentOperation", "events", "generatedAt", "recentPlans", "recentRuns", "routes"],
            root.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("Applied", root.GetProperty("current").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentOperation").ValueKind);
        Assert.Equal(4, root.GetProperty("routes").GetArrayLength());
        Assert.Equal("plan-42", root.GetProperty("recentPlans")[0].GetProperty("planId").GetString());
        Assert.Equal(2, root.GetProperty("recentPlans")[0].GetProperty("changed").GetInt32());
        Assert.Equal("Applied", root.GetProperty("recentRuns")[0].GetProperty("state").GetString());
        Assert.Contains(
            root.GetProperty("events").EnumerateArray(),
            item => item.GetProperty("message").GetString() == "Plan applied: 2 succeeded, 0 failed, 0 skipped.");
    }

    [Fact]
    public void DashboardStoreBoundsProcessLocalHistory()
    {
        var status = new EntitySyncSchedulerStatus();
        var dashboard = new EntitySyncSchedulerDashboardStore(TimeProvider.System);
        for (var index = 0; index < 45; index++)
            dashboard.RecordPlan($"plan-{index}", EntitySyncSchedulerOptions.NetSuiteToHalo);
        for (var index = 0; index < 205; index++)
            dashboard.RecordEvent("Information", $"Event {index}.");
        for (var index = 0; index < 25; index++)
        {
            dashboard.CompleteRun(status.Snapshot with
            {
                State = "Applied",
                LastStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                LastCompletedAt = DateTimeOffset.UtcNow,
                PlanId = $"run-{index}"
            });
        }

        var snapshot = dashboard.Snapshot(
            status.Snapshot,
            new EntitySyncSchedulerOptions([EntitySyncSchedulerOptions.NetSuiteToHalo]));

        Assert.Equal(40, snapshot.RecentPlans.Count);
        Assert.Equal(24, snapshot.RecentRuns.Count);
        Assert.Equal(200, snapshot.Events.Count);
        Assert.Equal("plan-44", snapshot.RecentPlans[0].PlanId);
        Assert.Equal("run-24", snapshot.RecentRuns[0].PlanId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-scheduler-run-token-0123456789abcdef")]
    public async Task RunEndpointRejectsMissingOrIncorrectBearerWithoutQueuing(string? token)
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        var rejected = await InvokeEndpointAsync(app, "/run", HttpMethods.Post, token);
        var accepted = await InvokeEndpointAsync(
            app,
            "/run",
            HttpMethods.Post,
            SchedulerHostEnvironment.RunToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, rejected.StatusCode);
        Assert.Equal("Bearer", rejected.WwwAuthenticate);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task RunEndpointQueuesOnlyOneRunAndAdvertisesStatusLocation()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        var endpoint = FindEndpoint(app, "/run");
        var methods = Assert.IsAssignableFrom<IHttpMethodMetadata>(
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>());
        var first = await InvokeEndpointAsync(
            app,
            "/run",
            HttpMethods.Post,
            SchedulerHostEnvironment.RunToken);
        var second = await InvokeEndpointAsync(
            app,
            "/run",
            HttpMethods.Post,
            SchedulerHostEnvironment.RunToken);
        using var firstJson = JsonDocument.Parse(first.Body);
        using var secondJson = JsonDocument.Parse(second.Body);

        Assert.Equal([HttpMethods.Post], methods.HttpMethods);
        Assert.Equal(StatusCodes.Status202Accepted, first.StatusCode);
        Assert.Equal("/status", first.Location);
        Assert.True(firstJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("Queued", firstJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);
        Assert.False(secondJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("Busy", secondJson.RootElement.GetProperty("status").GetString());
    }

    private static WebApplication BuildSchedulerApplication()
    {
        var hostType = typeof(EntitySyncSchedulerWorker).Assembly.GetType(
            "LISSTech.EntitySync.Scheduler.EntitySyncSchedulerHost");
        Assert.NotNull(hostType);
        var build = hostType.GetMethod(
            "Build",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string[])]);
        Assert.NotNull(build);
        return Assert.IsType<WebApplication>(build.Invoke(null, [Array.Empty<string>()]));
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string route) =>
        ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == route);

    private static async Task<(
        int StatusCode,
        string Body,
        string? WwwAuthenticate,
        string? Location,
        string? ContentType,
        string? CacheControl,
        string? ContentSecurityPolicy)> InvokeEndpointAsync(
        WebApplication app,
        string route,
        string method = "GET",
        string? bearerToken = null)
    {
        var endpoint = FindEndpoint(app, route);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };
        context.Request.Method = method;
        if (bearerToken is not null)
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.WWWAuthenticate,
            context.Response.Headers.Location,
            context.Response.ContentType,
            context.Response.Headers.CacheControl,
            context.Response.Headers["Content-Security-Policy"]);
    }
}

[CollectionDefinition(nameof(SchedulerHostEnvironmentCollection), DisableParallelization = true)]
public sealed class SchedulerHostEnvironmentCollection;

internal sealed class SchedulerHostEnvironment : IAsyncDisposable
{
    public const string RunToken = "test-scheduler-run-token-0123456789abcdef";

    private static readonly IReadOnlyDictionary<string, string?> Values =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DATABASE_URL"] = "Host=127.0.0.1;Database=entitysync_test;Username=test;Password=test",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "https://logfire-us.pydantic.dev/v1/logs",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=test-token",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_SERVICE_NAME"] = "lisstech-entitysync-scheduler-test",
            [EntitySyncSchedulerRunAuthorization.EnvironmentVariable] = RunToken,
            [EntitySyncSchedulerOptions.AutomaticRunsEnabledEnvironmentVariable] = "false",
            [EntitySyncSchedulerDashboardAuthentication.TenantIdEnvironmentVariable] =
                "c62ea180-bf49-4018-a795-cdc170ead90d",
            [EntitySyncSchedulerDashboardAuthentication.ClientIdEnvironmentVariable] =
                "0f099a83-c826-4c36-9256-8edbe82b4182",
            [EntitySyncSchedulerDashboardAuthentication.ClientSecretEnvironmentVariable] =
                "test-dashboard-client-secret",
            [EntitySyncSchedulerDashboardAuthentication.PublicOriginEnvironmentVariable] =
                "https://dashboard.example.test",
            ["NETSUITE_ACCOUNT_ID"] = "test-account",
            ["NETSUITE_CONSUMER_KEY"] = "test-consumer-key",
            ["NETSUITE_CONSUMER_SECRET"] = "test-consumer-secret",
            ["NETSUITE_TOKEN_ID"] = "test-token-id",
            ["NETSUITE_TOKEN_SECRET"] = "test-token-secret",
            ["HALO_BASE_URL"] = "https://halo.example.test",
            ["HALO_CLIENT_ID"] = "test-client-id",
            ["HALO_CLIENT_SECRET"] = "test-client-secret",
            ["HALO_NCENTRAL_INTEGRATION_ID"] = "7",
            ["NCENTRAL_BASE_URL"] = "https://ncentral.example.test",
            ["NCENTRAL_USER_API_TOKEN"] = "test-ncentral-token",
            ["NCENTRAL_SERVICE_ORG_ID"] = "test-service-org",
            ["NCENTRAL_SOAP_USERNAME"] = "test-soap-user",
            ["NCENTRAL_SOAP_PASSWORD"] = "test-soap-password",
            ["BILLCOM_BASE_URL"] = "https://bill.example.test",
            ["BILLCOM_API_TOKEN"] = "test-bill-token",
            ["BILLCOM_CLIENT_FIELD_NAME"] = "Client",
            ["SOPHOS_CENTRAL_CLIENT_ID"] = "test-sophos-client-id",
            ["SOPHOS_CENTRAL_CLIENT_SECRET"] = "test-sophos-client-secret"
        };

    private readonly Dictionary<string, string?> originalValues;

    private SchedulerHostEnvironment(Dictionary<string, string?> originalValues)
    {
        this.originalValues = originalValues;
    }

    public static SchedulerHostEnvironment Create(
        string? overriddenVariableName = null,
        string? overriddenValue = null,
        bool automaticRunsEnabled = false)
    {
        var variableNames = overriddenVariableName is null
            ? Values.Keys
            : Values.Keys.Append(overriddenVariableName);
        var originals = variableNames
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
        foreach (var pair in Values) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (automaticRunsEnabled)
        {
            Environment.SetEnvironmentVariable(
                EntitySyncSchedulerOptions.AutomaticRunsEnabledEnvironmentVariable,
                "true");
        }
        if (overriddenVariableName is not null)
        {
            Environment.SetEnvironmentVariable(overriddenVariableName, overriddenValue);
        }
        return new SchedulerHostEnvironment(originals);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var pair in originalValues) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        return ValueTask.CompletedTask;
    }
}

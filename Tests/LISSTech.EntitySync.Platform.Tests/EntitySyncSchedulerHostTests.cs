using System.Reflection;
using System.Text.Json;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Scheduler;
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
        Assert.IsType<EntitySyncScheduledRun>(app.Services.GetRequiredService<IEntitySyncScheduledRun>());
        Assert.IsType<PostgresEntitySyncSchedulerRunLock>(app.Services.GetRequiredService<IEntitySyncSchedulerRunLock>());
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
    public async Task SchedulerHostBuildRejectsInvalidFixedRouteConfiguration(
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
        string? Location)> InvokeEndpointAsync(
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
            context.Response.Headers.Location);
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
        string? overriddenValue = null)
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

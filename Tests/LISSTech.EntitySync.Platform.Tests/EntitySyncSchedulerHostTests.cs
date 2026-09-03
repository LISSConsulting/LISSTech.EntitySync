using System.Reflection;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Scheduler;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[Collection(nameof(SchedulerHostEnvironmentCollection))]
public sealed class EntitySyncSchedulerHostTests
{
    [Fact]
    public void SchedulerAssemblyHasExecutableEntryPoint()
    {
        Assert.NotNull(typeof(EntitySyncControlWorker).Assembly.EntryPoint);
    }

    [Fact]
    public async Task SchedulerHostBuildsWithDurableControlWorkers()
    {
        await using var environment = SchedulerHostEnvironment.Create();
        await using var app = BuildSchedulerApplication();

        Assert.IsType<PostgresSyncWorkQueue>(
            app.Services.GetRequiredService<ICanonicalChangeRepository>());
        Assert.IsType<PostgresRouteLock>(
            app.Services.GetRequiredService<IEntitySyncRouteLock>());
        Assert.NotNull(app.Services.GetRequiredService<CanonicalChangeService>());
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            app.Services.GetRequiredService<EntitySyncOperationWorkerOptions>().LeaseDuration);
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        var migration = Array.FindIndex(
            hosted, service => service is EntitySyncDatabaseMigrationHostedService);
        var control = Array.FindIndex(
            hosted, service => service is EntitySyncControlWorker);
        var retention = Array.FindIndex(
            hosted, service => service is AuditRetentionWorker);
        Assert.True(migration >= 0);
        Assert.True(control > migration);
        Assert.True(retention > control);
    }

    [Theory]
    [InlineData("Api", "ORCHESTRA_BASE_URL", "Production", "true", false)]
    [InlineData("Scheduler", "ORCHESTRA_BASE_URL", "Testing", null, false)]
    [InlineData("Api", "ORCHESTRA_BASE_URL", "Testing", "true", true)]
    [InlineData("Scheduler", "ORCHESTRA_AUTHORITY", "Production", "true", false)]
    [InlineData("Api", "ORCHESTRA_AUTHORITY", "Development", null, false)]
    [InlineData("Scheduler", "ORCHESTRA_AUTHORITY", "Development", "true", true)]
    public void Api_and_scheduler_gate_loopback_Orchestra_uris_for_tests_only(
        string host,
        string uriVariable,
        string environmentName,
        string? testOverride,
        bool shouldPass)
    {
        var environment = ValidOrchestraEnvironment();
        environment[uriVariable] = uriVariable == "ORCHESTRA_BASE_URL"
            ? "http://127.0.0.1:18083/api/v1/internal/client-directory/"
            : "http://127.0.0.1:18083/tenant";
        environment["ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"] = testOverride;

        var error = Record.Exception(() =>
            EntitySyncProductionConfiguration.ValidateOrchestra(
                environment,
                environmentName));

        Assert.True(
            shouldPass == (error is null),
            $"{host} {uriVariable} validation differed in {environmentName}.");
        if (error is not null)
            Assert.Contains("ENTITYSYNC_CONFIG_URI_INVALID", error.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ValidOrchestraEnvironment() =>
        new(StringComparer.Ordinal)
        {
            ["ORCHESTRA_BASE_URL"] =
                "https://directory.example.test/api/v1/internal/client-directory/",
            ["ORCHESTRA_AUTHORITY"] = "https://login.example.test/tenant",
            ["ORCHESTRA_TENANT_ID"] = "tenant",
            ["ORCHESTRA_CLIENT_ID"] = "client",
            ["ORCHESTRA_RESOURCE"] = "api://orchestra-directory",
            ["ORCHESTRA_CLIENT_SECRET"] = Guid.NewGuid().ToString("N")
        };

    private static WebApplication BuildSchedulerApplication()
    {
        var hostType = typeof(EntitySyncControlWorker).Assembly.GetType(
            "LISSTech.EntitySync.Scheduler.EntitySyncSchedulerHost");
        Assert.NotNull(hostType);
        var build = hostType.GetMethod(
            "Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string[])]);
        Assert.NotNull(build);
        return Assert.IsType<WebApplication>(build.Invoke(null, [Array.Empty<string>()]));
    }
}

[CollectionDefinition(nameof(SchedulerHostEnvironmentCollection), DisableParallelization = true)]
public sealed class SchedulerHostEnvironmentCollection;

internal sealed class SchedulerHostEnvironment : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string?> Values =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DATABASE_URL"] =
                "Host=127.0.0.1;Database=entitysync_test;Username=test;Password=test",
            ["ENTITYSYNC_DATA_PROTECTION_KEY_PATH"] =
                Path.Combine(Path.GetTempPath(), "entitysync-scheduler-host-tests"),
            ["ENTITYSYNC_TENANT_IDS"] = "tenant-a,tenant-b",
            ["ENTITYSYNC_WORKER_LEASE_SECONDS"] = "30",
            ["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"] = "10",
            ["ENTITYSYNC_WORKER_RETRY_SECONDS"] = "5",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] =
                "https://logfire-us.pydantic.dev/v1/logs",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization={Guid.NewGuid():N}",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_SERVICE_NAME"] = "lisstech-entitysync-scheduler-test"
        };

    private readonly Dictionary<string, string?> originalValues;

    private SchedulerHostEnvironment(Dictionary<string, string?> originalValues) =>
        this.originalValues = originalValues;

    public static SchedulerHostEnvironment Create()
    {
        var keyPath = Values["ENTITYSYNC_DATA_PROTECTION_KEY_PATH"]!;
        Directory.CreateDirectory(keyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                keyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originals = Values.Keys.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach (var pair in Values)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        return new SchedulerHostEnvironment(originals);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var pair in originalValues)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        return ValueTask.CompletedTask;
    }
}

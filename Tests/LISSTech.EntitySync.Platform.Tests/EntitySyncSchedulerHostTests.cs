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
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] =
                "https://logfire-us.pydantic.dev/v1/logs",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=test-token",
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

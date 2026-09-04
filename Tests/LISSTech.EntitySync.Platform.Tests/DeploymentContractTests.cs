using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class DeploymentContractTests
{
    private static readonly string Repository =
        Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private static readonly JsonElement Compose = RenderCompose();

    [Fact]
    public void Api_and_scheduler_share_only_the_entitysync_database_and_keyring()
    {
        var api = Service("entitysync-mcp");
        var scheduler = Service("entitysync-scheduler");
        var apiEnvironment = EnvironmentOf(api);
        var schedulerEnvironment = EnvironmentOf(scheduler);

        Assert.Equal(schedulerEnvironment["DATABASE_URL"], apiEnvironment["DATABASE_URL"]);
        Assert.Contains("entitysync-db", apiEnvironment["DATABASE_URL"], StringComparison.Ordinal);
        Assert.DoesNotContain("orchestra", apiEnvironment["DATABASE_URL"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/var/lib/entitysync/keys", apiEnvironment["ENTITYSYNC_DATA_PROTECTION_KEY_PATH"]);
        Assert.Equal("/var/lib/entitysync/keys", schedulerEnvironment["ENTITYSYNC_DATA_PROTECTION_KEY_PATH"]);
        AssertKeyringMount(api);
        AssertKeyringMount(scheduler);

        foreach (var environment in new[] { apiEnvironment, schedulerEnvironment })
        {
            Assert.DoesNotContain(environment.Keys, name =>
                name.StartsWith("ORCHESTRA_", StringComparison.Ordinal));
            Assert.DoesNotContain(environment.Keys, name =>
                name.Equals("ORCHESTRAMSP_DATABASE_URL", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Api_declares_exact_oauth_permissions_and_workload_allowlist()
    {
        var environment = EnvironmentOf(Service("entitysync-mcp"));

        Assert.Equal("https://login.example.invalid/tenant/v2.0", environment["MCP_OAUTH_AUTHORITY"]);
        Assert.Equal("https://entitysync.example.invalid/mcp", environment["MCP_OAUTH_RESOURCE"]);
        Assert.Equal("api://entitysync", environment["MCP_OAUTH_AUDIENCE"]);
        Assert.Equal(
            "EntitySync.Read EntitySync.Operate EntitySync.Approve EntitySync.Manage EntitySync.Audit EntitySync.Expert",
            environment["MCP_OAUTH_SCOPES"]);
        Assert.Equal("EntitySync.Read", environment["MCP_OAUTH_REQUIRED_SCOPE"]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", environment["ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST"]);
    }

    [Fact]
    public void Migrations_and_worker_heartbeat_gate_api_readiness()
    {
        var scheduler = Service("entitysync-scheduler");
        var api = Service("entitysync-mcp");

        Assert.Equal("service_healthy", DependencyCondition(scheduler, "entitysync-db"));
        Assert.Equal("service_healthy", DependencyCondition(api, "entitysync-db"));
        Assert.Equal("service_healthy", DependencyCondition(api, "entitysync-scheduler"));
        Assert.Contains("/health", HealthCommand(scheduler), StringComparison.Ordinal);
        Assert.DoesNotContain("/health/ready", HealthCommand(scheduler), StringComparison.Ordinal);
        Assert.Contains("/health/ready", HealthCommand(api), StringComparison.Ordinal);
        Assert.Equal("10", EnvironmentOf(scheduler)["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"]);
    }

    [Fact]
    public void Production_services_are_nonroot_readonly_and_bounded()
    {
        foreach (var name in new[] { "entitysync-db", "entitysync-scheduler", "entitysync-mcp" })
        {
            var service = Service(name);
            var user = service.GetProperty("user").GetString();
            Assert.False(string.IsNullOrWhiteSpace(user));
            Assert.NotEqual("0", user);
            Assert.NotEqual("root", user);
            Assert.True(service.GetProperty("read_only").GetBoolean());
            Assert.Equal("unless-stopped", service.GetProperty("restart").GetString());
            Assert.Contains("ALL", Strings(service.GetProperty("cap_drop")));
            Assert.Contains("no-new-privileges:true", Strings(service.GetProperty("security_opt")));
        }

        foreach (var name in new[] { "entitysync-scheduler", "entitysync-mcp" })
        {
            var service = Service(name);
            Assert.True(service.GetProperty("init").GetBoolean());
            Assert.Contains(
                "/tmp:size=64m,mode=1777,uid=1654,gid=1654",
                Strings(service.GetProperty("tmpfs")));
        }
    }

    [Fact]
    public void Production_compose_has_no_source_docker_socket_or_credential_mounts()
    {
        foreach (var name in new[] { "entitysync-db", "entitysync-scheduler", "entitysync-mcp" })
        {
            var service = Service(name);
            var rendered = service.GetRawText();
            Assert.DoesNotContain("/var/run/docker.sock", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("credential", rendered, StringComparison.OrdinalIgnoreCase);
            if (!service.TryGetProperty("volumes", out var volumes)) continue;
            foreach (var volume in volumes.EnumerateArray())
                Assert.NotEqual("bind", volume.GetProperty("type").GetString());
        }

        var volumesObject = Compose.GetProperty("volumes");
        var volumeNames = volumesObject.EnumerateObject().Select(item => item.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "entitysync-db-data", "entitysync-keyring" },
            volumeNames);
    }

    [Fact]
    public void Release_smoke_uses_the_production_keyring_path_as_numeric_user()
    {
        var workflow = File.ReadAllText(
            Path.Combine(Repository, ".github/workflows/release-mcp.yml"));

        Assert.DoesNotContain("/var/lib/entitysync-dataprotection", workflow);
        Assert.Equal(
            2,
            CountOccurrences(
                workflow,
                "--mount \"type=volume,source=$key_volume,target=/var/lib/entitysync/keys\""));
        Assert.Equal(
            2,
            CountOccurrences(
                workflow,
                "--env ENTITYSYNC_DATA_PROTECTION_KEY_PATH=/var/lib/entitysync/keys"));
        Assert.Contains(
            "docker exec --user 1654:1654 \"$api_container\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker exec --user 1654:1654 \"$scheduler_container\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(workflow, "test -w /var/lib/entitysync/keys"));
    }

    [Fact]
    public void Readme_commands_preserve_the_env_file_and_expand_database_identity_in_container()
    {
        var readme = File.ReadAllText(Path.Combine(Repository, "mcp/README.md"));
        var composeCommands = readme.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("docker compose", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(composeCommands);
        Assert.All(
            composeCommands,
            command => Assert.Contains(
                "--env-file \"$ENTITYSYNC_ENV_FILE\"",
                command,
                StringComparison.Ordinal));
        Assert.Contains(
            "sh -c 'pg_isready -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\"'",
            readme,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;

    private static void AssertKeyringMount(JsonElement service)
    {
        var mounts = service.GetProperty("volumes").EnumerateArray().ToArray();
        var keyring = Assert.Single(mounts, mount =>
            mount.GetProperty("target").GetString() == "/var/lib/entitysync/keys");
        Assert.Equal("volume", keyring.GetProperty("type").GetString());
        Assert.Equal("entitysync-keyring", keyring.GetProperty("source").GetString());
        Assert.False(
            keyring.TryGetProperty("read_only", out var readOnly) && readOnly.GetBoolean());
    }

    private static string DependencyCondition(JsonElement service, string dependency) =>
        service.GetProperty("depends_on").GetProperty(dependency).GetProperty("condition").GetString()
        ?? string.Empty;

    private static string HealthCommand(JsonElement service) =>
        string.Join(' ', Strings(service.GetProperty("healthcheck").GetProperty("test")));

    private static Dictionary<string, string> EnvironmentOf(JsonElement service) =>
        service.GetProperty("environment").EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();

    private static JsonElement Service(string name) =>
        Compose.GetProperty("services").GetProperty(name);

    private static JsonElement RenderCompose()
    {
        var repository = Repository;
        var envFile = Path.Combine(Path.GetTempPath(), $"entitysync-compose-{Guid.NewGuid():N}.env");
        string[] environmentLines =
        [
            "DATABASE_URL=Host=entitysync-db;Port=5432;Database=entitysync;Username=entitysync;Password=placeholder",
            "ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST=00000000-0000-0000-0000-000000000001",
            "ENTITYSYNC_TENANT_IDS=00000000-0000-0000-0000-000000000002",
            "ENTITYSYNC_WORKER_HEARTBEAT_SECONDS=10",
            "ENTITYSYNC_WORKER_LEASE_SECONDS=60",
            "ENTITYSYNC_WORKER_RETRY_SECONDS=5",
            "MCP_OAUTH_AUDIENCE=api://entitysync",
            "MCP_OAUTH_AUTHORITY=https://login.example.invalid/tenant/v2.0",
            "MCP_OAUTH_RESOURCE=https://entitysync.example.invalid/mcp",
            "MCP_OAUTH_REQUIRED_SCOPE=EntitySync.Read",
            "MCP_OAUTH_SCOPES=EntitySync.Read EntitySync.Operate EntitySync.Approve EntitySync.Manage EntitySync.Audit EntitySync.Expert",
            "OTEL_EXPORTER_OTLP_HEADERS=k=v",
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=https://telemetry.example.invalid/v1/logs",
            "POSTGRES_PASSWORD=x"
        ];
        File.WriteAllLines(envFile, environmentLines);
        try
        {
            var start = new ProcessStartInfo("docker")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var line in environmentLines)
                start.Environment.Remove(line[..line.IndexOf('=')]);
            foreach (var argument in new[]
                     {
                         "compose", "--env-file", envFile, "-f", "docker-compose.yaml",
                         "config", "--format", "json"
                     })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Docker Compose did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(envFile);
        }
    }
}

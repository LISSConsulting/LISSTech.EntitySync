using System.Globalization;
using System.Net;

namespace LISSTech.EntitySync.Hosting;

public sealed record EntitySyncWorkerSettings(
    TimeSpan LeaseDuration,
    TimeSpan HeartbeatInterval,
    TimeSpan RetryInterval)
{
    public TimeSpan MaximumHeartbeatAge => TimeSpan.FromTicks(
        checked(HeartbeatInterval.Ticks * 3));

    public static EntitySyncWorkerSettings FromCurrentEnvironment() =>
        FromEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ENTITYSYNC_WORKER_LEASE_SECONDS"] =
                Environment.GetEnvironmentVariable("ENTITYSYNC_WORKER_LEASE_SECONDS"),
            ["ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"] =
                Environment.GetEnvironmentVariable("ENTITYSYNC_WORKER_HEARTBEAT_SECONDS"),
            ["ENTITYSYNC_WORKER_RETRY_SECONDS"] =
                Environment.GetEnvironmentVariable("ENTITYSYNC_WORKER_RETRY_SECONDS")
        });

    public static EntitySyncWorkerSettings FromEnvironment(
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var lease = ReadSeconds(
            environment, "ENTITYSYNC_WORKER_LEASE_SECONDS", 30, 600);
        var heartbeat = ReadSeconds(
            environment, "ENTITYSYNC_WORKER_HEARTBEAT_SECONDS", 1, 30);
        var retry = ReadSeconds(
            environment, "ENTITYSYNC_WORKER_RETRY_SECONDS", 1, 60);
        if (heartbeat >= lease)
        {
            throw new InvalidOperationException(
                "ENTITYSYNC_WORKER_HEARTBEAT_SECONDS must be less than " +
                "ENTITYSYNC_WORKER_LEASE_SECONDS [ENTITYSYNC_CONFIG_WORKER_INTERVAL_INVALID].");
        }

        return new EntitySyncWorkerSettings(lease, heartbeat, retry);
    }

    private static TimeSpan ReadSeconds(
        IReadOnlyDictionary<string, string?> environment,
        string name,
        int minimum,
        int maximum)
    {
        environment.TryGetValue(name, out var configured);
        if (!int.TryParse(
                configured?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            || seconds < minimum
            || seconds > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be an integer from {minimum} through {maximum} " +
                "[ENTITYSYNC_CONFIG_WORKER_INTERVAL_INVALID].");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}

public static class EntitySyncProductionConfiguration
{
    private static readonly string[] RequiredOrchestraValues =
    [
        "ORCHESTRA_TENANT_ID",
        "ORCHESTRA_CLIENT_ID",
        "ORCHESTRA_RESOURCE",
        "ORCHESTRA_CLIENT_SECRET"
    ];

    public static void ValidateOrchestraCurrentEnvironment(string environmentName) =>
        ValidateOrchestra(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ORCHESTRA_BASE_URL"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_BASE_URL"),
                ["ORCHESTRA_AUTHORITY"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_AUTHORITY"),
                ["ORCHESTRA_TENANT_ID"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_TENANT_ID"),
                ["ORCHESTRA_CLIENT_ID"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_CLIENT_ID"),
                ["ORCHESTRA_RESOURCE"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_RESOURCE"),
                ["ORCHESTRA_CLIENT_SECRET"] =
                    Environment.GetEnvironmentVariable("ORCHESTRA_CLIENT_SECRET"),
                ["ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA"] =
                    Environment.GetEnvironmentVariable(
                        "ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA")
            },
            environmentName);

    public static void ValidateOrchestra(
        IReadOnlyDictionary<string, string?> environment,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var baseUrl = Require(environment, "ORCHESTRA_BASE_URL");
        var authority = Require(environment, "ORCHESTRA_AUTHORITY");
        foreach (var name in RequiredOrchestraValues) Require(environment, name);
        environment.TryGetValue(
            "ENTITYSYNC_TEST_ALLOW_HTTP_ORCHESTRA",
            out var allowInsecureValue);
        var allowLoopbackHttp =
            (environmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase)
             || environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
            && bool.TryParse(allowInsecureValue, out var allowInsecure)
            && allowInsecure;
        ValidateServiceUri(
            baseUrl,
            "ORCHESTRA_BASE_URL",
            requireDirectoryPath: true,
            allowLoopbackHttp);
        ValidateServiceUri(
            authority,
            "ORCHESTRA_AUTHORITY",
            requireDirectoryPath: false,
            allowLoopbackHttp);
    }

    private static string Require(
        IReadOnlyDictionary<string, string?> environment,
        string name)
    {
        environment.TryGetValue(name, out var configured);
        var value = configured?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is required [ENTITYSYNC_CONFIG_REQUIRED].");
        }

        return value;
    }

    private static void ValidateServiceUri(
        string value,
        string name,
        bool requireDirectoryPath,
        bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(allowLoopbackHttp
                     && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     && IPAddress.TryParse(uri.Host, out var address)
                     && IPAddress.IsLoopback(address)))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (requireDirectoryPath
                && !uri.AbsolutePath.EndsWith(
                    "/api/v1/internal/client-directory/",
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{name} must be a safe HTTPS service URI " +
                "[ENTITYSYNC_CONFIG_URI_INVALID].");
        }
    }
}

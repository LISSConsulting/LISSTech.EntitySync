using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace LISSTech.EntitySync.Mcp;

internal sealed class LogfireLoggingSettings
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "logfire-us.pydantic.dev",
        "logfire-eu.pydantic.dev"
    };

    private LogfireLoggingSettings(
        Uri logsEndpoint,
        string authorizationHeader,
        string serviceName,
        string deploymentEnvironment,
        string serviceVersion)
    {
        LogsEndpoint = logsEndpoint;
        AuthorizationHeader = authorizationHeader;
        ServiceName = serviceName;
        DeploymentEnvironment = deploymentEnvironment;
        ServiceVersion = serviceVersion;
    }

    internal Uri LogsEndpoint { get; }
    internal string AuthorizationHeader { get; }
    internal string ServiceName { get; }
    internal string DeploymentEnvironment { get; }
    internal string ServiceVersion { get; }

    internal static LogfireLoggingSettings FromCurrentEnvironment(
        string deploymentEnvironment,
        string serviceVersion) => FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"),
            ["OTEL_EXPORTER_OTLP_HEADERS"] = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL"),
            ["OTEL_SERVICE_NAME"] = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
        },
        deploymentEnvironment,
        serviceVersion);

    internal static LogfireLoggingSettings FromEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string deploymentEnvironment,
        string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var endpointText = Require(environment, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", trim: true);
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || !endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !AllowedHosts.Contains(endpoint.Host)
            || endpoint.AbsolutePath != "/v1/logs"
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT must be the official Logfire US or EU HTTPS /v1/logs endpoint.");
        }

        var protocol = Require(environment, "OTEL_EXPORTER_OTLP_PROTOCOL", trim: true);
        if (!protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_PROTOCOL must be http/protobuf for Logfire.");

        var authorizationHeader = Require(environment, "OTEL_EXPORTER_OTLP_HEADERS", trim: false);
        const string authorizationPrefix = "Authorization=";
        if (!authorizationHeader.StartsWith(authorizationPrefix, StringComparison.Ordinal)
            || authorizationHeader.Length == authorizationPrefix.Length
            || authorizationHeader[authorizationPrefix.Length..].Any(character => char.IsWhiteSpace(character) || character == ','))
        {
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_HEADERS must contain exactly one Logfire Authorization write token.");
        }

        var serviceName = Require(environment, "OTEL_SERVICE_NAME", trim: true);
        deploymentEnvironment = RequireValue(deploymentEnvironment, nameof(deploymentEnvironment));
        serviceVersion = RequireValue(serviceVersion, nameof(serviceVersion));
        return new LogfireLoggingSettings(endpoint, authorizationHeader, serviceName, deploymentEnvironment, serviceVersion);
    }

    public override string ToString() =>
        $"LogfireLoggingSettings(LogsEndpoint={LogsEndpoint},ServiceName={ServiceName},DeploymentEnvironment={DeploymentEnvironment},ServiceVersion={ServiceVersion})";

    private static string Require(
        IReadOnlyDictionary<string, string?> environment,
        string variableName,
        bool trim)
    {
        if (!environment.TryGetValue(variableName, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variableName} is required for Logfire logging.");
        return trim ? value.Trim() : value;
    }

    private static string RequireValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required for Logfire logging.");
        return value.Trim();
    }
}

internal static class LogfireLogging
{
    internal static void Configure(ILoggingBuilder logging, LogfireLoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(settings);

        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.UseUtcTimestamp = true;
        });
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(settings.ServiceName, serviceVersion: settings.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = settings.DeploymentEnvironment
                }));
            options.AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = settings.LogsEndpoint;
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                exporter.Headers = settings.AuthorizationHeader;
            });
        });
    }
}

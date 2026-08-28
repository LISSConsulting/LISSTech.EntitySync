using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LISSTech.EntitySync.Hosting;

public sealed class LogfireLoggingSettings
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
        TracesEndpoint = new Uri(logsEndpoint, "/v1/traces");
        AuthorizationHeader = authorizationHeader;
        ServiceName = serviceName;
        DeploymentEnvironment = deploymentEnvironment;
        ServiceVersion = serviceVersion;
    }

    public Uri LogsEndpoint { get; }
    public Uri TracesEndpoint { get; }
    public string AuthorizationHeader { get; }
    public string ServiceName { get; }
    public string DeploymentEnvironment { get; }
    public string ServiceVersion { get; }

    public static LogfireLoggingSettings FromCurrentEnvironment(
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

    public static LogfireLoggingSettings FromEnvironment(
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

public static class LogfireLogging
{
    public static void Configure(
        IServiceCollection services,
        ILoggingBuilder logging,
        LogfireLoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
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
            options.SetResourceBuilder(AddServiceResource(ResourceBuilder.CreateDefault(), settings));
            options.AddOtlpExporter(exporter =>
                ConfigureExporter(exporter, settings.LogsEndpoint, settings));
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => AddServiceResource(resource, settings))
            .WithTracing(tracing =>
            {
                AddAspNetCoreRequestTracing(tracing);
                tracing.AddOtlpExporter(exporter =>
                    ConfigureExporter(exporter, settings.TracesEndpoint, settings));
            });
    }

    public static TracerProviderBuilder AddAspNetCoreRequestTracing(TracerProviderBuilder tracing)
    {
        ArgumentNullException.ThrowIfNull(tracing);
        return tracing.AddAspNetCoreInstrumentation(options =>
        {
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                var clientAddress = ResolveClientAddress(request);
                if (clientAddress is not null) activity.SetTag("client.address", clientAddress);
            };
        });
    }

    private static string? ResolveClientAddress(HttpRequest request)
    {
        var forwardedFor = request.Headers["X-Forwarded-For"];
        for (var valueIndex = forwardedFor.Count - 1; valueIndex >= 0; valueIndex--)
        {
            var value = forwardedFor[valueIndex];
            if (string.IsNullOrWhiteSpace(value)) continue;

            var remaining = value.AsSpan();
            while (!remaining.IsEmpty)
            {
                var separator = remaining.LastIndexOf(',');
                var candidate = remaining[(separator + 1)..].Trim();
                if (IPAddress.TryParse(candidate, out var address)) return FormatAddress(address);
                if (separator < 0) break;
                remaining = remaining[..separator];
            }
        }

        return FormatAddress(request.HttpContext.Connection.RemoteIpAddress);
    }

    private static string? FormatAddress(IPAddress? address)
    {
        if (address is null) return null;
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static ResourceBuilder AddServiceResource(
        ResourceBuilder resource,
        LogfireLoggingSettings settings) =>
        resource
            .AddService(settings.ServiceName, serviceVersion: settings.ServiceVersion)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment.name"] = settings.DeploymentEnvironment
            });

    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        Uri endpoint,
        LogfireLoggingSettings settings)
    {
        exporter.Endpoint = endpoint;
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Headers = settings.AuthorizationHeader;
    }
}

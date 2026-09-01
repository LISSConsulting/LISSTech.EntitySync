using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record IdempotencyExecutionMetadata(IdempotencyExecutionMode Mode);

public sealed class IdempotencyEndpointFilter(
    IIdempotentCommandExecutor executor) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    private static readonly object ExecutionTokenKey = new();
    private static readonly object RecoveryKey = new();

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var http = invocationContext.HttpContext;
        if (!http.Request.Headers.TryGetValue(HeaderName, out var keys)
            || keys.Count != 1
            || string.IsNullOrWhiteSpace(keys[0]))
            return ControlProblem.Create(
                http,
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_REQUIRED",
                $"A single non-empty {HeaderName} header is required.");
        var key = keys[0]!.Trim();
        if (key.Length > 200)
            return ControlProblem.Create(
                http,
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_INVALID",
                $"{HeaderName} cannot exceed 200 characters.");

        var requestHash = ComputeRequestHash(invocationContext);
        var control = http.RequestServices.GetRequiredService<ControlRequestContext>();
        var response = await executor.ExecuteAsync(
            control.TenantId,
            key,
            requestHash.Value,
            http.GetEndpoint()?.Metadata.GetMetadata<IdempotencyExecutionMetadata>()?.Mode
                ?? IdempotencyExecutionMode.Recoverable,
            async (execution, cancellationToken) =>
            {
                http.Items[ExecutionTokenKey] = execution.Token;
                http.Items[RecoveryKey] = execution.IsRecovery;
                var endpointResult = await next(invocationContext).ConfigureAwait(false);
                if (endpointResult is not IResult result)
                    throw new InvalidOperationException(
                        "Idempotent control endpoints must return an HTTP result.");
                return await CaptureAsync(http, result, cancellationToken)
                    .ConfigureAwait(false);
            },
            http.RequestAborted).ConfigureAwait(false);
        return Results.Content(
            response.ResponseBody.Json,
            "application/json",
            Encoding.UTF8,
            response.StatusCode);
    }

    public static string GetExecutionToken(HttpContext context) =>
        context.Items.TryGetValue(ExecutionTokenKey, out var value) && value is string token
            ? token
            : throw new InvalidOperationException(
                "The idempotent execution token is unavailable.");

    public static bool IsRecovery(HttpContext context) =>
        context.Items.TryGetValue(RecoveryKey, out var value) && value is true;

    private static EntitySyncSha256 ComputeRequestHash(
        EndpointFilterInvocationContext invocationContext)
    {
        var context = invocationContext.HttpContext;
        var body = invocationContext.Arguments.FirstOrDefault(argument =>
            argument is not null
            && argument.GetType().Namespace == typeof(IdempotencyEndpointFilter).Namespace
            && argument.GetType().Name.EndsWith("Request", StringComparison.Ordinal));
        var bodyHash = body is null
            ? EntitySyncCanonicalDigest.Compute(new { Empty = true }).Value
            : EntitySyncCanonicalDigest.Compute(body).Value;

        var query = context.Request.Query
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                Name = pair.Key,
                Values = pair.Value.Order(StringComparer.Ordinal).ToArray()
            })
            .ToArray();
        var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant() ?? "/";
        return EntitySyncCanonicalDigest.Compute(new
        {
            Method = context.Request.Method.ToUpperInvariant(),
            Path = path.Length == 0 ? "/" : path,
            Query = query,
            BodySha256 = bodyHash
        });
    }

    private static async Task<IdempotentResponse> CaptureAsync(
        HttpContext source,
        IResult result,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        var target = new DefaultHttpContext
        {
            RequestServices = source.RequestServices,
            TraceIdentifier = source.TraceIdentifier
        };
        target.RequestAborted = cancellationToken;
        target.Response.Body = stream;
        await result.ExecuteAsync(target).ConfigureAwait(false);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) json = "null";
        return new IdempotentResponse(
            target.Response.StatusCode,
            new EntitySyncJsonValue(json));
    }
}

internal static class ControlProblem
{
    public static IResult Create(
        HttpContext context,
        int status,
        string code,
        string detail,
        Guid? operationId = null,
        Guid? runId = null) =>
        Results.Problem(
            statusCode: status,
            title: Title(status),
            detail: detail,
            type: $"https://entitysync.lisstech.com/problems/{code.ToLowerInvariant()}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier,
                ["operationId"] = operationId,
                ["runId"] = runId
            });

    private static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Invalid request",
        StatusCodes.Status401Unauthorized => "Authentication required",
        StatusCodes.Status403Forbidden => "Permission denied",
        StatusCodes.Status404NotFound => "Resource not found",
        StatusCodes.Status409Conflict => "Request conflict",
        StatusCodes.Status503ServiceUnavailable => "Dependency unavailable",
        _ => "Request failed"
    };
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public static class ControlRoles
{
    public const string Read = "EntitySync.Read";
    public const string Operate = "EntitySync.Operate";
    public const string Approve = "EntitySync.Approve";
    public const string Manage = "EntitySync.Manage";
    public const string Audit = "EntitySync.Audit";
    public const string Expert = "EntitySync.Expert";
}

public static class ControlPolicies
{
    public const string Read = "control.read";
    public const string Operate = "control.operate";
    public const string Approve = "control.approve";
    public const string Manage = "control.manage";
    public const string Audit = "control.audit";
    public const string Expert = "control.expert";
    public const string CanonicalChanges = "control.canonical-changes";
}

public static class ControlAuthorization
{
    public const string WorkloadAllowlistEnvironmentVariable =
        "ENTITYSYNC_OM_WORKLOAD_AZP_ALLOWLIST";

    public static void AddPolicies(AuthorizationOptions options, IEnumerable<string> allowedWorkloads)
    {
        ArgumentNullException.ThrowIfNull(options);
        var allowlist = allowedWorkloads
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);

        AddPermission(options, ControlPolicies.Read, ControlRoles.Read);
        AddPermission(options, ControlPolicies.Operate, ControlRoles.Operate);
        AddPermission(options, ControlPolicies.Approve, ControlRoles.Approve);
        AddPermission(options, ControlPolicies.Manage, ControlRoles.Manage);
        AddPermission(options, ControlPolicies.Audit, ControlRoles.Audit);
        AddPermission(options, ControlPolicies.Expert, ControlRoles.Expert);
        options.AddPolicy(ControlPolicies.CanonicalChanges, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(authorization =>
                ControlRequestContext.TryCreate(authorization.User, out var context)
                && context!.ActorKind == ControlActorKind.Workload
                && context.HasPermission(ControlRoles.Operate)
                && allowlist.Contains(context.ActorId)));
    }

    public static string[] ReadWorkloadAllowlist() =>
        (Environment.GetEnvironmentVariable(WorkloadAllowlistEnvironmentVariable) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddPermission(
        AuthorizationOptions options,
        string policyName,
        string permission) =>
        options.AddPolicy(policyName, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(authorization =>
                ControlRequestContext.TryCreate(authorization.User, out var context)
                && context!.HasPermission(permission)));
}

public sealed class ControlAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var controlSurface =
            context.Request.Path.StartsWithSegments("/api/v1/control")
            || context.Request.Path.StartsWithSegments("/openapi");
        if (!controlSurface)
            return fallback.HandleAsync(next, context, policy, authorizeResult);
        if (authorizeResult.Challenged)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return ControlProblem.Create(
                context,
                StatusCodes.Status401Unauthorized,
                "AUTHENTICATION_REQUIRED",
                "A valid bearer access token is required.").ExecuteAsync(context);
        }
        if (authorizeResult.Forbidden)
            return ControlProblem.Create(
                context,
                StatusCodes.Status403Forbidden,
                "PERMISSION_DENIED",
                "The access token does not satisfy this endpoint policy.")
                .ExecuteAsync(context);
        return fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}

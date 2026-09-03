using System.Security.Claims;
using LISSTech.EntitySync.Core;

namespace LISSTech.EntitySync.Mcp.ControlApi;

public enum ControlActorKind
{
    Delegated,
    Workload
}

public sealed record ControlRequestContext(
    string TenantId,
    string ActorId,
    ControlActorKind ActorKind,
    IReadOnlySet<string> Permissions)
{
    public EntitySyncActor Actor => new(ActorId);

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    public static ControlRequestContext Create(ClaimsPrincipal principal) =>
        TryCreate(principal, out var context)
            ? context!
            : throw new InvalidOperationException("The authenticated control identity is ambiguous or incomplete.");

    public static bool TryCreate(ClaimsPrincipal? principal, out ControlRequestContext? context)
    {
        context = null;
        if (principal?.Identity?.IsAuthenticated != true) return false;

        var tenants = principal.FindAll("tid").Select(claim => claim.Value).ToArray();
        if (tenants.Length != 1 || string.IsNullOrWhiteSpace(tenants[0])) return false;

        var delegatedScopes = principal.FindAll("scp").ToArray();
        var applicationRoles = principal.FindAll("roles").ToArray();
        if ((delegatedScopes.Length == 0) == (applicationRoles.Length == 0)) return false;

        var objectIds = principal.FindAll("oid").Select(claim => claim.Value).ToArray();
        var applicationIds = principal.FindAll("azp").Select(claim => claim.Value).ToArray();
        ControlActorKind kind;
        string actor;
        IEnumerable<string> permissions;
        if (delegatedScopes.Length > 0)
        {
            if (objectIds.Length != 1 || applicationIds.Length != 0
                || string.IsNullOrWhiteSpace(objectIds[0])) return false;
            kind = ControlActorKind.Delegated;
            actor = objectIds[0];
            permissions = delegatedScopes.SelectMany(claim => claim.Value.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else
        {
            if (applicationIds.Length != 1 || objectIds.Length != 0
                || string.IsNullOrWhiteSpace(applicationIds[0])) return false;
            kind = ControlActorKind.Workload;
            actor = applicationIds[0];
            permissions = applicationRoles.Select(claim => claim.Value.Trim())
                .Where(value => value.Length > 0);
        }

        context = new ControlRequestContext(
            tenants[0].Trim(),
            actor.Trim(),
            kind,
            permissions.ToHashSet(StringComparer.Ordinal));
        return true;
    }
}

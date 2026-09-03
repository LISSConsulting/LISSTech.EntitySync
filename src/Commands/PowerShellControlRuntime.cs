using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace LISSTech.EntitySync.Commands;

public sealed record EntitySyncControlConnectionInfo(
    string ConnectionId,
    string Vendor,
    string DisplayName,
    long Generation,
    bool Enabled,
    Guid? PlatformInstanceId)
{
    internal static EntitySyncControlConnectionInfo From(
        EntitySyncConnectionDefinition definition) =>
        new(
            definition.ConnectionId,
            definition.Vendor,
            definition.DisplayName,
            definition.Generation,
            definition.Enabled,
            definition.PlatformInstanceId);
}

internal static class PowerShellControlRuntime
{
    private static readonly object Gate = new();
    private static IServiceProvider? provider;
    private static string? providerKey;

    internal static bool IsDurableConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL"));

    internal static void RejectUnsafeLocalOrchestraApply(bool apply, string targetVendor)
    {
        if (apply && EntitySyncVendors.IsOrchestraMSP(targetVendor))
            throw new InvalidOperationException(
                "ORCHESTRA_DURABLE_CONTROL_REQUIRED: OrchestraMSP writes require a durable control operation. Use -PlanId, -IdempotencyKey, -ApprovalId, and -Apply.");
    }

    internal static PowerShellControlLease Open()
    {
        var tenantId = RequireEnvironment("ENTITYSYNC_TENANT_ID");
        var actorId = RequireEnvironment("ENTITYSYNC_ACTOR_ID");
        var connectionString = RequireEnvironment("DATABASE_URL");
        var keyPath = RequireEnvironment("ENTITYSYNC_DATA_PROTECTION_KEY_PATH");
        var key = string.Concat(connectionString, "\n", keyPath);
        IServiceProvider current;
        lock (Gate)
        {
            if (provider is null || !string.Equals(providerKey, key, StringComparison.Ordinal))
            {
                (provider as IDisposable)?.Dispose();
                var services = new ServiceCollection();
                services.AddEntitySyncPlatform(connectionString, EntitySyncHostMode.Http);
                provider = services.BuildServiceProvider(validateScopes: true);
                providerKey = key;
            }
            current = provider;
        }

        var scope = current.CreateScope();
        return new PowerShellControlLease(scope, tenantId, new EntitySyncActor(actorId));
    }

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required for durable PowerShell control operations.");
        return value.Trim();
    }
}

internal sealed class PowerShellControlLease(
    IServiceScope scope,
    string tenantId,
    EntitySyncActor actor) : IDisposable
{
    internal string TenantId { get; } = tenantId;
    internal EntitySyncActor Actor { get; } = actor;
    internal IEntitySyncControlCommands Commands =>
        scope.ServiceProvider.GetRequiredService<IEntitySyncControlCommands>();
    internal ConnectionDefinitionService Connections =>
        scope.ServiceProvider.GetRequiredService<ConnectionDefinitionService>();
    internal IServerManagedEntityAdapterFactory AdapterFactory =>
        scope.ServiceProvider.GetRequiredService<IServerManagedEntityAdapterFactory>();
    internal IConnectionRuntimeFactory Runtime =>
        scope.ServiceProvider.GetRequiredService<IConnectionRuntimeFactory>();
    internal IDurableSyncPlanRepository Plans =>
        scope.ServiceProvider.GetRequiredService<IDurableSyncPlanRepository>();
    internal ISyncOperationRepository Operations =>
        scope.ServiceProvider.GetRequiredService<ISyncOperationRepository>();
    internal IEntitySyncDataProtector DataProtection =>
        scope.ServiceProvider.GetRequiredService<IEntitySyncDataProtector>();

    internal PowerShellConnectionLease AcquireConnection(
        string vendor,
        string? connectionId)
    {
        var lease = Runtime.AcquireCurrentAsync(
                TenantId,
                EntitySyncVendors.Normalize(vendor),
                connectionId,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        return new PowerShellConnectionLease(this, lease);
    }

    internal void DisposeScope() => scope.Dispose();

    public void Dispose() => scope.Dispose();
}

internal sealed class PowerShellConnectionLease : IDisposable
{
    private readonly PowerShellControlLease? control;
    private readonly IConnectionRuntimeLease? durable;
    private readonly IEntityConnectionLease? local;

    private PowerShellConnectionLease(IEntityConnectionLease local)
    {
        this.local = local;
        Adapter = local.Connection.Adapter;
    }

    internal PowerShellConnectionLease(
        PowerShellControlLease control,
        IConnectionRuntimeLease durable)
    {
        this.control = control;
        this.durable = durable;
        Adapter = durable.Adapter;
    }

    internal IEntityAdapter Adapter { get; }

    internal static PowerShellConnectionLease Acquire(
        string vendor,
        string? connectionId)
    {
        if (PowerShellControlRuntime.IsDurableConfigured)
        {
            var control = PowerShellControlRuntime.Open();
            try
            {
                return control.AcquireConnection(vendor, connectionId);
            }
            catch
            {
                control.Dispose();
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException(
                "-ConnectionId requires durable PowerShell control configuration.");
        return new PowerShellConnectionLease(ConnectionRegistry.Acquire(vendor));
    }

    public void Dispose()
    {
        if (durable is not null)
            durable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        local?.Dispose();
        control?.DisposeScope();
    }
}

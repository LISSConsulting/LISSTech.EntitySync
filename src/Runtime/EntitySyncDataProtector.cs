using LISSTech.EntitySync.Ports;
using Microsoft.AspNetCore.DataProtection;

namespace LISSTech.EntitySync.Runtime;

public sealed class EntitySyncDataProtector : IEntitySyncDataProtector
{
    private readonly IDataProtector connectionSecretProtector;
    private readonly IDataProtector auditValueProtector;
    private readonly IDataProtector durablePlanArtifactProtector;
    public EntitySyncDataProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        connectionSecretProtector = provider.CreateProtector("connection-secret-v1");
        auditValueProtector = provider.CreateProtector("audit-value-v1");
        durablePlanArtifactProtector =
            provider.CreateProtector("durable-plan-artifact-v1");
    }

    public string Protect(EntitySyncDataProtectionPurpose purpose, string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            throw new ArgumentException("Plaintext is required.", nameof(plaintext));
        return Select(purpose).Protect(plaintext);
    }

    public string Unprotect(EntitySyncDataProtectionPurpose purpose, string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
            throw new ArgumentException("Ciphertext is required.", nameof(ciphertext));
        return Select(purpose).Unprotect(ciphertext);
    }

    private IDataProtector Select(EntitySyncDataProtectionPurpose purpose) => purpose switch
    {
        EntitySyncDataProtectionPurpose.ConnectionSecret => connectionSecretProtector,
        EntitySyncDataProtectionPurpose.AuditValue => auditValueProtector,
        EntitySyncDataProtectionPurpose.DurablePlanArtifact =>
            durablePlanArtifactProtector,
        _ => throw new ArgumentOutOfRangeException(
            nameof(purpose), purpose, "Unknown data-protection purpose.")
    };
}

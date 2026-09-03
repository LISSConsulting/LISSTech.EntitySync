using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LISSTech.EntitySync.Core;

public static class EntitySyncCanonicalDigest
{
    public static EntitySyncSha256 Compute<T>(T canonicalValue)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        var json = JsonSerializer.Serialize(canonicalValue);
        return new EntitySyncSha256(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
}

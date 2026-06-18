using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Runtime;

public sealed class EntitySyncConnection
{
    public string Vendor { get; set; } = string.Empty;
    public IEntityAdapter Adapter { get; set; } = default!;
}

using System.Management.Automation;

namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncTopLevel
{
    public string Vendor { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public PSObject? Raw { get; set; }
}

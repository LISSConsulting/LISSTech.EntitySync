namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncProgress
{
    public string Activity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int PercentComplete { get; set; } = -1;
}

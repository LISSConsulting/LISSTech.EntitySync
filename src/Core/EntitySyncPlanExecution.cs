namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncPlanExecution
{
    public string SourceConnectionId { get; set; } = string.Empty;
    public long SourceConnectionGeneration { get; set; }
    public string TargetConnectionId { get; set; } = string.Empty;
    public long TargetConnectionGeneration { get; set; }
    public MatchOptions MatchOptions { get; set; } = new();
}

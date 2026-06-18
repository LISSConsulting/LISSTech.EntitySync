namespace LISSTech.EntitySync.Core;

public sealed class EntitySyncPlanItem
{
    public string Action { get; set; } = "Review";
    public ExternalEntity Source { get; set; } = new();
    public ExternalEntity? Target { get; set; }
    public int Score { get; set; }
    public string MatchType { get; set; } = "NoMatch";
    public List<string> Reasons { get; set; } = new();
    public string Status { get; set; } = "Planned";
}

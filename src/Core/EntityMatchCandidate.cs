namespace LISSTech.EntitySync.Core;

public sealed class EntityMatchCandidate
{
    public ExternalEntity Source { get; set; } = new();
    public ExternalEntity Target { get; set; } = new();
    public int Score { get; set; }
    public string MatchType { get; set; } = "NoMatch";
    public List<string> Reasons { get; set; } = new();
}

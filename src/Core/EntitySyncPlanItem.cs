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
    public string? DesiredStateHash { get; set; }
    public int? DesiredStateHashVersion { get; set; }
}

public sealed record EntityFieldChange
{
    public EntityFieldChange(
        string field,
        EntitySyncJsonValue before,
        EntitySyncJsonValue desired,
        EntitySyncSha256 beforeSha256,
        EntitySyncSha256 desiredSha256,
        bool sensitive)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new ArgumentException("Field is required.", nameof(field));
        Field = field.Trim();
        Before = before ?? throw new ArgumentNullException(nameof(before));
        Desired = desired ?? throw new ArgumentNullException(nameof(desired));
        BeforeSha256 = beforeSha256 ?? throw new ArgumentNullException(nameof(beforeSha256));
        DesiredSha256 = desiredSha256 ?? throw new ArgumentNullException(nameof(desiredSha256));
        Sensitive = sensitive;
    }

    public string Field { get; }
    public EntitySyncJsonValue Before { get; }
    public EntitySyncJsonValue Desired { get; }
    public EntitySyncSha256 BeforeSha256 { get; }
    public EntitySyncSha256 DesiredSha256 { get; }
    public bool Sensitive { get; }
}

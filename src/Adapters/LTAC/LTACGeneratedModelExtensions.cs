using System.Text.Json.Serialization;

namespace LISSTech.EntitySync.Adapters.LTAC;

public partial class LTACCustomerScopeRequest
{
    [JsonIgnore]
    public string DisplayName
    {
        get => Display_name;
        set => Display_name = value;
    }

    [JsonIgnore]
    public string NCentralCustomerId
    {
        get => Ncentral_customer_id;
        set => Ncentral_customer_id = value;
    }

    [JsonIgnore]
    public string? NCentralParentCustomerId
    {
        get => Ncentral_parent_customer_id;
        set => Ncentral_parent_customer_id = value;
    }
}

public partial class LTACSyncResult
{
    [JsonIgnore]
    public int InsertedCount => Inserted_count;

    [JsonIgnore]
    public int UpdatedCount => Updated_count;

    [JsonIgnore]
    public int RetiredCount => Retired_count;

    [JsonIgnore]
    public int ActiveCount => Active_count;

    [JsonIgnore]
    public string? AuditEventId => Audit_event_id?.ToString();
}

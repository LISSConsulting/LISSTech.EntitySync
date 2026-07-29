namespace LISSTech.EntitySync.Adapters.BillCom;

public sealed class BillComOptions
{
    public string BaseUrl { get; set; } = "https://gateway.prod.bill.com/connect/v3/spend/custom-fields";
    public string ApiToken { get; set; } = string.Empty;
    public string ClientFieldName { get; set; } = "Client";
}

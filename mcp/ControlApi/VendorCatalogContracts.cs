namespace LISSTech.EntitySync.Mcp.ControlApi;

public sealed record VendorCatalogItem(
    string Vendor,
    string DisplayName,
    IReadOnlyList<string> EntityTypes);

public sealed record VendorCatalogResponse(IReadOnlyList<VendorCatalogItem> Items);

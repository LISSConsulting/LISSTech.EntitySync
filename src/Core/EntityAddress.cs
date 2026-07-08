namespace LISSTech.EntitySync.Core;

public sealed class EntityAddress
{
    public string? Attention { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? Line3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }

    public string Compact()
    {
        return string.Join(" ", new[] { Attention, Line1, Line2, Line3, City, State, PostalCode, Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}

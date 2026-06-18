using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.Halo;

public sealed class HaloEntityAdapter : IEntityAdapter, IDisposable
{
    private readonly HaloOptions options;
    private readonly HttpClient httpClient;

    public HaloEntityAdapter(HaloOptions options)
    {
        this.options = options;
        httpClient = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl)) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Vendor => "HaloPSA";

    public async Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Client", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("HaloPSA adapter currently supports EntityType Client.");
        var url = new StringBuilder("api/client?includeinactive=").Append(query.IncludeInactive ? "true" : "false");
        url.Append("&toplevel_id=").Append(options.TopLevelId);
        if (!string.IsNullOrWhiteSpace(query.Search)) url.Append("&search=").Append(Uri.EscapeDataString(query.Search));
        if (query.Count.HasValue) url.Append("&count=").Append(query.Count.Value);
        else url.Append("&count=9999");

        using var response = await httpClient.GetAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var clients = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("clients", out var array) ? array : root;
        var entities = new List<ExternalEntity>();
        foreach (var item in clients.EnumerateArray())
        {
            var entity = MapClient(item);
            if (query.FullObjects && !string.IsNullOrWhiteSpace(entity.Id)) entity = await GetFullClientAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            entities.Add(entity);
        }
        return entities;
    }

    public async Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new[] { ToHaloClientPayload(request, false) });
        using var response = await httpClient.PostAsync("api/client", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Action = "Create", Success = response.IsSuccessStatusCode, Message = response.IsSuccessStatusCode ? null : text, Raw = text };
    }

    public async Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new[] { ToHaloClientPayload(request, true) });
        using var response = await httpClient.PostAsync("api/client", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult { Vendor = Vendor, EntityType = request.EntityType, Id = request.Id, Action = "Update", Success = response.IsSuccessStatusCode, Message = response.IsSuccessStatusCode ? null : text, Raw = text };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/client?count=1", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<ExternalEntity> GetFullClientAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/client/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array && clients.GetArrayLength() > 0)
        {
            return MapClient(clients[0]);
        }
        return MapClient(root);
    }

    private ExternalEntity MapClient(JsonElement item)
    {
        var entity = new ExternalEntity
        {
            Vendor = Vendor,
            EntityType = "Client",
            Id = item.GetString("id", "client_id") ?? string.Empty,
            Name = item.GetString("name", "client_name") ?? string.Empty,
            Email = item.GetString("emailaddress", "email"),
            Phone = item.GetString("phonenumber", "phone"),
            Website = item.GetString("website"),
            Domain = EntityNormalizer.NormalizeDomain(item.GetString("website"), item.GetString("emailaddress", "email")),
            IsActive = item.GetBool("active", "isactive"),
            BillingAddress = new EntityAddress
            {
                Line1 = ReadNestedString(item, "invoice_address", "line1"),
                Line2 = ReadNestedString(item, "invoice_address", "line2"),
                City = ReadNestedString(item, "invoice_address", "line3"),
                State = ReadNestedString(item, "invoice_address", "line4"),
                PostalCode = ReadNestedString(item, "invoice_address", "postcode")
            }
        };

        if (item.TryGetProperty("customfields", out var customFields) && customFields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in customFields.EnumerateArray())
            {
                var name = field.GetString("name", "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var value = field.GetString("value", "Value");
                entity.CustomFields[name] = value;
                if (name.Equals(options.NetSuiteCustomerIdField, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    entity.ExternalIds["NetSuiteInternalId"] = value;
                }
            }
        }

        return entity;
    }

    private static string? ReadNestedString(JsonElement item, string objectName, string propertyName)
    {
        return item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object ? nested.GetString(propertyName) : null;
    }

    private Dictionary<string, object?> ToHaloClientPayload(EntityWriteRequest request, bool includeId)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (includeId) payload["id"] = request.Id;
        payload["name"] = request.Name;
        payload["toplevel_id"] = options.TopLevelId;
        payload["colour"] = options.DefaultColour;
        foreach (var field in request.Fields) payload[field.Key] = field.Value;
        if (request.CustomFields.Count > 0)
        {
            payload["customfields"] = request.CustomFields.Select(field => new Dictionary<string, object?> { ["name"] = field.Key, ["value"] = field.Value }).ToArray();
        }
        return payload;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
}

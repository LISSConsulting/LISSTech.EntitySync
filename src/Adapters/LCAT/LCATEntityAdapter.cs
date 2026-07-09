using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.LCAT;

public sealed class LCATCustomerScopeRequest
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NCentralCustomerId { get; set; } = string.Empty;
    public string? NCentralParentCustomerId { get; set; }
}

public sealed class LCATSyncResult
{
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RetiredCount { get; set; }
    public int ActiveCount { get; set; }
    public string? AuditEventId { get; set; }
}

public sealed class LCATEntityAdapter : IEntityAdapter, IDisposable
{
    private const string SyncReason = "EntitySync N-central to LCAT sync";
    private const string SyncPath = "rpc/sync_ncentral_customers";
    private static readonly Regex LcatSlugPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}[A-Za-z0-9]$", RegexOptions.Compiled);

    private readonly LCATOptions options;
    private readonly HttpClient httpClient = new();

    public LCATEntityAdapter(LCATOptions options)
    {
        this.options = options;
        httpClient.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    }

    public string Vendor => "LCAT";

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("LCAT adapter currently supports EntityType Customer.");

        // No LCAT customer-scope list/read endpoint is defined in the sync RPC contract
        // (contracts/lcat-sync-rpc.md); returning an empty set lets N-central sources plan as
        // create/sync candidates per contracts/powershell-command-contract.md.
        return Task.FromResult<IReadOnlyList<ExternalEntity>>(Array.Empty<ExternalEntity>());
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LCAT does not support per-item create. Apply an approved plan through the LCAT batch sync path.");
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LCAT does not support per-item update. Apply an approved plan through the LCAT batch sync path.");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LCAT connection test failed.", string.Empty);
        }
    }

    public async Task<LCATSyncResult> SyncCustomerScopesAsync(IReadOnlyList<LCATCustomerScopeRequest> customers, CancellationToken cancellationToken)
    {
        var normalizedCustomers = NormalizeCustomerScopeRequests(customers);
        EnsureCustomerScopeContract(normalizedCustomers);
        var body = BuildSyncRequestBody(normalizedCustomers);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        Trace?.Invoke("LCAT POST " + SyncPath);

        try
        {
            using var response = await httpClient.PostAsync(SyncPath, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw CreateRedactedAdapterException($"LCAT batch sync failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", SyncPath);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return ParseSyncResponse(text);
            }
            catch (JsonException)
            {
                throw CreateRedactedAdapterException("LCAT batch sync returned a malformed response.", SyncPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LCAT batch sync failed before a response was returned.", SyncPath);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LCAT batch sync failed before a response was returned.", SyncPath);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static string EnsureTrailingSlash(string value) => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private static bool IsTransportException(Exception ex)
    {
        return ex is HttpRequestException or IOException or ObjectDisposedException;
    }

    private static InvalidOperationException CreateRedactedAdapterException(string message, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new InvalidOperationException(message);
        return new InvalidOperationException($"{message} Path: {path}.");
    }

    private static IReadOnlyList<LCATCustomerScopeRequest> NormalizeCustomerScopeRequests(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        if (customers == null)
        {
            throw new InvalidOperationException("LCAT batch sync request is invalid: customers is required.");
        }

        return customers.Select(customer => new LCATCustomerScopeRequest
        {
            Slug = NormalizeRequiredValue(customer?.Slug),
            DisplayName = NormalizeRequiredValue(customer?.DisplayName),
            NCentralCustomerId = NormalizeRequiredValue(customer?.NCentralCustomerId),
            NCentralParentCustomerId = string.IsNullOrWhiteSpace(customer?.NCentralParentCustomerId) ? null : customer.NCentralParentCustomerId.Trim()
        }).ToArray();
    }

    private static string NormalizeRequiredValue(string? value) => value?.Trim() ?? string.Empty;

    private static void EnsureCustomerScopeContract(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        customers = NormalizeCustomerScopeRequests(customers);
        var errors = new List<string>();
        for (var i = 0; i < customers.Count; i++)
        {
            var customer = customers[i];
            var prefix = $"customers[{i}]";
            if (string.IsNullOrWhiteSpace(customer.Slug)) errors.Add($"{prefix}.slug is required");
            else if (!LcatSlugPattern.IsMatch(customer.Slug)) errors.Add($"{prefix}.slug must match the LCAT customer-scope contract");
            if (string.IsNullOrWhiteSpace(customer.DisplayName)) errors.Add($"{prefix}.display_name is required");
            if (string.IsNullOrWhiteSpace(customer.NCentralCustomerId)) errors.Add($"{prefix}.ncentral_customer_id is required");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("LCAT batch sync request is invalid: " + string.Join("; ", errors) + ".");
        }

        EnsureUniqueCustomerIds(customers);
        EnsureUniqueSlugs(customers);
    }

    private static void EnsureUniqueCustomerIds(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        var duplicates = customers
            .GroupBy(customer => customer.NCentralCustomerId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"LCAT batch sync request contains duplicate ncentral_customer_id value(s): {string.Join(", ", duplicates)}. " +
                "Each customer or site item must resolve to a unique ncentral_customer_id.");
        }
    }

    private static void EnsureUniqueSlugs(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        var duplicates = customers
            .GroupBy(customer => customer.Slug, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"LCAT batch sync request contains duplicate slug value(s): {string.Join(", ", duplicates)}. " +
                "Each customer or site item must resolve to a unique LCAT customer-scope slug.");
        }
    }

    private static string BuildSyncRequestBody(IReadOnlyList<LCATCustomerScopeRequest> customers)
    {
        customers = NormalizeCustomerScopeRequests(customers);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["customers"] = customers.Select(customer => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = customer.Slug,
                ["display_name"] = customer.DisplayName,
                ["ncentral_customer_id"] = customer.NCentralCustomerId,
                ["ncentral_parent_customer_id"] = customer.NCentralParentCustomerId
            }).ToArray(),
            ["reason"] = SyncReason,
            ["ticket"] = null
        };

        return JsonSerializer.Serialize(payload);
    }

    private static LCATSyncResult ParseSyncResponse(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("LCAT batch sync response must be a JSON object.");
        }

        return new LCATSyncResult
        {
            InsertedCount = ReadOptionalCount(root, "inserted_count"),
            UpdatedCount = ReadOptionalCount(root, "updated_count"),
            RetiredCount = ReadOptionalCount(root, "retired_count"),
            ActiveCount = ReadOptionalCount(root, "active_count"),
            AuditEventId = ReadOptionalAuditEventId(root)
        };
    }

    private static string? ReadOptionalAuditEventId(JsonElement root)
    {
        if (!root.TryGetPropertyIgnoreCase("audit_event_id", out var property)) return null;
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind == JsonValueKind.String) return property.GetString();
        throw new JsonException("LCAT batch sync response field 'audit_event_id' must be a string.");
    }

    private static int ReadOptionalCount(JsonElement root, string name)
    {
        if (!root.TryGetPropertyIgnoreCase(name, out var property)) return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric) && numeric >= 0) return numeric;
        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var textNumeric) && textNumeric >= 0) return textNumeric;
        throw new JsonException($"LCAT batch sync response field '{name}' must be a non-negative integer.");
    }
}

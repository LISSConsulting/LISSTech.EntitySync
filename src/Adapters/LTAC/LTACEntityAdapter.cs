using System.Net.Http.Headers;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.LTAC;

public sealed class LTACEntityAdapter : IEntityAdapter, IDisposable
{
    private const string SyncReason = "EntitySync N-central to LTAC sync";
    private const string SyncPath = "rpc/sync_ncentral_customers";
    private const string SyncScope = "operator_access:write";

    private readonly HttpClient httpClient = new();
    private readonly AgentControllerClient client;

    public LTACEntityAdapter(LTACOptions options)
    {
        httpClient.BaseAddress = new Uri(UrlHelpers.EnsureTrailingSlash(options.BaseUrl));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        client = new AgentControllerClient(UrlHelpers.EnsureTrailingSlash(options.BaseUrl), httpClient);
    }

    public string Vendor => EntitySyncVendors.AgentController;

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
    {
        if (!query.EntityType.Equals("Customer", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("LTAC adapter currently supports EntityType Customer.");

        // No LTAC customer-scope list/read endpoint is defined in the sync RPC contract
        // (contracts/ltac-sync-rpc.md); returning an empty set lets N-central sources plan as
        // create/sync candidates per contracts/powershell-command-contract.md.
        return Task.FromResult<IReadOnlyList<ExternalEntity>>(Array.Empty<ExternalEntity>());
    }

    public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Lookup type '{type}' is not supported for {Vendor}.");
    }

    public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LTAC does not support per-item create. Apply an approved plan through the LTAC batch sync path.");
    }

    public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("LTAC does not support per-item update. Apply an approved plan through the LTAC batch sync path.");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.HasScopeAsync(new HasScopeRequest { P_scope = SyncScope }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentControllerApiException)
        {
            return false;
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LTAC connection test failed.", string.Empty);
        }
    }

    public async Task<LTACSyncResult> SyncCustomerScopesAsync(IReadOnlyList<LTACCustomerScopeRequest> customers, CancellationToken cancellationToken)
    {
        var normalizedCustomers = NormalizeCustomerScopeRequests(customers);
        EnsureCustomerScopeContract(normalizedCustomers);
        Trace?.Invoke("LTAC POST " + SyncPath);

        try
        {
            var response = await client.SyncNcentralCustomersAsync(new LTACSyncRequest
            {
                Customers = normalizedCustomers.ToList(),
                Reason = SyncReason,
                Ticket = null
            }, cancellationToken).ConfigureAwait(false);
            if (response.Count != 1)
            {
                throw CreateRedactedAdapterException("LTAC batch sync returned a malformed response.", SyncPath);
            }
            var result = response.Single();
            EnsureSyncResultContract(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentControllerApiException ex) when (ex.StatusCode == 200)
        {
            throw CreateRedactedAdapterException("LTAC batch sync returned a malformed response.", SyncPath);
        }
        catch (AgentControllerApiException ex)
        {
            throw CreateRedactedAdapterException($"LTAC batch sync failed with HTTP {ex.StatusCode}.", SyncPath);
        }
        catch (ObjectDisposedException ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LTAC batch sync failed before a response was returned.", SyncPath);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LTAC batch sync failed before a response was returned.", SyncPath);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static bool IsTransportException(Exception ex)
    {
        return ex is HttpRequestException or IOException or ObjectDisposedException;
    }

    private static InvalidOperationException CreateRedactedAdapterException(string message, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new InvalidOperationException(message);
        return new InvalidOperationException($"{message} Path: {path}.");
    }

    private static IReadOnlyList<LTACCustomerScopeRequest> NormalizeCustomerScopeRequests(IReadOnlyList<LTACCustomerScopeRequest> customers)
    {
        if (customers == null)
        {
            throw new InvalidOperationException("LTAC batch sync request is invalid: customers is required.");
        }

        return customers.Select(customer => new LTACCustomerScopeRequest
        {
            Slug = NormalizeRequiredValue(customer?.Slug),
            DisplayName = NormalizeRequiredValue(customer?.DisplayName),
            NCentralCustomerId = NormalizeRequiredValue(customer?.NCentralCustomerId),
            NCentralParentCustomerId = string.IsNullOrWhiteSpace(customer?.NCentralParentCustomerId) ? null : customer.NCentralParentCustomerId.Trim()
        }).ToArray();
    }

    private static string NormalizeRequiredValue(string? value) => value?.Trim() ?? string.Empty;

    private static void EnsureCustomerScopeContract(IReadOnlyList<LTACCustomerScopeRequest> customers)
    {
        customers = NormalizeCustomerScopeRequests(customers);
        var errors = new List<string>();
        for (var i = 0; i < customers.Count; i++)
        {
            var customer = customers[i];
            var prefix = $"customers[{i}]";
            if (string.IsNullOrWhiteSpace(customer.Slug)) errors.Add($"{prefix}.slug is required");
            else if (!DefaultEntityMapper.IsValidLtacSlug(customer.Slug)) errors.Add($"{prefix}.slug must match the LTAC customer-scope contract");
            if (string.IsNullOrWhiteSpace(customer.DisplayName)) errors.Add($"{prefix}.display_name is required");
            if (string.IsNullOrWhiteSpace(customer.NCentralCustomerId)) errors.Add($"{prefix}.ncentral_customer_id is required");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("LTAC batch sync request is invalid: " + string.Join("; ", errors) + ".");
        }

        if (customers.Count == 0)
        {
            throw new InvalidOperationException("LTAC batch sync request is invalid: at least one customer-scope row is required.");
        }

        EnsureUniqueCustomerIds(customers);
        EnsureUniqueSlugs(customers);
    }

    private static void EnsureUniqueCustomerIds(IReadOnlyList<LTACCustomerScopeRequest> customers)
    {
        var duplicates = customers
            .GroupBy(customer => customer.NCentralCustomerId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"LTAC batch sync request contains duplicate ncentral_customer_id value(s): {string.Join(", ", duplicates)}. " +
                "Each customer or site item must resolve to a unique ncentral_customer_id.");
        }
    }

    private static void EnsureUniqueSlugs(IReadOnlyList<LTACCustomerScopeRequest> customers)
    {
        var duplicates = customers
            .GroupBy(customer => customer.Slug, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"LTAC batch sync request contains duplicate slug value(s): {string.Join(", ", duplicates)}. " +
                "Each customer or site item must resolve to a unique LTAC customer-scope slug.");
        }
    }

    private static void EnsureSyncResultContract(LTACSyncResult result)
    {
        if (result.InsertedCount < 0 || result.UpdatedCount < 0 || result.RetiredCount < 0 || result.ActiveCount < 0)
        {
            throw CreateRedactedAdapterException("LTAC batch sync returned a malformed response.", SyncPath);
        }
    }

}

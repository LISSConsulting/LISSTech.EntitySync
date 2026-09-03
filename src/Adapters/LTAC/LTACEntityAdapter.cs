using System.Net.Http.Headers;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Ports;

namespace LISSTech.EntitySync.Adapters.LTAC;

public sealed class LTACEntityAdapter : IEntityAdapter, IEntityBatchAdapter, IDisposable
{
    private const string SyncReason = "EntitySync N-central to LTAC sync";
    private const string SyncPath = "rpc/sync_ncentral_customers";
    private const string SyncScope = "customer_scope_sync:write";

    private readonly HttpClient httpClient;
    private readonly AgentControllerClient client;
    private readonly LTACOptions options;
    private readonly SemaphoreSlim bearerTokenRefreshGate = new(1, 1);

    public LTACEntityAdapter(LTACOptions options)
    {
        this.options = options;
        httpClient = VendorHttpClientFactory.Create(new Uri(UrlHelpers.EnsureTrailingSlash(options.BaseUrl)));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        SetAuthorization(options.BearerToken);
        client = new AgentControllerClient(UrlHelpers.EnsureTrailingSlash(options.BaseUrl), httpClient);
    }

    public string Vendor => EntitySyncVendors.AgentController;

    public IReadOnlyList<string> LookupTypes => EntitySyncLookupTypes.ForVendor(Vendor);

    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Test-only constructor. Allows a test <see cref="HttpClient"/> to be injected so
    /// focused platform tests can drive the forced-refresh path without a live
    /// AgentController deployment.
    /// </summary>
    internal LTACEntityAdapter(LTACOptions options, HttpClient httpClient)
    {
        this.options = options;
        this.httpClient = httpClient;
        this.httpClient.BaseAddress = new Uri(UrlHelpers.EnsureTrailingSlash(options.BaseUrl));
        this.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        SetAuthorization(options.BearerToken);
        client = new AgentControllerClient(UrlHelpers.EnsureTrailingSlash(options.BaseUrl), this.httpClient);
    }

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
    public async Task<EntityWriteResult> ApplyBatchAsync(
        IReadOnlyList<EntityWriteRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var customers = requests.Select(request => new LTACCustomerScopeRequest
        {
            Slug = StringField(request, "slug"),
            DisplayName = StringField(request, "display_name"),
            NCentralCustomerId = StringField(request, "ncentral_customer_id"),
            NCentralParentCustomerId = OptionalStringField(request, "ncentral_parent_customer_id")
        }).ToArray();
        var result = await SyncCustomerScopesAsync(customers, cancellationToken).ConfigureAwait(false);
        return new EntityWriteResult
        {
            Vendor = Vendor,
            EntityType = "Customer",
            Action = "BatchSync",
            Success = true,
            Message = $"AgentController batch sync applied (inserted {result.InsertedCount}, updated {result.UpdatedCount}, retired {result.RetiredCount}, active {result.ActiveCount}).",
            Raw = result
        };
    }


    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var rejectedBearerToken = options.BearerToken;
        try
        {
            var result = await client.HasScopeAsync(
                new HasScopeRequest { P_scope = SyncScope },
                cancellationToken).ConfigureAwait(false);
            if (!result && await TryRefreshBearerTokenAsync(rejectedBearerToken, cancellationToken).ConfigureAwait(false))
            {
                return await TestConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentControllerApiException ex)
        {
            if (!IsAuthorizationRejection(ex.StatusCode)
                || !await TryRefreshBearerTokenAsync(rejectedBearerToken, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                return await TestConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentControllerApiException)
            {
                return false;
            }
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException("LTAC connection test failed.", string.Empty);
        }
    }

    public async Task<LTACSyncResult> SyncCustomerScopesAsync(
        IReadOnlyList<LTACCustomerScopeRequest> customers,
        CancellationToken cancellationToken)
    {
        var normalizedCustomers = NormalizeCustomerScopeRequests(customers);
        EnsureCustomerScopeContract(normalizedCustomers);
        var rejectedBearerToken = options.BearerToken;
        Trace?.Invoke("LTAC POST " + SyncPath);

        try
        {
            return await SyncCustomerScopesAndValidateAsync(
                normalizedCustomers,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentControllerApiException ex)
        {
            if (ex.StatusCode == 200)
            {
                throw CreateRedactedAdapterException(
                    "LTAC batch sync returned a malformed response.",
                    SyncPath);
            }

            if (!IsAuthorizationRejection(ex.StatusCode)
                || !await TryRefreshBearerTokenAsync(rejectedBearerToken, cancellationToken).ConfigureAwait(false))
            {
                throw CreateRedactedAdapterException(
                    $"LTAC batch sync failed with HTTP {ex.StatusCode}.",
                    SyncPath);
            }

            try
            {
                return await SyncCustomerScopesAndValidateAsync(
                    normalizedCustomers,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentControllerApiException retryException)
            {
                var message = retryException.StatusCode == 200
                    ? "LTAC batch sync returned a malformed response."
                    : $"LTAC batch sync failed with HTTP {retryException.StatusCode}.";
                throw CreateRedactedAdapterException(message, SyncPath);
            }
        }
        catch (ObjectDisposedException ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException(
                "LTAC batch sync failed before a response was returned.",
                SyncPath);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (IsTransportException(ex))
        {
            throw CreateRedactedAdapterException(
                "LTAC batch sync failed before a response was returned.",
                SyncPath);
        }
    }

    public void Dispose()
    {
        var bearerTokenProviderOwner = options.BearerTokenProviderOwner;
        options.BearerToken = string.Empty;
        options.BearerTokenProvider = null;
        options.BearerTokenProviderOwner = null;
        bearerTokenProviderOwner?.Dispose();
        httpClient.Dispose();
        bearerTokenRefreshGate.Dispose();
    }

    private Task<bool> TestConnectionOnceAsync(CancellationToken cancellationToken)
    {
        return client.HasScopeAsync(
            new HasScopeRequest { P_scope = SyncScope },
            cancellationToken);
    }

    private async Task<LTACSyncResult> SyncCustomerScopesAndValidateAsync(
        IReadOnlyList<LTACCustomerScopeRequest> normalizedCustomers,
        CancellationToken cancellationToken)
    {
        var response = await SyncCustomerScopesOnceAsync(
            normalizedCustomers,
            cancellationToken).ConfigureAwait(false);
        if (response.Count != 1)
        {
            throw CreateRedactedAdapterException(
                "LTAC batch sync returned a malformed response.",
                SyncPath);
        }

        var result = response.Single();
        EnsureSyncResultContract(result);
        return result;
    }

    private Task<ICollection<LTACSyncResult>> SyncCustomerScopesOnceAsync(IReadOnlyList<LTACCustomerScopeRequest> normalizedCustomers, CancellationToken cancellationToken)
    {
        return client.SyncNcentralCustomersAsync(new LTACSyncRequest
        {
            Customers = normalizedCustomers.ToList(),
            Reason = SyncReason,
            Ticket = null
        }, cancellationToken);
    }

    private static string StringField(EntityWriteRequest request, string name)
    {
        return OptionalStringField(request, name) ?? string.Empty;
    }

    private static string? OptionalStringField(EntityWriteRequest request, string name)
    {
        return request.Fields.TryGetValue(name, out var value)
            ? value as string
            : null;
    }

    private void SetAuthorization(string bearerToken)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    private async Task<bool> TryRefreshBearerTokenAsync(
        string rejectedBearerToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.BearerTokenProvider == null)
        {
            return false;
        }

        await bearerTokenRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(options.BearerToken, rejectedBearerToken, StringComparison.Ordinal))
            {
                return true;
            }

            var bearerToken = await options.BearerTokenProvider(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                return false;
            }

            options.BearerToken = bearerToken;
            SetAuthorization(bearerToken);
            Trace?.Invoke("LTAC bearer token was rejected. Refreshed AgentController token and retrying request once.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Trace?.Invoke("LTAC bearer token refresh failed; reconnect the AgentController connection.");
            return false;
        }
        finally
        {
            bearerTokenRefreshGate.Release();
        }
    }

    private static bool IsTransportException(Exception ex)
    {
        return ex is HttpRequestException or IOException or ObjectDisposedException;
    }

    private static bool IsAuthorizationRejection(int statusCode)
    {
        return statusCode is 401 or 403;
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
            else if (!EntityScopeSlug.IsValid(customer.Slug)) errors.Add($"{prefix}.slug must match the LTAC customer-scope contract");
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

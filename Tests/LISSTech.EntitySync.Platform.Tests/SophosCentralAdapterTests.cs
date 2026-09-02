using System.Net;
using System.Text;
using System.Text.Json;
using LISSTech.EntitySync.Adapters.SophosCentral;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Hosting;
using LISSTech.EntitySync.Mapping;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class SophosCentralAdapterTests
{
    [Fact]
    public async Task GetEntitiesAuthenticatesPaginatesMapsAndFiltersInactive()
    {
        using var handler = new RecordingHandler((request, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"sophos-token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"partner-id","idType":"partner","apiHosts":{"global":"https://api.central.sophos.com"}}"""),
            2 => JsonResponse(HttpStatusCode.OK, """
                {
                  "pages":{"current":1,"size":2,"total":2,"items":3,"maxSize":100},
                  "items":[
                    {
                      "id":"tenant-active-one",
                      "name":"Acme Security",
                      "dataGeography":"US",
                      "dataRegion":"us03",
                      "billingType":"usage",
                      "apiHost":"https://api-us03.central.sophos.com",
                      "status":"active",
                      "managed":true,
                      "contact":{
                        "firstName":"Alex",
                        "lastName":"Admin",
                        "email":"admin@acme.test",
                        "phone":"555-0100",
                        "address":{"address1":"1 Main St","city":"New York","state":"NY","postalCode":"10001","countryCode":"US"}
                      },
                      "products":[{"code":"CIXA-MSP"},{"code":"CEMA-MSP"}]
                    },
                    {"id":"tenant-suspended","name":"Suspended Co","status":"suspended"}
                  ]
                }
                """),
            3 => JsonResponse(HttpStatusCode.OK, """
                {
                  "pages":{"current":2,"size":2,"maxSize":100},
                  "items":[{"id":"tenant-active-two","name":"Beta Security","status":"active"}]
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);

        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Customer", IncludeInactive = false },
            CancellationToken.None);

        Assert.Equal(2, entities.Count);
        var acme = Assert.Single(entities, entity => entity.Id == "tenant-active-one");
        Assert.Equal(EntitySyncVendors.SophosCentral, acme.Vendor);
        Assert.Equal("Customer", acme.EntityType);
        Assert.Equal("Acme Security", acme.Name);
        Assert.True(acme.IsActive);
        Assert.Equal("admin@acme.test", acme.Email);
        Assert.Equal("555-0100", acme.Phone);
        Assert.Equal("1 Main St", acme.PrimaryAddress?.Line1);
        Assert.Equal("Alex Admin", acme.PrimaryAddress?.Attention);
        Assert.Equal("tenant-active-one", acme.GetExternalId(SophosCentralEntityAdapter.TenantExternalIdName));
        Assert.Equal("us03", acme.GetCustomField("SophosDataRegion"));
        Assert.Equal("CEMA-MSP,CIXA-MSP", acme.GetCustomField("SophosProducts"));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://id.sophos.test/api/v2/oauth2/token", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Contains("grant_type=client_credentials", handler.Requests[0].Body);
        Assert.Contains("client_id=client-id", handler.Requests[0].Body);
        Assert.Contains("client_secret=client-secret", handler.Requests[0].Body);
        Assert.Equal("Bearer sophos-token", handler.Requests[1].Authorization);
        Assert.Contains("/partner/v1/tenants?page=1&pageSize=100&pageTotal=true", handler.Requests[2].Uri.PathAndQuery);
        Assert.Equal("partner-id", Assert.Single(handler.Requests[2].Headers["X-Partner-ID"]));
        Assert.Contains("/partner/v1/tenants?page=2&pageSize=100", handler.Requests[3].Uri.PathAndQuery);
        Assert.DoesNotContain("pageTotal", handler.Requests[3].Uri.Query);
    }

    [Fact]
    public async Task OrganizationCredentialsUseOrganizationRouteAndHeader()
    {
        using var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"organization-id","idType":"organization"}"""),
            2 => JsonResponse(HttpStatusCode.OK, """{"pages":{"current":1,"size":1,"total":1,"maxSize":100},"items":[{"id":"tenant","name":"Tenant"}]}"""),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);

        var entities = await adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Customer", Count = 1 },
            CancellationToken.None);

        Assert.Single(entities);
        Assert.Contains("/organization/v1/tenants", handler.Requests[2].Uri.AbsoluteUri);
        Assert.Equal("organization-id", Assert.Single(handler.Requests[2].Headers["X-Organization-ID"]));
        Assert.False(handler.Requests[2].Headers.ContainsKey("X-Partner-ID"));
    }

    [Fact]
    public async Task TenantCredentialsFailBeforeTenantEnumeration()
    {
        using var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"tenant-id","idType":"tenant"}"""),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Customer" },
            CancellationToken.None));

        Assert.Contains("partner or organization API credentials", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidPageMetadataFailsClosed()
    {
        using var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"partner-id","idType":"partner"}"""),
            2 => JsonResponse(HttpStatusCode.OK, """{"pages":{"current":2,"total":2},"items":[]}"""),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Customer" },
            CancellationToken.None));

        Assert.Contains("page 2 while page 1 was requested", error.Message);
    }

    [Fact]
    public async Task CreateAndUpdateUsePartnerTenantWriteContracts()
    {
        using var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"partner-id","idType":"partner"}"""),
            2 => JsonResponse(HttpStatusCode.Created, """{"id":"tenant-id","name":"Acme","status":"active"}"""),
            3 => JsonResponse(HttpStatusCode.OK, """{"id":"tenant-id","name":"Acme","showAs":"Acme Managed","status":"active"}"""),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);
        var createRequest = new EntityWriteRequest { EntityType = "Customer", Name = "Acme" };
        createRequest.Fields["dataGeography"] = "US";
        createRequest.Fields["billingType"] = "usage";
        createRequest.Fields["contactFirstName"] = "Alex";
        createRequest.Fields["contactLastName"] = "Admin";
        createRequest.Fields["contactEmail"] = "alex@acme.test";
        createRequest.Fields["contactPhone"] = "555-0100";
        createRequest.Fields["address"] = new Dictionary<string, object?>
        {
            ["address1"] = "1 Main St",
            ["city"] = "New York",
            ["state"] = "NY",
            ["postalCode"] = "10001",
            ["countryCode"] = "US"
        };
        createRequest.Fields["products"] = "CIXA-MSP,CEMA-MSP";
        createRequest.Fields["acceptedSampleSubmission"] = "true";

        var created = await adapter.CreateEntityAsync(createRequest, CancellationToken.None);
        var updated = await adapter.UpdateEntityAsync(
            new EntityWriteRequest { EntityType = "Customer", Id = created.Id, Name = "Acme Managed" },
            CancellationToken.None);

        Assert.Equal("tenant-id", created.Id);
        Assert.Contains("Acme Managed", updated.Message);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("https://api.sophos.test/partner/v1/tenants", handler.Requests[2].Uri.AbsoluteUri);
        Assert.Equal("partner-id", Assert.Single(handler.Requests[2].Headers["X-Partner-ID"]));
        using var createBody = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("US", createBody.RootElement.GetProperty("dataGeography").GetString());
        Assert.Equal("usage", createBody.RootElement.GetProperty("billingType").GetString());
        Assert.Equal("alex@acme.test", createBody.RootElement.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal(2, createBody.RootElement.GetProperty("products").GetArrayLength());
        Assert.True(createBody.RootElement.GetProperty("acceptedSampleSubmission").GetBoolean());

        Assert.Equal(HttpMethod.Patch, handler.Requests[3].Method);
        Assert.Equal("application/merge-patch+json", handler.Requests[3].ContentType);
        using var updateBody = JsonDocument.Parse(handler.Requests[3].Body);
        Assert.Equal("Acme Managed", updateBody.RootElement.GetProperty("showAs").GetString());
    }

    [Fact]
    public async Task OrganizationCredentialsRejectTenantWrites()
    {
        using var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}"""),
            1 => JsonResponse(HttpStatusCode.OK, """{"id":"organization-id","idType":"organization"}"""),
            _ => throw new InvalidOperationException($"Unexpected request {index}.")
        });
        using var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.UpdateEntityAsync(
            new EntityWriteRequest { EntityType = "Customer", Id = "tenant-id", Name = "Acme" },
            CancellationToken.None));

        Assert.Contains("partner API credentials", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void MapperCarriesCanonicalContactAndSophosProvisioningFields()
    {
        var source = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "halo-1",
            Name = "Acme",
            Email = "alex@acme.test",
            Phone = "555-0100",
            PrimaryAddress = new EntityAddress
            {
                Attention = "Alex Admin",
                Line1 = "1 Main St",
                City = "New York",
                State = "NY",
                PostalCode = "10001",
                Country = "US"
            }
        };
        source.CustomFields["SophosDataGeography"] = "US";
        source.CustomFields["SophosBillingType"] = "usage";
        source.CustomFields["SophosProducts"] = "CIXA-MSP";

        var request = new DefaultEntityMapper().MapCreate(
            source,
            EntitySyncVendors.SophosCentral,
            "Customer",
            new MatchOptions());

        Assert.Equal("US", request.Fields["dataGeography"]);
        Assert.Equal("usage", request.Fields["billingType"]);
        Assert.Equal("Alex", request.Fields["contactFirstName"]);
        Assert.Equal("Admin", request.Fields["contactLastName"]);
        Assert.Equal("alex@acme.test", request.Fields["contactEmail"]);
        var address = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(request.Fields["address"]);
        Assert.Equal("US", address["countryCode"]);
    }

    [Fact]
    public async Task AuthenticationErrorsRedactCredentials()
    {
        using var handler = new RecordingHandler((_, _) =>
            JsonResponse(HttpStatusCode.BadRequest, """{"error":"client-id client-secret"}"""));
        using var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.GetEntitiesAsync(
            new EntityQuery { EntityType = "Customer" },
            CancellationToken.None));

        Assert.DoesNotContain("client-id", error.Message);
        Assert.DoesNotContain("client-secret", error.Message);
        Assert.Contains("[REDACTED]", error.Message);
    }

    [Theory]
    [InlineData("Sophos Central")]
    [InlineData("SophosCentral")]
    [InlineData("Sophos")]
    public async Task ServerFactoryCreatesSophosAdapterForSupportedAliases(string vendor)
    {
        var factory = new ServerManagedEntityAdapterFactory(new Dictionary<string, string?>
        {
            ["SOPHOS_CENTRAL_CLIENT_ID"] = "client-id",
            ["SOPHOS_CENTRAL_CLIENT_SECRET"] = "client-secret"
        });

        using var adapter = (SophosCentralEntityAdapter)await factory.CreateAsync(vendor, null, CancellationToken.None);

        Assert.Equal(EntitySyncVendors.SophosCentral, adapter.Vendor);
    }

    [Fact]
    public async Task ServerFactoryRequiresSophosClientSecret()
    {
        var factory = new ServerManagedEntityAdapterFactory(new Dictionary<string, string?>
        {
            ["SOPHOS_CENTRAL_CLIENT_ID"] = "client-id"
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync("Sophos Central", null, CancellationToken.None));

        Assert.Contains("SOPHOS_CENTRAL_CLIENT_SECRET", error.Message);
    }

    private static SophosCentralEntityAdapter CreateAdapter(HttpMessageHandler handler)
    {
        return new SophosCentralEntityAdapter(
            new SophosCentralOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
                IdentityUrl = "https://id.sophos.test/api/v2/oauth2/token",
                GlobalApiUrl = "https://api.sophos.test/"
            },
            handler);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."),
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return responder(recorded, Requests.Count - 1);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        IReadOnlyDictionary<string, string[]> Headers,
        string? ContentType,
        string Body);
}

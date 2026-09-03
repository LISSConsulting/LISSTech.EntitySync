using System.Text.Json;
using LISSTech.EntitySync.Adapters.OrchestraMSP;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mapping;
using LISSTech.EntitySync.Mcp.ControlApi;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class CanonicalShadowProjectionTests
{
    private static readonly Guid ClientId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SiteId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid AddressId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void Canonical_shadow_contract_round_trips_the_complete_Orchestra_mapper_graph()
    {
        var mapped = OrchestraEntityMapper.MapClient(ClientContract());
        mapped.CreatedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        mapped.UpdatedAt = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        var roundTripped = ToRequest(mapped).ToDomain(ClientId, 7);

        Assert.Equal(JsonSerializer.Serialize(mapped), JsonSerializer.Serialize(roundTripped));

        var target = new ExternalEntity
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "42",
            Name = "Old Acme",
            CustomFields = { ["CFOrchestraClientID"] = ClientId.ToString("D") }
        };
        var mapper = new DefaultEntityMapper();
        var options = new MatchOptions
        {
            SourceExternalIdName = "OrchestraClientId",
            TargetCustomFieldName = "CFOrchestraClientID"
        };
        Assert.Equal(
            JsonSerializer.Serialize(mapper.MapUpdate(mapped, target, options)),
            JsonSerializer.Serialize(mapper.MapUpdate(roundTripped, target, options)));
    }

    [Fact]
    public void Canonical_shadow_contract_accepts_nullable_custom_fields_and_rejects_wrong_root_version()
    {
        var mapped = OrchestraEntityMapper.MapClient(ClientContract());
        mapped.CustomFields["nullable"] = null;
        var request = ToRequest(mapped);

        Assert.Null(request.ToDomain(ClientId, 7).CustomFields["nullable"]);
        Assert.Throws<ArgumentException>(() => request.ToDomain(ClientId, 8));
        Assert.Throws<ArgumentException>(() =>
            (request with { Email = " padded@example.test " }).ToDomain(ClientId, 7));
    }

    [Fact]
    public void Canonical_shadow_contract_rejects_graphs_deeper_than_Client_Site_Address()
    {
        var mapped = OrchestraEntityMapper.MapClient(ClientContract());
        var request = ToRequest(mapped);
        var address = request.Children.Single().Children.Single();
        var tooDeep = address with { Children = [address] };
        var site = request.Children.Single() with { Children = [tooDeep] };

        Assert.Throws<ArgumentException>(() =>
            (request with { Children = [site] }).ToDomain(ClientId, 7));
    }

    private static OrchestraClientContract ClientContract() => new()
    {
        ClientId = ClientId,
        Version = 7,
        Name = "Acme",
        LifecycleStatus = "active",
        MergedFromClientIds =
        [
            Guid.Parse("44444444-4444-4444-8444-444444444444")
        ],
        Fields = new Dictionary<string, JsonElement>
        {
            ["email"] = JsonSerializer.SerializeToElement("ops@acme.example"),
            ["phone"] = JsonSerializer.SerializeToElement("+1-512-555-0100"),
            ["website"] = JsonSerializer.SerializeToElement("https://acme.example"),
            ["domain"] = JsonSerializer.SerializeToElement("acme.example"),
            ["nullable"] = JsonSerializer.SerializeToElement<string?>(null),
            ["empty"] = JsonSerializer.SerializeToElement(string.Empty),
            ["object"] = JsonSerializer.SerializeToElement(new { z = 2, a = 1 })
        },
        Tags = ["priority", "west"],
        Sites =
        [
            new OrchestraSiteContract
            {
                SiteId = SiteId,
                ClientId = ClientId,
                Version = 3,
                Name = "Austin",
                LifecycleStatus = "active",
                Fields = new Dictionary<string, JsonElement>
                {
                    ["code"] = JsonSerializer.SerializeToElement("ATX")
                },
                Tags = ["hq"],
                Addresses = [AddressContract()],
                PlatformLinks =
                [
                    new OrchestraPlatformLinkContract
                    {
                        PlatformInstanceId = "ncentral-prod",
                        Platform = "NCentral",
                        ExternalId = "site-9",
                        Status = "active",
                        EntityType = "Site",
                        EntityId = SiteId
                    }
                ]
            }
        ],
        PlatformLinks =
        [
            new OrchestraPlatformLinkContract
            {
                PlatformInstanceId = "halo-prod",
                Platform = "HaloPSA",
                ExternalId = "42",
                Status = "active",
                EntityType = "Client",
                EntityId = ClientId
            }
        ]
    };

    private static OrchestraAddressContract AddressContract() => new()
    {
        AddressId = AddressId,
        ClientId = ClientId,
        SiteId = SiteId,
        Version = 2,
        AddressType = string.Empty,
        Attention = "Ops",
        Line1 = "1 Main",
        Line2 = "Suite 200",
        City = "Austin",
        State = "TX",
        PostalCode = "78701",
        Country = "US",
        Fields = new Dictionary<string, JsonElement>
        {
            ["zone"] = JsonSerializer.SerializeToElement("central"),
            ["empty"] = JsonSerializer.SerializeToElement(string.Empty)
        },
        Tags = ["primary"],
        PlatformLinks =
        [
            new OrchestraPlatformLinkContract
            {
                PlatformInstanceId = "halo-prod",
                Platform = "HaloPSA",
                ExternalId = "address-7",
                Status = "active",
                EntityType = "Address",
                EntityId = AddressId
            }
        ]
    };

    private static CanonicalShadowEntityRequest ToRequest(ExternalEntity entity) => new(
        entity.EntityType,
        entity.Id,
        entity.ParentId,
        entity.ParentEntityType,
        entity.Version,
        entity.LifecycleStatus,
        entity.IsDeleted,
        entity.MergeSurvivorId,
        entity.MergeDonorIds,
        entity.Tags,
        entity.Children.Select(ToRequest).ToArray(),
        entity.PlatformLinks.Select(link => new CanonicalShadowPlatformLinkRequest(
            link.PlatformInstanceId,
            link.Platform,
            link.ExternalId,
            link.Status,
            link.EntityType,
            link.EntityId)).ToArray(),
        entity.ExternalIds,
        entity.Name,
        entity.Email,
        entity.Phone,
        entity.Website,
        entity.Domain,
        entity.PrimarySiteId,
        entity.PrimarySiteName,
        ToRequest(entity.PrimaryAddress),
        ToRequest(entity.BillingAddress),
        ToRequest(entity.ShippingAddress),
        entity.IsActive,
        entity.CreatedAt,
        entity.UpdatedAt,
        entity.CustomFields);

    private static CanonicalShadowAddressRequest? ToRequest(EntityAddress? address) =>
        address is null
            ? null
            : new CanonicalShadowAddressRequest(
                address.AddressType,
                address.Attention,
                address.Line1,
                address.Line2,
                address.Line3,
                address.City,
                address.State,
                address.PostalCode,
                address.Country);
}

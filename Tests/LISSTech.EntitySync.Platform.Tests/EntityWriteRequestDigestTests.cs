using System.Globalization;
using LISSTech.EntitySync.Application;
using LISSTech.EntitySync.Core;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class EntityWriteRequestDigestTests
{
    private const string ValidScope = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void DigestIsStableAcrossDictionaryInsertionOrder()
    {
        var first = Request(
            fields: new Dictionary<string, object?>
            {
                ["website"] = "https://example.test",
                ["address"] = new Dictionary<string, object?>
                {
                    ["city"] = "Toronto",
                    ["line1"] = "1 Main"
                }
            },
            customFields: new Dictionary<string, string?>
            {
                ["CFNetSuiteCustomerName"] = "Acme",
                ["CFNetSuiteCustomerID"] = "42"
            });
        var second = Request(
            fields: new Dictionary<string, object?>
            {
                ["address"] = new Dictionary<string, object?>
                {
                    ["line1"] = "1 Main",
                    ["city"] = "Toronto"
                },
                ["website"] = "https://example.test"
            },
            customFields: new Dictionary<string, string?>
            {
                ["CFNetSuiteCustomerID"] = "42",
                ["CFNetSuiteCustomerName"] = "Acme"
            });

        Assert.Equal(EntityWriteRequestDigest.Compute(first), EntityWriteRequestDigest.Compute(second));
    }

    [Theory]
    [InlineData("vendor")]
    [InlineData("entity-type")]
    [InlineData("name")]
    [InlineData("target-id")]
    [InlineData("primary-site-id")]
    [InlineData("field")]
    [InlineData("custom-field")]
    public void DigestChangesWhenMappedWriteChanges(string mutation)
    {
        var baseline = Request();
        var changed = Request();
        Mutate(changed, mutation);

        Assert.NotEqual(EntityWriteRequestDigest.Compute(baseline), EntityWriteRequestDigest.Compute(changed));
    }

    [Fact]
    public void DigestCanonicalizesNestedDictionariesRecursively()
    {
        var first = Request(fields: new Dictionary<string, object?>
        {
            ["sites"] = new object?[]
            {
                new Dictionary<string, object?> { ["zip"] = "M5V", ["active"] = true },
                null
            }
        });
        var second = Request(fields: new Dictionary<string, object?>
        {
            ["sites"] = new object?[]
            {
                new Dictionary<string, object?> { ["active"] = true, ["zip"] = "M5V" },
                null
            }
        });

        Assert.Equal(EntityWriteRequestDigest.Compute(first), EntityWriteRequestDigest.Compute(second));
    }

    [Fact]
    public void DigestPreservesEnumerableOrderAndJsonScalarTypes()
    {
        var baseline = Request(fields: new Dictionary<string, object?>
        {
            ["values"] = new object?[] { true, null, 12.5m }
        });
        var reordered = Request(fields: new Dictionary<string, object?>
        {
            ["values"] = new object?[] { 12.5m, null, true }
        });
        var strings = Request(fields: new Dictionary<string, object?>
        {
            ["values"] = new object?[] { "True", "null", "12.5" }
        });

        Assert.NotEqual(EntityWriteRequestDigest.Compute(baseline), EntityWriteRequestDigest.Compute(reordered));
        Assert.NotEqual(EntityWriteRequestDigest.Compute(baseline), EntityWriteRequestDigest.Compute(strings));
    }

    [Fact]
    public void DigestDistinguishesNumbersFromNumericStrings()
    {
        var number = Request(fields: new Dictionary<string, object?> { ["amount"] = 12.5m });
        var numericString = Request(fields: new Dictionary<string, object?> { ["amount"] = "12.5" });

        Assert.NotEqual(EntityWriteRequestDigest.Compute(number), EntityWriteRequestDigest.Compute(numericString));
    }

    [Fact]
    public void DigestFormatsNumbersIndependentlyOfCurrentCulture()
    {
        var request = Request(fields: new Dictionary<string, object?> { ["amount"] = 12.5m });
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = EntityWriteRequestDigest.Compute(request);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = EntityWriteRequestDigest.Compute(request);

            Assert.Equal(english, french);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void DigestIsLowercaseSha256Hex()
    {
        var digest = EntityWriteRequestDigest.Compute(Request());

        Assert.Equal(64, digest.Length);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void ChangeStateRouteTrimsInputsAndNormalizesVendors()
    {
        var route = EntitySyncChangeStateRoute.Create(
            " tenant ", $" {ValidScope} ", " bill.com ", " source ", " Customer ",
            " agentcontroller ", " target ", " Client ");

        Assert.Equal("tenant", route.TenantId);
        Assert.Equal(ValidScope, route.Scope);
        Assert.Equal(EntitySyncVendors.BillCom, route.SourceVendor);
        Assert.Equal("source", route.SourceConnectionId);
        Assert.Equal("Customer", route.SourceEntityType);
        Assert.Equal(EntitySyncVendors.AgentController, route.TargetVendor);
        Assert.Equal("target", route.TargetConnectionId);
        Assert.Equal("Client", route.TargetEntityType);
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("scope")]
    [InlineData("sourceVendor")]
    [InlineData("sourceConnectionId")]
    [InlineData("sourceEntityType")]
    [InlineData("targetVendor")]
    [InlineData("targetConnectionId")]
    [InlineData("targetEntityType")]
    public void ChangeStateRouteRejectsBlankInputs(string parameter)
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRouteWith(parameter, " "));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("0123456789abcdef")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ChangeStateRouteRequiresLowercaseSha256Scope(string scope)
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRouteWith("scope", scope));

        Assert.Equal("scope", exception.ParamName);
    }

    [Theory]
    [InlineData("tenantId", 257)]
    [InlineData("sourceVendor", 65)]
    [InlineData("sourceConnectionId", 65)]
    [InlineData("sourceEntityType", 65)]
    [InlineData("targetVendor", 65)]
    [InlineData("targetConnectionId", 65)]
    [InlineData("targetEntityType", 65)]
    public void ChangeStateRouteCapsInputsLikeExclusionRoutes(string parameter, int length)
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRouteWith(parameter, new string('a', length)));

        Assert.Equal(parameter, exception.ParamName);
    }

    private static EntityWriteRequest Request(
        Dictionary<string, object?>? fields = null,
        Dictionary<string, string?>? customFields = null) =>
        new()
        {
            Vendor = "HaloPSA",
            EntityType = "Client",
            Id = "target-42",
            PrimarySiteId = "site-7",
            Name = "Acme",
            Fields = fields ?? new Dictionary<string, object?> { ["website"] = "https://example.test" },
            CustomFields = customFields ?? new Dictionary<string, string?> { ["CFNetSuiteCustomerID"] = "42" }
        };

    private static void Mutate(EntityWriteRequest request, string mutation)
    {
        switch (mutation)
        {
            case "vendor": request.Vendor = "AgentController"; break;
            case "entity-type": request.EntityType = "Organisation"; break;
            case "name": request.Name = "Changed"; break;
            case "target-id": request.Id = "target-99"; break;
            case "primary-site-id": request.PrimarySiteId = "site-9"; break;
            case "field": request.Fields["website"] = "https://changed.test"; break;
            case "custom-field": request.CustomFields["CFNetSuiteCustomerID"] = "99"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static EntitySyncChangeStateRoute CreateRouteWith(string parameter, string value) =>
        EntitySyncChangeStateRoute.Create(
            parameter == "tenantId" ? value : "tenant",
            parameter == "scope" ? value : ValidScope,
            parameter == "sourceVendor" ? value : "NetSuite",
            parameter == "sourceConnectionId" ? value : "source",
            parameter == "sourceEntityType" ? value : "Customer",
            parameter == "targetVendor" ? value : "HaloPSA",
            parameter == "targetConnectionId" ? value : "target",
            parameter == "targetEntityType" ? value : "Client");
}

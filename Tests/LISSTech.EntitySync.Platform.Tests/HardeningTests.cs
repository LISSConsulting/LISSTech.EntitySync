using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using LISSTech.EntitySync.Adapters;
using LISSTech.EntitySync.Adapters.NCentral;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Core;
using LISSTech.EntitySync.Mcp;
using LISSTech.EntitySync.Ports;
using LISSTech.EntitySync.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

public sealed class HardeningTests
{
    [Fact]
    public void EqualSubjectsFromDifferentIssuersHaveDifferentTenantPartitions()
    {
        var first = HttpContext("https://issuer-a.example/", "same-subject");
        var firstTenant = first.TenantId;
        var firstActor = first.Actor;
        var secondTenant = HttpContext("https://issuer-b.example/", "same-subject").TenantId;

        Assert.NotEqual(firstTenant, secondTenant);
        Assert.Equal("https://issuer-a.example::same-subject", firstTenant);
        Assert.Equal(firstTenant, firstActor);
    }

    [Theory]
    [InlineData("https://issuer.example/", "subject", "mcp:tools", HttpStatusCode.OK)]
    [InlineData("https://issuer.example/", "subject", "wrong", HttpStatusCode.Forbidden)]
    [InlineData("https://issuer.example/", "", "mcp:tools", HttpStatusCode.Forbidden)]
    [InlineData("", "subject", "mcp:tools", HttpStatusCode.Forbidden)]
    [InlineData("https://issuer.example/?variant=1", "subject", "mcp:tools", HttpStatusCode.Forbidden)]
    public async Task HttpAuthorizationPolicyRequiresScopeIssuerAndSubject(
        string issuer,
        string subject,
        string scope,
        HttpStatusCode expectedStatus)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options => McpAuthorization.AddPolicy(options, "mcp:tools"));

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/mcp-test", () => "ok").RequireAuthorization("mcp");
        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp-test");
            if (issuer.Length > 0) request.Headers.Add("X-Test-Issuer", issuer);
            if (subject.Length > 0) request.Headers.Add("X-Test-Subject", subject);
            request.Headers.Add("X-Test-Scope", scope);

            using var response = await client.SendAsync(request);

            Assert.Equal(expectedStatus, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task VendorTransportRejectsOversizedResponseBeforeCallerReadsIt()
    {
        using var handler = new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[9])
        });
        using var client = VendorHttpClientFactory.Create(
            new Uri("https://vendor.example/"),
            handler,
            maximumResponseBytes: 8,
            maximumConcurrency: 1,
            minimumRequestInterval: TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync("entities"));

        Assert.Contains("8-byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VendorTransportEvictsExcessIdleEndpointGates()
    {
        var baseline = VendorHttpPolicyHandler.CachedEndpointGateCount;
        var gateLimit = baseline + 4;

        for (var index = 0; index < 12; index++)
        {
            using var handler = new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = VendorHttpClientFactory.Create(
                new Uri($"https://vendor-{Guid.NewGuid():N}.example/"),
                handler,
                maximumResponseBytes: 8,
                maximumConcurrency: 1,
                minimumRequestInterval: TimeSpan.Zero);
            using var response = await client.GetAsync("entities");
        }

        var removed = VendorHttpPolicyHandler.TrimEndpointGates(gateLimit, TimeSpan.MaxValue);

        Assert.True(removed >= 8);
        Assert.True(VendorHttpPolicyHandler.CachedEndpointGateCount <= gateLimit);
    }

    [Fact]
    public async Task VendorTransportDoesNotEvictEndpointGateWithActiveRequest()
    {
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingResponseHandler(releaseResponse.Task);
        using var client = VendorHttpClientFactory.Create(
            new Uri($"https://active-{Guid.NewGuid():N}.example/"),
            handler,
            maximumResponseBytes: 8,
            maximumConcurrency: 1,
            minimumRequestInterval: TimeSpan.Zero);

        var request = client.GetAsync("entities");
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var countWhileActive = VendorHttpPolicyHandler.CachedEndpointGateCount;

        var removed = VendorHttpPolicyHandler.TrimEndpointGates(0, TimeSpan.Zero);

        Assert.Equal(countWhileActive - 1, removed);
        Assert.True(VendorHttpPolicyHandler.CachedEndpointGateCount >= 1);
        releaseResponse.SetResult();
        using var response = await request;
    }

    [Fact]
    public async Task VendorTransportSharesOneEndpointGateAcrossConcurrentRequests()
    {
        var releaseResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingResponseHandler(releaseResponses.Task);
        using var client = VendorHttpClientFactory.Create(
            new Uri($"https://shared-{Guid.NewGuid():N}.example/"),
            handler,
            maximumResponseBytes: 8,
            maximumConcurrency: 2,
            minimumRequestInterval: TimeSpan.Zero);
        var baseline = VendorHttpPolicyHandler.CachedEndpointGateCount;

        var requests = new[] { client.GetAsync("first"), client.GetAsync("second") };
        await handler.TwoRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(baseline + 1, VendorHttpPolicyHandler.CachedEndpointGateCount);
        releaseResponses.SetResult();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public void RegistrationAdmissionsReserveCapacityAndRejectDuplicates()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var admissions = Enumerable.Range(0, 32)
            .Select(index => repository.BeginRegistration("tenant", $"connection-{index}", "HaloPSA"))
            .ToArray();
        try
        {
            Assert.Throws<InvalidOperationException>(() => repository.BeginRegistration("tenant", "connection-0", "HaloPSA"));
            var capacity = Assert.Throws<InvalidOperationException>(() => repository.BeginRegistration("tenant", "overflow", "HaloPSA"));
            Assert.Contains("limit of 32", capacity.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var admission in admissions) admission.Dispose();
        }
    }

    [Fact]
    public void NCentralSoapEndpointMustRemainOnConfiguredOrigin()
    {
        var options = new NCentralOptions
        {
            BaseUrl = "https://ncentral.example/",
            UserApiToken = "token",
            ServiceOrgId = "1",
            SoapEndpointPath = "https://attacker.example/soap"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => new NCentralEntityAdapter(options));

        Assert.Contains("relative path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterCannotExceedOperationDelayCeiling()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(12));

        Assert.Equal(RateLimitHelper.MaximumRetryDelay, RateLimitHelper.RateLimitDelay(response, 0));
    }

    [Fact]
    public void SuiteQlSearchEscapesLiteralWildcardCharacters()
    {
        Assert.Equal("A\\%\\_\\\\''", NetSuiteEntityAdapter.EscapeSuiteQlLikeLiteral("A%_\\'"));
        var query = NetSuiteEntityAdapter.BuildCustomerQuery(new EntityQuery
        {
            Search = "A%_\\'",
            IncludeInactive = true
        });
        Assert.Equal(3, query.Split("ESCAPE '\\'", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ESCAPE '\\\\'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpEntityReadRejectsOversizedSearchBeforeAdapterUse()
    {
        using var repository = new InMemoryEntityConnectionRepository();
        var adapter = new RecordingAdapter();
        repository.Register("tenant", "netsuite", adapter);

        var result = await ConnectionTools.GetEntities(
            repository,
            new McpRequestContext("tenant", false),
            "NetSuite",
            connectionId: "netsuite",
            search: new string('x', 513));

        Assert.Contains("Search cannot exceed 512 characters", result, StringComparison.Ordinal);
        Assert.Null(adapter.LastQuery);
    }

    private static McpRequestContext HttpContext(string issuer, string subject)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", issuer),
            new Claim("sub", subject)
        ], "Bearer"));
        return new McpRequestContext(new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal }
        });
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            AddClaim(claims, "iss", "X-Test-Issuer");
            AddClaim(claims, "sub", "X-Test-Subject");
            AddClaim(claims, "scope", "X-Test-Scope");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        private void AddClaim(List<Claim> claims, string type, string header)
        {
            var value = Request.Headers[header].ToString();
            if (value.Length > 0) claims.Add(new Claim(type, value));
        }
    }

    private sealed class StaticResponseHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory());
        }
    }

    private sealed class BlockingResponseHandler(Task releaseResponse) : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TwoRequestsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 2) TwoRequestsStarted.SetResult();
            RequestStarted.TrySetResult();
            await releaseResponse.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingAdapter : IEntityAdapter
    {
        public string Vendor => "NetSuite";
        public IReadOnlyList<string> LookupTypes => [];
        public EntityQuery? LastQuery { get; private set; }
        public Task<IReadOnlyList<ExternalEntity>> GetEntitiesAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<ExternalEntity>>([]);
        }
        public Task<IReadOnlyList<EntitySyncLookup>> GetLookupsAsync(string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntitySyncLookup>>([]);
        public Task<EntityWriteResult> CreateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EntityWriteResult> UpdateEntityAsync(EntityWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}

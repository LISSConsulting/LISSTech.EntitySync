using System.Text.Json;
using LISSTech.EntitySync.Adapters.NetSuite;
using LISSTech.EntitySync.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LISSTech.EntitySync.Platform.Tests;

[Collection(nameof(NetSuitePaginationCollection))]
public sealed class NetSuitePaginationTests
{
    [Fact]
    public async Task CustomerReadFollowsEverySuiteQlPageWithinRequestedCount()
    {
        var firstPageItems = Enumerable.Range(1, 1000)
            .Select(index => new { id = $"customer-{index}", entityid = $"Customer {index:D4}" })
            .ToArray();
        var secondPageItems = Enumerable.Range(1001, 300)
            .Select(index => new { id = $"customer-{index}", entityid = $"Customer {index:D4}" })
            .ToArray();
        await using var server = await ScriptedSuiteQlServer.StartAsync(
            Page(1000, 0, 1300, true, firstPageItems),
            Page(300, 1000, 1300, false, secondPageItems));
        using var adapter = new NetSuiteEntityAdapter(Options(server.BaseUrl));

        var entities = await adapter.GetEntitiesAsync(new EntityQuery
        {
            EntityType = "Customer",
            IncludeInactive = true,
            Count = 1300
        }, default);

        Assert.Equal(1300, entities.Count);
        Assert.Equal("customer-1", entities[0].Id);
        Assert.Equal("customer-1000", entities[999].Id);
        Assert.Equal("customer-1300", entities[1299].Id);
        Assert.Equal(1300, entities.Select(entity => entity.Id).Distinct(StringComparer.Ordinal).Count());
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal("?limit=1000&offset=0", requests[0].Uri.Query);
        Assert.Equal("?limit=1000&offset=1000", requests[1].Uri.Query);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.Contains("FETCH FIRST 1300 ROWS ONLY", requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuiteQlReadRejectsCountThatDisagreesWithItems()
    {
        await using var server = await ScriptedSuiteQlServer.StartAsync(
            Page(2, 0, 1, false, new[] { new { id = "1" } }));
        using var adapter = new NetSuiteEntityAdapter(Options(server.BaseUrl));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InvokeSuiteQlAsync("SELECT id FROM customer ORDER BY id", default));

        Assert.Contains("pagination", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuiteQlReadRejectsPresentInvalidPaginationMetadata()
    {
        var response = JsonSerializer.Serialize(new { count = -1, items = new[] { new { id = "1" } } });
        await using var server = await ScriptedSuiteQlServer.StartAsync(response);
        using var adapter = new NetSuiteEntityAdapter(Options(server.BaseUrl));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InvokeSuiteQlAsync("SELECT id FROM customer ORDER BY id", default));

        Assert.Contains("pagination metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuiteQlReadRejectsFullPageWithoutPaginationMetadata()
    {
        var items = Enumerable.Range(1, 1000).Select(index => new { id = index.ToString() }).ToArray();
        var response = JsonSerializer.Serialize(new { items });
        await using var server = await ScriptedSuiteQlServer.StartAsync(response);
        using var adapter = new NetSuiteEntityAdapter(Options(server.BaseUrl));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InvokeSuiteQlAsync("SELECT id FROM customer ORDER BY id", default));

        Assert.Contains("pagination metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuiteQlReadPreservesTerminalRawArrayRows()
    {
        var items = Enumerable.Range(1, 1001).Select(index => new { id = index.ToString() }).ToArray();
        await using var server = await ScriptedSuiteQlServer.StartAsync(JsonSerializer.Serialize(items));
        using var adapter = new NetSuiteEntityAdapter(Options(server.BaseUrl));

        var rows = await adapter.InvokeSuiteQlAsync("SELECT id FROM customer ORDER BY id", default);

        Assert.Equal(1001, rows.Count);
        Assert.Equal("1001", rows[1000]["id"]);
        Assert.Single(server.Requests);
    }

    private static string Page<T>(int count, int offset, int totalResults, bool hasMore, IReadOnlyList<T> items) =>
        JsonSerializer.Serialize(new { count, offset, totalResults, hasMore, items });

    private static NetSuiteOptions Options(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        AccountId = "test-account",
        ConsumerKey = "test-consumer-key",
        ConsumerSecret = "test-consumer-secret",
        TokenId = "test-token-id",
        TokenSecret = "test-token-secret"
    };

    private sealed class ScriptedSuiteQlServer : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly Queue<string> responses;
        private readonly List<CapturedRequest> requests = [];
        private readonly object gate = new();

        private ScriptedSuiteQlServer(WebApplication application, IEnumerable<string> responses)
        {
            this.application = application;
            this.responses = new Queue<string>(responses);
        }

        public string BaseUrl { get; private set; } = string.Empty;

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (gate) return requests.ToArray();
            }
        }

        public static async Task<ScriptedSuiteQlServer> StartAsync(params string[] responses)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var application = builder.Build();
            var server = new ScriptedSuiteQlServer(application, responses);
            application.MapPost("/services/rest/query/v1/suiteql", server.HandleAsync);
            await application.StartAsync();
            var addresses = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses;
            server.BaseUrl = addresses.Single();
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            string response;
            lock (gate)
            {
                requests.Add(new CapturedRequest(
                    new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}"),
                    body));
                response = responses.Count > 0
                    ? responses.Dequeue()
                    : throw new InvalidOperationException("The adapter requested an unexpected SuiteQL page.");
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    public sealed record CapturedRequest(Uri Uri, string Body);
}

[CollectionDefinition(nameof(NetSuitePaginationCollection), DisableParallelization = true)]
public sealed class NetSuitePaginationCollection;

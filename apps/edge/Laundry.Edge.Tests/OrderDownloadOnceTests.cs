using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Laundry.Edge.Orders;
using Laundry.Edge.Persistence;
using Laundry.Edge.Synchronization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Laundry.Edge.Tests;

[Trait("Category", "Database")]
public sealed class OrderDownloadOnceTests(OrderStorageFixture fixture) : IClassFixture<OrderStorageFixture>
{
    private readonly OrderDownloadScope _scope = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Uri Endpoint = new("https://localhost:7100/api/v1/gateway/order-plans");
    private OrderDownloadStore Store => new(fixture.Options, _scope, TimeProvider.System);
    private JsonObject Order(long version = 1, Guid? id = null, bool visible = true)
    {
        var item = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "order-plan.v1.visible.json")))!.AsObject();
        item["tenantId"] = _scope.TenantId; item["plantId"] = _scope.PlantId;
        item["orderId"] = id ?? Guid.NewGuid(); item["version"] = version;
        item["notesVisibleToOperators"] = visible;
        if (!visible) item["notes"] = null;
        return item;
    }
    private static string Page(string cursor, bool more = false, params JsonObject[] items) =>
        new JsonObject { ["items"] = new JsonArray(items.Select(x => x.DeepClone()).ToArray()), ["nextCursor"] = cursor, ["hasMore"] = more }.ToJsonString();
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private OrderDownloadOnce Runner(HttpClient client, TestTokens? tokens = null, OrderDownloadStore? store = null, Uri? endpoint = null) =>
        new(client, tokens ?? new(), store ?? Store, _scope, endpoint ?? Endpoint);
    private async Task<int> CopyCount()
    {
        await using var db = new PlantDbContext(fixture.Options);
        return await db.LocalOrderPlans.CountAsync(x => x.TenantId == _scope.TenantId && x.PlantId == _scope.PlantId);
    }

    [Fact]
    public async Task DownloadOnce_PagesAuthenticateAndResumeAfterProcessRestart()
    {
        var first = Order(); var second = Order(); var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            Assert.DoesNotContain("tenant", request.RequestUri!.Query);
            requests++;
            Assert.Equal(requests == 1 ? "?limit=1" : "?limit=1&cursor=first%2B%2F%3D", request.RequestUri.Query);
            return Task.FromResult(Json(requests == 1 ? Page("first+/=", true, first) : Page("second", false, second)));
        }));
        var partial = await Runner(client).RunAsync(1, 1);
        Assert.Equal("more_available", partial.Status); Assert.Equal(1, partial.Applied);
        var next = await Runner(client).RunAsync(1); // New runner/store, saved progress only.
        Assert.Equal("caught_up", next.Status); Assert.Equal(1, next.Applied); Assert.Equal(2, await CopyCount());
        Assert.Equal("second", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task DownloadOnce_LaterNetworkFailureRetainsCommittedPageAndRerunContinues()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => ++calls == 1
            ? Task.FromResult(Json(Page("first", true, Order()))) : throw new HttpRequestException("synthetic-private-detail")));
        var result = await Runner(client).RunAsync();
        Assert.Equal("order_download_connection_failed", result.Error); Assert.Equal(1, result.Pages);
        Assert.Equal(1, await CopyCount()); Assert.Equal("first", (await Store.ReadProgressAsync()).Cursor);
        using var resumed = new HttpClient(new Handler((request, _) =>
        {
            Assert.Contains("cursor=first", request.RequestUri!.Query);
            return Task.FromResult(Json(Page("caught-up")));
        }));
        Assert.Equal("caught_up", (await Runner(resumed).RunAsync()).Status);
        Assert.Equal(1, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_LostCommitAcknowledgementRetriesExactBatchWithoutRefetch()
    {
        var options = new DbContextOptionsBuilder<PlantDbContext>(fixture.Options).AddInterceptors(new LoseCommit()).Options;
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Page("saved", false, Order()))); }));
        var result = await Runner(client, store: new(options, _scope, TimeProvider.System)).RunAsync();
        Assert.Equal("caught_up", result.Status); Assert.Equal(1, calls); Assert.Equal(1, result.Applied);
        await using var db = new PlantDbContext(fixture.Options);
        Assert.Equal(1, await db.OrderDownloadReceipts.CountAsync(x => x.TenantId == _scope.TenantId));
    }

    [Fact]
    public async Task DownloadOnce_RepeatedSaveFailureLeavesNoPartialPage()
    {
        var options = new DbContextOptionsBuilder<PlantDbContext>(fixture.Options).AddInterceptors(new FailSave()).Options;
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Page("not-saved", false, Order()))); }));
        var result = await Runner(client, store: new(options, _scope, TimeProvider.System)).RunAsync();
        Assert.Equal("order_download_storage_unavailable", result.Error); Assert.Equal(1, calls);
        Assert.Equal(0, result.Pages); Assert.Equal(0, await CopyCount());
        Assert.Equal(0, (await Store.ReadProgressAsync()).Revision);
    }

    [Fact]
    public async Task DownloadOnce_ExpiredTokenRefreshesOnceAndSecond401Stops()
    {
        var tokens = new TestTokens(); var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1 ? new(HttpStatusCode.Unauthorized) : Json(Page("saved")))));
        Assert.Equal("caught_up", (await Runner(client, tokens).RunAsync()).Status);
        Assert.Equal(2, tokens.Requests); Assert.Equal(1, tokens.Invalidations);
        using var rejected = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        Assert.Equal("order_download_http_401", (await Runner(rejected).RunAsync()).Error);
        Assert.Equal("saved", (await Store.ReadProgressAsync()).Cursor);
    }

    [Fact]
    public async Task DownloadOnce_RepeatedCopyAndEmptyPollKeepCurrentOrder()
    {
        var item = Order();
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("first", false, item));
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(++calls == 1
            ? Page("same", true, item) : Page("caught-up")))));
        var result = await Runner(client).RunAsync();
        Assert.Equal("caught_up", result.Status); Assert.Equal(0, result.Applied); Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, result.Pages); Assert.Equal(1, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_RequestTimeoutStopsWithoutAdvancingProgress()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("synthetic timeout")));
        Assert.Equal("order_download_timeout", (await Runner(client).RunAsync()).Error);
        Assert.Equal(0, (await Store.ReadProgressAsync()).Revision);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(302)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task DownloadOnce_HttpFailureNeverReadsPrivateBodyOrResetsProgress(int status)
    {
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("saved", false, Order()));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new ThrowOnRead() })));
        var result = await Runner(client).RunAsync();
        Assert.Equal($"order_download_http_{status}", result.Error); Assert.Equal(0, result.Pages);
        Assert.Equal("saved", (await Store.ReadProgressAsync()).Cursor); Assert.Equal(1, await CopyCount());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-scope")]
    [InlineData("hidden-note")]
    [InlineData("too-many")]
    [InlineData("no-progress")]
    [InlineData("empty-more")]
    [InlineData("oversized")]
    [InlineData("wrong-media")]
    [InlineData("invalid-utf8")]
    public async Task DownloadOnce_InvalidResponseNeverAdvancesProgress(string mode)
    {
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("saved"));
        var item = Order();
        if (mode == "wrong-scope") item["plantId"] = Guid.NewGuid();
        if (mode == "hidden-note") item["notesVisibleToOperators"] = false;
        var json = mode switch
        {
            "malformed" => "{",
            "too-many" => Page("bad", false, item, Order()),
            "no-progress" => Page("saved", true, item),
            "empty-more" => Page("bad", true),
            "oversized" => new string('x', 4 * 1024 * 1024 + 1),
            _ => Page("bad", false, item)
        };
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = Json(json);
            if (mode == "wrong-media") response.Content.Headers.ContentType!.MediaType = "text/html";
            if (mode == "invalid-utf8")
            {
                response.Content = new ByteArrayContent([0xff]);
                response.Content.Headers.ContentType = new("application/json");
            }
            return Task.FromResult(response);
        }));
        Assert.Equal("stopped", (await Runner(client).RunAsync(1)).Status);
        Assert.Equal("saved", (await Store.ReadProgressAsync()).Cursor); Assert.Equal(0, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_NewerHiddenCopyClearsNotesAndOlderCopyCannotRestoreThem()
    {
        var item = Order(); var id = item["orderId"]!.GetValue<Guid>();
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("visible", false, item));
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(++calls == 1
            ? Page("hidden", true, Order(2, id, false)) : Page("old", false, Order(1, id))))));
        var result = await Runner(client).RunAsync();
        Assert.Equal("caught_up", result.Status); Assert.Equal(1, result.Applied); Assert.Equal(1, result.Older);
        await using var db = new PlantDbContext(fixture.Options);
        var copy = await db.LocalOrderPlans.SingleAsync(x => x.TenantId == _scope.TenantId);
        Assert.Equal(2, copy.Version); Assert.Null(JsonNode.Parse(copy.Payload)!["notes"]);
    }

    [Fact]
    public async Task DownloadOnce_EqualRevisionConflictRollsBackWholePage()
    {
        var item = Order(); var id = item["orderId"]!.GetValue<Guid>();
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("saved", false, item));
        var conflict = Order(1, id); conflict["notes"] = "different synthetic note";
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Page("bad", false, Order(), conflict)))));
        Assert.Equal("order_publication_revision_conflict", (await Runner(client).RunAsync()).Error);
        Assert.Equal("saved", (await Store.ReadProgressAsync()).Cursor); Assert.Equal(1, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_OverlappingResponseCannotOverwriteLaterProgress()
    {
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("other"), ct);
            return Json(Page("stale", false, Order()));
        }));
        Assert.Equal("stale_order_download_progress", (await Runner(client).RunAsync()).Error);
        Assert.Equal("other", (await Store.ReadProgressAsync()).Cursor); Assert.Equal(0, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_IdentityOutageAndCancellationPreserveCopies()
    {
        await Store.ApplyAsync(Guid.NewGuid(), 0, null, Page("saved", false, Order()));
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("HTTP should not be called")));
        Assert.Equal("order_download_identity_unavailable", (await Runner(client, new() { Unavailable = true }).RunAsync()).Error);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal("cancelled", (await Runner(client).RunAsync(ct: cancellation.Token)).Error);
        Assert.Equal("saved", (await Store.ReadProgressAsync()).Cursor); Assert.Equal(1, await CopyCount());
    }

    [Fact]
    public async Task DownloadOnce_StorageOutageFailsBeforeNetworkWithoutEmptyStateFallback()
    {
        var bad = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Port = 1, Pooling = false, Timeout = 1 };
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("HTTP should not be called")));
        var store = new OrderDownloadStore(OrderStorageFixture.MakeOptions(bad.ConnectionString), _scope, TimeProvider.System);
        Assert.Equal("order_download_storage_unavailable", (await Runner(client, store: store).RunAsync()).Error);
    }

    [Theory]
    [InlineData("http://localhost:7100/api/v1/gateway/order-plans", 100, 100)]
    [InlineData("https://example.invalid/api/v1/gateway/order-plans", 100, 100)]
    [InlineData("https://localhost:7100/api/v1/gateway/order-plans", 0, 100)]
    [InlineData("https://localhost:7100/api/v1/gateway/order-plans", 100, 101)]
    public async Task DownloadOnce_RejectsUnsafeEndpointOrUnboundedRunBeforeNetwork(string uri, int limit, int pages)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("HTTP should not be called")));
        Assert.Equal("order_download_configuration_invalid", (await Runner(client, endpoint: new(uri)).RunAsync(limit, pages)).Error);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class TestTokens : IGatewayTokenProvider
    {
        public int Requests; public int Invalidations; public bool Unavailable;
        public Task<GatewayTokenResult> GetAsync(CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(Unavailable ? GatewayTokenResult.Failure("synthetic-private-error") : GatewayTokenResult.Success("synthetic-token")); }
        public void Invalidate(string accessToken) { Assert.Equal("synthetic-token", accessToken); Invalidations++; }
    }
    private sealed class LoseCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("synthetic lost acknowledgement");
    }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("synthetic failure after SQL writes");
    }
    private sealed class ThrowOnRead : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new Exception("Private error body must not be read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

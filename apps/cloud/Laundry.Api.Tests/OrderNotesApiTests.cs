using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Laundry.Api.Orders;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private static string NotesDefaultPath => Path + "/settings/notes";
    private async Task GrantNotesDefault()
    {
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO identity_access.plant_membership_permissions (membership_id,permission) SELECT id,{OrderPlanningPermissions.EditNotesDefault} FROM identity_access.plant_memberships WHERE tenant_id = {Tenant} AND plant_id = {Plant} AND actor_subject = {_subject}");
    }
    private static string WithVisibility(string body, string value) => body[..^1] + ",\"notesVisibleToOperators\":" + value + "}";

    [Fact]
    public async Task NotesApi_OrdersInheritOverridePreserveAndExposeVisibilityHistory()
    {
        await GrantNotesDefault();
        using var initial = await _client.GetAsync(NotesDefaultPath);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.False((await initial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notesVisibleToOperators").GetBoolean());
        using var setting = await Update(_client, Guid.NewGuid(), NotesDefaultPath, initial.Headers.ETag!.ToString(), "{\"notesVisibleToOperators\":true}");
        Assert.Equal(HttpStatusCode.OK, setting.StatusCode);
        using var created = await Create(_client, Guid.NewGuid());
        var order = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(order.GetProperty("notesVisibleToOperators").GetBoolean());
        using var explicitHidden = await Create(_client, Guid.NewGuid(), WithVisibility(Body, "false"));
        Assert.False((await explicitHidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notesVisibleToOperators").GetBoolean());
        var planBody = Body[..^1] + ",\"customerReference\":null}";
        using var preserved = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), planBody);
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.True((await preserved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notesVisibleToOperators").GetBoolean());
        var key = Guid.NewGuid();
        using var hidden = await Update(_client, key, created.Headers.Location + "/plan", preserved.Headers.ETag!.ToString(), WithVisibility(planBody, "false"));
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var result = await hidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("notesVisibleToOperators").GetBoolean()); Assert.Equal(2, result.GetProperty("version").GetInt64());
        Assert.Equal("Synthetic planned order", result.GetProperty("notes").GetString());
        using var retry = await Update(_client, key, created.Headers.Location + "/plan", preserved.Headers.ETag!.ToString(), WithVisibility(planBody, "false"));
        Assert.Equal(await hidden.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        var current = await _client.GetFromJsonAsync<JsonElement>(created.Headers.Location);
        Assert.False(current.GetProperty("notesVisibleToOperators").GetBoolean());
        var list = await _client.GetFromJsonAsync<JsonElement>(Path);
        Assert.All(list.GetProperty("items").EnumerateArray(), x => Assert.False(x.GetProperty("notesVisibleToOperators").GetBoolean()));
        var history = await _client.GetFromJsonAsync<JsonElement>(created.Headers.Location + "/history");
        var entries = history.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.True(entries[0].GetProperty("initialPlan").GetProperty("notesVisibleToOperators").GetBoolean());
        var change = Assert.Single(entries[1].GetProperty("changes").EnumerateArray());
        Assert.Equal("notesVisibleToOperators", change.GetProperty("field").GetString());
        Assert.Equal("true", change.GetProperty("before").GetString()); Assert.Equal("false", change.GetProperty("after").GetString());
        var id = order.GetProperty("id").GetGuid();
        foreach (var lang in new[] { "en", "sv" })
        {
            var page = await _client.GetStringAsync(HistoryPage(id) + "?lang=" + lang);
            Assert.Contains(lang == "sv" ? "Anteckningar visas för operatörer" : "Notes shown to operators", WebUtility.HtmlDecode(page));
            Assert.Contains(lang == "sv" ? "Visas" : "Shown", page); Assert.Contains(lang == "sv" ? "Dolda" : "Hidden", page);
            var preview = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-preview");
            Directory.CreateDirectory(preview);
            await File.WriteAllTextAsync(System.IO.Path.Combine(preview, $"notes-history-{lang}.html"), page);
        }
        await using var db = new OrderDbContext(_orders);
        Assert.Equal(3, await db.AuditEntries.CountAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("false,\"notesVisibleToOperators\":true")]
    public async Task NotesApi_InvalidOrderChoicesAreRejectedWithoutWrites(string value)
    {
        using var rejected = await Create(_client, Guid.NewGuid(), WithVisibility(Body, value));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var created = await Create(_client, Guid.NewGuid());
        using var invalid = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), WithVisibility(UpdateBody(), value));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task NotesApi_DefaultNeedsExplicitPermissionAndSessionCsrf()
    {
        using var initial = await _client.GetAsync(NotesDefaultPath);
        var tag = initial.Headers.ETag!.ToString();
        using var denied = await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymous = Client(_app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(NotesDefaultPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Update(anonymous, Guid.NewGuid(), NotesDefaultPath, tag, "{}")).StatusCode);
        await GrantNotesDefault();
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}")).StatusCode);
        await Csrf(_client);
        using var accepted = await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity_access.plant_membership_permissions WHERE permission = {OrderPlanningPermissions.EditNotesDefault}");
        Assert.Equal(HttpStatusCode.Forbidden, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, accepted.Headers.ETag!.ToString(), "{\"notesVisibleToOperators\":false}")).StatusCode);
        await using var orders = new OrderDbContext(_orders);
        Assert.Single(await orders.NotesDefaultCommands.ToArrayAsync());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(NotesDefaultPath)).StatusCode);
    }

    [Fact]
    public async Task NotesApi_DefaultRetriesConcurrencyAndScopeAreProtected()
    {
        await GrantNotesDefault();
        using var initial = await _client.GetAsync(NotesDefaultPath);
        var tag = initial.Headers.ETag!.ToString(); var key = Guid.NewGuid(); var body = "{\"notesVisibleToOperators\":true}";
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Update(_client, key, NotesDefaultPath, tag, body)));
        Assert.All(duplicates, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var original = await duplicates[0].Content.ReadAsStringAsync(); var version1 = duplicates[0].Headers.ETag!.ToString();
        foreach (var response in duplicates) response.Dispose();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Update(_client, key, NotesDefaultPath, tag, "{\"notesVisibleToOperators\":false}")).StatusCode);
        using var next = await Update(_client, Guid.NewGuid(), NotesDefaultPath, version1, "{\"notesVisibleToOperators\":false}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var retried = await Update(_client, key, NotesDefaultPath, tag, body);
        Assert.Equal(original, await retried.Content.ReadAsStringAsync()); Assert.Equal(version1, retried.Headers.ETag!.ToString());
        var current = await _client.GetFromJsonAsync<JsonElement>(NotesDefaultPath);
        Assert.False(current.GetProperty("notesVisibleToOperators").GetBoolean()); Assert.Equal(2, current.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/api/v1/plants/{Guid.NewGuid():D}/orders/settings/notes")).StatusCode);
        await using var members = new ManagementAccessDbContext(_members);
        await members.Database.ExecuteSqlRawAsync("UPDATE identity_access.plant_memberships SET tenant_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");
        Assert.Equal(HttpStatusCode.BadRequest, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, body)).StatusCode);
        var otherTenant = await _client.GetFromJsonAsync<JsonElement>(NotesDefaultPath);
        Assert.False(otherTenant.GetProperty("notesVisibleToOperators").GetBoolean()); Assert.Equal(0, otherTenant.GetProperty("version").GetInt64());
    }

    [Theory]
    [InlineData("{}", "valid", 400)]
    [InlineData("{\"notesVisibleToOperators\":null}", "valid", 400)]
    [InlineData("{\"notesVisibleToOperators\":\"true\"}", "valid", 400)]
    [InlineData("{\"notesVisibleToOperators\":true,\"tenantId\":\"forged\"}", "valid", 400)]
    [InlineData("{\"notesVisibleToOperators\":true,\"notesVisibleToOperators\":false}", "valid", 400)]
    [InlineData("[]", "valid", 400)]
    [InlineData("{", "valid", 400)]
    [InlineData("{\"notesVisibleToOperators\":true}", "missing", 428)]
    [InlineData("{\"notesVisibleToOperators\":true}", "*", 400)]
    [InlineData("{\"notesVisibleToOperators\":true}", "W/\"0\"", 400)]
    public async Task NotesApi_DefaultRejectsInvalidRequests(string body, string condition, int status)
    {
        await GrantNotesDefault();
        using var initial = await _client.GetAsync(NotesDefaultPath);
        var tag = condition == "valid" ? initial.Headers.ETag!.ToString() : condition == "missing" ? null : condition;
        using var rejected = await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, body);
        Assert.Equal(status, (int)rejected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(NotesDefaultPath + "?tenantId=forged")).StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.Empty(await db.NotesDefaults.ToArrayAsync()); Assert.Empty(await db.NotesDefaultCommands.ToArrayAsync());
    }

    [Fact]
    public async Task NotesApi_LegacyHistoryDoesNotInventVisibility()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new ServiceOrder
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            PlantId = Plant,
            OrderNumber = "SYNTHETIC-LEGACY",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByIssuer = ManagementAccessStore.DevelopmentIssuer,
            UpdatedByIssuer = ManagementAccessStore.DevelopmentIssuer,
            CreatedBySubject = _subject,
            UpdatedBySubject = _subject
        };
        var plan = new OrderPlan(Customer, new DateOnly(2030, 1, 1), new DateOnly(2030, 1, 3), Notes: "Synthetic legacy notes");
        order.Apply(plan);
        await using var db = new OrderDbContext(_orders);
        db.Orders.Add(order);
        db.AuditEntries.Add(new OrderAuditEntry
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            PlantId = Plant,
            OrderId = order.Id,
            OrderVersion = 1,
            Action = "order_created",
            RecordedAtUtc = now,
            ActorIssuer = ManagementAccessStore.DevelopmentIssuer,
            ActorSubject = _subject,
            RequestId = Guid.NewGuid(),
            AfterPlan = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        await db.SaveChangesAsync();
        var history = await _client.GetFromJsonAsync<JsonElement>($"{Path}/{order.Id:D}/history");
        Assert.Equal(JsonValueKind.Null, history.GetProperty("items")[0].GetProperty("initialPlan").GetProperty("notesVisibleToOperators").ValueKind);
        foreach (var lang in new[] { "en", "sv" })
        {
            var page = await _client.GetStringAsync(HistoryPage(order.Id) + "?lang=" + lang);
            Assert.Contains(lang == "sv" ? "Ej registrerat" : "Not recorded", page);
            var preview = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-preview"); Directory.CreateDirectory(preview);
            await File.WriteAllTextAsync(System.IO.Path.Combine(preview, $"notes-history-legacy-{lang}.html"), page);
        }
    }

    [Fact]
    public async Task NotesApi_LegacyReceiptStaysUnchangedAndCurrentReadsRemainExplicit()
    {
        using var created = await Create(_client, Guid.NewGuid());
        await using var db = new OrderDbContext(_orders);
        var receipt = await db.CommandReceipts.AsNoTracking().SingleAsync();
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(receipt.ResponseBody)!.AsObject();
        legacy.Remove("notesVisibleToOperators");
        receipt.ResponseBody = legacy.ToJsonString(); receipt.IdempotencyKey = Guid.NewGuid();
        db.CommandReceipts.Add(receipt); await db.SaveChangesAsync();
        await db.Entry(receipt).ReloadAsync();
        using var retry = await Create(_client, receipt.IdempotencyKey);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(receipt.ResponseBody, await retry.Content.ReadAsStringAsync());
        Assert.False((await retry.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("notesVisibleToOperators", out _));
        var current = await _client.GetFromJsonAsync<JsonElement>(created.Headers.Location);
        Assert.False(current.GetProperty("notesVisibleToOperators").GetBoolean());
        Assert.Single(await db.AuditEntries.ToArrayAsync());
    }

    [Fact]
    public async Task NotesApi_AmbiguousPlantAssignmentsCannotReadOrWriteDefault()
    {
        var membership = new PlantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            PlantId = Plant,
            ActorIssuer = ManagementAccessStore.DevelopmentIssuer,
            ActorSubject = _subject,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        membership.Permissions = [new() { MembershipId = membership.Id, Permission = OrderPlanningPermissions.Read },
            new() { MembershipId = membership.Id, Permission = OrderPlanningPermissions.EditNotesDefault }];
        await using var members = new ManagementAccessDbContext(_members); members.Memberships.Add(membership); await members.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync(NotesDefaultPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, null, "{}")).StatusCode);
        await using var orders = new OrderDbContext(_orders);
        Assert.Empty(await orders.NotesDefaultCommands.ToArrayAsync());
    }

    [Fact]
    public async Task NotesApi_DefaultOutageAndRequestBoundsAreSafe()
    {
        await GrantNotesDefault();
        using var initial = await _client.GetAsync(NotesDefaultPath); var tag = initial.Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Update(_client, Guid.NewGuid(), NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}" + new string(' ', 1024))).StatusCode);
        using var chunked = new HttpRequestMessage(HttpMethod.Put, NotesDefaultPath) { Content = new UnknownLengthJson(new string(' ', 1025)) };
        chunked.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D")); chunked.Headers.TryAddWithoutValidation("If-Match", tag);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _client.SendAsync(chunked)).StatusCode);
        using var duplicateHeaders = new HttpRequestMessage(HttpMethod.Put, NotesDefaultPath) { Content = JsonContent.Create(new { notesVisibleToOperators = true }) };
        duplicateHeaders.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        duplicateHeaders.Headers.TryAddWithoutValidation("If-Match", new[] { tag, tag });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(duplicateHeaders)).StatusCode);
        using var wrongType = new HttpRequestMessage(HttpMethod.Put, NotesDefaultPath) { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _client.SendAsync(wrongType)).StatusCode);
        using var missingKey = new HttpRequestMessage(HttpMethod.Put, NotesDefaultPath) { Content = JsonContent.Create(new { notesVisibleToOperators = true }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(missingKey)).StatusCode);
        await _postgres.StopAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync(NotesDefaultPath)).StatusCode);
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Update(_client, key, NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}")).StatusCode);
        await _postgres.StartAsync();
        _app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        using var recovered = await Update(_client, key, NotesDefaultPath, tag, "{\"notesVisibleToOperators\":true}");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthJson(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private static string HistoryPage(Guid id) => $"/management/orders/{Plant:D}/{id:D}/history";

    [Theory]
    [InlineData("en", "Order history", "Before", "After", "Name unavailable")]
    [InlineData("sv", "Orderhistorik", "Före", "Efter", "Namn saknas")]
    public async Task HistoryPage_ReadableLocalizedEvidenceWithoutWrites(string lang, string title, string before, string after, string missingName)
    {
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var editor = Client(_app); Cookie(editor, _app, displayName: "Editor <synthetic>"); await Csrf(editor);
        using var changed = await Update(editor, Guid.NewGuid(), created.Headers.Location + "/plan", created.Headers.ETag!.ToString(), UpdateBody(notes: "<script>alert('test')</script>\nSecond line"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.laundry_customers SET is_active = false; UPDATE customers.plant_services SET is_active = false");
        using var page = await _client.GetAsync(HistoryPage(id) + "?lang=" + lang);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"<h1>{title}</h1>", html);
        Assert.Contains($"<dt>{before}</dt>", html); Assert.Contains($"<dt>{after}</dt>", html);
        Assert.Contains(missingName, html);
        Assert.Contains("Editor &lt;synthetic&gt;", html);
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>", html);
        Assert.Contains("Second line", html);
        Assert.Contains("DEMO-001", html); Assert.Contains("Demo laundry customer", html);
        Assert.Contains("Europe/Stockholm", html);
        Assert.DoesNotContain(_subject, html);
        Assert.DoesNotContain("actorSubject", html);
        Assert.DoesNotContain("<h4>Customer</h4>", html);
        Assert.True(page.Headers.CacheControl!.NoStore);
        var detail = await _client.GetStringAsync($"/management/orders/{Plant:D}/{id:D}");
        Assert.Contains(HistoryPage(id), detail);
        await using var database = new OrderDbContext(_orders);
        Assert.Equal(1, await database.Orders.CountAsync());
        Assert.Equal(2, await database.AuditEntries.CountAsync());
        Assert.Equal(2, await database.CommandReceipts.CountAsync());
    }

    [Fact]
    public async Task HistoryPage_RequiresCurrentSessionReadPermissionAndScopedOrder()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var anonymous = Client(_app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(HistoryPage(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(HistoryPage(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync($"/management/orders/{Guid.NewGuid():D}/{id:D}/history")).StatusCode);
        await using var db = new Laundry.Api.Security.ManagementAccess.ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(HistoryPage(id))).StatusCode);
    }

    [Fact]
    public async Task HistoryPage_PagingPreservesLanguageAndRejectsTamperedOrOtherOrderCursor()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var tag = created.Headers.ETag!.ToString();
        for (var i = 2; i <= 26; i++)
        {
            using var update = await Update(_client, Guid.NewGuid(), created.Headers.Location + "/plan", tag, UpdateBody(notes: "Version " + i));
            Assert.Equal(HttpStatusCode.OK, update.StatusCode); tag = update.Headers.ETag!.ToString();
        }
        var first = await _client.GetStringAsync(HistoryPage(id) + "?lang=sv");
        Assert.Equal(25, System.Text.RegularExpressions.Regex.Matches(first, "<li class=history-entry>").Count);
        var next = System.Text.RegularExpressions.Regex.Match(first, "href='([^']+)'>Nästa sida</a>").Groups[1].Value;
        Assert.NotEmpty(next);
        var second = await _client.GetStringAsync(WebUtility.HtmlDecode(next));
        Assert.Contains("<h1>Orderhistorik</h1>", second); Assert.Contains("Version 26", second);
        Assert.Contains("Första sidan", second); Assert.Contains("name=cursor", second);
        Assert.DoesNotContain("Nästa sida</a>", second);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(HistoryPage(id) + "?cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(HistoryPage(id) + "?cursor=a&cursor=b")).StatusCode);
        using var other = await Create(_client, Guid.NewGuid());
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(WebUtility.HtmlDecode(next).Replace(id.ToString(), otherId.ToString()))).StatusCode);
    }

    [Fact]
    public async Task HistoryPage_CustomerLabelsAreBoundedAndNeverCrossScope()
    {
        await using var db = new CustomerDbContext(_customers);
        var store = new CustomerStore(db);
        Assert.Single(await store.ReadHistoryLabelsAsync(Tenant, Plant, [Customer]));
        Assert.Empty(await store.ReadHistoryLabelsAsync(Guid.NewGuid(), Plant, [Customer]));
        Assert.Empty(await store.ReadHistoryLabelsAsync(Tenant, Guid.NewGuid(), [Customer]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadHistoryLabelsAsync(Tenant, Plant, Enumerable.Repeat(Customer, 52).ToArray()));
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM customers.plant_services");
        var html = await _client.GetStringAsync(HistoryPage(id) + "?lang=en");
        Assert.Contains("Customer unavailable", html);
        Assert.DoesNotContain("Demo laundry customer", html);
        Assert.DoesNotContain(Customer.ToString(), html);
    }

    [Fact]
    public async Task HistoryPage_DatabaseOutageIsSafeAndRecoveryIsReadOnly()
    {
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await _postgres.StopAsync();
        using var unavailable = await _client.GetAsync(HistoryPage(id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("Nothing has been changed", await unavailable.Content.ReadAsStringAsync());
        await _postgres.StartAsync();
        _app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        _orders = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(HistoryPage(id))).StatusCode);
        await AssertCounts(1);
    }
}

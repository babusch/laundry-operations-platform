using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Api.Orders;
using Laundry.Api.Customers;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private static string EditPath(Guid id) => $"/management/orders/{Plant:D}/{id:D}/edit";
    private async Task<(Guid Id, Dictionary<string, string> Values, string Html)> EditForm()
    {
        if (!_client.DefaultRequestHeaders.Contains("X-Management-CSRF")) await Csrf(_client);
        using var created = await Create(_client, Guid.NewGuid());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        var html = await _client.GetStringAsync(EditPath(id) + "?lang=en");
        var values = HiddenValues(html);
        values["customer"] = Customer.ToString("D");
        values["receiving"] = "2030-01-01";
        values["delivery"] = "2030-01-03";
        values["reference"] = "";
        values["notes"] = "Synthetic planned order";
        values["visibility"] = "false";
        values["lang"] = "en";
        values["action"] = "save";
        return (id, values, html);
    }
    private static Dictionary<string, string> RetainValues(string html, Dictionary<string, string> original)
    {
        var values = HiddenValues(html);
        foreach (var entry in original.Where(x => x.Key != "draft" && !x.Key.StartsWith("__RequestVerificationToken", StringComparison.Ordinal)))
            values[entry.Key] = entry.Value;
        return values;
    }
    private async Task AssertEditState(long version, int audits, int receipts, string? notes = null)
    {
        await using var db = new OrderDbContext(_orders);
        var order = await db.Orders.SingleAsync();
        Assert.Equal(version, order.Version);
        if (notes is not null) Assert.Equal(notes, order.Notes);
        Assert.Equal(audits, await db.AuditEntries.CountAsync());
        Assert.Equal(receipts, await db.CommandReceipts.CountAsync());
        Assert.Equal(1, (await db.NumberCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task EditPage_PrefillsAndLanguageValidationKeepEntriesThenSaveRedirectsAndRetriesOnce()
    {
        var form = await EditForm();
        Assert.Contains("Edit order", form.Html);
        Assert.Contains("value='2030-01-03'", form.Html);
        Assert.Contains("Synthetic planned order</textarea>", form.Html);
        form.Values["delivery"] = "2029-01-01";
        form.Values["reference"] = "<synthetic reference>";
        using var invalid = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var html = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Delivery cannot be before receiving", html);
        var values = RetainValues(html, form.Values);
        values["validation"] = "1"; values["lang"] = "sv"; values["action"] = "language";
        using var language = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values));
        html = await language.Content.ReadAsStringAsync();
        Assert.Contains("Leverans kan inte vara före inleverans", WebUtility.HtmlDecode(html));
        Assert.Contains("&lt;synthetic reference&gt;", html);
        await AssertEditState(1, 1, 1);
        values = RetainValues(html, values); values["delivery"] = "2030-01-04"; values["action"] = "save";
        var replies = await Task.WhenAll(_client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values)), _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values)));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.SeeOther, x.StatusCode));
        Assert.Equal($"/management/orders/{Plant:D}/{form.Id:D}", replies[0].Headers.Location!.ToString());
        Assert.Equal(replies[0].Headers.Location, replies[1].Headers.Location);
        await AssertEditState(2, 2, 2);
        foreach (var reply in replies) reply.Dispose();
    }

    [Fact]
    public async Task EditPage_ConflictRetainsBothPlansAndRequiresExplicitReviewBeforeNewSave()
    {
        var form = await EditForm();
        var second = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), form.Values);
        form.Values["notes"] = "My retained unsaved edit";
        second["notes"] = "Other saved edit <synthetic>";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(second))).StatusCode);
        using var conflict = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var html = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("Latest saved values", html);
        Assert.Contains("Other saved edit &lt;synthetic&gt;", html);
        Assert.Contains("My retained unsaved edit</textarea>", html);
        Assert.Contains("name=action value=save disabled", html);
        // Ignored synthetic fixture for visual review; no live order/session is used.
        await File.WriteAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "edit-conflict-preview.html"), html);
        var values = RetainValues(html, form.Values);
        values["action"] = "language"; values["lang"] = "sv";
        using var language = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values));
        html = await language.Content.ReadAsStringAsync();
        Assert.Contains("Senast sparade uppgifter", html);
        Assert.Contains("name=action value=save disabled", html);
        values = RetainValues(html, values); values["action"] = "save";
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values))).StatusCode);
        await AssertEditState(2, 2, 2, "Other saved edit <synthetic>");
        values["action"] = "review";
        using var reviewed = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        html = await reviewed.Content.ReadAsStringAsync();
        await AssertEditState(2, 2, 2); // Reviewing is not a write.
        values = RetainValues(html, values); values["action"] = "save";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values))).StatusCode);
        await AssertEditState(3, 3, 3, "My retained unsaved edit");
    }

    [Fact]
    public async Task EditPage_AnotherChangeDuringReviewMustBeReviewedAgain()
    {
        var form = await EditForm();
        var other = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), form.Values);
        other["notes"] = "First other edit";
        await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(other));
        form.Values["notes"] = "Retained edit";
        using var conflict = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values));
        var review = RetainValues(await conflict.Content.ReadAsStringAsync(), form.Values); review["action"] = "review";
        other = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), other);
        other["notes"] = "Second other edit";
        await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(other));
        using var changed = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(review));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var html = await changed.Content.ReadAsStringAsync();
        Assert.Contains("Second other edit", html);
        Assert.Contains("name=action value=save disabled", html);
        await AssertEditState(3, 3, 3, "Second other edit");
    }

    [Fact]
    public async Task EditPage_PostCommitLostAcknowledgementLocksExactRequestAndRetryDoesNotOverwriteLaterEdit()
    {
        var form = await EditForm();
        await using var app = Application().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OrderDbContext>(options => options.AddInterceptors(new LoseCommitAcknowledgement()))));
        using var client = Client(app); Cookie(client, app);
        var values = RetainValues(await client.GetStringAsync(EditPath(form.Id)), form.Values);
        values["notes"] = "Uncertain committed edit";
        using var uncertain = await client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode);
        var html = await uncertain.Content.ReadAsStringAsync();
        Assert.Contains("readonly", html); Assert.Contains("Retry same save", html);
        await AssertEditState(2, 2, 2, "Uncertain committed edit");
        var later = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), form.Values); later["notes"] = "Later saved edit";
        await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(later));
        var retry = RetainValues(html, values);
        retry["notes"] = "Changed uncertain retry";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(retry))).StatusCode);
        retry["notes"] = values["notes"];
        Assert.Equal(HttpStatusCode.SeeOther, (await client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(retry))).StatusCode);
        await AssertEditState(3, 3, 3, "Later saved edit");
    }

    [Fact]
    public async Task EditPage_CsrfScopeActorTamperingAndCurrentPermissionsAreEnforced()
    {
        var form = await EditForm();
        var missing = new Dictionary<string, string>(form.Values);
        foreach (var key in missing.Keys.Where(x => x.StartsWith("__RequestVerificationToken", StringComparison.Ordinal)).ToArray()) missing.Remove(key);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(missing))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(EditPath(Guid.NewGuid()), new FormUrlEncodedContent(form.Values))).StatusCode);
        var tampered = new Dictionary<string, string>(form.Values) { ["draft"] = form.Values["draft"] + "tampered" };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(tampered))).StatusCode);
        using var anonymous = Client(_app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(EditPath(form.Id))).StatusCode);
        var subject = Guid.NewGuid().ToString();
        await using var members = new ManagementAccessDbContext(_members);
        await new ManagementAccessStore(members).InitializeDevelopmentAsync(subject);
        using var other = Client(_app); Cookie(other, _app, subject);
        var otherValues = RetainValues(await other.GetStringAsync(EditPath(form.Id)), form.Values);
        otherValues["draft"] = form.Values["draft"];
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(otherValues))).StatusCode);
        await members.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.plan.edit'");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values))).StatusCode);
        Assert.DoesNotContain("Edit order", await _client.GetStringAsync($"/management/orders/{Plant:D}/{form.Id:D}"));
        await AssertEditState(1, 1, 1);
    }

    [Fact]
    public async Task EditPage_UnchangedSaveKeepsVersionAndIneligibleCustomerRejectsWithoutWrite()
    {
        var form = await EditForm();
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values))).StatusCode);
        await AssertEditState(1, 1, 2);
        var values = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), form.Values);
        values["notes"] = "Must not save";
        await using var customers = new CustomerDbContext(_customers);
        await customers.Database.ExecuteSqlRawAsync("UPDATE customers.plant_services SET is_active = false");
        using var rejected = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Must not save</textarea>", await rejected.Content.ReadAsStringAsync());
        await AssertEditState(1, 1, 2);
    }

    [Fact]
    public async Task EditPage_NonPlannedAndFeatureBoundariesAreEnforced()
    {
        var form = await EditForm();
        await using var db = new OrderDbContext(_orders);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE orders.service_orders DROP CONSTRAINT ck_order_status; UPDATE orders.service_orders SET status = 'receiving'");
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync(EditPath(form.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values))).StatusCode);
        await AssertEditState(1, 1, 1);
    }
}

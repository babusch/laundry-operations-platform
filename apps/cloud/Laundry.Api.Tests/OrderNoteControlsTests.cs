using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Laundry.Api.Orders;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Laundry.Api.Tests;

public sealed partial class OrderCreateEndpointTests
{
    private static string NotesSettingsPath => $"/management/orders/{Plant:D}/settings";
    private async Task<(string Html, Dictionary<string, string> Values)> NotesSettingsForm(HttpClient? client = null)
    {
        client ??= _client; client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        var html = await client.GetStringAsync(NotesSettingsPath + "?lang=en");
        var values = HiddenValues(html); values["visibility"] = "false"; values["lang"] = "en"; values["action"] = "save";
        return (html, values);
    }
    private static Task SavePreview(string name, string html)
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-preview"); Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(System.IO.Path.Combine(directory, name + ".html"), html);
    }

    [Fact]
    public async Task NoteControls_OrdersCopyDisplayedDefaultAndPreserveChoiceAcrossLanguageAndEdits()
    {
        await GrantNotesDefault();
        var settings = await NotesSettingsForm(); settings.Values["visibility"] = "true";
        using var saved = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(settings.Values));
        Assert.Equal(HttpStatusCode.SeeOther, saved.StatusCode);
        var create = await CreateForm(); Assert.Contains("value=true selected", create.Html);
        create.Values["visibility"] = "true";
        await SavePreview("create-notes-en", create.Html);
        // A later default change must not silently change the choice already shown in this form.
        settings = await NotesSettingsForm(); settings.Values["visibility"] = "false";
        await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(settings.Values));
        create.Values["action"] = "language"; create.Values["lang"] = "sv";
        using var translated = await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(create.Values));
        var html = await translated.Content.ReadAsStringAsync(); Assert.Contains("value=true selected", html);
        Assert.Contains("Visa anteckningar", html); await SavePreview("create-notes-sv", html);
        var values = RetainValues(html, create.Values); values["action"] = "save";
        using var created = await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(values));
        Assert.Equal(HttpStatusCode.SeeOther, created.StatusCode);
        await using var db = new OrderDbContext(_orders);
        var order = await db.Orders.SingleAsync(); Assert.True(order.NotesVisibleToOperators);
        var editHtml = await _client.GetStringAsync(EditPath(order.Id) + "?lang=en");
        Assert.Contains("value=true selected", editHtml);
        var edit = RetainValues(editHtml, values); edit["visibility"] = "false"; edit["action"] = "save";
        using var edited = await _client.PostAsync(EditPath(order.Id), new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.SeeOther, edited.StatusCode);
        var current = await db.Orders.AsNoTracking().SingleAsync(); Assert.False(current.NotesVisibleToOperators); Assert.Equal(2, current.Version);
        Assert.Contains("Dolda", await _client.GetStringAsync(edited.Headers.Location + "?lang=sv"));
        Assert.Equal(2, await db.AuditEntries.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("yes")]
    public async Task NoteControls_InvalidChoiceIsRetainedAndTranslatedWithoutWrites(string choice)
    {
        var form = await CreateForm(); form.Values["visibility"] = choice;
        using var invalid = await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var html = await invalid.Content.ReadAsStringAsync(); Assert.Contains("visibility-error", html);
        var values = RetainValues(html, form.Values); values["action"] = "language"; values["lang"] = "sv"; values["validation"] = "1";
        using var translated = await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(values));
        Assert.Contains("Välj om anteckningarna", WebUtility.HtmlDecode(await translated.Content.ReadAsStringAsync()));
        await AssertCounts(0);
    }

    [Fact]
    public async Task NoteControls_LegacyAcceptedFormCanStillRetryWithoutAddingVisibilityToItsIdentity()
    {
        var form = await CreateForm();
        var key = Guid.NewGuid();
        var plan = new OrderPlan(Customer, new DateOnly(2030, 1, 2), new DateOnly(2030, 1, 3), form.Values["reference"], form.Values["notes"]);
        await Csrf(_client);
        using var created = await Create(_client, key, JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _client.DefaultRequestHeaders.Remove("X-Management-CSRF");
        var scope = new OrderPlanningScope(Tenant, Plant, ManagementAccessStore.DevelopmentIssuer, _subject);
        form.Values["draft"] = _app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Laundry.Management.CreateDraft.v1")
            .Protect(JsonSerializer.Serialize(new { Scope = scope, Key = key, LockedPlan = plan.Normalize() }));
        form.Values.Remove("visibility");
        using var retry = await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.SeeOther, retry.StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task NoteControls_CreateOnlyPermissionCanLoadItsDefaultWithoutOrderRead()
    {
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM identity_access.plant_membership_permissions WHERE permission = 'orders.read'");
        var form = await CreateForm(); Assert.Contains("value=false selected", form.Html);
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(CreatePagePath, new FormUrlEncodedContent(form.Values))).StatusCode);
        await AssertCounts(1);
    }

    [Fact]
    public async Task NoteControls_OrderVisibilityConflictRetainsChoiceUntilExplicitReviewAndSave()
    {
        var form = await EditForm(); var other = RetainValues(await _client.GetStringAsync(EditPath(form.Id)), form.Values); other["visibility"] = "true";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(other))).StatusCode);
        using var conflict = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var html = await conflict.Content.ReadAsStringAsync(); Assert.Contains("Shown", html); Assert.Contains("value=false selected", html);
        var review = RetainValues(html, form.Values); review["action"] = "review";
        using var reviewed = await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(review));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        await AssertEditState(2, 2, 2);
        var save = RetainValues(await reviewed.Content.ReadAsStringAsync(), review); save["action"] = "save";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(EditPath(form.Id), new FormUrlEncodedContent(save))).StatusCode);
        await AssertEditState(3, 3, 3);
        await using var db = new OrderDbContext(_orders); Assert.False((await db.Orders.SingleAsync()).NotesVisibleToOperators);
    }

    [Fact]
    public async Task NoteControls_SettingsAreReadOnlyWithoutSeparatePermissionAndNavigationIsPresent()
    {
        var form = await NotesSettingsForm(); Assert.Contains("Read only", form.Html); Assert.DoesNotContain("value=save", form.Html);
        await SavePreview("notes-settings-readonly-en", form.Html);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(form.Values))).StatusCode);
        var list = await _client.GetStringAsync($"/management/orders/{Plant:D}"); Assert.Contains(NotesSettingsPath, list);
        using var anonymous = Client(_app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(NotesSettingsPath)).StatusCode);
        await using var db = new OrderDbContext(_orders); Assert.Empty(await db.NotesDefaultCommands.ToArrayAsync());
    }

    [Fact]
    public async Task NoteControls_SettingsLanguageKeepsChoiceAndConcurrentDuplicatesMakeOneCommand()
    {
        await GrantNotesDefault(); var form = await NotesSettingsForm();
        await SavePreview("notes-settings-hidden-en", form.Html);
        form.Values["visibility"] = "true"; form.Values["action"] = "language"; form.Values["lang"] = "sv";
        using var translated = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(form.Values));
        var html = await translated.Content.ReadAsStringAsync(); Assert.Contains("value=true selected", html);
        Assert.Contains("Spara standard", html); await SavePreview("notes-settings-shown-sv", html);
        await using var db = new OrderDbContext(_orders); Assert.Empty(await db.NotesDefaultCommands.ToArrayAsync());
        var values = RetainValues(html, form.Values); values["action"] = "save";
        var saved = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(values))));
        Assert.All(saved, x => Assert.Equal(HttpStatusCode.SeeOther, x.StatusCode)); foreach (var response in saved) response.Dispose();
        Assert.True((await db.NotesDefaults.SingleAsync()).NotesVisibleToOperators); Assert.Single(await db.NotesDefaultCommands.ToArrayAsync());
        values["visibility"] = "false";
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(values))).StatusCode);
    }

    [Fact]
    public async Task NoteControls_SettingsStaleSavesRequireReviewWhichNeverSaves()
    {
        await GrantNotesDefault(); var first = await NotesSettingsForm(); var second = await NotesSettingsForm();
        first.Values["visibility"] = "true";
        await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(first.Values));
        using var conflict = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(second.Values));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var html = await conflict.Content.ReadAsStringAsync(); Assert.Contains("Latest saved default", html);
        Assert.Contains("value=false selected", html); Assert.Contains("value=save disabled", html);
        await SavePreview("notes-settings-conflict-en", html);
        var review = RetainValues(html, second.Values); review["action"] = "review";
        using var reviewed = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(review));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        await using var db = new OrderDbContext(_orders); Assert.Single(await db.NotesDefaultCommands.ToArrayAsync());
        var save = RetainValues(await reviewed.Content.ReadAsStringAsync(), review); save["action"] = "save";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(save))).StatusCode);
        Assert.Equal(2, await db.NotesDefaultCommands.CountAsync());
        Assert.False((await db.NotesDefaults.AsNoTracking().SingleAsync()).NotesVisibleToOperators);
    }

    [Fact]
    public async Task NoteControls_SettingsLostAcknowledgementLocksChoiceAndRetryNeverReappliesOldDefault()
    {
        await GrantNotesDefault();
        await using var app = Application().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<OrderDbContext>(options => options.AddInterceptors(new LoseCommitAcknowledgement()))));
        using var client = Client(app); Cookie(client, app);
        var form = await NotesSettingsForm(client); form.Values["visibility"] = "true";
        using var uncertain = await client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode);
        var html = await uncertain.Content.ReadAsStringAsync(); Assert.Contains("Retry same save", html); Assert.Contains("disabled", html);
        await SavePreview("notes-settings-uncertain-en", html);
        var later = await NotesSettingsForm(); later.Values["visibility"] = "false";
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(later.Values))).StatusCode);
        var retry = RetainValues(html, form.Values); retry["visibility"] = "false";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(retry))).StatusCode);
        retry["visibility"] = "true";
        Assert.Equal(HttpStatusCode.SeeOther, (await client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(retry))).StatusCode);
        await using var db = new OrderDbContext(_orders);
        Assert.False((await db.NotesDefaults.SingleAsync()).NotesVisibleToOperators); Assert.Equal(2, await db.NotesDefaultCommands.CountAsync());
    }

    [Fact]
    public async Task NoteControls_SettingsFormRejectsCsrfTamperingCrossPlantAndRevocation()
    {
        await GrantNotesDefault(); var form = await NotesSettingsForm();
        var missing = new Dictionary<string, string>(form.Values); missing.Remove(missing.Keys.Single(x => x.StartsWith("__RequestVerificationToken", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(missing))).StatusCode);
        var tampered = new Dictionary<string, string>(form.Values) { ["draft"] = form.Values["draft"] + "tampered" };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(tampered))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/management/orders/{Guid.NewGuid():D}/settings", new FormUrlEncodedContent(form.Values))).StatusCode);
        var invalid = new Dictionary<string, string>(form.Values) { ["visibility"] = "null" };
        using var rejected = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(invalid)); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await SavePreview("notes-settings-invalid-en", await rejected.Content.ReadAsStringAsync());
        await using var db = new ManagementAccessDbContext(_members);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity_access.plant_membership_permissions WHERE permission = {OrderPlanningPermissions.EditNotesDefault}");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(form.Values))).StatusCode);
        await using var orders = new OrderDbContext(_orders); Assert.Empty(await orders.NotesDefaultCommands.ToArrayAsync());
    }

    [Fact]
    public async Task NoteControls_SettingsOutageRetainsDraftAndRecoversWithoutDuplicates()
    {
        await GrantNotesDefault(); var form = await NotesSettingsForm(); form.Values["visibility"] = "true";
        await _postgres.StopAsync();
        using var failed = await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(form.Values));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var html = await failed.Content.ReadAsStringAsync(); Assert.Contains("value=true selected", html); Assert.DoesNotContain("Npgsql", html);
        await SavePreview("notes-settings-outage-en", html);
        await _postgres.StartAsync(); _app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Laundry"] = _postgres.GetConnectionString();
        var retry = RetainValues(html, form.Values);
        Assert.Equal(HttpStatusCode.SeeOther, (await _client.PostAsync(NotesSettingsPath, new FormUrlEncodedContent(retry))).StatusCode);
    }
}

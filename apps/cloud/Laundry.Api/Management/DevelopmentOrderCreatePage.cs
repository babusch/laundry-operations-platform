using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Management;

public static partial class DevelopmentManagementPages
{
    // Protected scope/key survives form retries; it is not an authorization grant.
    private sealed record CreateDraft(OrderPlanningScope Scope, Guid Key, OrderPlan? LockedPlan = null,
        Guid? OrderId = null, long? ExpectedVersion = null, long? ReviewVersion = null);
    private sealed record CreateFields(string Customer = "", string Receiving = "", string Delivery = "", string Reference = "", string Notes = "");

    private static async Task<IResult> CreatePage(WebApplication app, HttpContext http, Guid plantId, Guid? targetId = null)
    {
        if (!DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment)) return Results.NotFound();
        var lang = http.Request.Query["lang"].ToString();
        if (lang is not ("sv" or "en")) lang = http.Request.Cookies["laundry-management-language"] ??
            (http.Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase) ? "sv" : "en");
        if (lang is not ("sv" or "en")) lang = "en";
        string T(string en, string sv) => lang == "sv" ? sv : en;
        var editing = targetId.HasValue;
        string PageTitle() => editing ? T("Edit order", "Redigera order") : T("Create order", "Skapa order");
        var formPath = editing ? $"/management/orders/{plantId:D}/{targetId:D}/edit" : $"/management/orders/{plantId:D}/new";
        var cancelPath = editing ? $"/management/orders/{plantId:D}/{targetId:D}" : $"/management/orders/{plantId:D}";
        IResult Failure(int status, string en, string sv, bool signedIn = true) => Page(http, lang, signedIn,
            PageTitle(), Alert(T(en, sv)) + $"<p><a href='/management'>{T("Return to management", "Tillbaka till administration")}</a></p>", status);
        var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
        if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
            return Failure(401, "Sign in again before saving an order. No save was attempted.", "Logga in igen innan du sparar en order. Inget sparförsök gjordes.", false);

        var fields = new CreateFields();
        var errors = new Dictionary<string, string>();
        var draftToken = "";
        CreateDraft? draft = null;
        var statusCode = 200;
        var message = "";
        var choices = Array.Empty<CustomerChoice>();
        var attemptedSave = false;
        ServiceOrder? current = null;
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(editing ? "Laundry.Management.EditDraft.v1" : "Laundry.Management.CreateDraft.v1");
        try
        {
            IFormCollection? form = null;
            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!http.Request.HasFormContentType || http.Request.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                    return Failure(415, "Submit this page's order form.", "Använd orderformuläret på den här sidan.");
                // Bound even chunked requests before the framework parses their fields.
                const int maxBytes = 128 * 1024;
                var buffer = new byte[maxBytes + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), http.RequestAborted);
                    if (read == 0) break;
                    count += read;
                }
                if (count > maxBytes) return Failure(413, "The form is too large.", "Formuläret är för stort.");
                using var formBody = new MemoryStream(buffer, 0, count);
                http.Request.Body = formBody;
                form = await http.Request.ReadFormAsync(new FormOptions { ValueCountLimit = 12, KeyLengthLimit = 128, ValueLengthLimit = 32768 }, http.RequestAborted);
                var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
                try { await antiforgery.ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException)
                { return Failure(400, "The security check failed. Reopen the form before saving.", "Säkerhetskontrollen misslyckades. Öppna formuläret igen innan du sparar."); }
                var csrfField = antiforgery.GetTokens(http).FormFieldName;
                if (form.Keys.Any(x => x != csrfField && x is not ("draft" or "action" or "lang" or "customer" or "receiving" or "delivery" or "reference" or "notes" or "validation")) ||
                    form.Any(x => x.Value.Count != 1) || form["action"].ToString() is not ("save" or "language" or "review") ||
                    (!editing && form["action"] == "review"))
                    return Failure(400, "The form request is invalid.", "Formulärbegäran är ogiltig.");
                if (form["lang"].ToString() is "sv" or "en") lang = form["lang"].ToString();
                fields = new(form["customer"].ToString(), form["receiving"].ToString(), form["delivery"].ToString(), form["reference"].ToString(), form["notes"].ToString());
                draftToken = form["draft"].ToString();
                try { draft = JsonSerializer.Deserialize<CreateDraft>(protector.Unprotect(draftToken)); }
                catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { }
                if (draft is null || draft.Scope.PlantId != plantId || draft.Scope.ActorIssuer != actor!.Issuer || draft.Scope.ActorSubject != actor.Subject || draft.Key == Guid.Empty ||
                    draft.OrderId != targetId || (editing && draft.ExpectedVersion is not > 0))
                    return Failure(400, "This form belongs to another session or is no longer valid. Reopen it before saving.", "Formuläret tillhör en annan session eller är inte längre giltigt. Öppna det igen innan du sparar.");
            }

            var assignments = (await http.RequestServices.GetRequiredService<ManagementAccessStore>()
                .ReadAssignmentsAsync(actor!, http.RequestAborted)).Where(x => x.PlantId == plantId).ToArray();
            if (assignments.Length != 1) return Failure(assignments.Length > 1 ? 409 : 403,
                "This plant is not available to your account.", "Anläggningen är inte tillgänglig för ditt konto.");
            if (!assignments[0].Permissions.Contains(editing ? OrderPlanningPermissions.EditPlan : OrderPlanningPermissions.Create, StringComparer.Ordinal) ||
                (editing && !assignments[0].Permissions.Contains(OrderPlanningPermissions.Read, StringComparer.Ordinal)))
                return Failure(403, "You do not have permission to use this order form.", "Du saknar behörighet att använda det här orderformuläret.");
            var scope = new OrderPlanningScope(assignments[0].TenantId, plantId, actor!.Issuer, actor.Subject);
            var services = http.RequestServices;
            var access = new SessionOrderPlanningAccess(actor, services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>(), services.GetRequiredService<DbContextOptions<CustomerDbContext>>());
            var store = new OrderPlanningStore(services.GetRequiredService<DbContextOptions<OrderDbContext>>(), access, services.GetRequiredService<TimeProvider>());
            if (editing)
            {
                current = await store.GetAsync(scope, targetId!.Value, http.RequestAborted);
                if (current is null) return Failure(404, "Order not found in this plant.", "Ordern hittades inte i den här anläggningen.");
                if (form is null && current.Status != "planned")
                    return Failure(409, "Only planned orders can be edited here.", "Endast planerade ordrar kan redigeras här.");
            }
            if (form is not null)
            {
                if (draft!.Scope != scope) return Failure(400, "This form's plant scope has changed. Reopen it before saving.", "Formulärets anläggningstillhörighet har ändrats. Öppna det igen innan du sparar.");

                choices = await http.RequestServices.GetRequiredService<CustomerStore>().ReadChoicesAsync(scope.TenantId, plantId, http.RequestAborted);

                if (form["action"] == "language" && form["validation"] == "1")
                    ValidateCreate(fields, errors, T); // Translate existing feedback without attempting a save.

                if (form["action"] == "review")
                {
                    if (draft.ReviewVersion is null || draft.LockedPlan is not null)
                        return Failure(400, "Reopen the conflict review before continuing.", "Öppna konfliktgranskningen igen innan du fortsätter.");
                    if (current!.Status != "planned") return Failure(409, "This order is no longer planned. Nothing was changed.", "Ordern är inte längre planerad. Inget ändrades.");
                    if (draft.ReviewVersion == current.Version)
                    {
                        draft = draft with { Key = Guid.NewGuid(), ExpectedVersion = current.Version, ReviewVersion = null };
                        message = T("Your entries are kept against the reviewed version. Nothing was saved. Review your fields, then choose Save changes.", "Dina uppgifter finns kvar mot den granskade versionen. Inget sparades. Granska fälten och välj sedan Spara ändringar.");
                    }
                    else statusCode = 409; // Another edit since the displayed comparison: review again.
                }
                if (draft.ReviewVersion.HasValue)
                {
                    draft = draft with { ReviewVersion = current!.Version };
                    if (form["action"] == "save") statusCode = 409; // Review never doubles as a save.
                }

                if (form["action"] == "save" && draft.ReviewVersion is null)
                {
                    var plan = ValidateCreate(fields, errors, T);
                    if (errors.Count > 0) statusCode = 400;
                    else if (draft.LockedPlan is not null && draft.LockedPlan != plan!.Normalize())
                    {
                        statusCode = 409;
                        message = T("The retry must keep the original fields. Return to the previous form and retry unchanged.", "Försöket måste behålla originalfälten. Gå tillbaka till föregående formulär och försök igen utan ändringar.");
                    }
                    else
                    {
                        // The same transaction/authentication seam as the JSON API, not another business implementation.
                        draft = draft with { LockedPlan = plan!.Normalize() };
                        attemptedSave = true;
                        var receipt = editing
                            ? await store.UpdateAsync(scope, draft.Key, targetId!.Value, draft.ExpectedVersion!.Value, plan, http.RequestAborted)
                            : await store.CreateAsync(scope, draft.Key, plan, http.RequestAborted);
                        using var json = JsonDocument.Parse(receipt.Body);
                        var orderId = json.RootElement.GetProperty("id").GetGuid();
                        http.Response.Headers.Location = $"/management/orders/{plantId:D}/{orderId:D}";
                        return Results.StatusCode(StatusCodes.Status303SeeOther);
                    }
                }
            }
            else
            {
                draft = new(scope, Guid.NewGuid(), OrderId: targetId, ExpectedVersion: current?.Version);
                if (editing) fields = Fields(current!.Plan());
                choices = await http.RequestServices.GetRequiredService<CustomerStore>().ReadChoicesAsync(scope.TenantId, plantId, http.RequestAborted);
            }
        }
        catch (OrderPlanningException exception)
        {
            if (exception.Code == "access_denied") return Failure(403, "You no longer have permission to save orders.", "Du saknar nu behörighet att spara ordrar.");
            if (exception.Code == "order_not_found") return Failure(404, "Order not found in this plant.", "Ordern hittades inte i den här anläggningen.");
            if (exception.Code == "order_not_planned") return Failure(409, "This order is no longer planned. Nothing was changed.", "Ordern är inte längre planerad. Inget ändrades.");
            statusCode = exception.Code is "idempotency_conflict" or "version_conflict" ? 409 : 400;
            if (exception.Code == "version_conflict")
            {
                draft = draft! with { LockedPlan = null, ReviewVersion = current!.Version };
                // Reload after the failed transaction; the earlier page read may be stale.
                try
                {
                    var services = http.RequestServices;
                    var access = new SessionOrderPlanningAccess(actor!, services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>(), services.GetRequiredService<DbContextOptions<CustomerDbContext>>());
                    current = await new OrderPlanningStore(services.GetRequiredService<DbContextOptions<OrderDbContext>>(), access, services.GetRequiredService<TimeProvider>()).GetAsync(draft.Scope, targetId!.Value, http.RequestAborted);
                    draft = draft with { ReviewVersion = current?.Version ?? draft.ReviewVersion };
                }
                catch (OrderPlanningException) { return Failure(403, "You no longer have permission to review this order.", "Du saknar nu behörighet att granska den här ordern."); }
                catch (Exception ex) when (ex is DbException or TimeoutException || ex is InvalidOperationException { InnerException: DbException }) { current = null; }
            }
            else if (exception.Code == "customer_not_eligible")
            {
                errors["customer"] = T("This customer is no longer available for this plant. Select an eligible customer.", "Kunden är inte längre tillgänglig för den här anläggningen. Välj en tillgänglig kund.");
                draft = draft! with { LockedPlan = null }; // Definitive rejection: no successful receipt exists.
            }
            else message = T("This save conflicts with an earlier request. Do not create another order until you have checked the order list.", "Sparförsöket krockar med en tidigare begäran. Kontrollera orderlistan innan du skapar en annan order.");
        }
        catch (InvalidDataException) { return Failure(400, "The form request is invalid or too large.", "Formulärbegäran är ogiltig eller för stor."); }
        catch (Exception exception) when (exception is DbException or TimeoutException ||
            exception is InvalidOperationException { InnerException: DbException } || exception is DbUpdateException { InnerException: DbException })
        {
            statusCode = 503;
            message = attemptedSave
                ? T("The save result could not be confirmed. Your original request is preserved. Retry it unchanged; do not start another order.", "Sparresultatet kunde inte bekräftas. Originalbegäran har bevarats. Försök igen utan ändringar; skapa inte en annan order.")
                : T("Customers are temporarily unavailable. Nothing was saved. Try again later.", "Kunderna är tillfälligt otillgängliga. Inget sparades. Försök igen senare.");
        }

        if (draft is null) return Failure(statusCode, "Order creation is temporarily unavailable. No save was attempted.", "Det går tillfälligt inte att skapa en order. Inget sparförsök gjordes.");
        draftToken = protector.Protect(JsonSerializer.Serialize(draft));
        if (draft.LockedPlan is not null && message.Length == 0)
            message = T("The earlier save result is uncertain. Retry the same request; do not start another order.", "Det tidigare sparresultatet är osäkert. Försök igen med samma begäran; skapa inte en annan order.");
        if (draft.ReviewVersion.HasValue)
            message = T("This order has changed since you opened it. Your changes have not been saved. Compare the latest saved values with your entries before continuing.", "Ordern har ändrats sedan du öppnade den. Dina ändringar har inte sparats. Jämför de senast sparade uppgifterna med dina egna innan du fortsätter.");
        http.Response.Cookies.Append("laundry-management-language", lang, new CookieOptions
        { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/management", MaxAge = TimeSpan.FromDays(365) });
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        var content = new StringBuilder($"<section class='panel create-panel'><h2>{(editing ? H(current?.OrderNumber ?? T("Order plan", "Orderplan")) : T("Plan a service cycle", "Planera en tvättomgång"))}</h2><p>{(editing ? T("Edit the planning fields only. Receiving and dispatch are not changed.", "Redigera endast planeringsfälten. Inleverans och utleverans ändras inte.") : T("Required: customer and both dates. Saving does not start receiving.", "Obligatoriskt: kund och båda datumen. Sparandet startar inte inleverans."))}</p>");
        if (message.Length > 0) content.Append(Alert(message, statusCode == 503));
        if (errors.Count > 0) content.Append(Alert(T("Check the highlighted fields. Your entries are retained.", "Kontrollera de markerade fälten. Dina uppgifter finns kvar.")));
        if (choices.Length == 0 && draft.LockedPlan is null) content.Append(Alert(T("No eligible customers could be loaded. Retry after recovery or reopen the form to refresh the choices.", "Inga tillgängliga kunder kunde hämtas. Försök igen efter återhämtning eller öppna formuläret igen för att uppdatera valen."), true));
        if (choices.Length > 100) content.Append(Alert(T("Only the first 100 customers are shown in this development form.", "Endast de första 100 kunderna visas i det här utvecklingsformuläret."), true));
        if (draft.ReviewVersion.HasValue && current is not null) content.Append(Comparison(current, choices, T));
        content.Append($"<form id=order-create action='{formPath}' method=post><input type=hidden name='{H(tokens.FormFieldName)}' value='{H(tokens.RequestToken!)}'><input type=hidden name=draft value='{H(draftToken)}'><fieldset class=fields>");
        var locked = draft.LockedPlan is not null;
        if (errors.Count > 0) content.Append("<input type=hidden name=validation value=1>");
        string Invalid(string name) => errors.ContainsKey(name) ? $"aria-invalid=true aria-describedby={name}-error {(errors.Keys.First() == name ? "autofocus" : "")}" : "";
        string Field(string name, string label, string control) => $"<div class='field{(errors.ContainsKey(name) ? " invalid" : "")}'><label for={name}>{label}</label>{control}{(errors.TryGetValue(name, out var error) ? $"<p class=field-error id={name}-error>! {H(error)}</p>" : "")}</div>";
        var options = new StringBuilder($"<option value=''>{T("Select customer", "Välj kund")}</option>");
        foreach (var choice in choices.Take(100)) options.Append($"<option value='{choice.Id:D}' {(fields.Customer == choice.Id.ToString("D") ? "selected" : "")}>{H(choice.Code)} · {H(choice.DisplayName)}</option>");
        if (fields.Customer.Length > 0 && !choices.Take(100).Any(x => x.Id.ToString("D") == fields.Customer))
            options.Append($"<option value='{H(fields.Customer)}' selected>{T("Previously selected customer — refresh availability", "Tidigare vald kund — uppdatera tillgänglighet")}</option>");
        var chevron = "<svg class=select-chevron aria-hidden=true focusable=false viewBox='0 0 16 16'><path d='m3 6 5 5 5-5'/></svg>";
        content.Append(Field("customer", T("Customer", "Kund"), $"<span class=select-control><select id=customer name=customer required {Invalid("customer")} {(locked ? "disabled" : "")}>{options}</select>{chevron}</span>"));
        if (locked) content.Append($"<input type=hidden name=customer value='{H(fields.Customer)}'>");
        string Input(string name, string value, string type = "text", string extra = "") => $"<input id={name} name={name} type={type} value='{H(value)}' {extra} {Invalid(name)} {(locked ? "readonly" : "")}>";
        content.Append("<div class=field-row>");
        content.Append(Field("receiving", T("Planned receiving date", "Planerat inleveransdatum"), Input("receiving", fields.Receiving, "date", "required")));
        content.Append(Field("delivery", T("Planned delivery date", "Planerat leveransdatum"), Input("delivery", fields.Delivery, "date", "required")));
        content.Append("</div>");
        content.Append(Field("reference", T("Customer reference (optional)", "Kundreferens (valfri)"), Input("reference", fields.Reference, extra: "maxlength=100")));
        content.Append(Field("notes", T("Notes (optional)", "Anteckningar (valfria)"), $"<textarea id=notes name=notes rows=4 maxlength=2000 {Invalid("notes")} {(locked ? "readonly" : "")}>{H(fields.Notes)}</textarea>"));
        content.Append($"</fieldset><div class=create-actions><button name=action value=save {(draft.ReviewVersion.HasValue || (choices.Length == 0 && !locked && statusCode != 503) ? "disabled" : "")}>{(locked ? T("Retry same save", "Försök spara igen") : editing ? T("Save changes", "Spara ändringar") : T("Save planned order", "Spara planerad order"))}</button>");
        if (draft.ReviewVersion.HasValue && current is not null && current.Status == "planned")
            content.Append($"<button name=action value=review formnovalidate>{T("I reviewed the latest values — keep my entries", "Jag har granskat de senaste uppgifterna — behåll mina fält")}</button>");
        content.Append($"<a href='{cancelPath}'>{(locked ? T("Return to order", "Tillbaka till ordern") : T("Cancel", "Avbryt"))}</a></div></form></section>");
        return Page(http, lang, true, PageTitle(), content.ToString(), statusCode, create: true);
    }

    private static CreateFields Fields(OrderPlan plan) => new(plan.CustomerId.ToString("D"), plan.PlannedReceivingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        plan.PlannedDeliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), plan.CustomerReference ?? "", plan.Notes ?? "");

    private static string Comparison(ServiceOrder current, CustomerChoice[] choices, Func<string, string, string> t)
    {
        var customer = choices.FirstOrDefault(x => x.Id == current.CustomerId);
        string Row(string label, string value) => $"<dt>{label}</dt><dd class=notes>{H(value)}</dd>";
        return $"<section class=latest-plan aria-labelledby=latest-heading><h3 id=latest-heading>{t("Latest saved values", "Senast sparade uppgifter")} · {t("Version", "Version")} {current.Version}</h3><dl>" +
            Row(t("Customer", "Kund"), customer is null ? t("Customer not currently available in choices", "Kunden är inte tillgänglig bland valen") : $"{customer.Code} · {customer.DisplayName}") +
            Row(t("Planned receiving date", "Planerat inleveransdatum"), current.PlannedReceivingDate.ToString("d MMM yyyy", CultureInfo.GetCultureInfo(t("en-GB", "sv-SE")))) +
            Row(t("Planned delivery date", "Planerat leveransdatum"), current.PlannedDeliveryDate.ToString("d MMM yyyy", CultureInfo.GetCultureInfo(t("en-GB", "sv-SE")))) +
            Row(t("Customer reference", "Kundreferens"), current.CustomerReference ?? "—") + Row(t("Notes", "Anteckningar"), current.Notes ?? "—") +
            $"</dl><p>{t("Your unsaved entries are below. Reviewing does not save them.", "Dina osparade uppgifter finns nedan. Granskningen sparar dem inte.")}</p></section>";
    }

    private static OrderPlan? ValidateCreate(CreateFields fields, Dictionary<string, string> errors, Func<string, string, string> t)
    {
        if (!Guid.TryParseExact(fields.Customer, "D", out var customer) || customer == Guid.Empty) errors["customer"] = t("Select a customer.", "Välj en kund.");
        if (!DateOnly.TryParseExact(fields.Receiving, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var receiving) || receiving == default)
            errors["receiving"] = t("Enter a valid receiving date.", "Ange ett giltigt inleveransdatum.");
        if (!DateOnly.TryParseExact(fields.Delivery, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var delivery) || delivery == default)
            errors["delivery"] = t("Enter a valid delivery date.", "Ange ett giltigt leveransdatum.");
        else if (!errors.ContainsKey("receiving") && delivery < receiving)
            errors["delivery"] = t("Delivery cannot be before receiving.", "Leverans kan inte vara före inleverans.");
        if (fields.Reference.Length > 100) errors["reference"] = t("Use at most 100 characters.", "Använd högst 100 tecken.");
        if (fields.Notes.Length > 2000) errors["notes"] = t("Use at most 2000 characters.", "Använd högst 2000 tecken.");
        return errors.Count == 0 ? new(customer, receiving, delivery, fields.Reference, fields.Notes) : null;
    }
}

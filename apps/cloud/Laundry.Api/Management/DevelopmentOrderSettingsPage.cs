using System.Data.Common;
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
    private sealed record NotesSettingsDraft(OrderPlanningScope Scope, Guid Key, long Version,
        bool? LockedValue = null, long? ReviewVersion = null);

    private static async Task<IResult> NotesSettingsPage(WebApplication app, HttpContext http, Guid plantId)
    {
        if (!DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment)) return Results.NotFound();
        var lang = http.Request.Query["lang"].ToString();
        if (lang is not ("en" or "sv")) lang = http.Request.Cookies["laundry-management-language"] ??
            (http.Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase) ? "sv" : "en");
        if (lang is not ("en" or "sv")) lang = "en";
        string T(string en, string sv) => lang == "sv" ? sv : en;
        var path = $"/management/orders/{plantId:D}/settings";
        IResult Failure(int status, string en, string sv, bool signedIn = true) => Page(http, lang, signedIn,
            T("Order settings", "Orderinställningar"), Alert(T(en, sv)) + $"<p><a href='/management'>{T("Return to management", "Tillbaka till administration")}</a></p>", status);
        var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
        if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor))
            return Failure(401, "Sign in again to view order settings. Nothing was changed.", "Logga in igen för att visa orderinställningar. Inget ändrades.", false);
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("Laundry.Management.NotesDefaultDraft.v1");
        NotesSettingsDraft? draft = null;
        OrderNotesDefaultDetails? current = null;
        var value = "false"; var token = ""; var message = ""; var fieldError = false;
        var canEdit = false; var permissionsLoaded = false; var attempted = false; var status = 200;
        try
        {
            IFormCollection? form = null;
            if (HttpMethods.IsPost(http.Request.Method))
            {
                if (!http.Request.HasFormContentType || http.Request.ContentType!.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                    return Failure(415, "Use this page's settings form.", "Använd inställningsformuläret på den här sidan.");
                var buffer = new byte[16385]; var count = 0;
                while (count < buffer.Length)
                {
                    var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), http.RequestAborted);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 16384) return Failure(413, "The form is too large.", "Formuläret är för stort.");
                using var body = new MemoryStream(buffer, 0, count); http.Request.Body = body;
                form = await http.Request.ReadFormAsync(new FormOptions { ValueCountLimit = 8, KeyLengthLimit = 128, ValueLengthLimit = 8192 }, http.RequestAborted);
                var csrf = http.RequestServices.GetRequiredService<IAntiforgery>();
                try { await csrf.ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Failure(400, "The security check failed. Reopen settings before saving.", "Säkerhetskontrollen misslyckades. Öppna inställningarna igen innan du sparar."); }
                var csrfName = csrf.GetTokens(http).FormFieldName;
                if (form.Keys.Any(x => x != csrfName && x is not ("draft" or "action" or "lang" or "visibility" or "validation")) ||
                    form.Any(x => x.Value.Count != 1) || form["action"].ToString() is not ("save" or "language" or "review"))
                    return Failure(400, "The settings request is invalid.", "Inställningsbegäran är ogiltig.");
                if (form["lang"].ToString() is "en" or "sv") lang = form["lang"].ToString();
                value = form["visibility"].ToString(); token = form["draft"].ToString();
                try { draft = JsonSerializer.Deserialize<NotesSettingsDraft>(protector.Unprotect(token)); }
                catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { }
                if (draft is null || draft.Scope.PlantId != plantId || draft.Scope.ActorIssuer != actor!.Issuer ||
                    draft.Scope.ActorSubject != actor.Subject || draft.Key == Guid.Empty || draft.Version < 0 || draft.ReviewVersion < 0)
                    return Failure(400, "This form is no longer valid for your account and plant. Reopen settings.", "Formuläret gäller inte längre för ditt konto och din anläggning. Öppna inställningarna igen.");
                if (draft.LockedValue.HasValue && value != (draft.LockedValue.Value ? "true" : "false"))
                    return Failure(409, "Keep the original choice for this uncertain save. Return to the previous form and retry unchanged.", "Behåll originalvalet för det osäkra sparandet. Gå tillbaka till föregående formulär och försök igen utan ändringar.");
            }
            var services = http.RequestServices;
            var assignments = (await services.GetRequiredService<ManagementAccessStore>().ReadAssignmentsAsync(actor!, http.RequestAborted))
                .Where(x => x.PlantId == plantId).ToArray();
            if (assignments.Length != 1) return Failure(assignments.Length > 1 ? 409 : 403,
                "This plant is not available to your account.", "Anläggningen är inte tillgänglig för ditt konto.");
            if (!assignments[0].Permissions.Contains(OrderPlanningPermissions.Read, StringComparer.Ordinal))
                return Failure(403, "You do not have permission to view order settings.", "Du saknar behörighet att visa orderinställningar.");
            canEdit = assignments[0].Permissions.Contains(OrderPlanningPermissions.EditNotesDefault, StringComparer.Ordinal);
            permissionsLoaded = true;
            if (form is not null && !canEdit && (form["action"] != "language" || draft!.LockedValue.HasValue))
                return Failure(403, "Changing the plant default requires a separate permission. Nothing was changed.", "Det krävs en separat behörighet för att ändra anläggningens standard. Inget ändrades.");
            var scope = new OrderPlanningScope(assignments[0].TenantId, plantId, actor!.Issuer, actor.Subject);
            if (draft is not null && draft.Scope != scope)
                return Failure(400, "The form's plant scope changed. Reopen settings.", "Formulärets anläggningstillhörighet ändrades. Öppna inställningarna igen.");
            var access = new SessionOrderPlanningAccess(actor, services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>(), services.GetRequiredService<DbContextOptions<CustomerDbContext>>());
            var store = new OrderPlanningStore(services.GetRequiredService<DbContextOptions<OrderDbContext>>(), access, services.GetRequiredService<TimeProvider>());
            current = await store.ReadNotesDefaultAsync(scope, http.RequestAborted);
            if (form is null)
            {
                draft = new(scope, Guid.NewGuid(), current.Version);
                value = current.NotesVisibleToOperators ? "true" : "false";
            }
            else
            {
                if (!canEdit) value = current.NotesVisibleToOperators ? "true" : "false";
                if (form["action"] == "review")
                {
                    if (draft!.ReviewVersion is null || draft.LockedValue.HasValue)
                        return Failure(400, "Reopen the conflict review before continuing.", "Öppna konfliktgranskningen igen innan du fortsätter.");
                    if (draft.ReviewVersion == current.Version)
                    {
                        draft = draft with { Key = Guid.NewGuid(), Version = current.Version, ReviewVersion = null };
                        message = T("Your choice is retained against the reviewed version. Nothing was saved. Review it, then choose Save default.", "Ditt val finns kvar mot den granskade versionen. Inget sparades. Granska valet och välj sedan Spara standard.");
                    }
                    else status = 409;
                }
                if (draft!.ReviewVersion.HasValue)
                {
                    draft = draft with { ReviewVersion = current.Version };
                    if (form["action"] == "save") status = 409;
                }
                fieldError = (form["action"] == "save" || form["validation"] == "1") && value is not ("true" or "false");
                if (fieldError) status = 400;
                if (form["action"] == "save" && !fieldError && draft.ReviewVersion is null)
                {
                    draft = draft with { LockedValue = value == "true" };
                    attempted = true;
                    try
                    {
                        await store.ChangeNotesDefaultAsync(scope, draft.Key, draft.Version, draft.LockedValue.Value, http.RequestAborted);
                    }
                    catch (OrderPlanningException ex) when (ex.Code == "version_conflict")
                    {
                        draft = draft with { LockedValue = null, ReviewVersion = current.Version };
                        attempted = false;
                        current = null;
                        current = await store.ReadNotesDefaultAsync(scope, http.RequestAborted);
                        draft = draft with { ReviewVersion = current.Version }; status = 409;
                    }
                    if (status != 409)
                    {
                        http.Response.Headers.Location = path;
                        return Results.StatusCode(StatusCodes.Status303SeeOther);
                    }
                }
            }
        }
        catch (OrderPlanningException ex)
        {
            return Failure(ex.Code == "access_denied" ? 403 : 409,
                "Your permission or saved request has changed. Reopen settings before another save.", "Din behörighet eller sparade begäran har ändrats. Öppna inställningarna igen innan du sparar på nytt.");
        }
        catch (InvalidDataException) { return Failure(400, "The form is invalid or too large.", "Formuläret är ogiltigt eller för stort."); }
        catch (Exception ex) when (ex is DbException or TimeoutException || ex is InvalidOperationException { InnerException: DbException } || ex is DbUpdateException { InnerException: DbException })
        {
            status = 503;
            message = attempted || draft?.LockedValue is not null
                ? T("The save result could not be confirmed. The original request is preserved. Retry unchanged; do not start another save.", "Sparresultatet kunde inte bekräftas. Originalbegäran har bevarats. Försök igen utan ändringar; starta inte ett nytt sparande.")
                : T("Order settings are temporarily unavailable. Nothing was saved. Try again later.", "Orderinställningarna är tillfälligt otillgängliga. Inget sparades. Försök igen senare.");
        }
        if (draft is null) return Failure(status, "Order settings are temporarily unavailable. No save was attempted.", "Orderinställningarna är tillfälligt otillgängliga. Inget sparförsök gjordes.");
        http.Response.Cookies.Append("laundry-management-language", lang, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/management", MaxAge = TimeSpan.FromDays(365) });
        var locked = draft.LockedValue.HasValue;
        if (draft.ReviewVersion.HasValue) message = T("The default changed since this form was opened. Your choice has not been saved. Compare the latest value and review before saving.", "Standarden ändrades sedan formuläret öppnades. Ditt val har inte sparats. Jämför det senaste värdet och granska innan du sparar.");
        if (draft.ReviewVersion.HasValue && current is null) message = T("The latest saved value could not be loaded. Your choice is retained. Reload it before reviewing.", "Det senaste sparade värdet kunde inte hämtas. Ditt val finns kvar. Hämta värdet igen innan du granskar.");
        else if (locked && message.Length == 0) message = T("The earlier save result is uncertain. Retry the same request.", "Det tidigare sparresultatet är osäkert. Försök igen med samma begäran.");
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        var html = new StringBuilder($"<a class=back href='/management/orders/{plantId:D}'>← {T("All orders", "Alla ordrar")}</a><section class='panel create-panel'><h2>{T("Default note visibility", "Standard för anteckningars synlighet")}</h2><p>{T("Changes apply only to new orders. Existing orders keep their own choice. Management can still read hidden notes.", "Ändringar gäller endast nya ordrar. Befintliga ordrar behåller sina egna val. Administrationen kan fortfarande läsa dolda anteckningar.")}</p>");
        if (permissionsLoaded && !canEdit) html.Append(Alert(T("Read only. Changing this plant default requires a separate permission.", "Skrivskyddad. Det krävs en separat behörighet för att ändra anläggningens standard."), true));
        if (message.Length > 0) html.Append(Alert(message, status == 503 || draft.ReviewVersion.HasValue));
        if (draft.ReviewVersion.HasValue && current is not null)
            html.Append($"<section class=latest-plan><h3>{T("Latest saved default", "Senast sparade standard")} · {T("Version", "Version")} {current.Version}</h3><p>{(current.NotesVisibleToOperators ? T("Shown", "Visas") : T("Hidden", "Dolda"))}</p></section>");
        html.Append($"<form id=order-create method=post action='{path}'><input type=hidden name='{H(tokens.FormFieldName)}' value='{H(tokens.RequestToken!)}'><input type=hidden name=draft value='{H(protector.Protect(JsonSerializer.Serialize(draft)))}'><fieldset class=fields><div class='field{(fieldError ? " invalid" : "")}'><label for=visibility>{T("Show notes to operators by default", "Visa anteckningar för operatörer som standard")}</label>");
        html.Append(VisibilitySelect(value, locked || !canEdit, fieldError ? "aria-invalid=true aria-describedby=visibility-error autofocus" : "", T));
        if (fieldError) html.Append($"<input type=hidden name=validation value=1><p class=field-error id=visibility-error>! {T("Choose whether notes are shown or hidden.", "Välj om anteckningarna ska visas eller döljas.")}</p>");
        html.Append("</div></fieldset><div class=create-actions>");
        if (canEdit) html.Append($"<button name=action value=save {(draft.ReviewVersion.HasValue ? "disabled" : "")}>{(locked ? T("Retry same save", "Försök spara igen") : T("Save default", "Spara standard"))}</button>");
        if (canEdit && draft.ReviewVersion.HasValue && current is not null)
            html.Append($"<button name=action value=review formnovalidate>{T("I reviewed the latest value — keep my choice", "Jag har granskat det senaste värdet — behåll mitt val")}</button>");
        if (canEdit && draft.ReviewVersion.HasValue && current is null)
            html.Append($"<button name=action value=language formnovalidate>{T("Reload latest value", "Hämta senaste värdet igen")}</button>");
        if (!permissionsLoaded) html.Append($"<button name=action value=language formnovalidate>{T("Reload settings", "Hämta inställningarna igen")}</button>");
        html.Append($"<a href='/management/orders/{plantId:D}'>{T("Back to orders", "Tillbaka till ordrar")}</a></div></form></section>");
        return Page(http, lang, true, T("Order settings", "Orderinställningar"), html.ToString(), status, create: true);
    }
}

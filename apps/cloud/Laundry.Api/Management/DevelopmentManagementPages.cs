using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Laundry.Api.Security;
using Laundry.Api.Security.ManagementAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Laundry.Api.Management;

// Temporary Development navigation, not a replacement for the planned Next.js application.
public static partial class DevelopmentManagementPages
{
    public static void MapDevelopmentManagementPages(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("ManagementAuthentication:Enabled")) return;
        app.MapGet("/management", (Delegate)((HttpContext http) => Render(app, http, null, null)));
        app.MapGet("/management/orders/{plantId:guid}", (Guid plantId, HttpContext http) => Render(app, http, plantId, null));
        app.MapGet("/management/orders/{plantId:guid}/{orderId:guid}", (Guid plantId, Guid orderId, HttpContext http) => Render(app, http, plantId, orderId));
        app.MapGet("/management/orders/{plantId:guid}/{orderId:guid}/history", (Guid plantId, Guid orderId, HttpContext http) => Render(app, http, plantId, orderId, history: true));
        app.MapGet("/management/orders/{plantId:guid}/new", (Guid plantId, HttpContext http) => CreatePage(app, http, plantId));
        app.MapPost("/management/orders/{plantId:guid}/new", (Guid plantId, HttpContext http) => CreatePage(app, http, plantId));
        app.MapGet("/management/orders/{plantId:guid}/{orderId:guid}/edit", (Guid plantId, Guid orderId, HttpContext http) => CreatePage(app, http, plantId, orderId));
        app.MapPost("/management/orders/{plantId:guid}/{orderId:guid}/edit", (Guid plantId, Guid orderId, HttpContext http) => CreatePage(app, http, plantId, orderId));
        app.MapPost("/management/sign-out", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
            if (!ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out _))
                return Results.Redirect("/management");
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { code = "management_csrf_required" }); }
            await http.SignOutAsync(ManagementAuthentication.CookieScheme);
            return Results.Redirect("/management");
        });
    }

    private static async Task<IResult> Render(WebApplication app, HttpContext http, Guid? plantId, Guid? orderId, bool history = false)
    {
        if (history && !DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment)) return Results.NotFound();
        var lang = http.Request.Query["lang"].ToString();
        if (lang is not ("sv" or "en")) lang = http.Request.Cookies["laundry-management-language"] ??
            (http.Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase) ? "sv" : "en");
        if (lang is not ("sv" or "en")) lang = "en";
        http.Response.Cookies.Append("laundry-management-language", lang, new CookieOptions
        { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/management", MaxAge = TimeSpan.FromDays(365) });
        string T(string en, string sv) => lang == "sv" ? sv : en;
        var session = await http.AuthenticateAsync(ManagementAuthentication.CookieScheme);
        var signedIn = ManagementSessionAccess.TryReadActor(session, app.Configuration["ManagementAuthentication:Authority"]!.TrimEnd('/'), out var actor);
        var title = history ? T("Order history", "Orderhistorik") : orderId.HasValue ? T("Order details", "Orderdetaljer") : plantId.HasValue ? T("Orders", "Ordrar") : T("Management", "Administration");
        var content = new StringBuilder();
        var status = 200;
        if (!signedIn)
        {
            if (plantId.HasValue) status = 401;
            content.Append($"<section class=panel><h2>{T("Sign in to continue", "Logga in för att fortsätta")}</h2><p>{T("Use your laundry management account, not the Keycloak administrator account.", "Använd ditt konto för tvätteriets administration, inte Keycloaks administratörskonto.")}</p><a class=button href='/management/login?home=true'>{T("Sign in", "Logga in")}</a></section>");
        }
        else if (!DevelopmentOrderEndpoints.Enabled(app.Configuration, app.Environment))
        {
            content.Append(Alert(T("Order access is not enabled. Start the API with -WithOrders.", "Orderåtkomst är inte aktiverad. Starta API:t med -WithOrders."), warning: true));
        }
        else
        {
            try
            {
                var services = http.RequestServices;
                var assignments = await services.GetRequiredService<ManagementAccessStore>().ReadAssignmentsAsync(actor!, http.RequestAborted);
                if (!plantId.HasValue)
                {
                    content.Append($"<section class=panel><h2>{T("Your workspace", "Din arbetsyta")}</h2><p>{T("View saved orders and their planned dates. Receiving and dispatch are not enabled yet.", "Visa sparade ordrar och deras planerade datum. Inleverans och utleverans är inte aktiverade ännu.")}</p><div class=actions>");
                    var readable = assignments.Where(x => x.Permissions.Contains(OrderPlanningPermissions.Read, StringComparer.Ordinal)).ToArray();
                    foreach (var assignment in readable)
                        content.Append($"<a class=button href='/management/orders/{assignment.PlantId:D}'>{T("View orders", "Visa ordrar")}</a>");
                    if (readable.Length == 0) content.Append(Alert(T("Your account has no order-viewing permission.", "Ditt konto saknar behörighet att visa ordrar.")));
                    content.Append("</div></section>");
                }
                else
                {
                    var matches = assignments.Where(x => x.PlantId == plantId).ToArray();
                    if (matches.Length != 1 || !matches[0].Permissions.Contains(OrderPlanningPermissions.Read, StringComparer.Ordinal))
                    {
                        status = matches.Length > 1 ? 409 : 403;
                        content.Append(Alert(T("This plant is not available to your account. Return to management or contact your administrator.", "Anläggningen är inte tillgänglig för ditt konto. Gå tillbaka till administration eller kontakta din administratör.")));
                    }
                    else
                    {
                        var scope = new OrderPlanningScope(matches[0].TenantId, plantId.Value, actor!.Issuer, actor.Subject);
                        var access = new SessionOrderPlanningAccess(actor, services.GetRequiredService<DbContextOptions<ManagementAccessDbContext>>(), services.GetRequiredService<DbContextOptions<CustomerDbContext>>());
                        var store = new OrderPlanningStore(services.GetRequiredService<DbContextOptions<OrderDbContext>>(), access, services.GetRequiredService<TimeProvider>());
                        var culture = CultureInfo.GetCultureInfo(lang == "sv" ? "sv-SE" : "en-GB");
                        string Date(DateOnly date) => H(date.ToString("d MMM yyyy", culture));
                        if (orderId.HasValue)
                        {
                            if (!history) content.Append($"<a class=back href='/management/orders/{plantId:D}'>← {T("All orders", "Alla ordrar")}</a>");
                            var order = await store.GetAsync(scope, orderId.Value, http.RequestAborted);
                            if (order is null) { status = 404; content.Append(Alert(T("Order not found in this plant.", "Ordern hittades inte i den här anläggningen."))); }
                            else if (history)
                            {
                                var result = await RenderHistory(http, lang, scope, order, store);
                                status = result.Status;
                                content.Append(result.Content);
                            }
                            else
                            {
                                content.Append($"<section class=panel><div class=section-head><h2>{H(order.OrderNumber)}</h2><span class=badge>{T("Planned", "Planerad")}</span></div><dl class=dates><div><dt>{T("Planned receiving", "Planerad inleverans")}</dt><dd>{Date(order.PlannedReceivingDate)}</dd></div><div><dt>{T("Planned delivery", "Planerad leverans")}</dt><dd>{Date(order.PlannedDeliveryDate)}</dd></div></dl><dl><dt>{T("Customer reference", "Kundreferens")}</dt><dd>{H(order.CustomerReference ?? "—")}</dd><dt>{T("Notes", "Anteckningar")}</dt><dd class=notes>{H(order.Notes ?? "—")}</dd><dt>{T("Version", "Version")}</dt><dd>{order.Version}</dd></dl></section>");
                                content.Append("<p class=actions>");
                                if (order.Status == "planned" && matches[0].Permissions.Contains(OrderPlanningPermissions.EditPlan, StringComparer.Ordinal))
                                    content.Append($"<a class=button href='/management/orders/{plantId:D}/{order.Id:D}/edit'>{T("Edit order", "Redigera order")}</a>");
                                content.Append($"<a class=button href='/management/orders/{plantId:D}/{order.Id:D}/history'>{T("View history", "Visa historik")}</a></p>");
                            }
                        }
                        else
                        {
                            const int limit = 25;
                            var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Laundry.Orders.ListCursor.v1");
                            OrderListCursor? cursor = null;
                            if (http.Request.Query.ContainsKey("cursor") && (http.Request.Query["cursor"].Count != 1 || !OrderListCursor.TryRead(protector, http.Request.Query["cursor"][0] ?? "", scope, limit, out cursor)))
                            { status = 400; content.Append(Alert(T("This page link has expired or is invalid. Open orders again from management.", "Sidlänken har gått ut eller är ogiltig. Öppna ordrar igen från administration."))); }
                            else
                            {
                                var rows = await store.ListAsync(scope, limit, cursor?.ReceivingDate, cursor?.OrderId, http.RequestAborted);
                                content.Append($"<section class=panel><div class=section-head><h2>{T("Saved orders", "Sparade ordrar")}</h2><span class=badge>{T("Read only", "Skrivskyddad")}</span></div><p>{T("Sorted by planned receiving date. Select an order to view its details.", "Sorterat efter planerat inleveransdatum. Välj en order för att visa detaljer.")}</p>");
                                if (matches[0].Permissions.Contains(OrderPlanningPermissions.Create, StringComparer.Ordinal))
                                    content.Append($"<a class=button href='/management/orders/{plantId:D}/new'>{T("Create order", "Skapa order")}</a>");
                                content.Append("<div class=order-list>");
                                foreach (var order in rows.Take(limit))
                                    content.Append($"<a class=order href='/management/orders/{plantId:D}/{order.Id:D}'><div><strong>{H(order.OrderNumber)}</strong><span>{H(order.CustomerReference ?? T("No customer reference", "Ingen kundreferens"))}</span></div><div><span>{T("Receiving", "Inleverans")}</span><strong>{Date(order.PlannedReceivingDate)}</strong></div><div><span>{T("Delivery", "Leverans")}</span><strong>{Date(order.PlannedDeliveryDate)}</strong></div><span class=badge>{T("Planned", "Planerad")}</span></a>");
                                if (rows.Length == 0) content.Append($"<p>{T("No saved orders in this plant yet.", "Inga sparade ordrar i den här anläggningen ännu.")}</p>");
                                content.Append("</div>");
                                if (rows.Length > limit)
                                {
                                    var last = rows[limit - 1];
                                    var next = new OrderListCursor(scope.TenantId, plantId.Value, limit, last.PlannedReceivingDate, last.Id).Encode(protector);
                                    content.Append($"<a class=button href='/management/orders/{plantId:D}?cursor={Uri.EscapeDataString(next)}'>{T("Next page", "Nästa sida")}</a>");
                                }
                                content.Append("</section>");
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is DbException or TimeoutException || exception is InvalidOperationException { InnerException: DbException })
            { status = 503; content.Append(Alert(T("Saved orders are temporarily unavailable. Nothing has been changed. Try this page again.", "Sparade ordrar är tillfälligt otillgängliga. Inget har ändrats. Försök öppna sidan igen."))); }
            catch (OrderPlanningException)
            { status = 403; content.Append(Alert(T("You no longer have permission to view these orders.", "Du har inte längre behörighet att visa dessa ordrar."))); }
            catch (System.Text.Json.JsonException) when (history)
            { status = 503; content.Append(Alert(T("Order history is temporarily unavailable. Nothing has been changed.", "Orderhistoriken är tillfälligt otillgänglig. Inget har ändrats."))); }
        }
        return Page(http, lang, signedIn, title, content.ToString(), status);
    }

    private static IResult Page(HttpContext http, string lang, bool signedIn, string title, string content, int status, bool create = false)
    {
        string T(string en, string sv) => lang == "sv" ? sv : en;
        var languageMethod = create ? "post" : "get";
        var languageOwner = create ? "form=order-create" : "";
        var languageAction = create ? "form=order-create name=action value=language formnovalidate" : "";
        var sessionAction = $"<a class=button href='/management/login?home=true'>{T("Sign in", "Logga in")}</a>";
        if (signedIn)
        {
            var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
            sessionAction = $"<form action=/management/sign-out method=post><input type=hidden name='{H(tokens.FormFieldName)}' value='{H(tokens.RequestToken!)}'><button>{T("Sign out", "Logga ut")}</button></form>";
        }
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var languageCursor = http.Request.Query.TryGetValue("cursor", out var currentCursor)
            ? $"<input type=hidden name=cursor value='{H(currentCursor.ToString())}'>" : "";
        return Results.Content($"""
            <!doctype html><html lang="{lang}"><head><meta charset=utf-8><meta name=viewport content="width=device-width, initial-scale=1"><title>{title} · Laundry</title><style>{Styles}{Adjustments}</style></head>
            <body><a class=skip href=#content>{T("Skip to content", "Hoppa till innehåll")}</a><div class=shell><header><a class=brand href=/management>Laundry<span>{T("Management workspace", "Administrationsyta")}</span></a><nav aria-label="{T("Main navigation", "Huvudnavigation")}"><a href=/management>{T("Management", "Administration")}</a>{sessionAction}</nav></header>
            <main id=content tabindex=-1><div class=heading><div><p class=eyebrow>{T("Development · management workspace", "Utveckling · administrationsyta")}</p><h1>{title}</h1></div><form method={languageMethod}>{languageCursor}<label for=language>{T("Language", "Språk")}</label><span class=select-control><select id=language name=lang {languageOwner}><option value=sv {(lang == "sv" ? "selected" : "")}>Svenska</option><option value=en {(lang == "en" ? "selected" : "")}>English</option></select><svg class=select-chevron aria-hidden=true focusable=false viewBox="0 0 16 16"><path d="m3 6 5 5 5-5"/></svg></span><button {languageAction}>{T("Apply", "Välj")}</button></form></div><p class=session>{(signedIn ? "● " + T("Signed in", "Inloggad") : "○ " + T("Signed out", "Utloggad"))}</p>{content}</main>
            <footer>{T("Development only. Receiving and dispatch are not available. Orders can be created and edited only as plans. Sign out ends this application's session, not the identity provider session.", "Endast för utveckling. Inleverans och utleverans är inte tillgängliga. Ordrar kan skapas och redigeras endast som planerade. Utloggning avslutar appens session, inte identitetstjänstens session.")}</footer></div></body></html>
            """, "text/html; charset=utf-8", statusCode: status);
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string Alert(string text, bool warning = false) => $"<div class='{(warning ? "alert warning" : "alert")}' role=alert><strong>! </strong>{H(text)}</div>";
    private const string Adjustments = """
        .history-list{list-style:none;padding:0;margin:1.5rem 0;display:grid;gap:1.25rem}.history-entry{border:1px solid #42666f;border-radius:.75rem;padding:1.25rem;min-width:0}.history-entry h3{font-size:1.1rem;margin:0}.history-meta{display:flex;gap:.5rem 1.5rem;flex-wrap:wrap;font-size:.9rem;font-variant-numeric:tabular-nums}.history-meta span{overflow-wrap:anywhere}.initial-plan{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 1.5rem;margin:0}.initial-plan>div:last-child{grid-column:1/-1}.history-change{border-top:1px solid #244A53;padding-top:1rem;margin-top:1rem}.history-change h4{margin:0;font-size:1rem}.change-values{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.5rem;margin:0}.change-values dt{margin-top:.5rem}.history-value{white-space:pre-wrap;overflow-wrap:anywhere}.change-values>div{min-width:0}@media(max-width:48rem){.change-values,.initial-plan{grid-template-columns:1fr;gap:.75rem}.history-entry{padding:1rem}}
        .alert{border-color:#DD6C65;background:#30201f}
        .alert.warning{border-color:#D4A84F;background:#302a1a}
        .latest-plan{border:2px solid #D4A84F;border-radius:.75rem;padding:1rem;margin:1.5rem 0;background:#302a1a}.latest-plan h3{font-size:1.1rem;margin:0}.latest-plan dd{color:#DCE9E7}
        .order>div{min-width:0;overflow-wrap:anywhere}
        .select-control{position:relative;display:inline-flex;align-items:center}
        .select-control select{appearance:none;padding-inline:1rem 2.75rem;min-width:8.5rem}
        .select-chevron{position:absolute;right:1rem;width:1rem;height:1rem;pointer-events:none;fill:none;stroke:currentColor;stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
        input,textarea{font:inherit;min-height:2.75rem;border:1px solid #42666f;border-radius:.65rem;background:#102F38;color:#DCE9E7;padding:.65rem 1rem;width:100%;min-width:0}
        input:focus-visible,textarea:focus-visible{outline:3px solid #42B8AD;outline-offset:4px}
        .create-panel{max-width:52rem}.fields{display:grid;gap:1.25rem;margin:1.5rem 0}.field label{display:block;margin-bottom:.5rem}.field .select-control,.field select{width:100%;min-width:0}.field-row{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.25rem}.help,.field-error{font-size:.9rem;margin:.5rem 0 0;color:#9FB8B6}.field-error{color:#DCE9E7}.field.invalid{border-radius:.5rem;background:#30201f;box-shadow:0 0 0 4px #30201f,0 0 0 6px #DD6C65}.create-actions{display:flex;gap:1rem;align-items:center;flex-wrap:wrap}.create-actions button{background:#42B8AD;color:#071A21;font-weight:600}.create-actions button:hover{border-color:#DCE9E7}.create-actions button:active{background:#35978e}fieldset{padding:0;margin:0;border:0;min-width:0}textarea{resize:vertical}button:disabled{cursor:default;opacity:.65}button:disabled:hover{border-color:#42666f}@media(max-width:48rem){.field-row{grid-template-columns:1fr}}
        @media(forced-colors:active){.select-control select{appearance:auto}.select-chevron{display:none}}
        """;
    private const string Styles = """
        :root{color-scheme:dark;font:100%/1.5 system-ui,sans-serif;background:#071A21;color:#DCE9E7}*{box-sizing:border-box}html{scrollbar-gutter:stable}body{margin:0}a{color:#42B8AD}button,select{font:inherit}button,.button,select{min-height:2.75rem;border:1px solid #42666f;border-radius:.65rem;background:#102F38;color:#DCE9E7;padding:.65rem 1rem}button,.button{cursor:pointer;text-decoration:none;display:inline-flex;align-items:center;justify-content:center;touch-action:manipulation}button:hover,.button:hover{border-color:#42B8AD}button:active,.button:active{background:#244A53}a:focus-visible,button:focus-visible,select:focus-visible{outline:3px solid #42B8AD;outline-offset:4px}.shell{max-width:72rem;margin:auto;padding:0 1.5rem}header{display:flex;justify-content:space-between;align-items:center;gap:1rem;padding:1.5rem 0;border-bottom:1px solid #244A53}.brand{font-weight:700;font-size:1.35rem;color:#DCE9E7;text-decoration:none}.brand span{display:block;font-size:.85rem;color:#9FB8B6;font-weight:400}nav,.actions{display:flex;align-items:center;gap:1rem;flex-wrap:wrap}nav>a{padding:.65rem 0}main{min-height:60vh;padding:2.5rem 0}.heading,.section-head{display:flex;align-items:center;justify-content:space-between;gap:1rem;flex-wrap:wrap}.heading form{display:flex;gap:.6rem;align-items:center;flex-wrap:wrap}.eyebrow{color:#42B8AD;font-size:.8rem;letter-spacing:.06em;margin:0}h1{font-size:clamp(2rem,4vw,3rem);line-height:1.15;letter-spacing:-.025em;margin:.5rem 0}h2{font-size:1.3rem;margin:0}.session,p,dt,footer{color:#9FB8B6}.session{margin:1rem 0 1.5rem}.panel{background:#0C242C;border:1px solid #244A53;border-radius:1rem;padding:1.5rem}.badge{font-size:.85rem;border:1px solid #42666f;border-radius:2rem;padding:.25rem .75rem;white-space:nowrap}.order-list{display:grid;gap:.75rem;margin:1.5rem 0}.order{display:grid;grid-template-columns:1.4fr 1fr 1fr auto;gap:1rem;align-items:center;padding:1rem;border:1px solid #244A53;border-radius:.75rem;color:#DCE9E7;text-decoration:none;background:#102F38}.order:hover{border-color:#42B8AD}.order span:not(.badge){display:block;color:#9FB8B6;font-size:.9rem}.order strong{font-weight:600;display:block;font-variant-numeric:tabular-nums}dl.dates{display:grid;grid-template-columns:1fr 1fr;gap:1.5rem;margin:1.5rem 0}dt{margin-top:1rem;font-size:.9rem}dd{margin:0;overflow-wrap:anywhere}.notes{white-space:pre-wrap}.back{display:inline-block;margin-bottom:1rem}.alert{border:2px solid #D4A84F;background:#302a1a;color:#DCE9E7;padding:1rem;border-radius:.75rem}footer{font-size:.85rem;padding:1.5rem 0;border-top:1px solid #244A53}.skip{position:absolute;left:-100rem}.skip:focus{left:1rem;top:1rem;background:#071A21;padding:1rem;z-index:1}@media(max-width:42rem){header{align-items:flex-start;flex-wrap:wrap}.shell{padding:0 1rem}main{padding:1.5rem 0}.heading form{width:100%}.order{grid-template-columns:1fr 1fr}.order>div:first-child{grid-column:1/-1}.order>.badge{justify-self:start}.panel{padding:1rem}dl.dates{grid-template-columns:1fr}nav{width:100%;justify-content:space-between}}@media(prefers-contrast:more){:root{color:#fff}.panel,.order,header,footer{border-color:#9FB8B6}p,dt,footer,.session{color:#DCE9E7}}@media(prefers-reduced-motion:reduce){*{scroll-behavior:auto}} 
        """;
}

using System.Globalization;
using System.Text;
using Laundry.Api.Customers;
using Laundry.Api.Orders;
using Microsoft.AspNetCore.DataProtection;

namespace Laundry.Api.Management;

public static partial class DevelopmentManagementPages
{
    private static async Task<(string Content, int Status)> RenderHistory(HttpContext http, string lang,
        OrderPlanningScope scope, ServiceOrder order, OrderPlanningStore store)
    {
        string T(string en, string sv) => lang == "sv" ? sv : en;
        var path = $"/management/orders/{scope.PlantId:D}/{order.Id:D}";
        var html = new StringBuilder($"<a class=back href='{path}'>← {T("Order details", "Orderdetaljer")}</a>");
        const int limit = 25;
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("Laundry.Orders.HistoryCursor.v1");
        OrderHistoryCursor? cursor = null;
        if (http.Request.Query.ContainsKey("cursor") && (http.Request.Query["cursor"].Count != 1 ||
            !OrderHistoryCursor.TryRead(protector, http.Request.Query["cursor"].ToString(), scope, order.Id, limit, out cursor)))
        {
            html.Append(Alert(T("This history link has expired or is invalid. Open history again from order details.", "Historiklänken har gått ut eller är ogiltig. Öppna historiken igen från orderdetaljer.")));
            return (html.ToString(), 400);
        }
        var rows = await store.ReadHistoryAsync(scope, order.Id, limit, cursor?.AfterVersion ?? 0, http.RequestAborted);
        var entries = rows.Take(limit).ToArray();
        var ids = entries.SelectMany(x => x.InitialPlan is { } plan ? new[] { plan.CustomerId } :
                x.Changes.Where(c => c.Field == "customerId").SelectMany(c => new[] { c.Before, c.After })
                    .Where(v => Guid.TryParse(v, out _)).Select(v => Guid.Parse(v!)))
            .Distinct().ToArray();
        var customers = await http.RequestServices.GetRequiredService<CustomerStore>()
            .ReadHistoryLabelsAsync(scope.TenantId, scope.PlantId, ids, http.RequestAborted);
        var labels = customers.ToDictionary(x => x.Id, x => $"{x.Code} · {x.DisplayName}");
        var culture = CultureInfo.GetCultureInfo(lang == "sv" ? "sv-SE" : "en-GB");
        string Label(string field) => field switch
        {
            "customerId" => T("Customer", "Kund"),
            "plannedReceivingDate" => T("Planned receiving", "Planerad inleverans"),
            "plannedDeliveryDate" => T("Planned delivery", "Planerad leverans"),
            "customerReference" => T("Customer reference", "Kundreferens"),
            "notes" => T("Notes", "Anteckningar"),
            "notesVisibleToOperators" => T("Notes shown to operators", "Anteckningar visas för operatörer"),
            _ => throw new InvalidOperationException("Unknown history field.")
        };
        string Value(string field, string? value)
        {
            if (field == "notesVisibleToOperators") return H(value switch
            {
                "true" => T("Shown", "Visas"),
                "false" => T("Hidden", "Dolda"),
                _ => T("Not recorded", "Ej registrerat")
            });
            if (value is null) return H(T("Not set", "Ej angivet"));
            if (field == "customerId") return H(Guid.TryParse(value, out var id) && labels.TryGetValue(id, out var label)
                ? label : T("Customer unavailable", "Kunden är inte tillgänglig"));
            if (field is "plannedReceivingDate" or "plannedDeliveryDate")
                return H(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMM yyyy", culture));
            return H(value);
        }
        html.Append($"<section class=panel><div class=section-head><h2>{H(order.OrderNumber)}</h2><span class=badge>{T("Read only", "Skrivskyddad")}</span></div><p>{T("Creation and saved planning changes, oldest first. Times use Europe/Stockholm. Customer labels show current names.", "Skapande och sparade planändringar, äldst först. Tider visas i Europe/Stockholm. Kundetiketter visar nuvarande namn.")}</p><ol class=history-list>");
        foreach (var entry in entries)
        {
            var local = TimeZoneInfo.ConvertTime(entry.RecordedAtUtc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"));
            var action = entry.InitialPlan is not null ? T("Order created", "Order skapad") : T("Plan changed", "Plan ändrad");
            html.Append($"<li class=history-entry><div class=section-head><h3>{action}</h3><span class=badge>{T("Version", "Version")} {entry.Version}</span></div><p class=history-meta><time datetime='{entry.RecordedAtUtc:O}'>{H(local.ToString("d MMM yyyy · HH:mm:ss zzz", culture))}</time><span>{T("Account", "Konto")}: {H(entry.ActorDisplayName ?? T("Name unavailable", "Namn saknas"))}</span></p>");
            if (entry.InitialPlan is { } initial)
            {
                html.Append("<dl class=initial-plan>");
                foreach (var pair in new[] { ("customerId", initial.CustomerId.ToString()), ("plannedReceivingDate", initial.PlannedReceivingDate.ToString("yyyy-MM-dd")), ("plannedDeliveryDate", initial.PlannedDeliveryDate.ToString("yyyy-MM-dd")), ("customerReference", initial.CustomerReference), ("notes", initial.Notes) })
                    html.Append($"<div><dt>{Label(pair.Item1)}</dt><dd class=history-value>{Value(pair.Item1, pair.Item2)}</dd></div>");
                html.Append($"<div><dt>{Label("notesVisibleToOperators")}</dt><dd class=history-value>{Value("notesVisibleToOperators", initial.NotesVisibleToOperators?.ToString().ToLowerInvariant())}</dd></div>");
                html.Append("</dl>");
            }
            else
            {
                foreach (var change in entry.Changes)
                    html.Append($"<section class=history-change><h4>{Label(change.Field)}</h4><dl class=change-values><div><dt>{T("Before", "Före")}</dt><dd class=history-value>{Value(change.Field, change.Before)}</dd></div><div><dt>{T("After", "Efter")}</dt><dd class=history-value>{Value(change.Field, change.After)}</dd></div></dl></section>");
            }
            html.Append("</li>");
        }
        html.Append("</ol>");
        if (entries.Length == 0) html.Append($"<p>{T("No history entries on this page.", "Inga historikposter på den här sidan.")}</p>");
        html.Append("<div class=actions>");
        if (cursor is not null) html.Append($"<a class=button href='{path}/history?lang={lang}'>{T("First page", "Första sidan")}</a>");
        if (rows.Length > limit)
        {
            var next = new OrderHistoryCursor(scope.TenantId, scope.PlantId, order.Id, limit, entries[^1].Version).Encode(protector);
            html.Append($"<a class=button href='{path}/history?lang={lang}&amp;cursor={Uri.EscapeDataString(next)}'>{T("Next page", "Nästa sida")}</a>");
        }
        html.Append("</div></section>");
        return (html.ToString(), 200);
    }
}

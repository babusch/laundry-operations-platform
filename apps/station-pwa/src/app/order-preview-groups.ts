import type { LocalOrderSummary } from "@laundry/api-client";

// Current development plant; production must obtain this from plant configuration, not browser locale.
export const previewPlantTimeZone = "Europe/Stockholm";
export function plantDate(now = new Date(), timeZone = previewPlantTimeZone): string {
  const parts = new Intl.DateTimeFormat("en", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
  const part = (type: string) => parts.find(value => value.type === type)!.value;
  return `${part("year")}-${part("month")}-${part("day")}`;
}

const orderNumbers = new Intl.Collator("en", { numeric: true, sensitivity: "variant" });
const compare = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;
export function groupPreviewOrders(orders: readonly LocalOrderSummary[], today: string) {
  // Date-only ISO values are already validated by the API adapter. Never mutate its result.
  const sorted = [...orders].sort((a, b) => compare(a.plannedReceivingDate, b.plannedReceivingDate) ||
    compare(a.plannedDeliveryDate, b.plannedDeliveryDate) || orderNumbers.compare(a.orderNumber, b.orderNumber) || compare(a.id, b.id));
  return [
    { key: "today" as const, orders: sorted.filter(order => order.plannedReceivingDate === today) },
    { key: "future" as const, orders: sorted.filter(order => order.plannedReceivingDate > today) },
    { key: "overdue" as const, orders: sorted.filter(order => order.plannedReceivingDate < today) },
  ];
}

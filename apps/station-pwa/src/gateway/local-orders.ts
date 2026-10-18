import { createGatewayClient, type GatewayClient, type LocalOrderPage, type LocalOrderSummary } from "@laundry/api-client";

export type LocalOrderProblem = "sourceRequired" | "forbidden" | "disabled" | "changed" | "unavailable";
export type LocalOrderOutcome = { kind: "ready"; page: LocalOrderPage } | { kind: LocalOrderProblem };
export type LocalOrderLoader = (cursor?: string, signal?: AbortSignal) => Promise<LocalOrderOutcome>;

const uuid = /^(?!00000000-0000-0000-0000-000000000000$)[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i;
const record = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const exact = (value: Record<string, unknown>, keys: string[]) => Object.keys(value).length === keys.length && keys.every((key) => Object.hasOwn(value, key));
function dateOnly(value: unknown): value is string {
  if (typeof value !== "string" || !/^(?!0000)\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const parsed = new Date(value + "T00:00:00Z");
  return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}
function summary(value: unknown): LocalOrderSummary | null {
  if (!record(value) || !exact(value, ["id", "orderNumber", "customer", "plannedReceivingDate", "plannedDeliveryDate", "status", "version"]) ||
    typeof value.id !== "string" || !uuid.test(value.id) || typeof value.orderNumber !== "string" || !value.orderNumber.trim() ||
    !dateOnly(value.plannedReceivingDate) || !dateOnly(value.plannedDeliveryDate) || value.plannedDeliveryDate < value.plannedReceivingDate ||
    value.status !== "planned" || !Number.isSafeInteger(value.version) || (value.version as number) < 1 || !record(value.customer)) return null;
  const customer = value.customer;
  if (!exact(customer, ["id", "code", "displayName"]) || typeof customer.id !== "string" || !uuid.test(customer.id) ||
    typeof customer.code !== "string" || !customer.code.trim() || customer.code.length > 50 ||
    typeof customer.displayName !== "string" || !customer.displayName.trim() || customer.displayName.length > 200) return null;
  return { id: value.id.toLowerCase(), orderNumber: value.orderNumber, customer: { id: customer.id, code: customer.code, displayName: customer.displayName },
    plannedReceivingDate: value.plannedReceivingDate, plannedDeliveryDate: value.plannedDeliveryDate, status: "planned", version: value.version as number };
}

export async function loadLocalOrders(cursor?: string, signal?: AbortSignal, gateway: GatewayClient = createGatewayClient()): Promise<LocalOrderOutcome> {
  const deadline = new AbortController();
  const abort = () => deadline.abort();
  signal?.addEventListener("abort", abort, { once: true });
  if (signal?.aborted) abort();
  const timer = setTimeout(abort, 10000);
  try {
    const result = await gateway.GET("/api/order-plans", { params: { query: { limit: 10, ...(cursor ? { cursor } : {}) } }, signal: deadline.signal, cache: "no-store" });
    const status = result.response.status;
    if (status === 401) return { kind: "sourceRequired" };
    if (status === 403) return { kind: "forbidden" };
    if (status === 404) return { kind: "disabled" };
    if (status === 409 || (status === 400 && cursor)) return { kind: "changed" };
    const data: unknown = result.data;
    if (status !== 200 || !record(data) || !exact(data, ["items", "nextCursor", "hasMore", "lastSuccessfulPageAtUtc"]) ||
      !Array.isArray(data.items) || data.items.length > 10 || typeof data.hasMore !== "boolean") return { kind: "unavailable" };
    if (data.hasMore ? (typeof data.nextCursor !== "string" || !data.nextCursor.trim() || data.nextCursor.length > 2048 || data.nextCursor === cursor || !data.items.length) : data.nextCursor !== null)
      return { kind: "unavailable" };
    const timestamp = data.lastSuccessfulPageAtUtc;
    if (timestamp !== null && (typeof timestamp !== "string" || !/(Z|\+00:00)$/.test(timestamp) || !Number.isFinite(Date.parse(timestamp)))) return { kind: "unavailable" };
    const items = data.items.map(summary);
    if (items.some((item) => item === null) || new Set(items.map((item) => item!.id)).size !== items.length) return { kind: "unavailable" };
    return { kind: "ready", page: { items: items as LocalOrderSummary[], hasMore: data.hasMore, nextCursor: data.nextCursor as string | null, lastSuccessfulPageAtUtc: timestamp as string | null } };
  } catch { return { kind: "unavailable" }; }
  finally { clearTimeout(timer); signal?.removeEventListener("abort", abort); }
}

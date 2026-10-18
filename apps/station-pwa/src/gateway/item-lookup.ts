import { createGatewayClient, type DevelopmentItemLookupResult, type GatewayClient } from "@laundry/api-client";

export type LookupItem = NonNullable<DevelopmentItemLookupResult["item"]>;
export type ItemLookupOutcome =
  | { kind: "recognized"; item: LookupItem }
  | { kind: "notRecognized" | "sourceRequired" | "forbidden" | "disabled" | "invalid" | "unavailable" };
export type ItemLookup = (value: string, signal?: AbortSignal) => Promise<ItemLookupOutcome>;

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
const exact = (value: Record<string, unknown>, keys: string[]) => Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const uuid = (value: unknown): value is string => typeof value === "string" && /^(?!00000000-0000-0000-0000-000000000000$)[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(value);
const text = (value: unknown, max: number): value is string => typeof value === "string" && value.length > 0 && value.length <= max && value === value.trim() && !/[\u0000-\u001f\u007f]/.test(value);

type RequestFailure = { kind: "sourceRequired" | "forbidden" | "disabled" | "invalid" | "unavailable" };
export async function requestDevelopmentLookup(value: string, path: "/api/development/item-lookup" | "/api/development/scan-preview", signal: AbortSignal | undefined, gateway: GatewayClient): Promise<RequestFailure | { kind: "response"; status: number; data: unknown; error: unknown }> {
  if (!text(value, 256)) return { kind: "invalid" };
  const controller = new AbortController();
  const abort = () => controller.abort();
  signal?.addEventListener("abort", abort, { once: true });
  if (signal?.aborted) abort();
  const timeout = setTimeout(abort, 10000);
  try {
    const session = await gateway.GET("/api/source-session", { signal: controller.signal, cache: "no-store" });
    if (session.response.status === 401) return { kind: "sourceRequired" };
    if (session.response.status === 403) return { kind: "forbidden" };
    if (session.response.status !== 200 || !session.data || session.data.antiforgeryHeaderName !== "X-Laundry-CSRF" || !text(session.data.antiforgeryToken, 4096))
      return { kind: "unavailable" };
    if (controller.signal.aborted) return { kind: "unavailable" };
    const result = await gateway.POST(path, {
      body: { identifier: { technology: "rfid", value } },
      headers: { "X-Laundry-CSRF": session.data.antiforgeryToken }, signal: controller.signal, cache: "no-store",
    });
    return { kind: "response", status: result.response.status, data: result.data, error: result.error };
  } catch { return { kind: "unavailable" }; }
  finally { clearTimeout(timeout); signal?.removeEventListener("abort", abort); }
}

export function lookupFailure(status: number): RequestFailure["kind"] | null {
  switch (status) {
    case 200: return null;
    case 401: return "sourceRequired";
    case 403: return "forbidden";
    case 404: return "disabled";
    case 400: case 413: case 415: return "invalid";
    default: return "unavailable";
  }
}

export function parseItemLookupResult(data: unknown): ItemLookupOutcome {
    if (!record(data) || !exact(data, ["isSynthetic", "status", "item"]) || data.isSynthetic !== true)
      return { kind: "unavailable" };
    if (data.status === "notRecognized" && data.item === null) return { kind: "notRecognized" };
    const item = data.item;
    if (data.status !== "recognized" || !record(item) || !exact(item, ["id", "displayName", "articleType", "customer"]) ||
      !uuid(item.id) || !text(item.displayName, 200) || !["towel", "sheet", "mop"].includes(item.articleType as string) || !record(item.customer))
      return { kind: "unavailable" };
    const customer = item.customer;
    if (!exact(customer, ["id", "code", "displayName"]) || !uuid(customer.id) || !text(customer.code, 50) || !text(customer.displayName, 200))
      return { kind: "unavailable" };
    return { kind: "recognized", item: { id: item.id, displayName: item.displayName, articleType: item.articleType as LookupItem["articleType"],
      customer: { id: customer.id, code: customer.code, displayName: customer.displayName } } };
}

export async function lookupDevelopmentItem(value: string, signal?: AbortSignal, gateway: GatewayClient = createGatewayClient()): Promise<ItemLookupOutcome> {
  const result = await requestDevelopmentLookup(value, "/api/development/item-lookup", signal, gateway);
  if (result.kind !== "response") return result;
  const failure = lookupFailure(result.status);
  return failure ? { kind: failure } : parseItemLookupResult(result.data);
}

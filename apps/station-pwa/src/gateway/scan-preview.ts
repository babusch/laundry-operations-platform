import { createGatewayClient, type DevelopmentScanPreviewResult, type GatewayClient } from "@laundry/api-client";
import { lookupFailure, parseItemLookupResult, requestDevelopmentLookup, type LookupItem } from "./item-lookup";
import { parseLocalOrderSummary } from "./local-orders";

export type ScanPreviewOutcome =
  | { kind: "recognized"; item: LookupItem; orders: DevelopmentScanPreviewResult["orders"]; lastSuccessfulPageAtUtc: string | null }
  | { kind: "notRecognized" | "sourceRequired" | "forbidden" | "disabled" | "invalid" | "unavailable" | "changed" | "limit" };
export type ScanPreview = (value: string, signal?: AbortSignal) => Promise<ScanPreviewOutcome>;
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);

export async function previewDevelopmentScan(value: string, signal?: AbortSignal, gateway: GatewayClient = createGatewayClient()): Promise<ScanPreviewOutcome> {
  const response = await requestDevelopmentLookup(value, "/api/development/scan-preview", signal, gateway);
  if (response.kind !== "response") return response;
  if (response.status === 409) return { kind: "changed" };
  if (response.status === 503 && record(response.error) && response.error.code === "development_scan_preview_limit_exceeded") return { kind: "limit" };
  const failure = lookupFailure(response.status); if (failure) return { kind: failure };
  const data = response.data;
  if (!record(data) || Object.keys(data).length !== 5 || !["isSynthetic", "status", "item", "orders", "lastSuccessfulPageAtUtc"].every(key => Object.hasOwn(data, key)) ||
    data.isSynthetic !== true || !Array.isArray(data.orders) || data.orders.length > 50) return { kind: "unavailable" };
  const item = parseItemLookupResult({ isSynthetic: data.isSynthetic, status: data.status === "notRecognized" ? "notRecognized" : "recognized", item: data.item });
  if (data.status === "notRecognized") return item.kind === "notRecognized" && data.orders.length === 0 && data.lastSuccessfulPageAtUtc === null ? item : { kind: "unavailable" };
  if (item.kind !== "recognized") return { kind: "unavailable" };
  const activity = data.lastSuccessfulPageAtUtc;
  if (activity !== null && (typeof activity !== "string" || !/(Z|\+00:00)$/.test(activity) || !Number.isFinite(Date.parse(activity)))) return { kind: "unavailable" };
  const orders = data.orders.map(parseLocalOrderSummary);
  if (orders.some(order => order === null || order.customer.id.toLowerCase() !== item.item.customer.id.toLowerCase()) || new Set(orders.map(order => order!.id)).size !== orders.length)
    return { kind: "unavailable" };
  const expected = orders.length === 0 ? "noLocalOrders" : orders.length === 1 ? "onePlannedOrder" : "multiplePlannedOrders";
  if (data.status !== expected) return { kind: "unavailable" };
  return { kind: "recognized", item: item.item, orders: orders as DevelopmentScanPreviewResult["orders"], lastSuccessfulPageAtUtc: activity as string | null };
}

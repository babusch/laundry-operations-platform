import { afterEach, expect, it, vi } from "vitest";
import { createGatewayClient, type DevelopmentScanPreviewResult } from "@laundry/api-client";
import { previewDevelopmentScan } from "./scan-preview";

const item = { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1", displayName: "Synthetic towel", articleType: "towel" as const,
  customer: { id: "33333333-3333-4333-8333-333333333333", code: "DEMO-001", displayName: "Demo laundry customer" } };
const order = { id: "11111111-1111-4111-8111-111111111111", orderNumber: "ORD-001", customer: item.customer,
  plannedReceivingDate: "2030-01-02", plannedDeliveryDate: "2030-01-04", status: "planned" as const, version: 1 };
const result: DevelopmentScanPreviewResult = { isSynthetic: true, status: "onePlannedOrder", item, orders: [order], lastSuccessfulPageAtUtc: null };
function response(value: unknown, status = 200) {
  const fetch = vi.fn().mockResolvedValueOnce(Response.json({ antiforgeryHeaderName: "X-Laundry-CSRF", antiforgeryToken: "synthetic" }))
    .mockResolvedValueOnce(Response.json(value, { status }));
  vi.stubGlobal("fetch", fetch); return fetch;
}
const run = () => previewDevelopmentScan("SIMULATED-RFID-TOWEL-001", undefined, createGatewayClient({ baseUrl: "https://gateway.test" }));
afterEach(() => vi.unstubAllGlobals());
it("uses only the protected preview endpoint, not scan submission or cloud", async () => {
  const fetch = response(result); expect(await run()).toEqual({ kind: "recognized", item, orders: [order], lastSuccessfulPageAtUtc: null });
  const request = fetch.mock.calls[1]![0] as Request;
  expect(request.url).toBe("https://gateway.test/api/development/scan-preview");
  expect(request.headers.get("X-Laundry-CSRF")).toBe("synthetic"); expect(request.credentials).toBe("include");
  expect(await request.json()).toEqual({ identifier: { technology: "rfid", value: "SIMULATED-RFID-TOWEL-001" } });
  expect(fetch).toHaveBeenCalledTimes(2);
});
it.each(["noLocalOrders", "multiplePlannedOrders", "notRecognized"] as const)("accepts consistent %s", async status => {
  const orders = status === "multiplePlannedOrders" ? [order, { ...order, id: "22222222-2222-4222-8222-222222222222" }] : [];
  response({ ...result, status, item: status === "notRecognized" ? null : item, orders });
  expect((await run()).kind).toBe(status === "notRecognized" ? "notRecognized" : "recognized");
});
it.each([
  { ...result, status: "noLocalOrders" }, { ...result, orders: [order, order] },
  { ...result, orders: [{ ...order, customer: { ...item.customer, id: "44444444-4444-4444-8444-444444444444" } }] },
  { ...result, notes: "private" }, { ...result, lastSuccessfulPageAtUtc: "bad" },
  { ...result, status: "notRecognized", item: null }, { ...result, item: null },
  { ...result, orders: [{ ...order, status: "receiving" }] },
])("rejects inconsistent or private results %#", async value => { response(value); expect(await run()).toEqual({ kind: "unavailable" }); });
it.each([[409, {}, "changed"], [503, { code: "development_scan_preview_limit_exceeded" }, "limit"], [503, {}, "unavailable"], [403, {}, "forbidden"]])("classifies failure %s without partial matches", async (status, body, kind) => {
  response(body, status as number); expect(await run()).toEqual({ kind });
});

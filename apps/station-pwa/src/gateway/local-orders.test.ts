import { afterEach, describe, expect, it, vi } from "vitest";
import { createGatewayClient, type LocalOrderPage } from "@laundry/api-client";
import { loadLocalOrders } from "./local-orders";

const page: LocalOrderPage = {
  items: [{ id: "11111111-1111-4111-8111-111111111111", orderNumber: "DEMO-001", customer: { id: "22222222-2222-4222-8222-222222222222", code: "QA", displayName: "Synthetic customer" }, plannedReceivingDate: "2030-01-02", plannedDeliveryDate: "2030-01-04", status: "planned", version: 1 }],
  nextCursor: null, hasMore: false, lastSuccessfulPageAtUtc: "2030-01-01T12:00:00+00:00",
};
const gateway = () => createGatewayClient({ baseUrl: "https://gateway.test" });
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
describe("local order read adapter", () => {
  it("uses only the generated local GET with cookies, bounded paging, abort and no-store", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(Response.json(page)); vi.stubGlobal("fetch", fetch);
    const controller = new AbortController();
    const outcome = loadLocalOrders("opaque+/=", controller.signal, gateway());
    controller.abort();
    expect(await outcome).toEqual({ kind: "ready", page });
    const request = fetch.mock.calls[0]![0] as Request;
    expect(request.url).toBe("https://gateway.test/api/order-plans?limit=10&cursor=opaque%2B%2F%3D");
    expect(request.method).toBe("GET"); expect(request.cache).toBe("no-store"); expect(request.credentials).toBe("include");
    expect(request.signal.aborted).toBe(true);
  });
  it.each([[401, "sourceRequired"], [403, "forbidden"], [404, "disabled"], [409, "changed"], [503, "unavailable"], [500, "unavailable"]])("classifies HTTP %s without displaying private error details", async (status, kind) => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(Response.json({ code: "internal-private-detail" }, { status: status as number })));
    expect(await loadLocalOrders(undefined, undefined, gateway())).toEqual({ kind });
  });
  it("treats an invalid continuation as needing a list restart, not a download reset", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(Response.json({}, { status: 400 })));
    expect(await loadLocalOrders("bad", undefined, gateway())).toEqual({ kind: "changed" });
  });
  it("never converts network failure into an empty successful list", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("private network detail")));
    expect(await loadLocalOrders(undefined, undefined, gateway())).toEqual({ kind: "unavailable" });
  });
  it("bounds a hung gateway read to ten seconds without automatic retries", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn((request: Request) => new Promise<Response>((_, reject) => request.signal.addEventListener("abort", () => reject(new DOMException("synthetic", "AbortError")))));
    vi.stubGlobal("fetch", fetch);
    const pending = loadLocalOrders(undefined, undefined, gateway());
    await vi.advanceTimersByTimeAsync(10000);
    expect(await pending).toEqual({ kind: "unavailable" }); expect(fetch).toHaveBeenCalledTimes(1);
  });
  it.each(["notes", "date", "timestamp", "version", "duplicate", "over-limit", "cursor", "empty-more", "unknown-field", "customer", "malformed"]) ("fails closed for invalid %s data", async (mode) => {
    const value = structuredClone(page);
    if (mode === "notes") Object.assign(value.items[0]!, { notes: "private note" });
    if (mode === "date") value.items[0]!.plannedReceivingDate = "2030-02-30";
    if (mode === "timestamp") value.lastSuccessfulPageAtUtc = "not-a-date";
    if (mode === "version") value.items[0]!.version = 1.5;
    if (mode === "duplicate") value.items.push({ ...value.items[0]! });
    if (mode === "over-limit") value.items = Array.from({ length: 11 }, () => ({ ...value.items[0]! }));
    if (mode === "cursor") value.nextCursor = "unexpected";
    if (mode === "empty-more") { value.items = []; value.hasMore = true; value.nextCursor = "next"; }
    if (mode === "unknown-field") Object.assign(value, { tenantId: "not-public" });
    if (mode === "customer") Object.assign(value.items[0]!.customer, { customerReference: "private" });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(mode === "malformed" ? new Response("{", { headers: { "Content-Type": "application/json" } }) : Response.json(value)));
    expect(await loadLocalOrders(undefined, undefined, gateway())).toEqual({ kind: "unavailable" });
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import { createGatewayClient, type DevelopmentItemLookupResult } from "@laundry/api-client";
import { lookupDevelopmentItem } from "./item-lookup";

const recognized: DevelopmentItemLookupResult = { isSynthetic: true, status: "recognized", item: {
  id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1", displayName: "Synthetic towel 001", articleType: "towel",
  customer: { id: "33333333-3333-4333-8333-333333333333", code: "DEMO-001", displayName: "Demo laundry customer" },
} };
const session = { antiforgeryHeaderName: "X-Laundry-CSRF", antiforgeryToken: "synthetic-token" };
const client = () => createGatewayClient({ baseUrl: "https://gateway.test" });
function responses(value: unknown = recognized, status = 200) {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json(session)).mockResolvedValueOnce(Response.json(value, { status }));
  vi.stubGlobal("fetch", fetch); return fetch;
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("development item lookup adapter", () => {
  it("uses the generated lookup contract, fresh session/CSRF and cookies without submitting a scan", async () => {
    const fetch = responses();
    expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client())).toEqual({ kind: "recognized", item: recognized.item });
    const first = fetch.mock.calls[0]![0] as Request; const second = fetch.mock.calls[1]![0] as Request;
    expect(first.url).toBe("https://gateway.test/api/source-session"); expect(first.method).toBe("GET");
    expect(second.url).toBe("https://gateway.test/api/development/item-lookup"); expect(second.method).toBe("POST");
    expect(second.headers.get("X-Laundry-CSRF")).toBe("synthetic-token");
    expect(await second.json()).toEqual({ identifier: { technology: "rfid", value: "SIMULATED-RFID-TOWEL-001" } });
    for (const request of [first, second]) { expect(request.cache).toBe("no-store"); expect(request.credentials).toBe("include"); }
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  it("returns unknown only for an explicitly synthetic unknown result", async () => {
    responses({ isSynthetic: true, status: "notRecognized", item: null });
    expect(await lookupDevelopmentItem("SIMULATED-RFID-UNKNOWN", undefined, client())).toEqual({ kind: "notRecognized" });
  });
  it.each([[401, "sourceRequired"], [403, "forbidden"], [404, "disabled"], [400, "invalid"], [413, "invalid"], [415, "invalid"], [503, "unavailable"], [500, "unavailable"]])("classifies lookup HTTP %s without exposing error details", async (status, kind) => {
    responses({ code: "private-error-details" }, status as number);
    expect(await lookupDevelopmentItem("SIMULATED-RFID-UNKNOWN", undefined, client())).toEqual({ kind });
  });
  it.each([[401, "sourceRequired"], [403, "forbidden"], [503, "unavailable"], [404, "unavailable"]])("does not attempt lookup after session HTTP %s", async (status, kind) => {
    const fetch = vi.fn().mockResolvedValue(Response.json({}, { status: status as number })); vi.stubGlobal("fetch", fetch);
    expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client())).toEqual({ kind }); expect(fetch).toHaveBeenCalledTimes(1);
  });
  it.each([
    { ...session, antiforgeryHeaderName: "Authorization" }, { ...session, antiforgeryToken: "" },
    { ...session, antiforgeryToken: "bad\r\nvalue" }, {},
  ])("rejects malformed session material instead of sending arbitrary headers", async value => {
    const fetch = vi.fn().mockResolvedValue(Response.json(value)); vi.stubGlobal("fetch", fetch);
    expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client())).toEqual({ kind: "unavailable" }); expect(fetch).toHaveBeenCalledTimes(1);
  });
  it.each([
    { ...recognized, isSynthetic: false }, { ...recognized, notes: "private-data" }, { ...recognized, item: null },
    { ...recognized, status: "notRecognized" }, { ...recognized, status: "other" },
    { ...recognized, item: { ...recognized.item, id: "invalid" } },
    { ...recognized, item: { ...recognized.item, articleType: "other" } },
    { ...recognized, item: { ...recognized.item, displayName: "" } },
    { ...recognized, item: { ...recognized.item, notes: "private-data" } },
    { ...recognized, item: { ...recognized.item, customer: { ...recognized.item!.customer, id: "invalid" } } },
    { ...recognized, item: { ...recognized.item, customer: { ...recognized.item!.customer, code: "" } } },
    { ...recognized, item: { ...recognized.item, customer: { ...recognized.item!.customer, notes: "private-data" } } },
    { isSynthetic: true, status: "notRecognized" }, null,
  ])("fails closed on inconsistent or non-allowlisted data %#", async value => {
    responses(value); expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client())).toEqual({ kind: "unavailable" });
  });
  it.each(["", " tag ", "tag\n", "x".repeat(257)])("rejects malformed input before any request", async value => {
    const fetch = vi.fn(); vi.stubGlobal("fetch", fetch);
    expect(await lookupDevelopmentItem(value, undefined, client())).toEqual({ kind: "invalid" }); expect(fetch).not.toHaveBeenCalled();
  });
  it("does not convert a network error to unknown", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("private-network-details")));
    expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client())).toEqual({ kind: "unavailable" });
  });
  it("bounds the session and lookup together to ten seconds and does not automatically retry", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(Response.json(session)).mockImplementationOnce(request => new Promise((_, reject) => {
      (request as Request).signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
    })); vi.stubGlobal("fetch", fetch);
    const result = lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", undefined, client());
    await vi.advanceTimersByTimeAsync(10000);
    expect(await result).toEqual({ kind: "unavailable" }); expect(fetch).toHaveBeenCalledTimes(2);
  });
  it("forwards caller cancellation and does not POST after an aborted session", async () => {
    const fetch = responses(); const controller = new AbortController(); controller.abort();
    expect(await lookupDevelopmentItem("SIMULATED-RFID-TOWEL-001", controller.signal, client())).toEqual({ kind: "unavailable" });
    expect(fetch).toHaveBeenCalledTimes(1); expect((fetch.mock.calls[0]![0] as Request).signal.aborted).toBe(true);
  });
});

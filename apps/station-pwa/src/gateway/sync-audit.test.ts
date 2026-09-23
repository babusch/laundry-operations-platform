import { afterEach, describe, expect, it, vi } from "vitest";
import { createGatewayClient } from "@laundry/api-client";

import { loadSyncAudit } from "./sync-audit";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("synchronization audit", () => {
  it("loads a summary and recent metadata without requesting scan payloads", async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        Response.json({
          asOfUtc: "2026-09-23T10:00:00.000Z",
          pending: 1,
          synchronized: 2,
          needsAttention: 0,
          oldestPendingAgeSeconds: 5,
          lastCloudReceiptAtUtc: "2026-09-23T09:59:59.000Z",
          forwardingEnabled: true,
          worker: { lastIterationAtUtc: null, lastIterationError: null },
        }),
      )
      .mockResolvedValueOnce(
        Response.json({
          items: [
            {
              eventId: "11111111-1111-4111-8111-111111111111",
              status: "synchronized",
              acceptedAtUtc: "2026-09-23T09:59:58.000Z",
              attempts: 1,
              cloudReceivedAtUtc: "2026-09-23T09:59:59.000Z",
            },
          ],
          nextOffset: null,
        }),
      );
    vi.stubGlobal("fetch", fetchMock);

    const result = await loadSyncAudit(
      createGatewayClient({ baseUrl: "https://gateway.test" }),
    );

    expect(result?.summary.synchronized).toBe(2);
    expect(result?.recent).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const urls = fetchMock.mock.calls.map(([request]) => (request as Request).url);
    expect(urls).toContain("https://gateway.test/api/sync/summary");
    expect(urls).toContain(
      "https://gateway.test/api/sync/events?order=latest&limit=5",
    );
    expect(urls.every((url) => !url.includes("payload"))).toBe(true);
  });

  it("returns unavailable when either diagnostic request fails", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn<typeof fetch>()
        .mockResolvedValueOnce(Response.json({}, { status: 503 }))
        .mockResolvedValueOnce(Response.json({ items: [], nextOffset: null })),
    );

    await expect(
      loadSyncAudit(createGatewayClient({ baseUrl: "https://gateway.test" })),
    ).resolves.toBeNull();
  });
});

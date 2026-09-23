import { afterEach, describe, expect, it, vi } from "vitest";
import { createGatewayClient } from "@laundry/api-client";

import {
  createSimulatedScan,
  createSyntheticIdentifier,
  submitScanToGateway,
} from "./scan-submission";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("scan submission", () => {
  it.each(["rfid", "barcode"] as const)(
    "creates a minimal v2 %s observation without authority claims",
    (technology) => {
      const request = createSimulatedScan(technology, "  SIMULATED-123  ");

      expect(request).toMatchObject({
        schemaVersion: 2,
        eventType: "scan.observed",
        identifier: { technology, value: "SIMULATED-123" },
      });
      expect(request.eventId).toBe(request.correlationId);
      expect(request).not.toHaveProperty("tenantId");
      expect(request).not.toHaveProperty("plantId");
      expect(request).not.toHaveProperty("stationId");
      expect(request).not.toHaveProperty("sourceId");
      expect(request).not.toHaveProperty("deviceId");
      expect(request).not.toHaveProperty("operatorId");
    },
  );

  it("labels synthetic identifiers with their simulated technology", () => {
    expect(createSyntheticIdentifier("rfid")).toMatch(/^SIMULATED-RFID-/);
    expect(createSyntheticIdentifier("barcode")).toMatch(/^SIMULATED-BARCODE-/);
  });

  it("gets a fresh antiforgery token before sending the scan", async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            sourceId: "11111111-1111-4111-8111-111111111111",
            stationId: "22222222-2222-4222-8222-222222222222",
            permissions: ["scans.submit"],
            antiforgeryToken: "test-antiforgery-token",
            antiforgeryHeaderName: "X-Test-Antiforgery",
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
      )
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            eventId: "33333333-3333-4333-8333-333333333333",
            status: "acceptedLocally",
            gatewayAcceptedAtUtc: "2026-09-22T10:00:00.000Z",
            deliveryStatus: "pending",
          }),
          { status: 201, headers: { "Content-Type": "application/json" } },
        ),
      );
    vi.stubGlobal("fetch", fetchMock);

    const outcome = await submitScanToGateway(
      createSimulatedScan("rfid", "SIMULATED-123"),
      createGatewayClient({ baseUrl: "https://gateway.test" }),
    );

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(outcome.kind).toBe("accepted");
    const scanRequest = fetchMock.mock.calls[1]?.[0];
    expect(scanRequest).toBeInstanceOf(Request);
    expect((scanRequest as Request).headers.get("X-Test-Antiforgery")).toBe(
      "test-antiforgery-token",
    );
  });

  it("returns an uncertain result when the gateway connection fails", async () => {
    vi.stubGlobal("fetch", vi.fn<typeof fetch>().mockRejectedValue(new TypeError("offline")));

    const outcome = await submitScanToGateway(
      createSimulatedScan("rfid", "SIMULATED-123"),
      createGatewayClient({ baseUrl: "https://gateway.test" }),
    );

    expect(outcome).toEqual({ kind: "uncertain" });
  });
});

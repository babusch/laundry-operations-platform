import {
  createGatewayClient,
  type GatewayClient,
  type LocalReceipt,
  type SubmitScanV2,
} from "@laundry/api-client";

export type ScanSubmissionOutcome =
  | { kind: "accepted"; receipt: LocalReceipt }
  | { kind: "rejected" }
  | { kind: "uncertain" };

export type ScanTechnology = SubmitScanV2["identifier"]["technology"];

export function createSimulatedScan(
  technology: ScanTechnology,
  identifier: string,
): SubmitScanV2 {
  const eventId = crypto.randomUUID();

  return {
    schemaVersion: 2,
    eventId,
    eventType: "scan.observed",
    correlationId: eventId,
    observedAtUtc: new Date().toISOString(),
    identifier: {
      technology,
      value: identifier.trim(),
    },
  };
}

export function createSyntheticIdentifier(technology: ScanTechnology): string {
  return `SIMULATED-${technology.toUpperCase()}-${crypto.randomUUID().slice(0, 8).toUpperCase()}`;
}

export async function submitScanToGateway(
  request: SubmitScanV2,
  gateway: GatewayClient = createGatewayClient(),
): Promise<ScanSubmissionOutcome> {
  try {
    // Fetch a fresh token for each mutation. The secure source credential remains
    // in its HttpOnly cookie and is never exposed to application code.
    const session = await gateway.GET("/api/source-session");
    if (session.response.status !== 200 || !session.data) {
      return session.response.status === 503
        ? { kind: "uncertain" }
        : { kind: "rejected" };
    }

    const result = await gateway.POST("/api/scans", {
      body: request,
      headers: {
        [session.data.antiforgeryHeaderName]: session.data.antiforgeryToken,
      },
    });

    if (
      (result.response.status === 200 || result.response.status === 201) &&
      result.data
    ) {
      return { kind: "accepted", receipt: result.data };
    }

    return result.response.status === 503
      ? { kind: "uncertain" }
      : { kind: "rejected" };
  } catch {
    // A connection failure can happen after the gateway committed the scan.
    // The caller must retain and retry the exact same immutable request.
    return { kind: "uncertain" };
  }
}

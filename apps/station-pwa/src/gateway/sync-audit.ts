import {
  createGatewayClient,
  type GatewayClient,
  type QueueItem,
  type QueueSummary,
} from "@laundry/api-client";

export type SyncAuditSnapshot = {
  summary: QueueSummary;
  recent: QueueItem[];
};

export async function loadSyncAudit(
  gateway: GatewayClient = createGatewayClient(),
): Promise<SyncAuditSnapshot | null> {
  try {
    const [summary, events] = await Promise.all([
      gateway.GET("/api/sync/summary"),
      gateway.GET("/api/sync/events", {
        params: { query: { order: "latest", limit: 5 } },
      }),
    ]);

    if (
      summary.response.status !== 200 ||
      !summary.data ||
      events.response.status !== 200 ||
      !events.data
    ) {
      return null;
    }

    return { summary: summary.data, recent: events.data.items };
  } catch {
    return null;
  }
}

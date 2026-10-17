export { createGatewayClient } from "./client";
export type { GatewayClient, GatewayClientOptions } from "./client";
export type { components, operations, paths } from "./generated/station-api";
export { createOrderPlanningClient } from "./order-client";
export type { OrderPlanningClient } from "./order-client";
export type {
  paths as OrderDownloadPaths,
  components as OrderDownloadComponents,
  operations as OrderDownloadOperations,
} from "./generated/order-download-api";
export type {
  paths as OrderPlanningPaths,
  components as OrderPlanningComponents,
  operations as OrderPlanningOperations,
} from "./generated/orders-api";

import type { components } from "./generated/station-api";

export type SubmitScanV2 = components["schemas"]["SubmitScanV2"];
export type LocalReceipt = components["schemas"]["LocalReceipt"];
export type SourceSession = components["schemas"]["SourceSession"];
export type QueueItem = components["schemas"]["QueueItem"];
export type QueueSummary = components["schemas"]["QueueSummary"];

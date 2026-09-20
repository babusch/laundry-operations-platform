export { createGatewayClient } from "./client";
export type { GatewayClient, GatewayClientOptions } from "./client";
export type { components, operations, paths } from "./generated/station-api";

import type { components } from "./generated/station-api";

export type SubmitScanV2 = components["schemas"]["SubmitScanV2"];
export type LocalReceipt = components["schemas"]["LocalReceipt"];

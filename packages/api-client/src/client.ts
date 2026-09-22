import createClient from "openapi-fetch";

import type { paths } from "./generated/station-api";

export type GatewayClientOptions = {
  baseUrl?: string;
};

export function createGatewayClient(options: GatewayClientOptions = {}) {
  return createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "include",
  });
}

export type GatewayClient = ReturnType<typeof createGatewayClient>;

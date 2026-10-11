import createClient from "openapi-fetch";

import type { paths } from "./generated/orders-api";

export function createOrderPlanningClient(options: { baseUrl?: string } = {}) {
  return createClient<paths>({
    baseUrl: options.baseUrl ?? "",
    credentials: "same-origin",
  });
}

export type OrderPlanningClient = ReturnType<typeof createOrderPlanningClient>;

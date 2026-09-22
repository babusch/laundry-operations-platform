import { createGatewayClient } from "@laundry/api-client";

const gateway = createGatewayClient();

export async function checkGatewayReadiness(): Promise<boolean> {
  const { response } = await gateway.GET("/health/ready", { parseAs: "text" });
  return response.ok;
}

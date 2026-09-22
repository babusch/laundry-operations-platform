import { createGatewayClient } from "@laundry/api-client";

const gateway = createGatewayClient();

export type SourceSessionStatus = "enrolled" | "notEnrolled" | "unavailable";

export async function checkSourceSession(): Promise<SourceSessionStatus> {
  const { response } = await gateway.GET("/api/source-session");

  if (response.status === 200) return "enrolled";
  if (response.status === 401) return "notEnrolled";
  return "unavailable";
}

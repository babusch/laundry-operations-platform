import { createGatewayClient } from "@laundry/api-client";

const gateway = createGatewayClient();

export async function enrollDevelopmentBrowser(): Promise<boolean> {
  const created = await gateway.POST("/api/development/source-enrollments");
  if (created.response.status !== 201 || !created.data) return false;

  const exchanged = await gateway.POST("/api/source-enrollment/exchange", {
    body: { enrollmentCode: created.data.enrollmentCode },
  });

  return exchanged.response.status === 204;
}

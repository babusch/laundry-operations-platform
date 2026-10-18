import { describe, expect, it } from "vitest";
import type { LocalOrderSummary } from "@laundry/api-client";
import { groupPreviewOrders, plantDate } from "./order-preview-groups";

function order(id: string, receiving: string, delivery = "2030-02-01", orderNumber = id): LocalOrderSummary {
  return { id, orderNumber, plannedReceivingDate: receiving, plannedDeliveryDate: delivery,
    customer: { id: "33333333-3333-4333-8333-333333333333", code: "QA", displayName: "Synthetic" }, status: "planned", version: 1 };
}
describe("receiving preview presentation", () => {
  it("groups today, chronological future, then chronological overdue without hiding or mutating orders", () => {
    const input = [order("far", "2031-01-01"), order("old", "2029-12-31"), order("today", "2030-01-02"), order("near", "2030-01-03"), order("yesterday", "2030-01-01")];
    const before = [...input]; const groups = groupPreviewOrders(input, "2030-01-02");
    expect(groups.map(group => [group.key, group.orders.map(value => value.id)])).toEqual([
      ["today", ["today"]], ["future", ["near", "far"]], ["overdue", ["old", "yesterday"]],
    ]);
    expect(input).toEqual(before); expect(groups.flatMap(group => group.orders)).toHaveLength(input.length);
  });
  it("breaks ties by delivery date, numeric order number and immutable id, independently of input order", () => {
    const input = [order("b", "2030-01-02", "2030-01-04", "ORD-10"), order("d", "2030-01-02", "2030-01-03"), order("c", "2030-01-02", "2030-01-04", "ORD-2"), order("a", "2030-01-02", "2030-01-04", "ORD-2")];
    expect(groupPreviewOrders(input, "2030-01-02")[0].orders.map(value => value.id)).toEqual(["d", "a", "c", "b"]);
    expect(groupPreviewOrders([...input].reverse(), "2030-01-02")).toEqual(groupPreviewOrders(input, "2030-01-02"));
  });
  it("retains empty groups and never chooses an order", () => {
    expect(groupPreviewOrders([], "2030-01-02")).toEqual([{ key: "today", orders: [] }, { key: "future", orders: [] }, { key: "overdue", orders: [] }]);
  });
  it.each([
    ["2030-01-01T22:59:59Z", "2030-01-01"], ["2030-01-01T23:00:00Z", "2030-01-02"],
    ["2030-06-01T21:59:59Z", "2030-06-01"], ["2030-06-01T22:00:00Z", "2030-06-02"],
    ["2026-03-29T00:59:59Z", "2026-03-29"], ["2026-03-29T01:00:00Z", "2026-03-29"],
    ["2026-10-25T00:59:59Z", "2026-10-25"], ["2026-10-25T01:00:00Z", "2026-10-25"],
    ["2029-12-31T23:00:00Z", "2030-01-01"],
  ])("uses Stockholm calendar date at %s rather than UTC or browser timezone", (instant, date) => {
    expect(plantDate(new Date(instant))).toBe(date);
  });
});

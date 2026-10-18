import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { LocalOrderPage } from "@laundry/api-client";
import { LocalOrders } from "./LocalOrders";
import { App } from "./App";
import { i18n } from "../i18n/i18n";
import type { LocalOrderOutcome } from "../gateway/local-orders";

const page: LocalOrderPage = {
  items: [{ id: "11111111-1111-4111-8111-111111111111", orderNumber: "DEMO-001", customer: { id: "22222222-2222-4222-8222-222222222222", code: "QA", displayName: "Synthetic customer" }, plannedReceivingDate: "2030-01-02", plannedDeliveryDate: "2030-01-04", status: "planned", version: 1 }],
  nextCursor: null, hasMore: false, lastSuccessfulPageAtUtc: "2030-01-01T12:00:00+00:00",
};
const ready = (value = page): LocalOrderOutcome => ({ kind: "ready", page: value });
afterEach(cleanup);
beforeEach(async () => { window.localStorage.clear(); await i18n.changeLanguage("en"); });

describe("read-only local order view", () => {
  it("shows summaries and honest activity metadata, with no selection or internal IDs", async () => {
    const load = vi.fn().mockResolvedValue(ready()); render(<LocalOrders load={load} />);
    expect(await screen.findByText("DEMO-001")).toBeInTheDocument();
    expect(screen.getByText("Synthetic customer")).toBeInTheDocument();
    expect(screen.getByText("2 Jan 2030")).toBeInTheDocument();
    expect(screen.getByText("Planned")).toBeInTheDocument();
    expect(screen.getByText(/Last download activity:/)).toHaveTextContent("13:00");
    expect(screen.getByText(/does not guarantee/)).toBeInTheDocument();
    expect(screen.queryByText(page.items[0]!.id)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /select|start receiving/i })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Show more" })).toBeDisabled();
    expect(screen.getByRole("region", { name: "Locally stored orders" })).toHaveAttribute("tabindex", "0");
    expect(load).toHaveBeenCalledTimes(1);
  });
  it.each([null, "2030-01-01T12:00:00Z"])("distinguishes initial download state from a checked empty list: %s", async (timestamp) => {
    render(<LocalOrders load={vi.fn().mockResolvedValue(ready({ ...page, items: [], lastSuccessfulPageAtUtc: timestamp }))} />);
    expect(await screen.findByText(timestamp ? "No local orders to show." : "No orders have been downloaded yet.")).toBeInTheDocument();
  });
  it("shows loading without assuming no orders exist", async () => {
    render(<LocalOrders load={vi.fn(() => new Promise<LocalOrderOutcome>(() => {}))} />);
    expect(screen.getByText(/Reading orders/)).toBeInTheDocument();
    expect(screen.queryByText("No orders have been downloaded yet.")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Refresh list" })).toBeDisabled();
  });
  it.each(["sourceRequired", "forbidden", "disabled", "unavailable"] as const)("explains %s without showing an empty successful result", async (kind) => {
    render(<LocalOrders load={vi.fn().mockResolvedValue({ kind })} />);
    expect(await screen.findByText("Orders are not displayed.")).toBeInTheDocument();
    expect(screen.queryByText("No local orders to show.")).not.toBeInTheDocument();
  });
  it("appends a bounded next page and refreshes from the beginning", async () => {
    const second = { ...page.items[0]!, id: "33333333-3333-4333-8333-333333333333", orderNumber: "DEMO-002" };
    const load = vi.fn().mockResolvedValueOnce(ready({ ...page, hasMore: true, nextCursor: "opaque-next" })).mockResolvedValueOnce(ready({ ...page, items: [second] })).mockResolvedValueOnce(ready());
    render(<LocalOrders load={load} />);
    await screen.findByText("DEMO-001"); fireEvent.click(screen.getByRole("button", { name: "Show more" }));
    expect(await screen.findByText("DEMO-002")).toBeInTheDocument(); expect(screen.getByText("DEMO-001")).toBeInTheDocument();
    expect(load.mock.calls[1]![0]).toBe("opaque-next");
    fireEvent.click(screen.getByRole("button", { name: "Refresh list" }));
    await waitFor(() => expect(screen.queryByText("DEMO-002")).not.toBeInTheDocument());
    expect(load.mock.calls[2]![0]).toBeUndefined();
  });
  it("retains old copies only with an explicit refresh-failure warning", async () => {
    const load = vi.fn().mockResolvedValueOnce(ready()).mockRejectedValueOnce(new Error("private detail")); render(<LocalOrders load={load} />);
    await screen.findByText("DEMO-001"); fireEvent.click(screen.getByRole("button", { name: "Refresh list" }));
    expect(await screen.findByText(/Previously displayed copies remain/)).toBeInTheDocument();
    expect(screen.getByText("DEMO-001")).toBeInTheDocument(); expect(screen.queryByText("private detail")).not.toBeInTheDocument();
  });
  it.each(["sourceRequired", "forbidden", "disabled"] as const)("clears displayed copies immediately on %s", async (kind) => {
    const load = vi.fn().mockResolvedValueOnce(ready()).mockResolvedValueOnce({ kind }); render(<LocalOrders load={load} />);
    await screen.findByText("DEMO-001"); fireEvent.click(screen.getByRole("button", { name: "Refresh list" }));
    await waitFor(() => expect(screen.queryByText("DEMO-001")).not.toBeInTheDocument());
    expect(screen.queryByText(/Last download activity:/)).not.toBeInTheDocument();
  });
  it("requires explicit restart after a changed list and never mixes duplicate pages", async () => {
    const load = vi.fn().mockResolvedValueOnce(ready({ ...page, hasMore: true, nextCursor: "first" })).mockResolvedValueOnce(ready());
    render(<LocalOrders load={load} />); await screen.findByText("DEMO-001");
    fireEvent.click(screen.getByRole("button", { name: "Show more" }));
    expect(await screen.findByText(/The local list changed/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Show more" })).toBeDisabled();
    expect(screen.getAllByText("DEMO-001")).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Restart list" }));
    await waitFor(() => expect(load).toHaveBeenCalledTimes(3)); expect(load.mock.calls[2]![0]).toBeUndefined();
  });
  it("translates loaded rows, dates and existing failure feedback without fetching again", async () => {
    const load = vi.fn().mockResolvedValueOnce(ready()).mockResolvedValueOnce({ kind: "unavailable" }); render(<LocalOrders load={load} />);
    await screen.findByText("DEMO-001"); fireEvent.click(screen.getByRole("button", { name: "Refresh list" }));
    await screen.findByText(/Previously displayed copies remain/);
    await act(() => i18n.changeLanguage("sv"));
    expect(screen.getByRole("heading", { name: "Lokala ordrar" })).toBeInTheDocument(); expect(screen.getByText("Planerad")).toBeInTheDocument();
    expect(screen.getByText(/Tidigare visade kopior/)).toBeInTheDocument(); expect(screen.getByText("2 jan. 2030")).toBeInTheDocument();
    expect(load).toHaveBeenCalledTimes(2);
  });
  it("ignores a late response after a different loader/session replaces it", async () => {
    let resolve!: (outcome: LocalOrderOutcome) => void;
    const first = vi.fn(() => new Promise<LocalOrderOutcome>((done) => { resolve = done; }));
    const next = vi.fn().mockResolvedValue(ready({ ...page, items: [{ ...page.items[0]!, orderNumber: "NEW-SESSION" }] }));
    const view = render(<LocalOrders load={first} />); view.rerender(<LocalOrders load={next} />);
    await screen.findByText("NEW-SESSION"); await act(async () => { resolve(ready()); });
    expect(screen.queryByText("DEMO-001")).not.toBeInTheDocument(); expect(screen.getByText("NEW-SESSION")).toBeInTheDocument();
  });
  it("invalidates the parent station state on a 401 without disturbing pending scan data", async () => {
    render(<App checkGateway={vi.fn().mockResolvedValue(true)} checkEnrollment={vi.fn().mockResolvedValue("enrolled")} loadOrders={vi.fn().mockResolvedValue({ kind: "sourceRequired" })} />);
    expect(await screen.findByText("Station setup required")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Simulated RFID scan" })).not.toBeInTheDocument();
  });
});

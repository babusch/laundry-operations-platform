import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ItemLookup } from "./ItemLookup";
import { App } from "./App";
import { i18n } from "../i18n/i18n";
import type { ScanPreviewOutcome as ItemLookupOutcome } from "../gateway/scan-preview";
import { plantDate } from "./order-preview-groups";

const result: ItemLookupOutcome = { kind: "recognized", item: {
  id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1", displayName: "Synthetic towel 001", articleType: "towel",
  customer: { id: "33333333-3333-4333-8333-333333333333", code: "DEMO-001", displayName: "Demo laundry customer" },
}, orders: [], lastSuccessfulPageAtUtc: null };
const click = () => fireEvent.click(screen.getByRole("button", { name: "Simulate RFID read" }));
afterEach(() => { cleanup(); vi.useRealTimers(); });
beforeEach(async () => { await i18n.changeLanguage("en"); });

describe("lookup-only item preview", () => {
  const order = { id: "11111111-1111-4111-8111-111111111111", orderNumber: "ORD-001", customer: result.item.customer, plannedReceivingDate: "2030-01-02", plannedDeliveryDate: "2030-01-04", status: "planned" as const, version: 1 };
  it("refreshes the plant date on returning to the page without refetching or losing a preview", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(new Date("2030-01-01T22:59:59Z"));
    const lookup = vi.fn().mockResolvedValue({ ...result, orders: [order] });
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText(/Previewing ORD-001/);
    expect(within(screen.getByRole("region", { name: "Upcoming (1)" })).getByText("ORD-001")).toBeInTheDocument();
    vi.setSystemTime(new Date("2030-01-01T23:00:00Z")); fireEvent(window, new Event("focus"));
    expect(within(screen.getByRole("region", { name: "Today (1)" })).getByText("ORD-001")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview ORD-001" })).toHaveAttribute("aria-pressed", "true");
    expect(lookup).toHaveBeenCalledTimes(1);
  });
  it("renders three receiving-date sections in priority order and translates without reloading matches", async () => {
    const lookup = vi.fn().mockResolvedValue({ ...result, orders: [
      { ...order, id: "old", orderNumber: "OLD", plannedReceivingDate: "2000-01-01" },
      { ...order, id: "future", orderNumber: "FUTURE", plannedReceivingDate: "2099-01-01" },
      { ...order, id: "today", orderNumber: "TODAY", plannedReceivingDate: plantDate() },
    ] });
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText("TODAY");
    const list = screen.getByRole("region", { name: "Matching downloaded plans" });
    expect(within(list).getAllByRole("heading", { level: 4 }).map(heading => heading.textContent)).toEqual(["Today (1)", "Upcoming (1)", "Overdue receiving (1)"]);
    expect(within(screen.getByRole("region", { name: "Today (1)" })).getByText("TODAY")).toBeInTheDocument();
    expect(within(list).getAllByRole("button").every(button => button.getAttribute("aria-pressed") === "false")).toBe(true);
    await act(async () => { await i18n.changeLanguage("sv"); });
    expect(within(list).getAllByRole("heading", { level: 4 }).map(heading => heading.textContent)).toEqual(["Idag (1)", "Kommande (1)", "Försenad inleverans (1)"]);
    expect(lookup).toHaveBeenCalledTimes(1);
  });
  it("previews a single plan without starting receiving", async () => {
    const lookup = vi.fn().mockResolvedValue({ ...result, orders: [order] }); render(<ItemLookup lookup={lookup} />); click();
    expect(await screen.findByText(/Previewing ORD-001/)).toHaveTextContent("receiving not started");
    expect(screen.getByRole("button", { name: "Preview ORD-001" })).toHaveAttribute("aria-pressed", "true");
    expect(lookup).toHaveBeenCalledTimes(1);
  });
  it("requires an explicit preview for several plans and clears it for another item", async () => {
    const lookup = vi.fn().mockResolvedValue({ ...result, orders: [order, { ...order, id: "22222222-2222-4222-8222-222222222222", orderNumber: "ORD-002" }] });
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText(/Several planned orders/);
    expect(screen.getByRole("button", { name: "Preview ORD-001" })).toHaveAttribute("aria-pressed", "false");
    fireEvent.click(screen.getByRole("button", { name: "Preview ORD-002" })); expect(screen.getByText(/Previewing ORD-002/)).toBeInTheDocument();
    await act(async () => { await i18n.changeLanguage("sv"); });
    expect(screen.getByText(/Förhandsvisar ORD-002/)).toBeInTheDocument(); expect(lookup).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "mop" } }); expect(screen.queryByText("ORD-002")).not.toBeInTheDocument();
  });
  it("does not lookup automatically, exposes all fixture choices and explains the boundary", () => {
    const lookup = vi.fn(); render(<ItemLookup lookup={lookup} />);
    expect(lookup).not.toHaveBeenCalled(); expect(screen.getAllByRole("option")).toHaveLength(4);
    expect(screen.getByText(/No scan is saved/)).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Test item" })).toHaveValue("towel");
    expect(screen.getByLabelText("Synthetic RFID tag")).toHaveTextContent("SIMULATED-RFID-TOWEL-001");
    expect(screen.queryByRole("button", { name: /save|start|select order/i })).not.toBeInTheDocument();
  });
  it("shows synthetic item/customer/type without internal IDs or order actions", async () => {
    const lookup = vi.fn().mockResolvedValue(result); render(<ItemLookup lookup={lookup} />); click();
    expect(await screen.findByText("Synthetic towel 001")).toBeInTheDocument();
    expect(screen.getByText("Demo laundry customer")).toBeInTheDocument(); expect(screen.getByText("Towel", { exact: true })).toBeInTheDocument();
    expect(screen.queryByText(result.item.id)).not.toBeInTheDocument();
    expect(lookup.mock.calls[0]![0]).toBe("SIMULATED-RFID-TOWEL-001");
  });
  it("clears the previous result when changing fixture and waits for an explicit lookup", async () => {
    const lookup = vi.fn().mockResolvedValueOnce(result).mockResolvedValueOnce({ kind: "notRecognized" });
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText("Synthetic towel 001");
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "unknown" } });
    expect(screen.queryByText("Demo laundry customer")).not.toBeInTheDocument(); expect(lookup).toHaveBeenCalledTimes(1); click();
    expect(await screen.findByText(/not recognized in the test catalog/)).toBeInTheDocument();
    expect(lookup.mock.calls[1]![0]).toBe("SIMULATED-RFID-UNKNOWN");
  });
  it("keeps controls interruptible and ignores a late result for the old fixture", async () => {
    let finish!: (value: ItemLookupOutcome) => void;
    const lookup = vi.fn().mockImplementationOnce(() => new Promise<ItemLookupOutcome>(resolve => { finish = resolve; })).mockResolvedValueOnce({ kind: "notRecognized" });
    render(<ItemLookup lookup={lookup} />); click();
    expect(screen.getByRole("button")).toBeDisabled(); expect(screen.getByRole("combobox")).toBeEnabled();
    const signal = lookup.mock.calls[0]![1] as AbortSignal;
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "unknown" } }); expect(signal.aborted).toBe(true); click();
    await screen.findByText(/not recognized in the test catalog/); await act(async () => { finish(result); });
    expect(screen.queryByText("Demo laundry customer")).not.toBeInTheDocument();
  });
  it.each(["sourceRequired", "forbidden", "disabled", "invalid", "unavailable", "changed", "limit"] as const)("clears recognized data on %s and does not claim unknown", async kind => {
    const lookup = vi.fn().mockResolvedValueOnce(result).mockResolvedValueOnce({ kind });
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText("Synthetic towel 001"); click();
    await waitFor(() => expect(screen.getByRole("button")).toBeEnabled());
    expect(screen.queryByText("Demo laundry customer")).not.toBeInTheDocument();
    expect(screen.queryByText(/not recognized in the test catalog/)).not.toBeInTheDocument();
  });
  it("translates an existing result and retains the choice without refetching", async () => {
    const lookup = vi.fn().mockResolvedValue(result); render(<ItemLookup lookup={lookup} />); click(); await screen.findByText("Synthetic towel 001");
    await act(async () => { await i18n.changeLanguage("sv"); });
    expect(screen.getByRole("button", { name: "Simulera RFID-läsning" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Testartikel" })).toHaveValue("towel");
    expect(screen.getByText("Handduk", { exact: true })).toBeInTheDocument(); expect(lookup).toHaveBeenCalledTimes(1);
    expect(screen.getByText(/Syntetisk artikel identifierad/)).toBeInTheDocument();
  });
  it("retranslates an error, catches unexpected errors and permits manual retry", async () => {
    const lookup = vi.fn().mockRejectedValueOnce(new Error("private-detail")).mockResolvedValueOnce(result);
    render(<ItemLookup lookup={lookup} />); click(); await screen.findByText(/Lookup is unavailable/);
    expect(screen.queryByText("private-detail")).not.toBeInTheDocument();
    await act(async () => { await i18n.changeLanguage("sv"); }); expect(screen.getByText(/Sökning är inte tillgänglig/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Simulera RFID-läsning" })); await screen.findByText("Synthetic towel 001");
  });
  it("aborts on unmount and never reports an expired old response", async () => {
    let finish!: (value: ItemLookupOutcome) => void; const required = vi.fn();
    const lookup = vi.fn((_value: string, _signal?: AbortSignal) => new Promise<ItemLookupOutcome>(resolve => { finish = resolve; }));
    const { unmount } = render(<ItemLookup lookup={lookup} onSourceRequired={required} />); click();
    const signal = lookup.mock.calls[0]![1] as AbortSignal; unmount(); expect(signal.aborted).toBe(true);
    await act(async () => { finish({ kind: "sourceRequired" }); }); expect(required).not.toHaveBeenCalled();
  });
  it("invalidates parent station state on lost source without submitting any scan", async () => {
    const submitScan = vi.fn();
    render(<App checkGateway={async () => true} checkEnrollment={async () => "enrolled"} lookupItem={async () => ({ kind: "sourceRequired" })} submitScan={submitScan}
      loadOrders={async () => ({ kind: "disabled" })} loadAudit={async () => null} />);
    const region = await screen.findByRole("region", { name: "Scan-to-order preview" });
    fireEvent.click(within(region).getByRole("button", { name: "Simulate RFID read" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Simulated RFID scan" })).not.toBeInTheDocument());
    expect(submitScan).not.toHaveBeenCalled();
  });
});

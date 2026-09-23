import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SubmitScanV2 } from "@laundry/api-client";

import { App } from "./App";
import { i18n, languageStorageKey } from "../i18n/i18n";

const scanRequest: SubmitScanV2 = {
  schemaVersion: 2,
  eventId: "11111111-1111-4111-8111-111111111111",
  eventType: "scan.observed",
  correlationId: "11111111-1111-4111-8111-111111111111",
  observedAtUtc: "2026-09-22T10:00:00.000Z",
  identifier: { technology: "barcode", value: "SIMULATED-BARCODE-TEST" },
};

const acceptedReceipt = {
  eventId: scanRequest.eventId,
  status: "acceptedLocally" as const,
  gatewayAcceptedAtUtc: "2026-09-22T10:00:00.100Z",
  deliveryStatus: "pending" as const,
};

afterEach(cleanup);

beforeEach(async () => {
  window.localStorage.clear();
  await i18n.changeLanguage("en");
});

describe("station application foundation", () => {
  it("shows an enrolled browser without claiming operator authentication", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("enrolled")}
      />,
    );

    expect(
      screen.getByRole("heading", { name: "Laundry station" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Development simulator")).toBeInTheDocument();
    expect(screen.getByText(/Development-only application/)).toBeInTheDocument();
    expect(await screen.findByText("Gateway ready")).toBeInTheDocument();
    expect(screen.getByText(/Cloud status is not checked yet/)).toBeInTheDocument();
    expect(await screen.findByText("Station setup complete")).toBeInTheDocument();
    expect(screen.getByText(/Operator sign-in is not implemented yet/)).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Simulated barcode scan" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/raw observation only/i)).toBeInTheDocument();
  });

  it("switches to Swedish and remembers the station preference", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("notEnrolled")}
        allowDevelopmentEnrollment
      />,
    );

    fireEvent.change(screen.getByLabelText("Language"), {
      target: { value: "sv" },
    });

    expect(
      await screen.findByRole("heading", { name: "Tvätteristation" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Gateway är redo")).toBeInTheDocument();
    expect(screen.getByLabelText("Språk")).toHaveValue("sv");
    expect(document.documentElement.lang).toBe("sv");
    expect(document.title).toBe("Simulator för tvätteristation");
    expect(window.localStorage.getItem(languageStorageKey)).toBe("sv");
  });

  it("shows when the browser still needs station setup", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("notEnrolled")}
        allowDevelopmentEnrollment
      />,
    );

    expect(await screen.findByText("Station setup required")).toBeInTheDocument();
    expect(screen.getByText(/has not been enrolled/)).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Set up this browser" }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/sourceId/i)).not.toBeInTheDocument();
  });

  it("enrolls this browser and verifies the new session", async () => {
    let finishEnrollment: (result: boolean) => void = () => undefined;
    const enrollBrowser = vi.fn(
      () =>
        new Promise<boolean>((resolve) => {
          finishEnrollment = resolve;
        }),
    );
    const checkEnrollment = vi
      .fn<() => Promise<"notEnrolled" | "enrolled">>()
      .mockResolvedValueOnce("notEnrolled")
      .mockResolvedValueOnce("enrolled");
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={checkEnrollment}
        enrollBrowser={enrollBrowser}
        allowDevelopmentEnrollment
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", { name: "Set up this browser" }),
    );
    expect(
      screen.getByRole("button", { name: "Setting up browser…" }),
    ).toBeDisabled();

    finishEnrollment(true);

    expect(await screen.findByText("Station setup complete")).toBeInTheDocument();
    expect(enrollBrowser).toHaveBeenCalledTimes(1);
    expect(checkEnrollment).toHaveBeenCalledTimes(2);
    expect(
      screen.queryByRole("button", { name: /setup/i }),
    ).not.toBeInTheDocument();
  });

  it("shows a retryable error when Development enrollment fails", async () => {
    const enrollBrowser = vi.fn().mockResolvedValue(false);
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("notEnrolled")}
        enrollBrowser={enrollBrowser}
        allowDevelopmentEnrollment
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", { name: "Set up this browser" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Station setup could not be completed",
    );
    expect(
      screen.getByRole("button", { name: "Try setup again" }),
    ).toBeEnabled();
  });

  it("does not offer browser enrollment outside the gateway HTTPS origin", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("notEnrolled")}
        allowDevelopmentEnrollment={false}
      />,
    );

    expect(await screen.findByText("Station setup required")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /set up/i })).not.toBeInTheDocument();
    expect(screen.getByText(/gateway HTTPS address/)).toBeInTheDocument();
  });

  it("does not check enrollment until the gateway is ready and retries both", async () => {
    const checkGateway = vi
      .fn<() => Promise<boolean>>()
      .mockResolvedValueOnce(false)
      .mockResolvedValueOnce(true);
    const checkEnrollment = vi.fn().mockResolvedValue("notEnrolled" as const);
    render(<App checkGateway={checkGateway} checkEnrollment={checkEnrollment} />);

    expect(await screen.findByText("Gateway unavailable")).toBeInTheDocument();
    expect(screen.getByText(/Scans cannot be saved/)).toBeInTheDocument();
    expect(checkEnrollment).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Retry connection" }));

    expect(screen.getByText("Checking gateway…")).toBeInTheDocument();
    await waitFor(() => expect(checkGateway).toHaveBeenCalledTimes(2));
    expect(await screen.findByText("Gateway ready")).toBeInTheDocument();
    expect(await screen.findByText("Station setup required")).toBeInTheDocument();
    expect(checkEnrollment).toHaveBeenCalledTimes(1);
  });

  it("shows a retryable session-check failure and recovers", async () => {
    const checkEnrollment = vi
      .fn<() => Promise<"unavailable" | "enrolled">>()
      .mockResolvedValueOnce("unavailable")
      .mockResolvedValueOnce("enrolled");
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={checkEnrollment}
      />,
    );

    expect(await screen.findByText("Station setup unavailable")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry connection" }));

    expect(await screen.findByText("Station setup complete")).toBeInTheDocument();
    expect(checkEnrollment).toHaveBeenCalledTimes(2);
  });

  it("shows local durability without claiming the laundry operation completed", async () => {
    const submitScan = vi.fn().mockResolvedValue({
      kind: "accepted",
      receipt: acceptedReceipt,
    });
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("enrolled")}
        createScanRequest={() => scanRequest}
        submitScan={submitScan}
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", { name: "Send simulated barcode" }),
    );

    expect(await screen.findByText("Saved locally")).toBeInTheDocument();
    expect(screen.getByText(/waiting for cloud synchronization/i)).toBeInTheDocument();
    expect(screen.getByText(/does not record receiving/i)).toBeInTheDocument();
    expect(submitScan).toHaveBeenCalledWith(scanRequest);
  });

  it("retries an uncertain result with the exact same immutable request", async () => {
    const submitScan = vi
      .fn()
      .mockResolvedValueOnce({ kind: "uncertain" })
      .mockResolvedValueOnce({ kind: "accepted", receipt: acceptedReceipt });
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("enrolled")}
        createScanRequest={() => scanRequest}
        submitScan={submitScan}
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", { name: "Send simulated barcode" }),
    );

    expect(await screen.findByText("Save result unknown")).toBeInTheDocument();
    expect(screen.getByLabelText("Synthetic barcode identifier")).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Retry same scan" }));

    expect(await screen.findByText("Saved locally")).toBeInTheDocument();
    expect(submitScan).toHaveBeenCalledTimes(2);
    expect(submitScan.mock.calls[0]?.[0]).toBe(submitScan.mock.calls[1]?.[0]);
  });

  it("allows correction after a definite rejection", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("enrolled")}
        createScanRequest={() => scanRequest}
        submitScan={vi.fn().mockResolvedValue({ kind: "rejected" })}
      />,
    );

    fireEvent.click(
      await screen.findByRole("button", { name: "Send simulated barcode" }),
    );

    expect(await screen.findByRole("alert")).toHaveTextContent("Not saved");
    expect(screen.getByLabelText("Synthetic barcode identifier")).toBeEnabled();
    expect(
      screen.getByRole("button", { name: "Send simulated barcode" }),
    ).toBeEnabled();
  });
});

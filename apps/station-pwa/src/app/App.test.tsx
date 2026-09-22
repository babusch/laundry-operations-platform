import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { App } from "./App";

afterEach(cleanup);

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
    expect(screen.getByText(/No enrollment is created/)).toBeInTheDocument();
    expect(await screen.findByText("Gateway ready")).toBeInTheDocument();
    expect(screen.getByText(/Cloud status is not checked yet/)).toBeInTheDocument();
    expect(await screen.findByText("Station setup complete")).toBeInTheDocument();
    expect(screen.getByText(/Operator sign-in is not implemented yet/)).toBeInTheDocument();
  });

  it("shows when the browser still needs station setup", async () => {
    render(
      <App
        checkGateway={vi.fn().mockResolvedValue(true)}
        checkEnrollment={vi.fn().mockResolvedValue("notEnrolled")}
      />,
    );

    expect(await screen.findByText("Station setup required")).toBeInTheDocument();
    expect(screen.getByText(/has not been enrolled/)).toBeInTheDocument();
    expect(screen.queryByText(/sourceId/i)).not.toBeInTheDocument();
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
});

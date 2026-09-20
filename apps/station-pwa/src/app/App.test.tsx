import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { App } from "./App";

afterEach(cleanup);

describe("station application foundation", () => {
  it("shows gateway readiness without claiming cloud connectivity", async () => {
    render(<App checkGateway={vi.fn().mockResolvedValue(true)} />);

    expect(
      screen.getByRole("heading", { name: "Laundry station" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Development simulator")).toBeInTheDocument();
    expect(screen.getByText(/No scans are sent yet/)).toBeInTheDocument();
    expect(await screen.findByText("Gateway ready")).toBeInTheDocument();
    expect(screen.getByText(/Cloud status is not checked yet/)).toBeInTheDocument();
  });

  it("shows an honest unavailable state and allows a manual retry", async () => {
    const checkGateway = vi
      .fn<() => Promise<boolean>>()
      .mockResolvedValueOnce(false)
      .mockResolvedValueOnce(true);
    render(<App checkGateway={checkGateway} />);

    expect(await screen.findByText("Gateway unavailable")).toBeInTheDocument();
    expect(screen.getByText(/Scans cannot be saved/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Retry connection" }));

    expect(screen.getByText("Checking gateway…")).toBeInTheDocument();
    await waitFor(() => expect(checkGateway).toHaveBeenCalledTimes(2));
    expect(await screen.findByText("Gateway ready")).toBeInTheDocument();
  });
});

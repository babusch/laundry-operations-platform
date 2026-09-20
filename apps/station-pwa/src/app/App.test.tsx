import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { App } from "./App";

describe("station application foundation", () => {
  it("identifies itself as a development simulator and sends no scans", () => {
    render(<App />);

    expect(
      screen.getByRole("heading", { name: "Laundry station" }),
    ).toBeInTheDocument();
    expect(screen.getByText("Development simulator")).toBeInTheDocument();
    expect(screen.getByText(/No scans are sent yet/)).toBeInTheDocument();
  });
});

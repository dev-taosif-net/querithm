// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { lazyDialog } from "../LazyDialog";
import "./domPolyfills";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe("lazy dialog feedback", () => {
  it("immediately shows an accessible, dismissible loading shell", async () => {
    let resolve!: (value: { default: () => React.JSX.Element }) => void;
    const load = vi.fn(() => new Promise<{ default: () => React.JSX.Element }>((done) => { resolve = done; }));
    const Dialog = lazyDialog(load);
    const close = vi.fn();
    render(<Dialog loadingTitle="Settings" onLoadClose={close} />);
    expect(screen.getByRole("status").textContent).toContain("Loading settings…");
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(close).toHaveBeenCalledOnce();
    await act(async () => resolve({ default: () => <div>Settings loaded</div> }));
    expect(screen.getByText("Settings loaded")).toBeTruthy();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("retries a rejected chunk rather than leaving the whole app on an error screen", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const load = vi.fn().mockRejectedValueOnce(new Error("Chunk unavailable")).mockResolvedValueOnce({ default: () => <div>Recovered</div> });
    const Dialog = lazyDialog(load);
    await act(async () => { render(<Dialog loadingTitle="Import data" onLoadClose={() => {}} />); });
    expect(screen.getByRole("alert").textContent).toContain("Chunk unavailable");
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Retry" })); });
    expect(screen.getByText("Recovered")).toBeTruthy();
    expect(load).toHaveBeenCalledTimes(2);
  });
});

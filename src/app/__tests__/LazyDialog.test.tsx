// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { lazyDialog } from "../LazyDialog";
import { Modal } from "../Modal";
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
  it("returns focus to the opener after the loading shell handed over to the loaded dialog", async () => {
    // Regression: the loaded dialog took the loading shell (by then gone) as its opener, so closing it left focus on
    // <body> and the next keystrokes never reached the editor that opened it.
    let resolve!: (value: { default: (p: { onClose: () => void }) => React.JSX.Element }) => void;
    const Dialog = lazyDialog((): Promise<{ default: (p: { onClose: () => void }) => React.JSX.Element }> => new Promise((done) => { resolve = done; }));
    function Host() {
      const [open, setOpen] = useState(false);
      return <>
        <button onClick={() => setOpen(true)}>Open import</button>
        {open && <Dialog loadingTitle="Import data" onLoadClose={() => setOpen(false)} onClose={() => setOpen(false)} />}
      </>;
    }
    render(<Host />);
    const opener = screen.getByRole("button", { name: "Open import" });
    opener.focus();
    fireEvent.click(opener);
    expect(screen.getByRole("status")).toBeTruthy();
    await act(async () => resolve({ default: ({ onClose }) => <Modal title="Import data" onClose={onClose}><div>Wizard</div></Modal> }));
    expect(screen.getByText("Wizard")).toBeTruthy();
    fireEvent.keyDown(window, { key: "Escape" });
    expect(screen.queryByText("Wizard")).toBeNull();
    expect(document.activeElement).toBe(opener);
  });
});

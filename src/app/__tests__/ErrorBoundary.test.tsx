// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { useEffect } from "react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({ rpc: vi.fn(async (..._a: any[]) => undefined as any) }));
vi.mock("../../ipc/client", () => ({ rpc: (...a: any[]) => h.rpc(...a) }));

import { ErrorBoundary, errorReport, MAX_REPORTS, maskMessage, REPORT_WINDOW_MS, reportRenderError, resetErrorReports, withBoundary } from "../ErrorBoundary";

/** Throws while `boom.on`, counting its mounts. */
const boom = { on: true, mounts: 0 };
function Fragile() {
  if (boom.on) throw new TypeError(`Cannot read properties of undefined (reading 'rows'); JSON "alice@example.com" was not valid`);
  return <div>fragile panel works</div>;
}
function Counted() {
  boom.mounts++;
  return <Fragile />;
}

beforeEach(() => {
  boom.on = true;
  boom.mounts = 0;
  h.rpc.mockClear();
  resetErrorReports();
  // React logs every caught error to the console; the boundary is what is under test.
  vi.spyOn(console, "error").mockImplementation(() => {});
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("ErrorBoundary (M17)", () => {
  it("a panel that throws shows 'This panel failed' in its place; the shell and the other panels stay rendered", async () => {
    render(
      <div>
        <div>top bar</div>
        <ErrorBoundary name="Results">
          <Counted />
        </ErrorBoundary>
        <ErrorBoundary name="Messages">
          <div>messages panel</div>
        </ErrorBoundary>
      </div>,
    );
    expect(screen.getByText("This panel failed")).toBeInTheDocument();
    expect(screen.getByText("top bar")).toBeInTheDocument();
    expect(screen.getByText("messages panel")).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveAttribute("data-panel-failed", "Results");
    // The quoted text the error echoed (a value, SQL) is masked on screen and in the report.
    expect(screen.getByText(/Results · TypeError: Cannot read properties of undefined/).textContent).not.toContain("alice@example.com");
    await vi.waitFor(() => expect(h.rpc).toHaveBeenCalledWith("system.reportError", expect.objectContaining({ where: "Results", name: "TypeError" })));
    const report = h.rpc.mock.calls[0][1];
    expect(report.message).toBe('Cannot read properties of undefined (reading "…"); JSON "…" was not valid');
    expect(report.componentStack).toContain("Counted");
    expect(JSON.stringify(report)).not.toContain("alice@example.com");
  });

  it("Reload panel mounts the panel again", async () => {
    render(
      <ErrorBoundary name="Plan">
        <Counted />
      </ErrorBoundary>,
    );
    const before = boom.mounts;
    boom.on = false;
    await userEvent.setup().click(screen.getByRole("button", { name: "Reload panel" }));
    expect(screen.getByText("fragile panel works")).toBeInTheDocument();
    expect(boom.mounts).toBeGreaterThan(before);
    expect(screen.queryByText("This panel failed")).toBeNull();
  });

  it("Copy details copies where, the masked message and the stacks", async () => {
    const user = userEvent.setup();
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    render(
      <ErrorBoundary name="Notebook">
        <Fragile />
      </ErrorBoundary>,
    );
    await user.click(screen.getByRole("button", { name: "Copy details" }));
    const text = (writeText.mock.calls[0] as unknown as [string])[0];
    expect(text).toMatch(/^Querithm render error in Notebook\nTypeError: Cannot read properties of undefined \(reading "…"\)/);
    expect(text).toContain("Component stack:");
    expect(text).not.toContain("alice@example.com");
  });

  it("the root boundary offers Reload window", async () => {
    const reload = vi.fn();
    const original = Object.getOwnPropertyDescriptor(window, "location")!;
    Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, reload } });
    try {
      render(
        <ErrorBoundary name="root" variant="root">
          <Fragile />
        </ErrorBoundary>,
      );
      expect(screen.getByText("Querithm hit an error")).toBeInTheDocument();
      await userEvent.setup().click(screen.getByRole("button", { name: "Reload window" }));
      expect(reload).toHaveBeenCalled();
    } finally {
      Object.defineProperty(window, "location", original);
    }
  });

  it("a wrapped Dockview panel keeps its props; a failing tab header shows a small reload button", async () => {
    const Panel = withBoundary(({ params }: { params: { tabId: string } }) => <div>panel {params.tabId}</div>, "Query tab");
    const Header = withBoundary(() => <Fragile />, "Tab header", "tab");
    render(
      <>
        <Panel params={{ tabId: "t1" }} />
        <Header />
      </>,
    );
    expect(screen.getByText("panel t1")).toBeInTheDocument();
    boom.on = false;
    await userEvent.setup().click(screen.getByRole("button", { name: "Tab failed · reload" }));
    expect(screen.getByText("fragile panel works")).toBeInTheDocument();
  });

  it("a render loop sends at most 20 reports; the engine being down never throws", async () => {
    h.rpc.mockRejectedValue(new Error("engine down"));
    for (let i = 0; i < 25; i++) {
      render(
        <ErrorBoundary name={`p${i}`}>
          <Fragile />
        </ErrorBoundary>,
      );
    }
    await vi.waitFor(() => expect(h.rpc).toHaveBeenCalledTimes(20));
    await new Promise((r) => setTimeout(r, 10));
    expect(h.rpc).toHaveBeenCalledTimes(20);
  });

  it("the cap is a rolling window: once the window has passed, reports go out again, still bounded (W9)", async () => {
    const r = errorReport("Results", new Error("x"));
    const t0 = 1_000_000;
    for (let i = 0; i < MAX_REPORTS + 5; i++) reportRenderError(r, t0 + i);
    await vi.waitFor(() => expect(h.rpc).toHaveBeenCalledTimes(MAX_REPORTS));
    reportRenderError(r, t0 + REPORT_WINDOW_MS - 1);
    // Before, a session that hit 20 never reported again.
    for (let i = 0; i < MAX_REPORTS + 5; i++) reportRenderError(r, t0 + REPORT_WINDOW_MS + 100 + i);
    await vi.waitFor(() => expect(h.rpc).toHaveBeenCalledTimes(2 * MAX_REPORTS));
    await new Promise((res) => setTimeout(res, 10));
    expect(h.rpc).toHaveBeenCalledTimes(2 * MAX_REPORTS);
  });

  it("resetKey: a failed panel tries again when it changes; a panel that works is not remounted by it (W7)", () => {
    let mounted = 0;
    function Mounted() {
      useEffect(() => void mounted++, []);
      return <Fragile />;
    }
    const at = (key: string) => (
      <ErrorBoundary name="Results" resetKey={key}>
        <Mounted />
      </ErrorBoundary>
    );
    const { rerender } = render(at("run-1"));
    expect(screen.getByText("This panel failed")).toBeInTheDocument();
    boom.on = false;
    rerender(at("run-1"));
    expect(screen.getByText("This panel failed"), "the same run stays failed until Reload panel").toBeInTheDocument();
    rerender(at("run-2"));
    expect(screen.getByText("fragile panel works")).toBeInTheDocument();
    expect(mounted).toBe(1);
    rerender(at("run-3"));
    expect(screen.getByText("fragile panel works")).toBeInTheDocument();
    expect(mounted).toBe(1); // re-rendered, never unmounted and mounted again (its own state stays)
  });

  it("only stack frames are reported, never the stack's message line", () => {
    const e = new Error('bad value "secret-cell"');
    e.stack = 'Error: bad value "secret-cell"\n    at Grid (http://tauri.localhost/assets/index-1.js:10:5)\nrender@http://tauri.localhost/assets/index-1.js:20:7';
    const r = errorReport("Results", e);
    expect(r.stack).toBe("    at Grid (http://tauri.localhost/assets/index-1.js:10:5)\nrender@http://tauri.localhost/assets/index-1.js:20:7");
    expect(maskMessage("x".repeat(600))).toHaveLength(500);
    expect(errorReport("root", "a string").message).toBe("a string");
  });
});

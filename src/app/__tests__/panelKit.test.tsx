// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import "./domPolyfills";
import { Chips, PanelRow, PanelWidthContext, Seg, useWidthBucket, widthBucket, type ChipModel, type PanelWidth } from "../panelKit";
import { useRef } from "react";

afterEach(cleanup);

const chips: ChipModel[] = [
  { key: "s0", n: "1", icon: "results", text: "1,234 rows", delta: "+4", aria: "Result set 1, 1,234 rows" },
  { key: "s1", n: "2", icon: "results", text: "18 rows", aria: "Result set 2, 18 rows" },
  { key: "e0", n: "!", icon: "errorCircle", text: "Error · line 12", error: true, aria: "Error 1, line 12: Invalid object name" },
];
const at = (width: PanelWidth, ui: React.ReactNode) => render(<PanelWidthContext.Provider value={width}>{ui}</PanelWidthContext.Provider>);

describe("widthBucket / useWidthBucket", () => {
  it("wide from 1100 px, mid from 760 px, narrow below", () => {
    expect([1600, 1100, 1099, 760, 759, 650].map(widthBucket)).toEqual(["wide", "wide", "mid", "mid", "narrow", "narrow"]);
  });

  it("re-renders only when the bucket changes", () => {
    let cb: ((e: { contentRect: { width: number } }[]) => void) | undefined;
    const RO = vi.fn(function (this: unknown, f: typeof cb) { cb = f; return { observe() {}, disconnect() {} }; });
    vi.stubGlobal("ResizeObserver", RO);
    let renders = 0;
    function Probe() {
      const ref = useRef<HTMLDivElement>(null);
      const b = useWidthBucket(ref);
      renders++;
      return <div ref={ref}>{b}</div>;
    }
    render(<Probe />);
    expect(screen.getByText("wide")).toBeTruthy();
    const before = renders;
    act(() => cb!([{ contentRect: { width: 1400 } }]));
    act(() => cb!([{ contentRect: { width: 1300 } }]));
    expect(renders).toBe(before);
    act(() => cb!([{ contentRect: { width: 900 } }]));
    expect(screen.getByText("mid")).toBeTruthy();
    vi.unstubAllGlobals();
  });
});

describe("PanelRow", () => {
  it("one row when wide, two below 1100 px (ledger, then tools); never a wrapping row", () => {
    const { container, unmount } = at("wide", <PanelRow label="Tools" ledger={<span>L</span>} tools={<span>T</span>} />);
    expect(container.querySelectorAll("[data-q-panel-row]")).toHaveLength(1);
    unmount();
    const mid = at("mid", <PanelRow label="Tools" ledger={<span>L</span>} tools={<span>T</span>} />);
    const rows = [...mid.container.querySelectorAll("[data-q-panel-row]")];
    expect(rows.map((r) => r.getAttribute("data-q-panel-row"))).toEqual(["ledger", "tools"]);
    for (const r of rows) expect(r.className).not.toMatch(/flex-wrap/);
    expect(rows[1].getAttribute("role")).toBe("toolbar");
    expect(rows[1].getAttribute("aria-label")).toBe("Tools");
  });
});

describe("Chips (the ledger)", () => {
  it("a tablist with one Tab stop; ←/→/Home/End move and select; the change since the last run is text", () => {
    const onSelect = vi.fn();
    at("wide", <Chips label="Result sets and errors" chips={chips} selected="s0" onSelect={onSelect} />);
    const tabs = within(screen.getByRole("tablist", { name: "Result sets and errors" })).getAllByRole("tab");
    expect(tabs.map((t) => t.getAttribute("tabindex"))).toEqual(["0", "-1", "-1"]);
    expect(tabs[0].textContent).toContain("+4");
    fireEvent.keyDown(tabs[0], { key: "ArrowRight" });
    fireEvent.keyDown(tabs[0], { key: "End" });
    fireEvent.keyDown(tabs[0], { key: "ArrowLeft" }); // stays at the first
    expect(onSelect.mock.calls.map((c) => c[0])).toEqual(["s1", "e0", "s0"]);
  });

  it("Ctrl+click passes the modifier (compare); the chip menu opens from Shift+F10 or the context-menu key", () => {
    const onSelect = vi.fn();
    const run = vi.fn();
    at("wide", <Chips label="L" chips={chips} selected="s0" onSelect={onSelect} menuFor={(k) => [{ label: `Act on ${k}`, run }]} />);
    fireEvent.click(screen.getByRole("tab", { name: "Result set 2, 18 rows" }), { ctrlKey: true });
    expect(onSelect).toHaveBeenCalledWith("s1", { ctrl: true });
    fireEvent.keyDown(screen.getByRole("tab", { name: "Result set 2, 18 rows" }), { key: "F10", shiftKey: true });
    fireEvent.click(screen.getByRole("menuitem", { name: "Act on s1" }));
    expect(run).toHaveBeenCalled();
  });

  it("locked (staged edits): one chip that says why and does nothing", () => {
    const onSelect = vi.fn();
    const { container } = at("wide", <Chips label="L" chips={chips} selected="s1" onSelect={onSelect} locked="Apply or discard the staged changes" />);
    const locked = container.querySelector("[data-q-ledger-locked]") as HTMLElement;
    expect(locked.textContent).toBe("18 rows");
    expect(locked.title).toBe("Apply or discard the staged changes");
    expect(screen.queryByRole("tab")).toBeNull();
  });

  it("narrow: folds into one chip that opens the list, with the first error pinned beside it", () => {
    const onSelect = vi.fn();
    at("narrow", <Chips label="Result sets and errors" chips={chips} selected="s0" onSelect={onSelect} />);
    const fold = screen.getByRole("button", { name: "Result sets and errors: Result set 1, 1,234 rows. 3 in all" });
    expect(fold.textContent).toContain("1 of 3 · 1,234 rows");
    fireEvent.click(screen.getByRole("button", { name: /Error 1, line 12/ }));
    expect(onSelect).toHaveBeenCalledWith("e0", { ctrl: false });
    fireEvent.click(fold);
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: /2 · 18 rows/ }));
    expect(onSelect).toHaveBeenLastCalledWith("s1", { ctrl: false });
  });

  it("nothing to show, nothing drawn", () => {
    const { container } = at("wide", <Chips label="L" chips={[]} onSelect={() => {}} />);
    expect(container.innerHTML).toBe("");
  });
});

describe("Seg", () => {
  it("one option pressed; an unavailable option keeps its place, says why, and is still offered to onChange", () => {
    const onChange = vi.fn();
    at("wide", <Seg label="Tool" value="a" options={[{ value: "a", label: "Trace" }, { value: "b", label: "Debug", unavailable: "Not available on SQL Server" }]} onChange={onChange} />);
    const group = screen.getByRole("group", { name: "Tool" });
    const [a, b] = within(group).getAllByRole("button");
    expect(a.getAttribute("aria-pressed")).toBe("true");
    expect(b.title).toBe("Not available on SQL Server");
    fireEvent.click(a);
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(b);
    expect(onChange).toHaveBeenCalledWith("b");
  });
});

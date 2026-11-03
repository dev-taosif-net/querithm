// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useRef, useState } from "react";
import "./domPolyfills";
import { DropMenu, type MenuEntry } from "../ToolbarMenu";

afterEach(cleanup);

function Harness({ items }: { items: MenuEntry[] }) {
  const [open, setOpen] = useState(true);
  const anchor = useRef<HTMLButtonElement>(null);
  return (
    <>
      <button ref={anchor} onClick={() => setOpen((o) => !o)}>
        caret
      </button>
      <button>elsewhere</button>
      {open && <DropMenu anchor={anchor} label="Run" items={items} onClose={() => setOpen(false)} />}
    </>
  );
}

const make = () => {
  const run = { a: vi.fn(), b: vi.fn(), c: vi.fn() };
  const hl = vi.fn();
  const items: MenuEntry[] = [
    { label: "Run selection or whole script", hint: "F5", run: run.a, onHighlight: hl },
    { label: "Explain analyze…", detail: "runs, asks first", run: run.b },
    "sep",
    { label: "Include actual plan on every run", checked: false, unavailable: "SQL Server only", run: run.c },
  ];
  return { run, hl, items };
};

describe("toolbar drop-down menu", () => {
  it("opens with focus on the first item; ↑/↓/Home/End move and wrap; the separator is skipped", () => {
    render(<Harness items={make().items} />);
    expect(screen.getByRole("menu", { name: "Run" })).toBeInTheDocument();
    const items = [...screen.getByRole("menu").querySelectorAll<HTMLElement>('[role^="menuitem"]')];
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(items[0], { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[1]);
    fireEvent.keyDown(items[1], { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[2]);
    fireEvent.keyDown(items[2], { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(items[0], { key: "ArrowUp" });
    expect(document.activeElement).toBe(items[2]);
    fireEvent.keyDown(items[2], { key: "Home" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(items[0], { key: "End" });
    expect(document.activeElement).toBe(items[2]);
  });

  it("shows detail and shortcut, and names each item cleanly; a checkable item is a menuitemcheckbox", () => {
    render(<Harness items={make().items} />);
    expect(screen.getByText("F5")).toBeInTheDocument();
    expect(screen.getByText(/runs, asks first/)).toBeInTheDocument();
    expect(screen.getByRole("menuitem", { name: "Run selection or whole script" })).toHaveAttribute("aria-keyshortcuts", "F5");
    expect(screen.getByRole("menuitem", { name: "Explain analyze…, runs, asks first" })).toBeInTheDocument();
    expect(screen.getByRole("menuitemcheckbox", { name: "Include actual plan on every run, SQL Server only" })).toHaveAttribute("aria-checked", "false");
  });

  it("clicking an item closes the menu and runs it", async () => {
    const { run, items } = make();
    render(<Harness items={items} />);
    await userEvent.setup().click(screen.getByRole("menuitem", { name: /Explain analyze/ }));
    expect(run.b).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("an unavailable item stays focusable, says why and does nothing", async () => {
    const { run, items } = make();
    render(<Harness items={items} />);
    const off = screen.getByRole("menuitemcheckbox");
    expect(off).toHaveAttribute("aria-disabled", "true");
    expect(off).toHaveTextContent("SQL Server only");
    await userEvent.setup().click(off);
    expect(run.c).not.toHaveBeenCalled();
    expect(screen.getByRole("menu")).toBeInTheDocument();
  });

  it("Escape closes and gives focus back to the trigger; Tab closes", () => {
    const { unmount } = render(<Harness items={make().items} />);
    fireEvent.keyDown(screen.getAllByRole("menuitem")[0], { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(screen.getByText("caret"));
    unmount();
    render(<Harness items={make().items} />);
    fireEvent.keyDown(screen.getAllByRole("menuitem")[0], { key: "Tab" });
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("a press outside closes it", () => {
    render(<Harness items={make().items} />);
    fireEvent.pointerDown(screen.getByText("elsewhere"));
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("highlight follows pointer and focus, and is released when the menu goes", () => {
    const { hl, items } = make();
    render(<Harness items={items} />);
    expect(hl).toHaveBeenLastCalledWith(true); // focused on open
    const first = screen.getAllByRole("menuitem")[0];
    fireEvent.pointerLeave(first);
    expect(hl).toHaveBeenLastCalledWith(false);
    fireEvent.pointerEnter(first);
    expect(hl).toHaveBeenLastCalledWith(true);
    fireEvent.keyDown(first, { key: "Escape" });
    expect(hl).toHaveBeenLastCalledWith(false);
  });
});

function ContextHarness({ items, at = { x: 40, y: 50 } }: { items: MenuEntry[]; at?: { x: number; y: number } }) {
  const [open, setOpen] = useState(true);
  const anchor = useRef<HTMLButtonElement>(null);
  return (
    <>
      <button ref={anchor}>row</button>
      {open && <DropMenu anchor={anchor} at={at} label="Actions for dbo.Orders" header={<span>DEV · Bench › SalesDB · dbo.Orders</span>} items={items} onClose={() => setOpen(false)} />}
    </>
  );
}

describe("context menu (at a point, with a header and a submenu)", () => {
  const make = () => {
    const run = { select: vi.fn(), create: vi.fn(), drop: vi.fn() };
    const items: MenuEntry[] = [
      { label: "Select top 1000", hint: "Double-click", bold: true, run: run.select },
      "sep",
      { label: "Script as", detail: "new tab, nothing runs", run: () => {}, sub: [{ label: "CREATE", run: run.create }, "sep", { label: "DROP", run: run.drop }] },
    ];
    return { run, items };
  };

  it("opens at the point, names its target in a header that is not an item, and bolds the default action", () => {
    render(<ContextHarness items={make().items} />);
    const menu = screen.getByRole("menu", { name: "Actions for dbo.Orders" });
    expect(menu.style.left).toBe("40px");
    expect(menu.style.top).toBe("50px");
    expect(screen.getByText("DEV · Bench › SalesDB · dbo.Orders").closest('[role^="menuitem"]')).toBeNull();
    expect(screen.getByRole("menuitem", { name: "Select top 1000" }).className).toMatch(/font-semibold/);
    expect(document.activeElement).toBe(screen.getByRole("menuitem", { name: "Select top 1000" }));
  });

  it("is kept inside the window", () => {
    render(<ContextHarness items={make().items} at={{ x: 5000, y: 5000 }} />);
    const menu = screen.getByRole("menu");
    expect(parseFloat(menu.style.left)).toBeLessThanOrEqual(window.innerWidth);
    expect(parseFloat(menu.style.top)).toBeLessThanOrEqual(window.innerHeight);
  });

  it("→ opens the submenu on its first item, ← and Esc close only the submenu, a sub item runs and closes everything", async () => {
    const { run, items } = make();
    render(<ContextHarness items={items} />);
    const scriptAs = screen.getByRole("menuitem", { name: "Script as, new tab, nothing runs" });
    expect(scriptAs).toHaveAttribute("aria-haspopup", "menu");
    scriptAs.focus();
    fireEvent.keyDown(scriptAs, { key: "ArrowRight" });
    expect(scriptAs).toHaveAttribute("aria-expanded", "true");
    const subMenu = screen.getByRole("menu", { name: "Script as" });
    await new Promise((r) => requestAnimationFrame(() => r(null)));
    expect(document.activeElement).toBe(within(subMenu).getByRole("menuitem", { name: "CREATE" }));
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(document.activeElement).toBe(within(subMenu).getByRole("menuitem", { name: "DROP" }));
    fireEvent.keyDown(document.activeElement!, { key: "ArrowLeft" });
    expect(screen.queryByRole("menu", { name: "Script as" })).toBeNull();
    expect(document.activeElement).toBe(scriptAs);
    // Esc inside the submenu closes it alone; Esc again closes the menu.
    fireEvent.keyDown(scriptAs, { key: "Enter" });
    await new Promise((r) => requestAnimationFrame(() => r(null)));
    fireEvent.keyDown(document.activeElement!, { key: "Escape" });
    expect(screen.queryByRole("menu", { name: "Script as" })).toBeNull();
    expect(screen.getByRole("menu", { name: "Actions for dbo.Orders" })).toBeInTheDocument();
    fireEvent.keyDown(scriptAs, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(run.create).not.toHaveBeenCalled();
  });

  it("hovering the parent opens the submenu, hovering another item closes it; clicking a sub item runs it", async () => {
    const { run, items } = make();
    render(<ContextHarness items={items} />);
    fireEvent.pointerEnter(screen.getByRole("menuitem", { name: /Script as/ }));
    expect(screen.getByRole("menu", { name: "Script as" })).toBeInTheDocument();
    fireEvent.pointerEnter(screen.getByRole("menuitem", { name: "Select top 1000" }));
    expect(screen.queryByRole("menu", { name: "Script as" })).toBeNull();
    await userEvent.setup().click(screen.getByRole("menuitem", { name: /Script as/ }));
    await userEvent.setup().click(within(screen.getByRole("menu", { name: "Script as" })).getByRole("menuitem", { name: "DROP" }));
    expect(run.drop).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
  });
});

describe("context menu re-rendered while open", () => {
  it("a caller passing a new {x, y} object with the same point does not move focus back to the first item", () => {
    const items: MenuEntry[] = [{ label: "One", run: vi.fn() }, { label: "Two", run: vi.fn() }];
    function Rerender() {
      const anchor = useRef<HTMLButtonElement>(null);
      const [, setN] = useState(0);
      return (
        <>
          <button ref={anchor} onClick={() => setN((n) => n + 1)}>
            rerender
          </button>
          <DropMenu anchor={anchor} at={{ x: 10, y: 10 }} label="Ctx" items={items} onClose={() => {}} />
        </>
      );
    }
    render(<Rerender />);
    const [one, two] = screen.getAllByRole("menuitem");
    expect(document.activeElement).toBe(one);
    fireEvent.keyDown(one, { key: "ArrowDown" });
    expect(document.activeElement).toBe(two);
    fireEvent.click(screen.getByText("rerender"));
    expect(document.activeElement).toBe(two);
  });
});

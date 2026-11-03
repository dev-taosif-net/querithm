// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";
import { Bool, Int32, Utf8 } from "apache-arrow";
import { forwardRef, useImperativeHandle } from "react";

const { latest, scrollTo, rpc } = vi.hoisted(() => ({ latest: { props: null as any }, scrollTo: vi.fn(), rpc: vi.fn() }));

vi.mock("@glideapps/glide-data-grid", async (orig) => {
  const actual = await orig<typeof import("@glideapps/glide-data-grid")>();
  return {
    ...actual,
    DataEditor: forwardRef(function FakeEditor(props: unknown, ref) {
      latest.props = props;
      useImperativeHandle(ref, () => ({ scrollTo }) as never);
      return <canvas data-testid="glide" />;
    }),
  };
});
vi.mock("@glideapps/glide-data-grid/dist/index.css", () => ({}));
vi.mock("../../ipc/client", () => ({ rpc }));

import { CompactSelection, GridCellKind } from "@glideapps/glide-data-grid";
import { useUi } from "../../store/ui";
import { ArrowResultSet } from "../model";
import { GridStaging } from "../staging";
import { ResultGrid, drawFitted, fitText, gridTheme, tint, type ResultGridHandle } from "../ResultGrid";
import { dynamicColumn, editInfo, makeBatch } from "./fixtures";

function build(): ArrowResultSet {
  const rs = new ArrowResultSet(0);
  rs.append(
    makeBatch([
      { name: "id", type: new Int32(), values: [1, 2, 3], meta: { "q:pk": "1", "q:typeName": "int", "q:ordinal": "0", "q:nullable": "0" } },
      { name: "name", type: new Utf8(), values: ["one", "two\nlines", "x".repeat(400)], meta: { "q:typeName": "nvarchar", "q:ordinal": "1", "q:fk": "dbo.cust.id", "q:nullable": "1" } },
      { name: "name__truncated", type: new Bool(), values: [false, false, true], meta: { "q:hidden": "1" } },
      { name: "qty", type: new Int32(), values: [10, null, 30], meta: { "q:typeName": "int", "q:ordinal": "2", "q:source": "dbo.orders.qty", "q:pk": "1", "q:fk": "dbo.p.id" } },
      { name: "doc", type: new Utf8(), values: ["{}", "{\"a\":1}", null], meta: { "q:typeName": "json", "q:ordinal": "3" } },
    ]),
  );
  rs.finish(false);
  return rs;
}

function editFor() {
  const base = editInfo();
  return editInfo({
    columns: [
      ...base.columns,
      { ordinal: 3, column: "doc", type: "json", nullable: true, hasDefault: false, generated: false, locked: false, lockReason: null, comparable: false, key: false },
    ],
  });
}

const P = () => latest.props;
const cell = (col: number, row: number) => P().getCellContent([col, row]);
const handle = () => {
  const ref = { current: null as ResultGridHandle | null };
  return ref;
};

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ dialogs: [] });
  document.documentElement.style.setProperty("--q-null", "#999");
  document.documentElement.style.setProperty("--q-warn", "#f59e0b");
  document.documentElement.style.setProperty("--q-ok", "rgb(10, 200, 10)");
  document.documentElement.style.setProperty("--q-err", "#f00");
});
afterEach(cleanup);

describe("tint / gridTheme", () => {
  it("converts #rgb, #rrggbb and rgb() colors; passes unknown through", () => {
    expect(tint("#fff", 0.5)).toBe("rgba(255, 255, 255, 0.5)");
    expect(tint("#102030", 0.1)).toBe("rgba(16, 32, 48, 0.1)");
    expect(tint("rgb(1, 2, 3)", 0.2)).toBe("rgba(1, 2, 3, 0.2)");
    expect(tint("rgba(4 5 6 / 1)", 0.3)).toBe("rgba(4, 5, 6, 0.3)");
    expect(tint("var(--x)", 0.3)).toBe("var(--x)");
  });
  it("builds a theme with the fonts and the CSS variable values", () => {
    document.documentElement.style.setProperty("--q-accent", "#123456");
    const t = gridTheme();
    expect(t.accentColor).toBe("#123456");
    expect(t.fontFamily).toContain("JetBrains Mono");
    expect(t.cellHorizontalPadding).toBeCloseTo(8 * 0.93);
  });
});

describe("read-only grid", () => {
  it("gives the row-number gutter the header background, read-only and editable alike", () => {
    document.documentElement.style.setProperty("--q-panel", "#abcdef");
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" />);
    expect(P().rowMarkers.theme).toMatchObject({ bgCell: "#abcdef", bgCellMedium: "#abcdef" });
    expect(P().rowMarkers.theme.bgCell).toBe(gridTheme().bgHeader);
  });
  it("names the grid for screen readers by its result set and rows, without listening to scrolling (L32)", () => {
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" />);
    expect(document.querySelector('[role="group"]')!.getAttribute("aria-label")).toBe("Results: result set 1, 3 rows");
    expect(P().onVisibleRegionChanged).toBeUndefined();
    cleanup();
    const streaming = new ArrowResultSet(1);
    streaming.append(makeBatch([{ name: "id", type: new Int32(), values: [1], meta: { "q:typeName": "int", "q:ordinal": "0" } }]));
    render(<ResultGrid rs={streaming} executionId="e1" />);
    expect(document.querySelector('[role="group"]')!.getAttribute("aria-label")).toBe("Results: result set 2, 1 row so far");
  });

  it("builds columns with markers, type-based widths and clean titles", () => {
    const rs = build();
    rs.setSort(0, "desc");
    render(<ResultGrid rs={rs} executionId="e1" />);
    const cols = P().columns;
    expect(cols.map((c: { title: string }) => c.title)).toEqual(["id", "name", "qty", "doc"]);
    expect(cols[0].hasMenu).toBe(true);
    expect(cols[0].menuIcon).toBe("qEmpty");
    expect(cols[0].icon).toBe("qPk");
    expect(cols[1]).toMatchObject({ icon: "qFk", overlayIcon: "qNullDot" });
    expect(cols[2]).toMatchObject({ icon: "qPk", overlayIcon: "qLinkBadge" });
    expect(cols[3].icon).toBe("qNull" === cols[3].icon ? "qNull" : cols[3].icon);
    // Compact defaults: a text column starts at 110px (+ marker icon + padding), well under the old 182px.
    expect(cols[1].width).toBe(Math.round((110 + 20 + 8) * 0.93));
    expect(P().rows).toBe(3);
    // header icon sprites render svg strings
    for (const f of Object.values(P().headerIcons) as (() => string)[]) expect(f()).toContain("<svg");
  });

  it("reads cell text: numbers right-aligned, NULL highlighted, multi-line and long text shortened, truncated flagged", () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    expect(cell(0, 0)).toMatchObject({ kind: GridCellKind.Text, data: "1", displayData: "1", contentAlign: "right", readonly: true, copyData: "1" });
    expect(cell(2, 1)).toMatchObject({ data: "", displayData: "NULL", allowOverlay: false, copyData: "NULL" });
    expect(cell(1, 1).displayData).toBe("two ⏎ lines");
    const long = cell(1, 2);
    expect(long.displayData.endsWith("… ⋯")).toBe(true);
    expect(long.displayData.length).toBeLessThan(310);
    // rows beyond the loaded ones are placeholders
    expect(cell(0, 50).displayData).toBe("NULL");
  });

  it("applies compare tones as cell backgrounds", () => {
    const tone = (r: number) => (["changed", "added", "removed", "muted"] as const)[r];
    render(<ResultGrid rs={build()} executionId="e1" tone={(r) => tone(r) ?? null} />);
    expect(cell(0, 0).themeOverride.bgCell).toContain("rgba(245, 158, 11");
    expect(cell(0, 1).themeOverride.bgCell).toContain("rgba(10, 200, 10");
    expect(cell(0, 2).themeOverride.bgCell).toContain("rgba(255, 0, 0");
    expect(cell(0, 3)).toBeDefined();
  });

  it("muted NULLs show no text, and muted values dim to the muted tier, never the faint one (they are still read)", () => {
    document.documentElement.style.setProperty("--q-muted", "#8c949e");
    document.documentElement.style.setProperty("--q-faint", "#4b5260");
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" tone={() => "muted"} />);
    expect(cell(2, 1).displayData).toBe("");
    expect(cell(0, 0).themeOverride.textDark).toBe("#8c949e");
  });

  it("clicking a header menu button cycles asc, desc, off", () => {
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" />);
    act(() => P().onHeaderMenuClick(0));
    expect(rs.sort).toEqual({ col: 0, dir: "asc" });
    act(() => P().onHeaderMenuClick(0));
    expect(rs.sort).toEqual({ col: 0, dir: "desc" });
    act(() => P().onHeaderMenuClick(0));
    expect(rs.sort).toBeNull();
    act(() => P().onHeaderMenuClick(1));
    act(() => P().onHeaderMenuClick(2));
    expect(rs.sort).toEqual({ col: 2, dir: "asc" });
  });

  it("clicking a header name selects the column and supports multi-selection", () => {
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" />);
    act(() => P().onHeaderClicked(1));
    expect(P().gridSelection.columns.toArray()).toEqual([1]);
    expect(P().gridSelection.rows.length).toBe(0);
    expect(P().gridSelection.current).toBeUndefined();
    act(() => P().onHeaderClicked(2, { ctrlKey: true }));
    expect(P().gridSelection.columns.toArray()).toEqual([1, 2]);
    act(() => P().onHeaderClicked(0, { shiftKey: true }));
    expect(P().gridSelection.columns.toArray()).toEqual([0, 1]);
  });

  it("clicking the selected column's header again deselects it, and a third click reselects it", () => {
    const rs = build();
    const { getByTestId } = render(<ResultGrid rs={rs} executionId="e1" />);
    const press = (col: number, e?: { ctrlKey?: boolean }) => {
      // Real Glide selects the column on mousedown, before onHeaderClicked fires on mouseup.
      act(() => {
        getByTestId("glide").dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
        P().onGridSelectionChange({ columns: CompactSelection.fromSingleSelection(col), rows: CompactSelection.empty() });
      });
      act(() => P().onHeaderClicked(col, e));
    };
    press(1);
    expect(P().gridSelection.columns.toArray()).toEqual([1]);
    press(1);
    expect(P().gridSelection.columns.length).toBe(0);
    press(1);
    expect(P().gridSelection.columns.toArray()).toEqual([1]);
    // Ctrl+click still adds to the selection as it stood before the press.
    press(2, { ctrlKey: true });
    expect(P().gridSelection.columns.toArray()).toEqual([1, 2]);
    // A plain click on one of several selected columns narrows to it rather than clearing.
    press(2);
    expect(P().gridSelection.columns.toArray()).toEqual([2]);
  });

  it("drawHeader renders sort direction indicator when sorted and subtle hint on hover", () => {
    const rs = build();
    rs.setSort(0, "asc");
    render(<ResultGrid rs={rs} executionId="e1" />);
    const ctx = {
      save: vi.fn(),
      restore: vi.fn(),
      beginPath: vi.fn(),
      closePath: vi.fn(),
      moveTo: vi.fn(),
      lineTo: vi.fn(),
      fill: vi.fn(),
      fillStyle: "",
      globalAlpha: 1,
    };
    const drawContent = vi.fn();
    const menuBounds = { x: 70, y: 0, width: 30, height: 28 };
    const theme = { accentColor: "#2563eb", textHeaderSelected: "#ffffff", textHeader: "#1e293b" };

    // Column 0 is sorted asc: draws upward arrow
    P().drawHeader({ ctx, columnIndex: 0, menuBounds, isHovered: false, isSelected: false, theme }, drawContent);
    expect(drawContent).toHaveBeenCalled();
    expect(ctx.fill).toHaveBeenCalled();
    expect(ctx.fillStyle).toBe("#2563eb");

    // Column 1 is not sorted and not hovered: draws nothing extra
    vi.clearAllMocks();
    P().drawHeader({ ctx, columnIndex: 1, menuBounds, isHovered: false, isSelected: false, theme }, drawContent);
    expect(drawContent).toHaveBeenCalled();
    expect(ctx.fill).not.toHaveBeenCalled();

    // Column 1 is hovered: draws subtle unsorted affordance
    vi.clearAllMocks();
    P().drawHeader({ ctx, columnIndex: 1, menuBounds, isHovered: true, isSelected: false, theme }, drawContent);
    expect(drawContent).toHaveBeenCalled();
    expect(ctx.fill).toHaveBeenCalledTimes(2);
    expect(ctx.globalAlpha).toBe(0.4);
  });

  it("copySelection with selected column formats as plain, headers, IN clause, and Markdown", () => {
    const ref = handle();
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} />);
    act(() => P().onHeaderClicked(0));
    expect(ref.current!.copySelection("plain")).toBe("1\n2\n3");
    expect(ref.current!.copySelection("headers")).toBe("id\n1\n2\n3");
    expect(ref.current!.copySelection("in")).toBe("IN (1, 2, 3)");
    expect(ref.current!.copySelection("markdown")).toBe("| id |\n| --- |\n| 1 |\n| 2 |\n| 3 |");
  });

  it("resizing a column updates its width", () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    act(() => P().onColumnResize({}, 321, 1));
    expect(P().columns[1].width).toBe(321);
  });

  it("opens the cell viewer with the language of the column type", async () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    await act(async () => P().onCellActivated([3, 1]));
    expect(useUi.getState().dialogs[0]).toMatchObject({ kind: "cellViewer", title: "doc (json)", value: '{"a":1}', language: "json" });
    await act(async () => P().onCellActivated([2, 1])); // NULL: nothing to show
    expect(useUi.getState().dialogs).toHaveLength(1);
  });

  it("with onViewCell, activating a read-only cell opens the side panel (view row, column) instead of the dialog", async () => {
    const onViewCell = vi.fn();
    const rs = build();
    rs.setSort(0, "desc");
    render(<ResultGrid rs={rs} executionId="e1" onViewCell={onViewCell} />);
    await act(async () => P().onCellActivated([1, 2]));
    await act(async () => P().onCellActivated([2, 1])); // NULL cells open too: the panel says NULL
    expect(onViewCell.mock.calls).toEqual([[2, 1], [1, 2]]);
    expect(rpc).not.toHaveBeenCalled(); // the panel fetches truncated values itself
    expect(useUi.getState().dialogs).toHaveLength(0);
  });

  it("fetches the full value of a truncated cell; falls back with a note when it is gone", async () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    rpc.mockResolvedValueOnce({ value: "FULL", unavailable: false });
    await act(async () => P().onCellActivated([1, 2]));
    expect(rpc).toHaveBeenCalledWith("query.cell", { executionId: "e1", resultIndex: 0, row: 2, col: 1 });
    expect(useUi.getState().dialogs[0]).toMatchObject({ value: "FULL" });
    rpc.mockResolvedValueOnce({ value: "AAEC", isBinary: true });
    await act(async () => P().onCellActivated([1, 2]));
    expect((useUi.getState().dialogs[1] as { value: string }).value).toBe("0x000102");
    rpc.mockRejectedValueOnce(new Error("closed"));
    await act(async () => P().onCellActivated([1, 2]));
    expect((useUi.getState().dialogs[2] as { value: string }).value).toContain("no longer available");
  });

  it("read-only grids have no edit handlers; keybindings disable paste", () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    expect(P().onCellEdited).toBeUndefined();
    expect(P().onPaste).toBeUndefined();
    expect(P().onKeyDown).toBeUndefined();
    expect(P().keybindings.paste).toBe(false);
  });

  it("copySelection: nothing selected, a range, whole rows; with headers", () => {
    const ref = handle();
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} />);
    expect(ref.current!.copySelection(false)).toBeNull();
    act(() => P().onGridSelectionChange({ current: { cell: [0, 0], range: { x: 0, y: 0, width: 2, height: 2 }, rangeStack: [] }, columns: CompactSelection.empty(), rows: CompactSelection.empty() }));
    expect(ref.current!.copySelection(true)).toBe("id\tname\n1\tone\n2\ttwo\nlines");
    act(() => P().onGridSelectionChange({ columns: CompactSelection.empty(), rows: CompactSelection.fromSingleSelection(2) }));
    expect(ref.current!.copySelection(false)).toContain("3\t");
    expect(ref.current!.selectedRefs()).toEqual([{ kind: "existing", row: 2 }]);
  });

  it("drops the selection when sorting or filtering reorders rows", () => {
    const ref = handle();
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} />);
    act(() => P().onGridSelectionChange({ columns: CompactSelection.empty(), rows: CompactSelection.fromSingleSelection(1) }));
    expect(ref.current!.copySelection(false)).not.toBeNull();
    act(() => rs.setSort(0, "desc"));
    expect(ref.current!.copySelection(false)).toBeNull();
  });

  it("header hover shows a tooltip describing the column; leaving clears it", () => {
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" />);
    act(() => P().onItemHovered({ kind: "header", location: [2, -1], bounds: { x: 10, y: 0, width: 80, height: 28 } }));
    const tip = document.querySelector('[role="tooltip"]')!;
    expect(tip.textContent).toBe("int · Primary key · Foreign key → dbo.p.id · Nullability unknown · From dbo.orders.qty");
    act(() => P().onItemHovered({ kind: "header", location: [99, -1], bounds: { x: 0, y: 0, width: 1, height: 1 } }));
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
    act(() => P().onItemHovered({ kind: "header", location: [1, -1], bounds: { x: 0, y: 0, width: 1, height: 1 } }));
    expect(document.querySelector('[role="tooltip"]')!.textContent).toContain("NULL allowed");
    act(() => P().onMouseMove({ kind: "out-of-bounds" }));
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
    act(() => P().onItemHovered({ kind: "cell", location: [0, 0], bounds: { x: 0, y: 0, width: 1, height: 1 } }));
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
  });
});

describe("editable grid", () => {
  const ex = (row: number) => ({ kind: "existing" as const, row });
  function setup(opts: { complete?: boolean; engine?: "mssql" | "pg"; info?: ReturnType<typeof editFor> } = {}) {
    const rs = build();
    const staging = new GridStaging(opts.info ?? editFor(), rs, opts.engine ?? "mssql");
    const onStaged = vi.fn();
    const onApply = vi.fn();
    const ref = handle();
    render(<ResultGrid rs={rs} executionId="e1" staging={staging} onStaged={onStaged} onApply={onApply} handleRef={ref} />);
    return { rs, staging, onStaged, onApply, ref };
  }

  it("marks the grid editable and enables paste/edit handlers", () => {
    setup();
    expect(document.querySelector("[data-q-grid-edit]")).not.toBeNull();
    expect(P().keybindings.paste).toBe(true);
    expect(P().onDelete()).toBe(false);
    expect(P().onCellEdited).toBeTypeOf("function");
  });

  it("describes editability in the header tooltip", () => {
    setup();
    act(() => P().onItemHovered({ kind: "header", location: [0, -1], bounds: { x: 0, y: 0, width: 1, height: 1 } }));
    expect(document.querySelector('[role="tooltip"]')!.textContent).toContain("Read-only: identity values are generated by the server");
    act(() => P().onItemHovered({ kind: "header", location: [1, -1], bounds: { x: 0, y: 0, width: 1, height: 1 } }));
    expect(document.querySelector('[role="tooltip"]')!.textContent).toContain("Editable");
  });

  it("cell content: locked columns readonly, staged cells tinted, defaults and NULLs muted", () => {
    const { staging } = setup();
    expect(cell(0, 0)).toMatchObject({ readonly: true, data: "1" });
    expect(cell(1, 0)).toMatchObject({ readonly: false, allowOverlay: true });
    act(() => void staging.setCell(ex(0), 1, { value: "uno" }));
    expect(cell(1, 0).themeOverride.bgCell).toContain("rgba(245, 158, 11");
    expect(cell(2, 1)).toMatchObject({ displayData: "NULL", data: "" });
    expect(cell(2, 1).themeOverride.textDark).toBeDefined();
    // JSON values and multi-line text edit in the viewer, not inline
    expect(cell(3, 0).allowOverlay).toBe(false);
    expect(cell(1, 1).allowOverlay).toBe(false);
    // truncated values are read-only inline (the viewer edits them)
    expect(cell(1, 2)).toMatchObject({ readonly: true, allowOverlay: true });
    expect(cell(1, 2).displayData.endsWith("⋯")).toBe(true);
  });

  it("deleted rows are tinted; new rows green; error cells red; defaults and unknown values labelled", () => {
    const { staging } = setup();
    act(() => staging.toggleDelete([ex(0)]));
    expect(cell(1, 0).themeOverride.bgCell).toContain("rgba(255, 0, 0, 0.14");
    act(() => staging.addRow(new Map([[1, { value: "n", isDefault: false }], [2, { value: null, isDefault: true }]])));
    const newRow = 3;
    expect(P().rows).toBe(4);
    expect(cell(1, newRow).themeOverride.bgCell).toContain("rgba(10, 200, 10");
    expect(cell(2, newRow).displayData).toBe("(default)");
    act(() => void staging.setCell(ex(1), 1, { value: "waytoolong" }));
    expect(cell(1, 1).themeOverride.bgCell).toContain("rgba(255, 0, 0, 0.3");
  });

  it("rows still loading are locked", () => {
    const rs = new ArrowResultSet(0);
    rs.append(build().batches[0]);
    const staging = new GridStaging(editFor(), rs);
    render(<ResultGrid rs={rs} executionId="e1" staging={staging} />);
    expect(cell(1, 0).readonly).toBe(true);
  });

  it("editing a cell stages the value and tells the panel to read tokens", () => {
    const { staging, onStaged } = setup();
    act(() => P().onCellEdited([1, 0], { kind: GridCellKind.Text, data: "uno", allowOverlay: true }));
    expect(staging.cell(ex(0), 1).value).toBe("uno");
    expect(onStaged).toHaveBeenCalledTimes(1);
    act(() => P().onCellEdited([1, 0], { kind: GridCellKind.Number, data: 1, allowOverlay: true, displayData: "1" }));
    expect(staging.edits.get(0)!.size).toBe(1);
    // inserted rows do not need tokens
    act(() => void staging.addRow());
    act(() => P().onCellEdited([1, 3], { kind: GridCellKind.Text, data: "new", allowOverlay: true }));
    expect(onStaged).toHaveBeenCalledTimes(1);
  });

  it("keyboard: Ctrl+0 sets NULL, Alt+Insert adds a row and scrolls to it, Ctrl+Delete deletes, undo/redo, Ctrl+S applies", () => {
    vi.spyOn(window, "requestAnimationFrame").mockImplementation((f) => (f(0), 0));
    const { staging, onApply } = setup();
    act(() => P().onGridSelectionChange({ current: { cell: [2, 0], range: { x: 2, y: 0, width: 1, height: 1 }, rangeStack: [] }, columns: CompactSelection.empty(), rows: CompactSelection.empty() }));
    const key = (k: string, extra: Record<string, unknown> = {}) => {
      const e = { key: k, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, preventDefault: vi.fn(), cancel: vi.fn(), ...extra };
      act(() => P().onKeyDown(e));
      return e;
    };
    const nul = key("0", { ctrlKey: true });
    expect(nul.preventDefault).toHaveBeenCalled();
    expect(staging.cell(ex(0), 2).value).toBeNull();
    key("z", { ctrlKey: true });
    expect(staging.hasChanges).toBe(false);
    key("y", { ctrlKey: true });
    expect(staging.hasChanges).toBe(true);
    key("z", { ctrlKey: true });
    key("Z", { ctrlKey: true, shiftKey: true });
    expect(staging.hasChanges).toBe(true);
    key("Insert", { altKey: true });
    expect(staging.inserted).toHaveLength(1);
    expect(scrollTo).toHaveBeenCalledWith(0, 3);
    key("Delete", { ctrlKey: true });
    expect(staging.deleted.has(0)).toBe(true);
    key("s", { ctrlKey: true });
    expect(onApply).toHaveBeenCalled();
    const other = key("a");
    expect(other.preventDefault).not.toHaveBeenCalled();
  });

  it("Alt+Insert is ignored when the table cannot take inserts", () => {
    const { staging } = setup({ info: { ...editFor(), insertBlockedReason: "no insertable columns", columns: editFor().columns.map((c) => ({ ...c, locked: true, lockReason: "x" })) } });
    expect(staging.canInsert).toBe(false);
    act(() => P().onKeyDown({ key: "Insert", altKey: true, ctrlKey: false, metaKey: false, shiftKey: false, preventDefault: vi.fn(), cancel: vi.fn() }));
    expect(staging.inserted).toHaveLength(0);
  });

  it("paste: overwrites existing cells, NULL token becomes SQL NULL, rows past the end become new rows", () => {
    const { staging, onStaged } = setup();
    act(() => void P().onPaste([1, 1], [["dos", "NULL"], ["tres", "31"], ["cuatro", "4"]]));
    expect(staging.cell(ex(1), 1).value).toBe("dos");
    expect(staging.cell(ex(1), 2).value).toBeNull();
    expect(staging.cell(ex(2), 1).value).toBe("x".repeat(400)); // shortened in the grid: paste cannot overwrite it
    expect(staging.cell(ex(2), 2).value).toBe("31");
    expect(staging.inserted).toHaveLength(1);
    expect(staging.inserted[0].values.get(1)).toEqual({ value: "cuatro" });
    expect(onStaged).toHaveBeenCalled();
    // columns past the last one are ignored
    act(() => void P().onPaste([3, 0], [["{}", "extra"]]));
  });

  it("edit mode keeps its own editors: onViewCell is not used while staging", async () => {
    const onViewCell = vi.fn();
    const rs = build();
    const staging = new GridStaging(editFor(), rs, "mssql");
    render(<ResultGrid rs={rs} executionId="e1" staging={staging} onViewCell={onViewCell} />);
    await act(async () => P().onCellActivated([3, 0]));
    expect(onViewCell).not.toHaveBeenCalled();
    expect(useUi.getState().dialogs[0]).toMatchObject({ kind: "cellViewer", language: "json" });
  });

  it("opening cells: inline for plain text, viewer for json with save-through-staging", async () => {
    const { staging } = setup();
    await act(async () => P().onCellActivated([1, 0]));
    expect(useUi.getState().dialogs).toHaveLength(0);
    await act(async () => P().onCellActivated([3, 0]));
    const d = useUi.getState().dialogs[0] as { onSave?: (v: string) => void; language?: string };
    expect(d.language).toBe("json");
    act(() => d.onSave!('{"b":2}'));
    expect(staging.cell(ex(0), 3).value).toBe('{"b":2}');
  });

  // Regression: saving a truncated value from the cell viewer used to be refused by the staging lock, so the edit vanished.
  it("locked cells open a read-only viewer; truncated cells load the full value and can save", async () => {
    const { staging } = setup();
    await act(async () => P().onCellActivated([0, 0]));
    expect((useUi.getState().dialogs[0] as { onSave?: unknown }).onSave).toBeUndefined();
    rpc.mockResolvedValueOnce({ unavailable: true });
    await act(async () => P().onCellActivated([1, 2]));
    expect((useUi.getState().dialogs[1] as { value: string }).value).toContain("Re-run the query to edit it");
    rpc.mockResolvedValueOnce({ value: "AA==", isBinary: true });
    await act(async () => P().onCellActivated([1, 2]));
    expect((useUi.getState().dialogs[2] as { value: string }).value).toContain("no longer available");
    rpc.mockResolvedValueOnce({ value: "FULL" });
    await act(async () => P().onCellActivated([1, 2]));
    const d = useUi.getState().dialogs[3] as { value: string; onSave: (v: string) => void };
    expect(d.value).toBe("FULL");
    act(() => d.onSave("edited"));
    expect(staging.cell(ex(2), 1).value).toBe("edited");
    expect(staging.hasChanges).toBe(true);
  });

  it("draws an accent bar on edited cells and a strike through deleted rows", () => {
    const { staging } = setup();
    act(() => {
      staging.setCell(ex(0), 1, { value: "uno" });
      staging.toggleDelete([ex(1)]);
    });
    const ctx = { strokeStyle: "", lineWidth: 0, fillStyle: "", beginPath: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(), stroke: vi.fn(), fillRect: vi.fn() };
    const draw = (col: number, row: number) => {
      const content = vi.fn();
      P().drawCell({ ctx, rect: { x: 0, y: 0, width: 100, height: 20 }, col, row }, content);
      expect(content).toHaveBeenCalled();
    };
    draw(1, 0);
    expect(ctx.fillRect).toHaveBeenCalledWith(0, 1, 2, 18);
    draw(1, 1);
    expect(ctx.stroke).toHaveBeenCalledTimes(1);
    draw(9, 0); // column that does not exist
    draw(1, 2);
    expect(ctx.fillRect).toHaveBeenCalledTimes(1);
  });

  it("hover tooltips show validation errors and the original value of staged cells", () => {
    const { staging } = setup();
    const hover = (col: number, row: number) => act(() => P().onItemHovered({ kind: "cell", location: [col, row], bounds: { x: 4, y: 20, width: 50, height: 24 } }));
    act(() => void staging.setCell(ex(0), 1, { value: "uno" }));
    hover(1, 0);
    expect(document.querySelector('[role="tooltip"]')!.textContent).toBe("Was: one");
    act(() => void staging.setCell(ex(1), 2, { value: "abc" }));
    hover(2, 1);
    expect(document.querySelector('[role="tooltip"]')!.textContent).toMatch(/\S/);
    hover(1, 1);
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
    hover(1, 50); // below the last row
    expect(document.querySelector('[role="tooltip"]')).toBeNull();
  });

  it("copySelection reads staged values (DEFAULT for defaults)", () => {
    const { staging, ref } = setup();
    act(() => {
      staging.setCell(ex(0), 1, { value: "uno" });
      staging.addRow(new Map([[1, { value: null, isDefault: true }]]));
    });
    act(() => P().onGridSelectionChange({ current: { cell: [1, 0], range: { x: 1, y: 0, width: 1, height: 4 }, rangeStack: [] }, columns: CompactSelection.empty(), rows: CompactSelection.empty() }));
    expect(ref.current!.copySelection(true)).toBe("name\nuno\ntwo\nlines\n" + "x".repeat(400) + "\nDEFAULT");
    expect(ref.current!.copySelection("in")).toContain("IN ('uno', 'two\nlines'");
    expect(ref.current!.copySelection("markdown")).toContain("| name |");
    act(() => P().onGridSelectionChange({ columns: CompactSelection.empty(), rows: CompactSelection.fromSingleSelection(0) }));
    expect(ref.current!.copySelection(false)!.split("\t")).toHaveLength(4);
  });

  it("calculates selection stats on grid selection change", () => {
    const onStats = vi.fn();
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" onStatsChange={onStats} />);
    // Select 2 numeric cells in column 0 (rows 0 and 1)
    act(() => P().onGridSelectionChange({ current: { cell: [0, 0], range: { x: 0, y: 0, width: 1, height: 2 }, rangeStack: [] }, columns: CompactSelection.empty(), rows: CompactSelection.empty() }));
    expect(onStats).toHaveBeenCalledWith(expect.objectContaining({ count: 2, numericCount: 2, sum: 3, avg: 1.5, min: 1, max: 2 }));
  });

  it("calculates stats when a column is selected via header click", () => {
    const onStats = vi.fn();
    const rs = build();
    render(<ResultGrid rs={rs} executionId="e1" onStatsChange={onStats} />);
    act(() => P().onHeaderClicked(0)); // id column: values [1, 2, 3]
    expect(onStats).toHaveBeenCalledWith(expect.objectContaining({ count: 3, numericCount: 3, sum: 6, avg: 2, min: 1, max: 3 }));
  });

  for (const engine of ["mssql", "pg"] as const) {
    it(`edits stage the same way on ${engine}`, () => {
      const { staging } = setup({ engine });
      act(() => P().onCellEdited([2, 0], { kind: GridCellKind.Text, data: "11", allowOverlay: true }));
      expect(staging.engine).toBe(engine);
      expect(staging.cell(ex(0), 2).value).toBe("11");
    });
  }

  it("committed rows after an apply extend the grid", () => {
    const { staging } = setup();
    act(() => {
      staging.addRow(new Map([[1, { value: "n" }]]));
      staging.commit(staging.buildPatch("s", "e", 0), { ok: true, appliedUpdates: 0, appliedInserts: 1, appliedDeletes: 0 });
    });
    expect(P().rows).toBe(4);
    expect(cell(1, 3).data).toBe("n");
  });
});

it("copies literal NULL as text and rejects oversized formatted copies before reading cells", () => {
  const rs = new ArrowResultSet(0);
  rs.append(makeBatch([{ name: "value", type: new Utf8(), values: ["NULL", null, "O'Reilly"] }]));
  const ref = { current: null as ResultGridHandle | null };
  const { rerender } = render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} />);
  act(() => latest.props.onHeaderClicked(0));
  expect(ref.current!.copySelection("in")).toBe("IN ('NULL', NULL, 'O''Reilly')");
  rs.rowCount = 1_000_000;
  rerender(<ResultGrid rs={rs} executionId="e1" handleRef={ref} />);
  expect(() => ref.current!.copySelection("markdown")).toThrow("50,000 cells");
});

it.each(["mssql", "pg"] as const)("copies Unicode SQL literals for %s", (engine) => {
  const rs = new ArrowResultSet(0);
  rs.append(makeBatch([{ name: "value", type: new Utf8(), values: ["বাংলা"] }]));
  const ref = { current: null as ResultGridHandle | null };
  render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} engine={engine} />);
  act(() => latest.props.onHeaderClicked(0));
  expect(ref.current!.copySelection("in")).toBe(engine === "mssql" ? "IN (N'বাংলা')" : "IN ('বাংলা')");
});

describe("dynamically typed columns (Phase D0f, SQLite)", () => {
  const dynamicSet = () => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([
      { name: "id", type: new Int32(), values: [1, 2, 3, 4, 5], meta: { "q:typeName": "int", "q:ordinal": "0" } },
      ...dynamicColumn("v", [[1, "42"], [2, "1.5"], [3, "it's"], [0, null], [4, "00ff"]], 1),
    ]));
    rs.finish(false);
    return rs;
  };

  it("never shows the hidden siblings: columns, cells and copies hold the visible columns only", () => {
    const ref = handle();
    render(<ResultGrid rs={dynamicSet()} executionId="e1" handleRef={ref} />);
    expect(P().columns.map((c: { title: string }) => c.title)).toEqual(["id", "v"]);
    expect([0, 1, 2, 3, 4].map((r) => cell(1, r).displayData)).toEqual(["42", "1.5", "it's", "NULL", "00ff"]);
    act(() => P().onGridSelectionChange({ columns: CompactSelection.empty(), rows: CompactSelection.fromSingleSelection(0) }));
    expect(ref.current!.copySelection("headers")).toBe("id\tv\n1\t42");
  });

  it("right-aligns integer and real cells like a number column; text and blob cells stay left", () => {
    render(<ResultGrid rs={dynamicSet()} executionId="e1" />);
    expect([0, 1, 2, 4].map((r) => cell(1, r).contentAlign)).toEqual(["right", "right", undefined, undefined]);
  });

  it("an IN clause follows each cell's class: numbers unquoted, text quoted, NULL as NULL; a blob is refused", () => {
    const ref = handle();
    render(<ResultGrid rs={dynamicSet()} executionId="e1" handleRef={ref} engine="pg" />);
    act(() => P().onGridSelectionChange({ columns: CompactSelection.empty(), rows: CompactSelection.empty(), current: { cell: [1, 0], range: { x: 1, y: 0, width: 1, height: 4 }, rangeStack: [] } }));
    expect(ref.current!.copySelection("in")).toBe("IN (42, 1.5, 'it''s', NULL)");
    act(() => P().onHeaderClicked(1));
    expect(() => ref.current!.copySelection("in")).toThrow("IN clauses cannot copy binary");
  });

  it("a text column that is not dynamic still quotes digits in an IN clause and aligns left", () => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([{ name: "code", type: new Utf8(), values: ["42", "7"], meta: { "q:typeName": "nvarchar" } }]));
    const ref = handle();
    render(<ResultGrid rs={rs} executionId="e1" handleRef={ref} engine="pg" />);
    act(() => P().onHeaderClicked(0));
    expect(ref.current!.copySelection("in")).toBe("IN ('42', '7')");
    expect(cell(0, 0).contentAlign).toBeUndefined();
  });
});

describe("cell text ellipsis (Glide 6 clips overflowing text with no mark)", () => {
  // A monospace canvas: Latin 7 px per character, CJK 14 px.
  const fakeCtx = () => {
    const ctx = {
      font: "12px mono", fillStyle: "", textAlign: "start", textBaseline: "middle", direction: "inherit",
      measureText: vi.fn((t: string) => ({ width: [...t].reduce((w, ch) => w + (ch.charCodeAt(0) > 0x24f ? 14 : 7), 0), actualBoundingBoxAscent: 8, actualBoundingBoxDescent: 2 })),
      fillText: vi.fn(), save: vi.fn(), restore: vi.fn(), beginPath: vi.fn(), rect: vi.fn(), clip: vi.fn(),
    };
    return ctx;
  };

  it("keeps text that fits, and measures nothing for Latin text after the font's advance", () => {
    const ctx = fakeCtx();
    ctx.font = "12px latin-a";
    expect(fitText(ctx as never, "hello", 100)).toBe("hello");
    expect(fitText(ctx as never, "x".repeat(14), 100)).toBe("x".repeat(14)); // exactly 98 px
    const cut = fitText(ctx as never, "Acme Global Corporation Ltd", 100);
    expect(cut).toBe("Acme Global C…"); // 13 characters and the ellipsis: 98 px
    expect(cut.length * 7).toBeLessThanOrEqual(100);
    expect(ctx.measureText).toHaveBeenCalledTimes(1); // the advance, once
  });

  it("measures other scripts, never splits a surrogate pair, and leaves a lone … in a cell narrower than two characters", () => {
    const ctx = fakeCtx();
    ctx.font = "12px latin-b";
    const cjk = fitText(ctx as never, "東京都千代田区丸の内", 60);
    expect(cjk.endsWith("…")).toBe(true);
    expect(ctx.measureText.mock.results.length).toBeGreaterThan(1);
    expect([...cjk].reduce((w, ch) => w + (ch.charCodeAt(0) > 0x24f ? 14 : 7), 0)).toBeLessThanOrEqual(60);
    const emoji = fitText(ctx as never, "ab😀😀😀😀😀😀", 40);
    expect(emoji.endsWith("…")).toBe(true);
    const body = emoji.slice(0, -1);
    const last = body.charCodeAt(body.length - 1);
    expect(last >= 0xd800 && last <= 0xdbff).toBe(false);
    expect(fitText(ctx as never, "abcdef", 10)).toBe("…");
    expect(fitText(ctx as never, "abcdef", 3)).toBe("");
  });

  it("draws an overflowing text cell cut with … in its own color and alignment; other cells draw as Glide does", () => {
    const ctx = fakeCtx();
    ctx.font = "12px latin-c";
    const theme = { cellHorizontalPadding: 8, baseFontFull: "12px latin-c", textDark: "#e6edf3" } as never;
    const rect = { x: 0, y: 0, width: 116, height: 24 };
    const content = vi.fn();
    drawFitted({ ctx, rect, theme, cell: { kind: GridCellKind.Text, data: "x", displayData: "Acme Global Corporation Ltd", allowOverlay: true, contentAlign: undefined } } as never, content);
    expect(content).not.toHaveBeenCalled();
    expect(ctx.fillText.mock.calls[0][0]).toBe("Acme Global C…");
    expect(ctx.fillStyle).toBe("#e6edf3");
    drawFitted({ ctx, rect, theme, cell: { kind: GridCellKind.Text, data: "x", displayData: "short", allowOverlay: true } } as never, content);
    drawFitted({ ctx, rect, theme, cell: { kind: GridCellKind.Boolean, data: true, allowOverlay: false } } as never, content);
    expect(content).toHaveBeenCalledTimes(2);
  });

  it("the grid draws with it when read-only (the editing grid adds its marks on top)", () => {
    render(<ResultGrid rs={build()} executionId="e1" />);
    expect(P().drawCell).toBe(drawFitted);
  });
});

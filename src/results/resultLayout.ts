// How a run with several result sets is laid out in the Results panel (§12 bottom panel): one set at a time behind the
// ledger chips (Tabs, the default), every set one under another (Stacked, as SSMS shows them), or up to three beside
// each other (Side by side). The choice is the user's: a default in Settings, and each tab's own choice from the
// Results tool row or the palette. Pure functions over tab state; no React.
import { useSettings } from "../store/settings";
import { useTabs, type TabState } from "../store/tabs";
import { toast } from "../store/ui";
import type { PanelWidth } from "../app/panelKit";
import { GRID_HEADER_PX, GRID_ROW_PX } from "./gridScale";

export type ResultLayout = "tabs" | "stacked" | "side";
export const RESULT_LAYOUTS: readonly ResultLayout[] = ["tabs", "stacked", "side"];
export const LAYOUT_LABEL: Record<ResultLayout, string> = { tabs: "Tabs", stacked: "Stacked", side: "Side by side" };

/** Side by side shows at most this many sets: more columns would be too narrow to read (each wants ~300 px). */
export const MAX_SIDE = 3;
/** Pinned sets (from any kept run) stay on screen across runs; two, so side by side keeps a column for the new run. */
export const MAX_PINS = 2;
/**
 * Stacked mounts at most this many grids at once (the panes near the view; the others are placeholders of the same
 * height). Each mounted grid holds its own canvases, so 500 result sets must never mean 500 grids.
 */
export const MAX_MOUNTED = 8;
/** Sets after the first 20 start collapsed in Stacked: a run of 500 SELECTs stays a list the user can scan. */
export const COLLAPSE_AFTER = 20;

export const SIDE_TOO_NARROW = "Side by side needs a wider panel: showing Stacked until there is room";

/** A layout read back from the workspace file (untrusted): unknown values are dropped, so the default applies. */
export function cleanLayout(raw: unknown): ResultLayout | undefined {
  return typeof raw === "string" && (RESULT_LAYOUTS as readonly string[]).includes(raw) ? (raw as ResultLayout) : undefined;
}

/** The tab's chosen layout: its own, else the Settings default. */
export function chosenLayout(t: Pick<TabState, "resultLayout">, fallback: ResultLayout = useSettings.getState().resultLayout): ResultLayout {
  return t.resultLayout ?? fallback;
}

/** What the panel draws for a chosen layout at its width: side by side falls back to Stacked when narrow, and says so. */
export function shownLayout(chosen: ResultLayout, width: PanelWidth): { layout: ResultLayout; why?: string } {
  if (chosen === "side" && width === "narrow") return { layout: "stacked", why: SIDE_TOO_NARROW };
  return { layout: chosen };
}

/** Sets the tab's layout (toolbar, palette); leaving Stacked / Side by side also ends a maximized set. */
export function setResultLayout(tabId: string, layout: ResultLayout) {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return;
  useTabs.getState().update(tabId, { resultLayout: layout, resultMaximized: undefined, bottomTab: "results", bottomOpen: true });
}

const ROW_PX = GRID_ROW_PX;
const HEADER_PX = GRID_HEADER_PX;
/** Room for the grid's horizontal scrollbar, so a set that fits its rows never needs a vertical one. */
const SCROLLBAR_PX = 14;
export const MIN_PANE_ROWS = 3;

/**
 * A stacked grid's height: its rows, between 3 rows and 60 % of the panel. From the full row count, never the
 * filtered one, so a filter never resizes a pane and moves the ones below it under the pointer.
 */
export function fitHeight(rowCount: number, available: number): number {
  const min = HEADER_PX + MIN_PANE_ROWS * ROW_PX + SCROLLBAR_PX;
  const max = Math.max(min, Math.floor(available * 0.6));
  return Math.min(max, Math.max(min, HEADER_PX + rowCount * ROW_PX + SCROLLBAR_PX));
}

/** The limits a stacked pane can be resized within (its splitter's range). */
export function heightRange(available: number): { min: number; max: number } {
  const min = HEADER_PX + MIN_PANE_ROWS * ROW_PX + SCROLLBAR_PX;
  return { min, max: Math.max(min, Math.floor(available * 0.9)) };
}

/** One step of the splitter's arrow keys: a row. */
export const HEIGHT_STEP = ROW_PX;

/**
 * The run's sets that side by side shows, in `slots` columns: the tab's choice (sets that still exist, no repeats),
 * filled with the next sets in order. The selected set is always one of them: when it is not, it takes the last column.
 */
export function sideColumns(sets: readonly number[], chosen: readonly number[] | undefined, selected: number | undefined, slots: number): number[] {
  const n = Math.max(0, Math.min(slots, sets.length));
  const out: number[] = [];
  for (const i of chosen ?? []) if (sets.includes(i) && !out.includes(i) && out.length < n) out.push(i);
  for (const i of sets) if (out.length < n && !out.includes(i)) out.push(i);
  if (selected !== undefined && sets.includes(selected) && !out.includes(selected) && out.length) out[out.length - 1] = selected;
  return out;
}

/**
 * The panes Stacked mounts: the ones it must keep (the selected set, a set with staged changes), then the visible ones
 * in order, up to `cap`. Without visibility information (no IntersectionObserver yet) the first ones in order.
 */
export function mountedPanes(order: readonly string[], visible: ReadonlySet<string> | null, keep: ReadonlySet<string>, cap = MAX_MOUNTED): Set<string> {
  const out = new Set<string>([...keep].filter((k) => order.includes(k)));
  for (const k of order) {
    if (out.size >= Math.max(cap, keep.size)) break;
    if (!visible || visible.has(k)) out.add(k);
  }
  return out;
}

export type Pin = NonNullable<TabState["pins"]>[number];
export const samePin = (a: Pin, b: Pin) => a.executionId === b.executionId && a.resultIndex === b.resultIndex;

/** Why pinning is unavailable in this layout, else null. */
export function pinUnavailable(layout: ResultLayout): string | null {
  return layout === "tabs" ? "Pinned result sets show in the Stacked and Side by side layouts" : null;
}

/** Pins or unpins a set of a kept run. Returns why it could not be pinned (two are pinned already), else null. */
export function togglePin(tabId: string, pin: Pin): string | null {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return null;
  const pins = t.pins ?? [];
  if (pins.some((p) => samePin(p, pin))) {
    useTabs.getState().update(tabId, { pins: pins.filter((p) => !samePin(p, pin)) });
    return null;
  }
  if (!t.executions.includes(pin.executionId)) return "That run is no longer kept";
  if (pins.length >= MAX_PINS) return `Unpin a result set first: up to ${MAX_PINS} stay pinned`;
  useTabs.getState().update(tabId, { pins: [...pins, pin] });
  return null;
}

/** The palette's "Pin or unpin the shown result set": says why when it cannot. */
export function pinShown(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  if (!t?.activeExecutionId) return toast("info", "Run a query first.");
  const why = pinUnavailable(chosenLayout(t)) ?? togglePin(tabId, { executionId: t.activeExecutionId, resultIndex: t.resultIndex ?? 0 });
  if (why) toast("info", `${why}.`);
}

/** The palette's "Maximize or restore the shown result set" (Stacked and Side by side only). */
export function toggleResultMaximized(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return;
  if (chosenLayout(t) === "tabs") return toast("info", "Tabs already shows one result set at a time.");
  useTabs.getState().update(tabId, { resultMaximized: !t.resultMaximized || undefined, bottomTab: "results", bottomOpen: true });
}

/** Pins of the runs a tab still keeps; the others are dropped (their rows are gone). */
export function keptPins(pins: TabState["pins"], executions: readonly string[]): TabState["pins"] {
  if (!pins?.length) return pins;
  const kept = pins.filter((p) => executions.includes(p.executionId));
  return kept.length === pins.length ? pins : kept.length ? kept : undefined;
}

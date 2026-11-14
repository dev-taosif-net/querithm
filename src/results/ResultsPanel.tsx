// The Results view of the bottom panel (§12 bottom panel): the statement ledger and the tools on one 28 px row, the
// notice line, the grid (or chart, error card, run summary, comparison) and a 22 px footer about the shown result set.
import { BusyButtonContent, LoadingStatus } from "../app/LoadingStatus";
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { createPortal } from "react-dom";
import type { TabState } from "../store/tabs";
import { useTabs } from "../store/tabs";
import { useExecutions, type ExecutionView } from "../store/executions";
import { executionData, type ArrowResultSet } from "./model";
import type { CopyFormat, ResultGridHandle, SelectionStats } from "./ResultGrid";
import { formatStatNumber } from "./selectionStats";
import { exportAllFormats, exportAllResultSets, exportFormats, exportResultSet } from "./export";
import { setBottomTab } from "../app/actions";
import { ask, toast } from "../store/ui";
import { useDismiss } from "../app/dismiss";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { knownEngine } from "../engines";
import { MAX_DUPLICATE, stagings, type GridStaging } from "./staging";
import { applyChanges, reviewChanges, scheduleTokens, stagingFor } from "./gridEdit";
import { setChartSpec, setResultChart } from "./resultViews";
import type { ChartSpec } from "../charts/chartSpec";
import { Icon, type IconName } from "../app/icons";
import type { MenuEntry } from "../app/ToolbarMenu";
import { Chips, MenuButton, PanelFooter, PanelIconButton, PanelRow, Seg, SplitButton, usePanelWidth, type ChipModel } from "../app/panelKit";
import { isFinal, isLatestRun, ledgerItems, lineOf, previousRun, selectPatch, selectedItem, type LedgerItem } from "./ledger";
import { RunNotice } from "./RunNotice";
import { revealEditorLine } from "./reveal";
import { useThrottledVersion } from "./useThrottledVersion";
import { useSettings } from "../store/settings";
import { chosenLayout, LAYOUT_LABEL, MAX_SIDE, pinUnavailable, samePin, setResultLayout, shownLayout, sideColumns, togglePin, type ResultLayout } from "./resultLayout";
import { SideSets, StackedSets, paneTitle, type PaneModel, type StackItem } from "./ResultLayouts";

// The cell viewer side panel (Phase C2) loads the first time a cell is opened in it.
const CellViewerPanel = lazy(() => import("./viewers/CellViewerPanel").then((m) => ({ default: m.CellViewerPanel })));
// The chart view (uPlot, the chart worker) loads the first time a result set is shown as a chart.
const ChartView = lazy(() => import("../charts/ChartView").then((m) => ({ default: m.ChartView })));

// The canvas grid loads with the first result set, not at startup.
const ResultGrid = lazy(() => import("./ResultGrid").then((m) => ({ default: m.ResultGrid })));

const EMPTY = { subscribe: () => () => {}, getVersion: () => 0 };

export function useExecutionData(executionId?: string) {
  const data = executionId ? executionData.get(executionId) : undefined;
  const src = data ?? EMPTY;
  useSyncExternalStore(src.subscribe, src.getVersion);
  return data;
}

// One formatter for every number the panel writes: toLocaleString() builds a new one per call, which was the top cost
// of drawing 500 chips and captions (CPU profile of §14 metric 37, 2026-11-14: ~650 ms of a Tabs → Stacked switch run).
const NUMBER = new Intl.NumberFormat();
const fmt = (n: number) => NUMBER.format(n);
const rowsText = (n: number) => `${fmt(n)} ${n === 1 ? "row" : "rows"}`;

/** A ledger item as a chip. */
export function chipOf(item: LedgerItem): ChipModel {
  if (item.kind === "set") {
    const state = item.interrupted ? " · stopped" : item.truncated ? " · limit" : item.complete ? "" : "…";
    const delta = item.delta ? (item.delta > 0 ? `+${fmt(item.delta)}` : `−${fmt(-item.delta)}`) : undefined;
    return {
      key: item.key, n: String(item.resultIndex + 1), icon: "results", text: `${rowsText(item.rows)}${state}`, delta,
      aria: `Result set ${item.resultIndex + 1}, ${rowsText(item.rows)}${item.truncated ? ", row limit reached" : item.complete ? "" : ", still loading"}${delta ? `, ${delta} since the previous run` : ""}`,
    };
  }
  const line = lineOf(item.message);
  return {
    key: item.key, n: "!", icon: "errorCircle", text: `Error${line ? ` · line ${line}` : ""}`, error: true,
    aria: `Error ${item.errorIndex + 1}${line ? `, line ${line}` : ""}: ${item.message.message.split("\n")[0].slice(0, 120)}`, title: item.message.message,
  };
}

export function ResultsPanel({ tab }: { tab: TabState }) {
  const executionId = tab.activeExecutionId;
  const view = useExecutions((s) => (executionId ? s.views[executionId] : undefined));
  const data = executionId ? executionData.get(executionId) : undefined;
  // The chrome follows a streaming result at most 4 times a second; the grid follows every batch itself.
  useThrottledVersion(data);
  const prevId = previousRun(tab, executionId);
  const prevView = useExecutions((s) => (prevId ? s.views[prevId] : undefined));
  const items = ledgerItems(view, data, prevId ? { view: prevView, data: executionData.get(prevId) } : undefined);
  const item = selectedItem(items, tab);
  const rs = item?.kind === "set" ? data?.byIndex(item.resultIndex) : undefined;
  // Sorting and filtering notify the shown set itself (not the execution): follow it too, at the same pace.
  useThrottledVersion(rs);
  const latest = isLatestRun(tab);
  const width = usePanelWidth();

  const [filter, setFilter] = useState("");
  const [stats, setStats] = useState<SelectionStats | null>(null);
  /** The cell open in the side viewer: its result set (`key`), view row and column, and the row order they refer to. */
  const [viewer, setViewer] = useState<{ key: string; row: number; col: number; order: number } | null>(null);
  const handle = useRef<ResultGridHandle | null>(null);
  const engine = knownEngine(useProfiles((st) => st.profiles.find((x) => x.id === tab.profileId)?.engine));
  const edit = rs ? view?.edits?.[rs.index] : undefined;
  const key = rs ? `${executionId}:${rs.index}` : null;
  // The sidecar forgets a run's edit record when its session closes (disconnect, engine restart, profile switch): the
  // set is read-only from then on. Changes staged before that stay visible so they can be discarded, never hidden.
  const sessionLive = !!view?.sessionId && view.sessionId === tab.sessionId;
  const stagedBefore = key ? stagings.get(key)?.hasChanges : false;
  // An interrupted set never completes and the sidecar drops its edit record, so it stays read-only; an older run too.
  const editable = rs && executionId && latest && (sessionLive || stagedBefore) && edit?.editable && !rs.interrupted ? stagingFor(executionId, rs, engine) : undefined;
  const chart = rs ? tab.charts?.[rs.index] : undefined;
  // Results open read-only; Edit (shown only when the set is editable) turns the grid into an editor. Staged changes
  // keep it in edit mode so they never sit hidden behind a read-only grid.
  const [editKey, setEditKey] = useState<string | null>(null);
  // Edit mode belongs to the set it was turned on for, while it is shown as a grid: showing another set, another run or
  // the chart ends it, so coming back always starts read-only (clicked Edit once must not mean editable forever).
  if (editKey !== null && (editKey !== key || !editable || chart?.view === "chart")) setEditKey(null);
  useSyncExternalStore(editable?.subscribe ?? EMPTY.subscribe, editable?.getVersion ?? EMPTY.getVersion);
  const editing = !!editable && (editKey === key || editable.hasChanges);
  // Staged edits never hide behind a chart: the grid stays until they are applied or discarded.
  const chartOn = chart?.view === "chart" && !editable?.hasChanges;
  const staging = editing && !chartOn ? editable : undefined;
  const shownIndex = rs?.index;
  const compareRs = tab.compare ? executionData.get(tab.compare.executionId)?.byIndex(tab.compare.resultIndex) : undefined;
  const comparing = !!compareRs && !!rs && !staging && !chartOn;
  // Stable, so the memoized chart view does not re-render for every arriving batch.
  const onViewCell = useCallback((row: number, col: number) => key && rs && setViewer({ key, row, col, order: rs.viewVersion }), [key, rs]);
  const onSpecChange = useCallback((spec: ChartSpec) => shownIndex !== undefined && setChartSpec(tab.id, shownIndex, spec), [tab.id, shownIndex]);

  // Several result sets: Tabs (one at a time behind the chips), Stacked or Side by side — the tab's choice, else the
  // Settings default. One set (and no pinned set) always shows alone, as does a comparison or a maximized set.
  const defaultLayout = useSettings((st) => st.resultLayout);
  const chosen = chosenLayout(tab, defaultLayout);
  const { layout: drawn, why: layoutWhy } = shownLayout(chosen, width);
  const sets = data?.resultSets ?? [];
  const runName = (id: string) => (id === executionId ? "This run" : `Run ${tab.executions.indexOf(id) + 1}`);
  const pinPanes: PaneModel[] = [];
  for (const pin of tab.pins ?? []) {
    if (pin.executionId === executionId) continue; // a pinned set of the shown run is shown where it belongs
    const prs = executionData.get(pin.executionId)?.byIndex(pin.resultIndex);
    if (prs) pinPanes.push({ key: `${pin.executionId}:${prs.index}`, executionId: pin.executionId, rs: prs, current: false, pinned: true, run: runName(pin.executionId) });
  }
  const isPinned = (id: string, i: number) => !!tab.pins?.some((x) => samePin(x, { executionId: id, resultIndex: i }));
  const paneOf = (s: ArrowResultSet): PaneModel => ({ key: `${executionId}:${s.index}`, executionId: executionId!, rs: s, current: true, pinned: isPinned(executionId!, s.index) });
  const multi = sets.length + pinPanes.length >= 2;
  const layout: ResultLayout = !multi || !!(tab.compare && compareRs) || (tab.resultMaximized && chosen !== "tabs") ? "tabs" : drawn;
  const maximized = multi && chosen !== "tabs" && !!tab.resultMaximized;
  const [sideDiff, setSideDiff] = useState(false);
  const sideIdx = layout === "side" ? sideColumns(sets.map((x) => x.index), tab.sideSets, rs?.index, Math.max(1, MAX_SIDE - pinPanes.length)) : [];
  const sideCols: PaneModel[] = layout === "side"
    ? [...pinPanes.slice(0, MAX_SIDE - 1), ...sideIdx.map((i) => data?.byIndex(i)).filter((x): x is ArrowResultSet => !!x).map(paneOf)]
    : [];
  const sideDiffOn = layout === "side" && sideDiff && sideCols.length === 2;
  const sideOther = sideDiffOn && rs ? sideCols.find((c) => c.rs !== rs)?.rs : undefined;
  const sideKey = sideIdx.join(",");
  // The selected set is always in a column; the columns are kept so selecting another one never moves the rest.
  useEffect(() => {
    if (layout === "side" && rs && sideKey && sideKey !== (tab.sideSets ?? []).join(",")) useTabs.getState().update(tab.id, { sideSets: sideKey.split(",").map(Number) });
  }, [layout, rs, sideKey, tab.id, tab.sideSets]);
  // Charts of panes other than the selected one keep their own spec setter, so a batch does not re-render them.
  const specSetters = useRef(new Map<string, (spec: ChartSpec) => void>());
  const specSetter = (i: number) => {
    const k = `${tab.id}:${i}`;
    let f = specSetters.current.get(k);
    if (!f) specSetters.current.set(k, (f = (spec: ChartSpec) => setChartSpec(tab.id, i, spec)));
    return f;
  };

  useEffect(() => {
    setStats(null);
  }, [executionId, key]);
  useEffect(() => {
    const t = setTimeout(() => rs && rs.filter !== filter && rs.setFilter(filter), 200);
    return () => clearTimeout(t);
  }, [filter, rs]);

  const select = (k: string, e: { ctrl: boolean }) => {
    const it = items.find((i) => i.key === k);
    if (!it) return;
    // Ctrl+click a second result set: show it beside the shown one (compare).
    if (e.ctrl && it.kind === "set" && rs && it.resultIndex !== rs.index && executionId) {
      useTabs.getState().update(tab.id, { compare: { executionId, resultIndex: it.resultIndex } });
      return;
    }
    useTabs.getState().update(tab.id, { ...selectPatch(it), ...(tab.compare && it.kind === "set" && tab.compare.executionId === executionId && tab.compare.resultIndex === it.resultIndex ? { compare: undefined } : {}) });
  };

  if (!view) return <Empty text="Run a query (F5 or Ctrl+Enter) to see results here." />;

  const selectPane = (pane: PaneModel) => pane.current && useTabs.getState().update(tab.id, { resultIndex: pane.rs.index, ledgerError: undefined });
  const pinToggle = (id: string, i: number) => {
    const why = pinUnavailable(chosen) ?? togglePin(tab.id, { executionId: id, resultIndex: i });
    if (why) toast("info", `${why}.`);
  };
  const exportName = (i: number) => `${tab.title.replace(/\.sql$/, "")}-result${i + 1}`;
  const exportItems = (s: ArrowResultSet): MenuEntry[] => exportFormats.map(({ format: f, label }) => ({
    label,
    run: () => {
      void exportResultSet(s, f, exportName(s.index))
        .then(() => s.truncated && toast("warn", `Exported the first ${fmt(s.rowCount)} rows: the row limit cut this result set. Fetch all rows to export everything.`))
        .catch((e) => toast("error", String(e)));
    },
  }));
  const exportAllItems = (): MenuEntry[] => sets.length < 2 ? [] : [
    "sep" as const,
    ...exportAllFormats.map(({ format: f, label }) => ({
      label: `All ${sets.length} result sets: ${label}`,
      run: () => void exportAllResultSets(sets, f, tab.title.replace(/\.sql$/, "")).catch((e) => toast("error", String(e))),
    })),
  ];

  const copy = async (format: CopyFormat | boolean) => {
    try {
      const text = handle.current?.copySelection(format);
      if (text == null) return toast("info", "Select cells, rows or columns first.");
      await navigator.clipboard.writeText(text);
      const msg = format === "in" ? "Copied as IN clause." : format === "markdown" ? "Copied as Markdown table." : format === true || format === "headers" ? "Copied with headers." : "Copied to clipboard.";
      // A cut-off result set: the copy holds the rows that were fetched, and the user is told so.
      toast(rs?.truncated ? "warn" : "success", rs?.truncated ? `${msg} The row limit cut this result set; the copy holds the fetched rows only.` : msg);
    } catch (error) {
      toast("error", error instanceof Error ? error.message : "Could not copy the selection.");
    }
  };

  const chipMenu = (k: string): MenuEntry[] => {
    const it = items.find((i) => i.key === k);
    if (!it) return [];
    if (it.kind === "error") {
      return [
        { label: "Show", run: () => select(k, { ctrl: false }) },
        ...(it.message.editorLine ? [{ label: `Go to line ${it.message.editorLine}${it.message.editorColumn ? `, column ${it.message.editorColumn}` : ""}`, run: () => revealEditorLine(tab.id, it.message.editorLine, it.message.editorColumn) }] : []),
        { label: "Show in Messages", run: () => setBottomTab(tab.id, "messages") },
        { label: "Copy error", run: () => void navigator.clipboard.writeText(it.message.message).then(() => toast("success", "Copied the error.")) },
      ];
    }
    return [
      { label: "Show", run: () => select(k, { ctrl: false }) },
      {
        label: "Compare with the shown result set", hint: "Ctrl+click",
        unavailable: !rs || it.resultIndex === rs.index ? "Pick another result set: this one is shown" : undefined,
        run: () => executionId && useTabs.getState().update(tab.id, { compare: { executionId, resultIndex: it.resultIndex } }),
      },
      {
        label: executionId && isPinned(executionId, it.resultIndex) ? "Unpin" : "Pin (stays on screen across runs)",
        unavailable: pinUnavailable(chosen) ?? undefined,
        run: () => executionId && pinToggle(executionId, it.resultIndex),
      },
      ...(chosen !== "tabs" && multi
        ? [{ label: "Show alone (maximize)", run: () => useTabs.getState().update(tab.id, { resultIndex: it.resultIndex, ledgerError: undefined, resultMaximized: true }) }]
        : []),
    ];
  };

  const lockedWhy = editable?.hasChanges ? "Apply or discard the staged changes to switch result sets" : undefined;

  /** What a pane shows: the selected set gets the tools' grid handle, staging and statistics; the others are views. */
  const renderBody = (p: PaneModel, selected: boolean) => {
    const ch = p.current ? tab.charts?.[p.rs.index] : undefined;
    const asChart = selected ? chartOn : ch?.view === "chart";
    const title = exportName(p.rs.index);
    if (asChart) {
      return (
        <Suspense fallback={<Empty text="Loading the chart…" />}>
          <div className="flex-1 min-w-0 flex flex-col"><ChartView key={p.key} rs={p.rs} spec={ch?.spec} onSpecChange={selected ? onSpecChange : specSetter(p.rs.index)} title={title} /></div>
        </Suspense>
      );
    }
    const other = sideDiffOn ? sideCols.find((c) => c !== p && c.key !== p.key)?.rs : undefined;
    return (
      <Suspense fallback={<div className="flex-1 flex items-center justify-center"><LoadingStatus label="Loading the grid…" /></div>}>
        <ResultGrid
          key={p.key} engine={engine} rs={p.rs} executionId={p.executionId}
          {...(selected ? {
            handleRef: handle, staging,
            onStaged: staging ? () => scheduleTokens(executionId!, staging) : undefined,
            onApply: staging ? () => void applyChanges(tab.id, executionId!, staging) : undefined,
            onStatsChange: setStats, onViewCell,
          } : {})}
          tone={other ? (r, c) => (differs(p.rs, other, r, c) ? "changed" : null) : undefined}
        />
        {!p.rs.complete && p.rs.rowCount === 0 && <div className="absolute inset-0 z-10 flex items-center justify-center pointer-events-none"><LoadingStatus label="Waiting for rows…" startedAt={view.startedAt} /></div>}
      </Suspense>
    );
  };

  /** A pane caption's buttons: pin, show alone (the shown run's sets), and export. */
  const paneTools = (p: PaneModel) => {
    const n = p.rs.index + 1;
    const pinned = isPinned(p.executionId, p.rs.index);
    return (
      <>
        <PanelIconButton icon="pin" label={pinned ? `Unpin result set ${n}` : `Pin result set ${n} (stays on screen across runs)`} pressed={pinned} onClick={() => pinToggle(p.executionId, p.rs.index)} />
        {p.current && (
          <PanelIconButton icon="maximize" label={`Show result set ${n} alone`} onClick={() => useTabs.getState().update(tab.id, { resultIndex: p.rs.index, ledgerError: undefined, resultMaximized: true })} />
        )}
        <MenuButton label={`Export result set ${n}`} title={`Export result set ${n}`} icon="download" className="!h-[20px] !px-1" items={() => exportItems(p.rs)} />
      </>
    );
  };

  /** A side by side column's caption: which set of the run it shows (picking one already in a column swaps them). */
  const sidePicker = (p: PaneModel, slot: number) => (
    <MenuButton
      label={`Column ${slot + 1}: ${paneTitle(p)}`}
      title="Choose the result set this column shows"
      className="!h-[20px] !px-1 !border-transparent min-w-0"
      items={() => sets.map((x) => ({
        label: `Result set ${x.index + 1}`, detail: rowsText(x.rowCount), checked: x.index === p.rs.index,
        unavailable: lockedWhy,
        run: () => {
          const cols = [...sideIdx];
          const at = cols.indexOf(x.index);
          if (at >= 0) cols[at] = cols[slot];
          cols[slot] = x.index;
          useTabs.getState().update(tab.id, { sideSets: cols, resultIndex: x.index, ledgerError: undefined });
        },
      }))}
    >
      <span className={`truncate ${p.key === key ? "font-semibold" : ""}`}>{paneTitle(p)}</span>
    </MenuButton>
  );

  /** Stacked: pinned sets first, then the run's sets and errors in the ledger's order. */
  const stackItems = (): StackItem[] => [
    ...pinPanes.map((p): StackItem => ({ kind: "pane", pane: p })),
    ...items.flatMap((it): StackItem[] => {
      if (it.kind === "error") return [{ kind: "error", key: `err:${it.errorIndex}`, node: <InlineError tab={tab} item={it} selected={item === it} onShow={() => select(it.key, { ctrl: false })} /> }];
      const s = data?.byIndex(it.resultIndex);
      return s ? [{ kind: "pane", pane: paneOf(s) }] : [];
    }),
  ];

  const ledger = (
    <Chips
      label="Result sets and errors"
      chips={items.map(chipOf)}
      selected={item?.key}
      compared={tab.compare && tab.compare.executionId === executionId ? `s${tab.compare.resultIndex}` : undefined}
      onSelect={select}
      menuFor={chipMenu}
      locked={lockedWhy}
    />
  );

  const filterBox = rs && (
    <FilterField rs={rs} filter={filter} setFilter={setFilter} compact={width === "narrow"} />
  );

  // The layout switch (only with two or more sets to lay out), side by side's Differences and Restore for a
  // maximized set. They lead the row, left of Grid / Chart, so they stay put while the tools after them come and
  // go (Copy, Filter and Edit hide for a chart). Hidden while editing: the edit row replaces the tools, and the
  // ledger is locked anyway.
  const layoutTools = multi ? (
    <>
      <Seg
        label="Lay out the result sets"
        value={chosen}
        compact
        options={[
          { value: "tabs", label: "Tabs: one result set at a time", icon: "layoutTabs" },
          { value: "stacked", label: "Stacked: one under another", icon: "layoutStacked" },
          { value: "side", label: "Side by side", icon: "split", unavailable: layoutWhy },
        ]}
        onChange={(v) => setResultLayout(tab.id, v)}
      />
      {layout === "side" && sideCols.length === 2 && (
        <button
          type="button" className="q-btn h-[22px] px-2 shrink-0" aria-pressed={sideDiff} data-q-side-diff
          title="Tint the cells that differ between the two columns (rows by position)"
          onClick={() => setSideDiff(!sideDiff)}
        >
          <Icon name="compare" size={13} strokeWidth={2} />
          {width !== "narrow" && "Differences"}
        </button>
      )}
      {maximized && (
        <button type="button" className="q-btn h-[22px] px-2 shrink-0" title={`Back to ${LAYOUT_LABEL[chosen]} (Esc)`} data-q-layout-restore onClick={() => useTabs.getState().update(tab.id, { resultMaximized: undefined })}>
          <Icon name="restore" size={13} strokeWidth={2} />
          {width !== "narrow" && `Back to ${LAYOUT_LABEL[chosen]}`}
        </button>
      )}
    </>
  ) : undefined;

  const tools = rs && staging ? (
    <EditTools tabId={tab.id} executionId={executionId!} rs={rs} staging={staging} handle={handle} filterBox={filterBox} onDone={() => setEditKey(null)} />
  ) : rs ? (
    <>
      {layoutTools && <>{layoutTools}<span aria-hidden className="w-px h-4 bg-line shrink-0" /></>}
      <Seg
        label="Show the result as"
        value={chartOn ? "chart" : "grid"}
        compact={width === "narrow"}
        options={[
          { value: "grid", label: "Grid", icon: "table", title: "Grid (Ctrl+1)" },
          { value: "chart", label: "Chart", icon: "chart", title: "Chart (Ctrl+2)", unavailable: editable?.hasChanges ? "Apply or discard the staged changes to show a chart" : undefined },
        ]}
        onChange={(v) => !(v === "chart" && editable?.hasChanges) && setResultChart(tab.id, rs.index, { view: v })}
      />
      {!chartOn && filterBox}
      {!chartOn && <EditButton rs={rs} editable={!!editable} latest={latest} sessionLive={sessionLive} reason={edit?.reason} onEdit={() => setEditKey(key)} compact={width === "narrow"} />}
      <span aria-hidden className="w-px h-4 bg-line shrink-0" />
      {!chartOn && width !== "narrow" && (
        <SplitButton
          face="Copy" icon="copy" title="Copy the selection (Ctrl+C in the grid)" onFace={() => void copy(false)} menuLabel="Copy as"
          items={() => [
            { label: "Copy with headers", run: () => void copy(true) },
            { label: "Copy as IN (…) clause", run: () => void copy("in") },
            { label: "Copy as Markdown table", run: () => void copy("markdown") },
          ]}
        />
      )}
      <MenuButton
        label={chartOn ? "Export rows" : "Export"}
        title="Save all rows as a file"
        icon="download"
        items={() => [
          ...(width === "narrow" && !chartOn
            ? [{ label: "Copy", hint: "Ctrl+C", run: () => void copy(false) }, { label: "Copy with headers", run: () => void copy(true) }, "sep" as const]
            : []),
          ...exportItems(rs),
          ...exportAllItems(),
        ]}
      >
        {width === "narrow" ? undefined : chartOn ? "Export rows" : "Export"}
      </MenuButton>
    </>
  ) : multi ? layoutTools : undefined;

  return (
    <div
      className="flex-1 flex flex-col min-h-0" data-q-results data-q-result-layout={layout}
      onKeyDown={(e) => {
        // Esc leaves a maximized set, unless it belongs to a field or an open cell editor.
        if (e.key !== "Escape" || !maximized || e.defaultPrevented || (e.target as HTMLElement).closest?.("input, textarea, select, [contenteditable], [role=dialog], [role=menu]")) return;
        e.preventDefault();
        useTabs.getState().update(tab.id, { resultMaximized: undefined });
      }}
    >
      <PanelRow label="Result tools" ledger={ledger} tools={tools} fill={!!staging} />
      {staging && <EnvRule profileId={tab.profileId} />}
      <RunNotice tab={tab} view={view} data={data} />
      <div key={executionId ?? "empty"} className="flex-1 min-h-0 flex">
        {layout === "stacked" ? (
          <StackedSets
            items={stackItems()}
            chartKeys={new Set(sets.filter((x) => (x === rs ? chartOn : tab.charts?.[x.index]?.view === "chart")).map((x) => `${executionId}:${x.index}`))}
            selectedKey={item?.kind === "error" ? `err:${item.errorIndex}` : key ?? undefined}
            locked={lockedWhy}
            renderBody={renderBody}
            onSelect={selectPane}
            tools={paneTools}
          />
        ) : layout === "side" && item?.kind !== "error" ? (
          <SideSets columns={sideCols} selectedKey={key ?? undefined} locked={lockedWhy} renderBody={renderBody} onSelect={selectPane} tools={paneTools} picker={sidePicker} />
        ) : (
        <div key={chartOn ? "chart" : "grid"} className={`flex-1 min-h-0 min-w-0 relative ${chartOn ? "flex flex-col" : "flex"}`}>
          {!rs ? (
            item?.kind === "error" ? <ErrorCard tab={tab} item={item} /> : <RunSummary tab={tab} view={view} />
          ) : chartOn ? (
            <Suspense fallback={<Empty text="Loading the chart…" />}>
              <ChartView key={`${executionId}:${rs.index}`} rs={rs} spec={chart?.spec} onSpecChange={onSpecChange} title={`${tab.title.replace(/\.sql$/, "")}-result${rs.index + 1}`} />
            </Suspense>
          ) : (
            <>
              <Pane caption={comparing ? `${latest ? "This run" : "Shown run"} · result set ${rs.index + 1}` : undefined} active>
                <Suspense fallback={<div className="flex-1 flex items-center justify-center"><LoadingStatus label="Loading the grid…" /></div>}>
                  <ResultGrid
                    key={`${executionId}:${rs.index}`} engine={engine} rs={rs} executionId={executionId!} handleRef={handle} staging={staging}
                    onStaged={staging ? () => scheduleTokens(executionId!, staging) : undefined}
                    onApply={staging ? () => void applyChanges(tab.id, executionId!, staging) : undefined}
                    onStatsChange={setStats}
                    onViewCell={onViewCell}
                    tone={comparing ? (r, c) => (differs(rs, compareRs!, r, c) ? "changed" : null) : undefined}
                  />
                  {!rs.complete && rs.rowCount === 0 && <div className="absolute inset-0 z-10 flex items-center justify-center pointer-events-none"><LoadingStatus label="Waiting for rows…" startedAt={view.startedAt} /></div>}
                </Suspense>
              </Pane>
              {comparing && (
                <Pane caption={compareCaption(tab, compareRs!)} onClose={() => useTabs.getState().update(tab.id, { compare: undefined })}>
                  <Suspense fallback={null}>
                    <ResultGrid viewKey="compare" key={`cmp:${tab.compare!.executionId}:${compareRs!.index}`} engine={engine} rs={compareRs!} executionId={tab.compare!.executionId} tone={(r, c) => (differs(compareRs!, rs, r, c) ? "changed" : null)} />
                  </Suspense>
                </Pane>
              )}
            </>
          )}
        </div>
        )}
        {rs && !chartOn && !comparing && viewer?.key === key && viewer.order === rs.viewVersion && viewer.col < rs.columns.length && (
          <Suspense fallback={null}>
            <CellViewerPanel key={`${viewer.row}:${viewer.col}`} tabId={tab.id} executionId={executionId!} rs={rs} row={viewer.row} col={viewer.col} engine={engine} onClose={() => setViewer(null)} />
          </Suspense>
        )}
      </div>
      <ResultsFooter rs={rs} item={item} view={view} stats={stats} staging={staging} comparing={comparing ? compareRs : sideOther} />
    </div>
  );
}

/** Two cells differ (compare): same view row and column, different text; a row or column only one side has differs. */
function differs(a: ArrowResultSet, b: ArrowResultSet, row: number, col: number): boolean {
  // Either side can be the shorter one (the footer count runs to the longer set's rows): read neither past its end.
  if (row >= a.visibleRows || col >= a.columns.length || row >= b.visibleRows || col >= b.columns.length) return true;
  return a.text(row, col) !== b.text(row, col);
}

function compareCaption(tab: TabState, rs: ArrowResultSet): string {
  const id = tab.compare!.executionId;
  const run = id === tab.activeExecutionId ? "This run" : `Run ${tab.executions.indexOf(id) + 1}`;
  return `${run} · result set ${rs.index + 1} · ${rowsText(rs.rowCount)} · read-only`;
}

function Pane({ caption, active, onClose, children }: { caption?: string; active?: boolean; onClose?: () => void; children: ReactNode }) {
  if (!caption) return <>{children}</>;
  return (
    <div className={`flex-1 min-w-0 flex flex-col ${onClose ? "q-side-panel-in border-l-2 border-line" : ""}`} data-q-compare-pane={onClose ? "other" : "shown"}>
      <div className={`h-[20px] px-2 flex items-center gap-2 text-[11px] text-muted bg-panel border-b border-line shrink-0 ${active ? "shadow-[inset_0_2px_0_var(--q-accent)]" : ""}`}>
        <span className="truncate">{caption}</span>
        {onClose && (
          <button type="button" className="ml-auto w-4 h-4 rounded flex items-center justify-center hover:bg-hover hover:text-fg" aria-label="Stop comparing" title="Stop comparing" onClick={onClose}>
            <Icon name="close" size={11} strokeWidth={2} />
          </button>
        )}
      </div>
      <div className="flex-1 min-h-0 relative flex">{children}</div>
    </div>
  );
}

/** The selected error, in the body (the ledger's red chip). */
function ErrorCard({ tab, item }: { tab: TabState; item: Extract<LedgerItem, { kind: "error" }> }) {
  const m = item.message;
  const head = [m.number != null ? `Msg ${m.number}` : null, m.class != null ? `Level ${m.class}` : null, m.state != null ? `State ${m.state}` : null, m.procedure ? `Procedure ${m.procedure}` : null, m.line ? `Line ${m.line}` : null]
    .filter(Boolean)
    .join(", ");
  return (
    <div className="flex-1 overflow-auto p-4" data-q-error-card>
      <div className="max-w-3xl rounded-md border border-err/40 bg-err/10 q-hl p-3 space-y-2">
        <div className="text-[12.5px] font-semibold text-err flex items-center gap-1.5">
          <Icon name="errorCircle" size={14} strokeWidth={2} />
          Error{lineOf(m) ? ` at line ${lineOf(m)}` : ""}
        </div>
        {head && <div className="q-mono text-[11.5px] text-muted">{head}</div>}
        <div className="q-mono text-[12px] text-fg whitespace-pre-wrap select-text">{m.message}</div>
        <div className="flex gap-2 pt-1">
          {m.editorLine && (
            <button type="button" className="q-btn h-[22px]" onClick={() => revealEditorLine(tab.id, m.editorLine)}>
              <Icon name="goToLine" size={12} strokeWidth={2} />Go to line {m.editorLine}
            </button>
          )}
          <button type="button" className="q-btn h-[22px]" onClick={() => setBottomTab(tab.id, "messages")}>Show in Messages</button>
          <button type="button" className="q-btn h-[22px]" onClick={() => void navigator.clipboard.writeText(m.message).then(() => toast("success", "Copied the error."))}>
            <Icon name="copy" size={12} strokeWidth={2} />Copy error
          </button>
        </div>
      </div>
    </div>
  );
}

/** Stacked: an error in place, one line between the sets around it (the full text is in its card and in Messages). */
function InlineError({ tab, item, selected, onShow }: { tab: TabState; item: Extract<LedgerItem, { kind: "error" }>; selected: boolean; onShow: () => void }) {
  const m = item.message;
  const line = lineOf(m);
  return (
    <div
      className={`mx-2 my-1 h-[26px] px-2 rounded border q-hl flex items-center gap-2 text-[11.5px] text-err min-w-0 ${selected ? "border-err bg-err/15" : "border-err/40 bg-err/10"}`}
      role="group" aria-label={`Error${line ? ` at line ${line}` : ""}`} data-q-stack-error
    >
      <Icon name="errorCircle" size={13} strokeWidth={2} />
      <span className="font-semibold shrink-0">Error{line ? ` · line ${line}` : ""}</span>
      <span className="q-mono truncate min-w-0 text-fg" title={m.message}>{m.message.split("\n")[0]}</span>
      <span className="flex-1" />
      {m.editorLine && (
        <button type="button" className="q-btn h-[20px] px-1.5 shrink-0" onClick={() => revealEditorLine(tab.id, m.editorLine, m.editorColumn)}>
          <Icon name="goToLine" size={12} strokeWidth={2} />Go to line {m.editorLine}
        </button>
      )}
      <button type="button" className="q-btn h-[20px] px-1.5 shrink-0" onClick={onShow}>Show</button>
    </div>
  );
}

/** A run without result sets: what it did, in place of an empty grid (the view never switches by itself). */
function RunSummary({ tab, view }: { tab: TabState; view: ExecutionView }) {
  if (!isFinal(view)) return <div className="flex-1 flex items-center justify-center p-4"><LoadingStatus label={view.state === "queued" ? "Queued — waiting for a free connection…" : "Executing…"} startedAt={view.startedAt} /></div>;
  const info = view.messages.filter((m) => m.severity !== "error").slice(-6);
  const head =
    view.state === "cancelled" ? "Cancelled before any result set arrived."
    : view.notRun ? "Nothing ran."
    : view.affectedRows != null ? `${fmt(view.affectedRows)} ${view.affectedRows === 1 ? "row" : "rows"} affected.`
    : "The statement returned no result set.";
  return (
    <div className="flex-1 overflow-auto p-4" data-q-run-summary>
      <div className="max-w-3xl space-y-2">
        <div className="text-[12.5px] text-fg font-semibold">{head}</div>
        {info.length > 0 && (
          <div className="q-mono text-[12px] text-fg whitespace-pre-wrap select-text space-y-0.5">
            {info.map((m, i) => (
              <div key={i} className={m.severity === "warning" ? "text-warn" : ""}>{m.message}</div>
            ))}
          </div>
        )}
        <button type="button" className="q-btn h-[22px]" onClick={() => setBottomTab(tab.id, "messages")}>Show in Messages</button>
      </div>
    </div>
  );
}

function ResultsFooter({ rs, item, view, stats, staging, comparing }: {
  rs: ArrowResultSet | undefined; item: LedgerItem | undefined; view: ExecutionView; stats: SelectionStats | null; staging?: GridStaging; comparing?: ArrowResultSet;
}) {
  useSyncExternalStore(staging?.subscribe ?? EMPTY.subscribe, staging?.getVersion ?? EMPTY.getVersion);
  if (!rs) {
    const left = item?.kind === "error" ? `Error${lineOf(item.message) ? ` · line ${lineOf(item.message)}` : ""}` : isFinal(view) ? "No result sets" : "Waiting for results…";
    return <PanelFooter left={left} />;
  }
  const shown = rs.visibleRows;
  let state = "";
  if (rs.interrupted) state = " · incomplete — execution stopped";
  else if (rs.truncated) state = " · row limit reached";
  else if (!rs.complete) state = " · streaming…";
  let left: ReactNode = (
    <span className="truncate" title={`Arrow stream · ${rs.batches.length} batch${rs.batches.length === 1 ? "" : "es"} · ${rs.columns.length} columns`}>
      Result set {rs.index + 1} · {shown !== rs.rowCount ? `${fmt(shown)} of ` : ""}{rowsText(rs.rowCount)} · {rs.columns.length} {rs.columns.length === 1 ? "column" : "columns"}{state}
    </span>
  );
  if (staging) {
    const c = staging.counts;
    const problems = staging.hasChanges ? staging.problems().length : 0;
    left = !rs.complete ? (
      <span>Editing starts when all rows have arrived…</span>
    ) : staging.hasChanges ? (
      <span className="truncate">
        {[c.updates && `${c.updates} changed`, c.inserts && `${c.inserts} new`, c.deletes && `${c.deletes} deleted`].filter(Boolean).join(" · ")}
        {problems > 0 && <span className="text-err"> · {problems} invalid</span>} · stays local until applied as one transaction
      </span>
    ) : (
      <span className="truncate">Type or double-click to edit · Ctrl+0 NULL · Alt+Insert new row · Ctrl+D duplicate · Ctrl+Delete delete · Ctrl+S apply</span>
    );
  } else if (comparing) {
    left = <CompareCount a={rs} b={comparing} />;
  }
  return <PanelFooter left={left} right={stats ? <StatsLine stats={stats} /> : undefined} />;
}

/** How many cells differ, over the first 2,000 rows (read once per version of either side, not per render). */
const COMPARE_COUNT_ROWS = 2000;
function CompareCount({ a, b }: { a: ArrowResultSet; b: ArrowResultSet }) {
  const diff = useMemo(() => {
    let n = 0;
    const rows = Math.min(Math.max(a.visibleRows, b.visibleRows), COMPARE_COUNT_ROWS);
    for (let r = 0; r < rows; r++) for (let c = 0; c < a.columns.length; c++) if (differs(a, b, r, c)) n++;
    return n;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [a, b, a.version, b.version, a.viewVersion, b.viewVersion]);
  const capped = Math.max(a.visibleRows, b.visibleRows) > COMPARE_COUNT_ROWS ? ` in the first ${fmt(COMPARE_COUNT_ROWS)} rows` : "";
  return <span className="truncate">{diff ? `${fmt(diff)} ${diff === 1 ? "cell differs" : "cells differ"}${capped}` : `No differences${capped}`} · rows compared by position</span>;
}

function StatsLine({ stats }: { stats: SelectionStats }) {
  const v = (s: string) => <b className="text-fg q-mono font-semibold">{s}</b>;
  const sep = <span aria-hidden className="text-line-strong">·</span>;
  const approx = stats.sampled ? "≈ " : "";
  return (
    <span className="flex items-center gap-1.5" data-q-selection-stats>
      <span>Count {v(fmt(stats.count))}</span>
      {stats.nullCount != null && <>{sep}<span>NULL {v(`${approx}${fmt(stats.nullCount)}`)}</span></>}
      {stats.distinct != null && <>{sep}<span>Distinct {v(`${approx}${fmt(stats.distinct)}`)}</span></>}
      {stats.numericCount > 0 && (
        <>
          {sep}<span>Sum {v(`≈ ${formatStatNumber(stats.sum)}`)}</span>
          {sep}<span>Avg {v(`≈ ${formatStatNumber(stats.avg)}`)}</span>
          {sep}<span>Min {v(formatStatNumber(stats.min))}</span>
          {sep}<span>Max {v(formatStatNumber(stats.max))}</span>
        </>
      )}
      {stats.sampled && <>{sep}<span title="Statistics read the first 50,000 cells of the selection">first 50,000 cells</span></>}
    </span>
  );
}

/** Edit / Read-only in the same place: an uneditable set says why (the edit rule teaches itself). */
function EditButton({ rs, editable, latest, sessionLive, reason, onEdit, compact }: {
  rs: ArrowResultSet; editable: boolean; latest: boolean; sessionLive: boolean; reason?: string | null; onEdit: () => void; compact: boolean;
}) {
  if (editable) {
    return (
      <button type="button" className="q-btn h-[22px] px-2 shrink-0" data-q-edit-toggle aria-label={compact ? "Edit the rows" : undefined} title="Edit the rows of this table" onClick={onEdit}>
        <Icon name="edit" size={13} strokeWidth={2} />
        {!compact && "Edit"}
      </button>
    );
  }
  const why = readOnlyWhy(rs, latest, sessionLive, reason);
  return (
    <button type="button" className="q-btn h-[22px] px-2 shrink-0" data-q-edit-toggle aria-disabled title={why} aria-label={compact ? why : undefined} aria-description={compact ? undefined : why}>
      <Icon name="lock" size={13} strokeWidth={2} />
      {!compact && "Read-only"}
    </button>
  );
}

/** Why a set is read-only, as one "Read-only: …" sentence (the sidecar's reasons may carry the prefix already). */
export function readOnlyWhy(rs: Pick<ArrowResultSet, "interrupted">, latest: boolean, sessionLive: boolean, reason?: string | null): string {
  if (!latest) return "Read-only: this is an older run — edit the latest result";
  if (rs.interrupted) return "Read-only: the run stopped before every row arrived";
  if (reason) return /^read-only\b/i.test(reason.trim()) ? reason.trim() : `Read-only: ${reason}`;
  if (!sessionLive) return "Read-only: the connection was closed since this ran — run the query again to edit";
  return "Read-only: these rows are not one table with a key";
}

/** A text button of the edit row: always in its place; unavailable ones stay focusable and say why (aria-disabled). */
function EditAction({ label, icon, title, disabled, onClick, className = "", minWidth, compact, busy, busyLabel, ...data }: {
  label: string; icon?: IconName; title: string; disabled?: string | false; onClick: () => void; className?: string; minWidth?: number; compact?: boolean; busy?: boolean; busyLabel?: string;
  [k: `data-${string}`]: string | undefined;
}) {
  return (
    <button
      type="button"
      className={`q-btn h-[22px] px-2 shrink-0 justify-center ${className}`}
      style={minWidth ? { minWidth } : undefined}
      aria-disabled={disabled ? true : undefined}
      aria-busy={busy || undefined}
      aria-label={compact && icon ? label : undefined}
      title={disabled || title}
      onClick={() => !disabled && onClick()}
      {...data}
    >
      {icon && <Icon name={icon} size={12} strokeWidth={2} />}
      {busyLabel ? <BusyButtonContent busy={!!busy} label={label} busyLabel={busyLabel} /> : !(compact && icon) && label}
    </button>
  );
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** After leaving edit mode the Edit button is where focus belongs (the button that had it is gone). */
function focusEditToggle() {
  requestAnimationFrame(() => document.querySelector<HTMLElement>("[data-q-edit-toggle]")?.focus());
}

/** The 3 px environment rule under the row while editing: the environment's color, its word in the pill beside. */
function EnvRule({ profileId }: { profileId?: string }) {
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const groups = useProfiles((s) => s.groups);
  return <div aria-hidden className="h-[3px] shrink-0" style={{ background: envColor(profile, groups) }} data-q-env-rule />;
}

/** Edit mode replaces the tool row (no extra height) and keeps the filter, which is how people find the row to change. */
function EditTools({ tabId, executionId, rs, staging, handle, filterBox, onDone }: {
  tabId: string; executionId: string; rs: ArrowResultSet; staging: GridStaging; handle: React.MutableRefObject<ResultGridHandle | null>; filterBox: ReactNode; onDone: () => void;
}) {
  useSyncExternalStore(staging.subscribe, staging.getVersion);
  const width = usePanelWidth();
  const compact = width === "narrow";
  // A review dialog opened from the menu is in flight before the Apply starts; staging.busy covers every Apply.
  const [reviewing, setReviewing] = useState(false);
  const busy = staging.busy || reviewing;
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === useTabs.getState().tabs[tabId]?.profileId));
  const env = profile?.environment;
  // Prod and staging always show the SQL first (the sidecar's ProdWrite guard still decides).
  const reviewFirst = env === "prod" || env === "staging";
  const c = staging.counts;
  const any = staging.hasChanges;
  const problems = any ? staging.problems().length : 0;
  const total = c.updates + c.inserts + c.deletes;
  const selected = () => handle.current?.selectedRefs() ?? [];
  const word = env ? envLabel[env].toUpperCase() : "";
  const table = `${staging.edit.schema}.${staging.edit.table}`;
  const WAIT = "Wait for Apply to finish";
  const loading = rs.complete ? undefined : "Wait for all rows to arrive";
  const insertBlocked = busy ? WAIT : loading ?? (staging.canInsert ? undefined : staging.edit.insertBlockedReason ?? "Rows cannot be added to this table");
  const applyBlocked = busy ? WAIT
    : staging.outcomeUnknown ? "The last Apply may have been written already — run the query again before applying"
    : !any ? "Nothing to apply"
    : problems > 0 ? `Fix ${plural(problems, "invalid value")} first (marked red)`
    : undefined;
  const leave = () => {
    onDone();
    focusEditToggle();
  };
  /** Cancel: leave edit mode; staged changes are discarded only after the user agrees (Keep editing is the default). */
  const cancel = async () => {
    if (!staging.hasChanges) return leave();
    const parts = [c.updates && `${c.updates} changed`, c.inserts && `${c.inserts} new`, c.deletes && `${c.deletes} deleted`].filter(Boolean).join(" · ");
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm", danger: true, title: `Discard ${plural(total, "staged change")} to ${table}?`,
      body: `${parts}. Nothing has been written to the database yet.`, ok: "Discard and leave", cancel: "Keep editing", resolve,
    }));
    if (!ok || staging.busy) return;
    staging.discardAll();
    leave();
  };
  const duplicate = () => {
    const rows = selected();
    if (!rows.length) return toast("info", "Select the rows to duplicate first (click a row number).");
    const made = staging.duplicateRows(rows);
    if (made.length) handle.current?.revealRow?.({ kind: "inserted", index: made[0] });
    if (rows.length > made.length && made.length === MAX_DUPLICATE) toast("info", `Duplicated the first ${MAX_DUPLICATE.toLocaleString("en-US")} rows.`);
  };
  const remove = () => {
    const rows = selected();
    if (!rows.length) return toast("info", "Select the rows to delete first (click a row number).");
    staging.toggleDelete(rows);
  };
  return (
    <>
      {env && (
        <span className={`h-[18px] px-1.5 rounded flex items-center gap-1 text-[10px] font-bold tracking-wide shrink-0 ${env === "prod" ? "bg-err-solid text-white" : "border border-line-strong text-fg"}`}>
          {env === "prod" && <Icon name="shield" size={10} strokeWidth={2.5} />}
          {word}
        </span>
      )}
      {width !== "narrow" && (
        <span className="text-[11.5px] text-fg truncate min-w-0">
          Editing <b className="q-mono font-semibold">{staging.edit.schema}.{staging.edit.table}</b>
        </span>
      )}
      <span className="text-[11.5px] shrink-0" data-q-edit-counts aria-live="polite">
        {any ? (
          <>
            {c.updates > 0 && <span className="text-warn">{c.updates} changed </span>}
            {c.inserts > 0 && <span className="text-ok">{c.inserts} new </span>}
            {c.deletes > 0 && <span className="text-err">{c.deletes} deleted </span>}
            {problems > 0 && <span className="text-err">· {problems} invalid</span>}
          </>
        ) : (
          <span className="text-muted">No changes</span>
        )}
      </span>
      <span className="flex-1" />
      {filterBox}
      {/* Fixed order, nothing ever hidden: the same button is always in the same place for repetitive work. */}
      <EditAction icon="plus" label="Row" compact={compact} title="Add a row (Alt+Insert)" disabled={insertBlocked} onClick={() => staging.addRow()} />
      <EditAction icon="copy" label="Duplicate" compact={compact} title="Copy the selected rows as new rows (Ctrl+D)" disabled={insertBlocked} onClick={duplicate} />
      <EditAction icon="trash" label="Delete rows" compact={compact} title="Mark the selected rows for deletion, or unmark them (Ctrl+Delete)" disabled={busy ? WAIT : loading} onClick={remove} />
      <PanelIconButton icon="undo" label="Undo (Ctrl+Z)" disabled={busy ? WAIT : staging.canUndo ? undefined : "Nothing to undo"} onClick={() => staging.undo()} />
      <PanelIconButton icon="redo" label="Redo (Ctrl+Y)" disabled={busy ? WAIT : staging.canRedo ? undefined : "Nothing to redo"} onClick={() => staging.redo()} />
      <MenuButton
        label="More edit actions"
        icon="more"
        disabled={busy ? WAIT : undefined}
        items={() => [
          { label: "Duplicate selected rows", unavailable: insertBlocked, run: duplicate },
          { label: "Revert selected rows", unavailable: any ? undefined : "Nothing is staged", run: () => staging.revert(selected()) },
          { label: "Discard all", unavailable: any ? undefined : "Nothing is staged", run: () => staging.discardAll() },
          {
            label: "Review SQL",
            unavailable: reviewFirst ? `Apply always shows the SQL on ${word}` : applyBlocked,
            run: () => {
              setReviewing(true);
              void (async () => {
                if (await reviewChanges(tabId, executionId, staging)) await applyChanges(tabId, executionId, staging, false, true);
              })().finally(() => setReviewing(false));
            },
          },
        ]}
      />
      <span aria-hidden className="w-px h-4 bg-line shrink-0" />
      <EditAction
        label={reviewFirst ? `Review & apply to ${word}…` : "Apply"}
        busy={staging.busy}
        busyLabel="Applying…"
        className={reviewFirst && env === "prod" ? "q-btn-danger" : "q-btn-primary"}
        // The longest label sets the width, so "Applying…" never moves Cancel.
        minWidth={reviewFirst ? undefined : 74}
        title={reviewFirst ? `Shows the SQL, then writes ${plural(total, "change")} in one transaction (Ctrl+S)` : `Write ${plural(total, "change")} in one transaction (Ctrl+S)`}
        disabled={applyBlocked}
        onClick={() => void applyChanges(tabId, executionId, staging)}
      />
      {/* The one way out, always here: with nothing staged it leaves at once; with changes it asks, then discards. */}
      <EditAction
        label="Cancel" data-q-edit-cancel=""
        title={any ? `Discard the ${plural(total, "staged change")} and leave edit mode…` : "Leave edit mode (back to the read-only grid)"}
        disabled={busy ? WAIT : undefined}
        onClick={() => void cancel()}
      />
    </>
  );
}

/** The filter box with its column scope as a prefix: all columns (default), one, or several. */
function FilterField({ rs, filter, setFilter, compact }: { rs: ArrowResultSet; filter: string; setFilter: (v: string) => void; compact: boolean }) {
  return (
    <div className={`h-[22px] flex items-center rounded border border-line-strong bg-bg shrink-0 ${compact ? "w-[170px]" : "w-[260px]"}`}>
      <FilterColumns rs={rs} filter={filter} compact={compact} />
      <Icon name="filter" size={12} strokeWidth={2} className="text-muted ml-1.5" />
      <input
        className="flex-1 min-w-0 h-full bg-transparent px-1.5 text-[11.5px] text-fg placeholder:text-muted outline-none"
        placeholder={`Filter ${fmt(rs.rowCount)} rows…`}
        aria-label="Filter rows"
        value={filter}
        onChange={(e) => setFilter(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setFilter("");
            e.stopPropagation();
          }
        }}
      />
      {filter && (
        <button type="button" className="mr-0.5 w-4 h-4 rounded flex items-center justify-center text-muted hover:text-fg" onClick={() => setFilter("")} title="Clear filter (Esc)" aria-label="Clear filter">
          <Icon name="close" size={11} strokeWidth={2} />
        </button>
      )}
    </div>
  );
}

/** Which columns the filter searches: all (default), one, or several. The filter box's prefix. */
function FilterColumns({ rs, filter, compact }: { rs: ArrowResultSet; filter: string; compact: boolean }) {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const [, bump] = useState(0);
  const btn = useRef<HTMLButtonElement>(null);
  const pop = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top?: number; bottom?: number; maxHeight: number }>({ left: 0, top: 0, maxHeight: 288 });
  const chosen = rs.filterColumns;
  // The panel follows the set at most every 250 ms; the picker answers each click at once, reading the live state.
  const set = (cols: number[] | null) => {
    rs.setFilter(filter, cols);
    bump((n) => n + 1);
  };
  const toggle = (c: number) => {
    const cur = rs.filterColumns ?? [];
    set(cur.includes(c) ? cur.filter((x) => x !== c) : [...cur, c]); // none left = all columns again
  };

  useDismiss(open, [btn, pop], () => setOpen(false));

  // The tools row is 28 px and clips what overflows it, so the list goes to the body, fixed under the button (over it
  // when the panel is too low for the list to fit below).
  useLayoutEffect(() => {
    const r = open ? btn.current?.getBoundingClientRect() : undefined;
    if (!r) return;
    const left = Math.max(4, Math.min(r.left, window.innerWidth - (pop.current?.offsetWidth ?? 240) - 4));
    const below = window.innerHeight - r.bottom - 6;
    const above = r.top - 6;
    setPos(below < 200 && above > below
      ? { left, bottom: window.innerHeight - r.top + 2, maxHeight: Math.min(288, above) }
      : { left, top: r.bottom + 2, maxHeight: Math.min(288, below) });
  }, [open]);

  const label = !chosen ? "All columns" : chosen.length === 1 ? rs.columns[chosen[0]]?.name : `${chosen.length} columns`;
  const needle = q.trim().toLowerCase();
  const shown = rs.columns.map((c, i) => ({ c, i })).filter(({ c }) => !needle || c.name.toLowerCase().includes(needle));
  return (
    <div className="h-full">
      <button
        ref={btn}
        type="button"
        className={`h-full max-w-[120px] px-1.5 flex items-center gap-1 border-r border-line text-[11px] rounded-l ${chosen ? "text-accent" : "text-muted"} hover:bg-hover`}
        onClick={() => setOpen(!open)}
        title={chosen ? `Filter searches: ${chosen.map((c) => rs.columns[c]?.name).join(", ")}` : "Filter searches every column"}
        aria-label={`Filter columns: ${label}`}
        aria-haspopup="listbox"
        aria-expanded={open}
      >
        <span className="truncate">{compact && !chosen ? "All" : label}</span>
        <Icon name="chevron" size={10} strokeWidth={2} />
      </button>
      {open && createPortal(
        <div ref={pop} className="q-popover-in fixed z-50 bg-raised border border-line rounded shadow-xl w-60 flex flex-col" style={pos} role="listbox" aria-multiselectable>
          {rs.columns.length > 8 && <input autoFocus className="q-input h-[22px] m-1.5" placeholder="Find column…" value={q} onChange={(e) => setQ(e.target.value)} />}
          <label className="flex items-center gap-2 px-3 py-1 hover:bg-hover cursor-pointer border-b border-line">
            <input type="checkbox" checked={!chosen} onChange={() => set(null)} />
            <span className="font-semibold">All columns</span>
          </label>
          <div className="overflow-y-auto py-1">
            {shown.map(({ c, i }) => (
              <label key={i} className="flex items-center gap-2 px-3 py-0.5 hover:bg-hover cursor-pointer" title={c.typeName}>
                <input type="checkbox" checked={!!chosen?.includes(i)} onChange={() => toggle(i)} />
                <span className="truncate q-mono">{c.name}</span>
              </label>
            ))}
            {!shown.length && <div className="px-3 py-1 text-muted">No matching column</div>}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

export function Empty({ text }: { text: string }) {
  return <div className="flex-1 flex items-center justify-center text-muted text-xs p-6 text-center">{text}</div>;
}

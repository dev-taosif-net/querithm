// One activity monitor's data in the webview. Snapshots land here, outside React: panes subscribe to topics and the
// sessions grid repaints only the cells that changed (no tab-level re-render per tick — plan B1a, U1).
import type {
  ActivityRow, BlockingInfo, DeadlockEvent, DeadlocksResult, IoDelta, LockRow, MetricSample, MonitorCapabilities, MonitorConnectionState, MonitorPane,
  MonitorSnapshot, PaneStatus, RollbackProgress, TopQueryRow, WaitCoverage, WaitDelta,
} from "../ipc/contract";
import { TextCache, type FetchTexts } from "./textCache";
import { DASH, escapeForDisplay, isRunning, isSleepingWithTx, keyOf, ms, num, kb, text } from "./format";

export type Topic = "rows" | "counts" | "status" | "connection" | "locks" | "waits" | "metrics" | "io" | "top" | "deadlocks" | "selection" | "texts" | "blocking";

export interface Column {
  id: string;
  title: string;
  width: number;
  /** Sort value: numbers sort numerically, strings by locale; nulls last. */
  value: (r: ActivityRow) => number | string | null | undefined;
  display: (r: ActivityRow, m: MonitorModel) => string;
}

const textOf = (r: ActivityRow, m: MonitorModel) => {
  if (r.textId) {
    const t = m.texts.get(r.textId);
    if (t != null) return escapeForDisplay(t);
    if (m.texts.isMissing(r.textId)) return "text no longer available (session ended)";
    return "…";
  }
  return r.textNote ?? DASH;
};

const blockers = (r: ActivityRow) => (r.blockedByNote ? `${r.blockedByNote}` : r.blockedBy.length ? r.blockedBy.join(", ") : "");

export const COLUMNS: Column[] = [
  { id: "spid", title: "Session", width: 70, value: (r) => r.key.id, display: (r) => String(r.key.id) },
  { id: "login", title: "Login", width: 110, value: (r) => r.login, display: (r) => escapeForDisplay(text(r.login)) },
  { id: "database", title: "Database", width: 110, value: (r) => r.database, display: (r) => escapeForDisplay(text(r.database)) },
  { id: "status", title: "Status", width: 110, value: (r) => r.status, display: (r) => (r.rollingBack ? `rolling back${r.rollbackPercent != null ? ` ${Math.round(r.rollbackPercent)}%` : ""}` : r.status) },
  { id: "wait", title: "Wait", width: 150, value: (r) => r.waitType, display: (r) => (r.waitType ? `${r.waitType}${r.waitMs != null ? ` ${ms(r.waitMs)}` : ""}` : DASH) },
  { id: "blockedBy", title: "Blocked by", width: 90, value: (r) => r.blockedBy[0] ?? (r.blockedByNote ? -1 : null), display: (r) => blockers(r) || DASH },
  { id: "elapsed", title: "Elapsed", width: 90, value: (r) => r.elapsedMs, display: (r) => ms(r.elapsedMs) },
  { id: "cpu", title: "CPU", width: 80, value: (r) => r.cpuMs, display: (r) => (r.cpuMs == null ? DASH : ms(r.cpuMs)) },
  { id: "reads", title: "Reads", width: 90, value: (r) => r.reads, display: (r) => num(r.reads) },
  { id: "text", title: "Text", width: 420, value: (r) => r.textId ?? r.textNote, display: textOf },
  { id: "host", title: "Host", width: 110, value: (r) => r.host, display: (r) => escapeForDisplay(text(r.host)) },
  { id: "program", title: "Program", width: 140, value: (r) => r.program, display: (r) => escapeForDisplay(text(r.program)) },
  { id: "command", title: "Command", width: 100, value: (r) => r.command ?? r.backendType, display: (r) => text(r.command ?? r.backendType) },
  { id: "waitResource", title: "Wait resource", width: 160, value: (r) => r.waitResource, display: (r) => escapeForDisplay(text(r.waitResource)) },
  { id: "tx", title: "Transaction", width: 90, value: (r) => r.txElapsedMs, display: (r) => ms(r.txElapsedMs) },
  { id: "idle", title: "Idle for", width: 90, value: (r) => r.stateChangeMs, display: (r) => ms(r.stateChangeMs) },
  { id: "openTx", title: "Open tx", width: 70, value: (r) => r.openTx, display: (r) => num(r.openTx) },
  { id: "writes", title: "Writes", width: 80, value: (r) => r.writes, display: (r) => num(r.writes) },
  { id: "logicalReads", title: "Logical reads", width: 100, value: (r) => r.logicalReads, display: (r) => num(r.logicalReads) },
  { id: "rows", title: "Rows", width: 80, value: (r) => r.rowCount, display: (r) => num(r.rowCount) },
  { id: "percent", title: "% done", width: 70, value: (r) => r.percentComplete, display: (r) => (r.percentComplete == null ? DASH : `${r.percentComplete.toFixed(1)}%`) },
  { id: "tempdb", title: "tempdb", width: 80, value: (r) => r.tempdbKb, display: (r) => kb(r.tempdbKb) },
  { id: "memory", title: "Memory grant", width: 100, value: (r) => r.grantedMemoryKb, display: (r) => kb(r.grantedMemoryKb) },
  { id: "dop", title: "DOP", width: 50, value: (r) => r.dop, display: (r) => num(r.dop) },
  { id: "queryHash", title: "Query hash", width: 150, value: (r) => r.queryHash, display: (r) => text(r.queryHash) },
  { id: "querithm", title: "Querithm", width: 140, value: (r) => r.querithmKind, display: (r) => querithmLabel(r) },
];

export const DEFAULT_COLUMNS = ["spid", "login", "database", "status", "wait", "blockedBy", "elapsed", "cpu", "reads", "text"];

export function querithmLabel(r: ActivityRow, tabTitle?: (sessionId: string) => string | undefined): string {
  if (!r.querithmKind) return DASH;
  if (r.ownSessionId) return `your tab${tabTitle?.(r.ownSessionId) ? `: ${tabTitle(r.ownSessionId)}` : ""}`;
  if (r.querithmKind === "pool") return r.querithmNote === "idle" || !r.querithmNote ? "Querithm pool (idle)" : `Querithm pool (${r.querithmNote})`;
  return `Querithm (${r.querithmKind})`;
}

export interface Filters {
  hideIdle: boolean;
  hideSystem: boolean;
  database?: string;
  login?: string;
  program?: string;
}

export interface Counts {
  total: number;
  running: number;
  blocked: number;
  sleepingWithTx: number;
  headBlockers: number;
}

const METRIC_WINDOW_MS = 5 * 60_000;
/** Pane states after which a deadlock read has completed (error and collecting have not). */
const READ_DONE = new Set<string>(["ok", "truncated", "degraded"]);

export class MonitorModel {
  seq = 0;
  /** All rows of the latest snapshot, keyed. */
  byKey = new Map<string, ActivityRow>();
  /** The rows shown: filtered and sorted (stable: ties broken by session key — U5). */
  rows: ActivityRow[] = [];
  blocking: BlockingInfo | null = null;
  status = new Map<MonitorPane, PaneStatus>();
  caps: MonitorCapabilities | null = null;
  connection: MonitorConnectionState = "connecting";
  connectionError: string | null = null;
  retryInMs: number | null = null;
  lagMs = 0;
  connectionCount = 0;
  throttled = false;
  paused = false;
  pausedAt: number | null = null;
  stopped: string | null = null;
  locks: LockRow[] | null = null;
  waits: WaitDelta[] | null = null;
  coverage: WaitCoverage | null = null;
  io: IoDelta[] | null = null;
  top: TopQueryRow[] | null = null;
  deadlocks: DeadlockEvent[] = [];
  rollbacks: RollbackProgress[] = [];
  metrics: MetricSample[] = [];
  filters: Filters = { hideIdle: false, hideSystem: true };
  sort: { column: string; desc: boolean } | null = null;
  selectedKey: string | null = null;
  /** Rows received at least once (loading vs empty — U9). */
  received = false;
  readonly texts: TextCache;
  private listeners = new Map<Topic, Set<() => void>>();
  private versions = new Map<Topic, number>();
  private lastStatus = "";

  constructor(readonly monitorId: string, fetchTexts: FetchTexts, readonly onNewDeadlocks?: (events: DeadlockEvent[]) => void) {
    this.texts = new TextCache(fetchTexts, () => this.emit("texts"));
  }

  subscribe(topic: Topic, fn: () => void): () => void {
    let set = this.listeners.get(topic);
    if (!set) this.listeners.set(topic, (set = new Set()));
    set.add(fn);
    return () => set!.delete(fn);
  }

  /** Emits so far of a topic: a view that subscribes after an emit still sees that it missed one (useTopics). */
  version(topic: Topic): number {
    return this.versions.get(topic) ?? 0;
  }

  emit(topic: Topic) {
    this.versions.set(topic, this.version(topic) + 1);
    this.listeners.get(topic)?.forEach((f) => f());
  }

  /** Applies one snapshot; an older or repeated Seq is ignored (M6). Returns false when ignored. */
  apply(s: MonitorSnapshot): boolean {
    if (s.seq <= this.seq) return false;
    this.seq = s.seq;
    this.texts.putAll(s.texts);
    if (s.capabilities) this.caps = s.capabilities;
    const conn = `${s.connection}|${s.connectionError}|${s.paused}|${s.throttled}|${s.connectionCount}`;
    const reconnected = this.connection !== "connected" && s.connection === "connected";
    this.connection = s.connection;
    this.connectionError = s.connectionError ?? null;
    this.retryInMs = s.retryInMs ?? null;
    this.lagMs = s.lagMs;
    this.throttled = s.throttled;
    this.connectionCount = s.connectionCount;
    if (s.paused && !this.paused) this.pausedAt = s.at;
    if (!s.paused) this.pausedAt = null;
    this.paused = s.paused;
    if (conn !== this.lastConn || s.capabilities) this.emit("connection");
    this.lastConn = conn;
    const status = JSON.stringify(s.status);
    if (status !== this.lastStatus) {
      this.lastStatus = status;
      this.status = new Map(s.status.map((x) => [x.pane, x]));
      this.emit("status");
    }
    if (s.sessions) {
      this.received = true;
      this.byKey = new Map(s.sessions.map((r) => [keyOf(r.key), r]));
      this.blockerIds = null;
      this.blocking = s.blocking ?? null;
      this.rows = this.view();
      if (this.selectedKey && !this.byKey.has(this.selectedKey)) {
        this.selectedKey = null; // the session ended (U2)
        this.emit("selection");
      }
      this.emit("rows");
      this.emit("counts");
      this.emit("blocking");
      void this.texts.ensure(this.rows.map((r) => r.textId));
    }
    if (s.rollbacks?.length) this.rollbacks = s.rollbacks;
    if (s.locks) { this.locks = s.locks; this.emit("locks"); }
    if (s.waits || s.waitCoverage) { this.waits = s.waits ?? this.waits; this.coverage = s.waitCoverage ?? this.coverage; this.emit("waits"); }
    if (s.metrics) {
      this.metrics.push(s.metrics);
      const cut = s.metrics.at - METRIC_WINDOW_MS;
      while (this.metrics.length > 0 && this.metrics[0].at < cut) this.metrics.shift();
      this.emit("metrics");
    }
    if (s.io) { this.io = s.io; this.emit("io"); }
    if (s.topQueries) {
      this.top = s.topQueries;
      this.emit("top");
      void this.texts.ensure(s.topQueries.map((q) => q.textId));
    }
    if (s.newDeadlocks?.length) {
      const seen = new Set(this.deadlocks.map((d) => d.id));
      const fresh = s.newDeadlocks.filter((d) => !seen.has(d.id));
      if (fresh.length) {
        this.deadlocks = [...fresh, ...this.deadlocks].sort((a, b) => b.at - a.at).slice(0, 200);
        if (!this.deadlockBaseline) this.onNewDeadlocks?.(fresh);
        this.emit("deadlocks");
      }
    }
    // The first successful deadlock read sends the last 24 h: that batch is history. Its pane status and its events are
    // set together and leave in the next full snapshot (a status-only one has no rows and holds the events back), so the
    // baseline ends with the first full snapshot that reports a successful read. Server timestamps are never compared
    // with this machine's clock (X16).
    if (this.deadlockBaseline && s.sessions && READ_DONE.has(this.status.get("deadlocks")?.state ?? "collecting")) this.deadlockBaseline = false;
    // A history read that failed (often: the monitor was disconnected) is tried again once each time the connection is
    // back; one that succeeded is kept, snapshots keep it current (B7).
    if (reconnected && this.deadlockRead?.error != null && this.deadlockReader) void this.readDeadlocks(this.deadlockReader, true);
    return true;
  }
  /** True until the deadlock history has arrived: deadlocks seen until then are not announced (X16). */
  private deadlockBaseline = true;
  private lastConn = "";

  private view(): ActivityRow[] {
    const f = this.filters;
    let rows = [...this.byKey.values()].filter((r) => {
      if (f.hideSystem && !r.isUserSession && !r.querithmKind) return false;
      if (f.hideIdle && !isRunning(r) && !isSleepingWithTx(r) && r.blockedBy.length === 0 && !this.isBlocker(r)) return false;
      if (f.database && r.database !== f.database) return false;
      if (f.login && !(r.login ?? "").toLowerCase().includes(f.login.toLowerCase())) return false;
      if (f.program && !(r.program ?? "").toLowerCase().includes(f.program.toLowerCase())) return false;
      return true;
    });
    const byKey = (a: ActivityRow, b: ActivityRow) => a.key.id - b.key.id || (a.key.instance < b.key.instance ? -1 : a.key.instance > b.key.instance ? 1 : 0);
    const col = this.sort ? COLUMNS.find((c) => c.id === this.sort!.column) : undefined;
    if (col) {
      const dir = this.sort!.desc ? -1 : 1;
      rows = rows.sort((a, b) => {
        const va = col.value(a), vb = col.value(b);
        if (va == null && vb == null) return byKey(a, b);
        if (va == null) return 1;
        if (vb == null) return -1;
        const c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
        return c !== 0 ? c * dir : byKey(a, b);
      });
    } else rows.sort(byKey);
    return rows;
  }

  /** Session ids some row of the latest snapshot waits on: built once per snapshot, not once per row ("hide idle"). */
  private blockerIds: Set<number> | null = null;
  /** How often that set was built (tests: once per snapshot). */
  blockerSetBuilds = 0;

  isBlocker(r: ActivityRow) {
    if (!this.blockerIds) {
      this.blockerSetBuilds++;
      const ids = new Set<number>();
      for (const o of this.byKey.values()) for (const b of o.blockedBy) ids.add(b);
      this.blockerIds = ids;
    }
    return this.blockerIds.has(r.key.id);
  }

  setFilters(f: Partial<Filters>) {
    this.filters = { ...this.filters, ...f };
    this.rows = this.view();
    this.emit("rows");
  }

  setSort(column: string) {
    this.sort = this.sort?.column === column ? (this.sort.desc ? null : { column, desc: true }) : { column, desc: false };
    this.rows = this.view();
    this.emit("rows");
  }

  select(key: string | null) {
    this.selectedKey = key;
    this.emit("selection");
  }

  get selected(): ActivityRow | undefined {
    return this.selectedKey ? this.byKey.get(this.selectedKey) : undefined;
  }

  counts(): Counts {
    let running = 0, blocked = 0, sleepingWithTx = 0;
    for (const r of this.byKey.values()) {
      if (isRunning(r)) running++;
      if (r.blockedBy.length > 0 || r.blockedByNote) blocked++;
      if (isSleepingWithTx(r)) sleepingWithTx++;
    }
    return { total: this.byKey.size, running, blocked, sleepingWithTx, headBlockers: this.blocking?.headBlockers.length ?? 0 };
  }

  /**
   * The Deadlocks pane's full read (B7: the server's whole retained history, seconds of work on a big system_health):
   * done once per monitor and on Refresh, kept here across pane switches. Snapshots' NewDeadlocks keep `deadlocks`
   * current meanwhile. A failed read is kept too (with its reason) until Refresh, so revisiting the pane never re-reads.
   */
  deadlockRead: { events: DeadlockEvent[]; error: string | null } | null = null;
  private deadlockReading: Promise<void> | null = null;
  private deadlockReader: (() => Promise<DeadlocksResult>) | null = null;

  readDeadlocks(read: () => Promise<DeadlocksResult>, force = false): Promise<void> {
    this.deadlockReader = read;
    if (this.deadlockReading) return this.deadlockReading;
    if (this.deadlockRead && !force) return Promise.resolve();
    this.deadlockReading = read()
      .then(
        (r) => {
          // degraded (not available) and error (the read failed or timed out) both carry their reason: never an empty list.
          const failed = r.status.state === "degraded" || r.status.state === "error";
          this.deadlockRead = { events: r.events, error: failed ? r.status.reason ?? (r.status.state === "error" ? "The deadlock history could not be read." : "not available") : null };
        },
        (e) => { this.deadlockRead = { events: this.deadlockRead?.events ?? [], error: String((e as Error)?.message ?? e) }; },
      )
      .finally(() => {
        this.deadlockReading = null;
        this.emit("deadlocks");
      });
    this.emit("deadlocks"); // "reading…"
    return this.deadlockReading;
  }

  get readingDeadlocks() {
    return this.deadlockReading != null;
  }

  markStopped(reason: string) {
    this.stopped = reason;
    this.emit("connection");
    this.emit("status");
  }
}

/**
 * What each row object displayed per column when it was compared as the new row. The next tick compares against that
 * instead of formatting the previous rows again — which also matches what the grid painted. Formatting both sides was
 * ~70 % of a steady 500-session tick (metric 28ui). Text arrivals repaint the text column separately ("texts").
 */
const shown = new WeakMap<ActivityRow, { columns: Column[]; values: (string | undefined)[] }>();

/** One small array per row, aligned to the column list (a Map per row was most of the tick's garbage). */
function shownAs(r: ActivityRow, columns: Column[], col: number, m: MonitorModel): string {
  let e = shown.get(r);
  if (!e || e.columns !== columns) shown.set(r, (e = { columns, values: new Array(columns.length) }));
  let v = e.values[col];
  if (v === undefined) e.values[col] = v = columns[col].display(r, m);
  return v;
}

/** The cells of the shown rows whose display text differs between two snapshots (damage for the grid). */
export function changedCells(before: ActivityRow[], after: ActivityRow[], columns: Column[], m: MonitorModel): { cell: [number, number] }[] {
  const out: { cell: [number, number] }[] = [];
  for (let row = 0; row < after.length; row++) {
    const a = after[row];
    const b = before[row];
    const moved = !b || (b !== a && (b.key.id !== a.key.id || b.key.instance !== a.key.instance));
    for (let col = 0; col < columns.length; col++) {
      if (moved || shownAs(b, columns, col, m) !== shownAs(a, columns, col, m)) out.push({ cell: [col, row] });
    }
  }
  return out;
}

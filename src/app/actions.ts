// User actions that span stores, the sidecar and the editor. UI components call these; they never call rpc for
// executions directly, so guards (§6), transaction prompts (§6 rule 5) and lazy sessions (§6 rule 1) live in one place.
import { stopMonitorsFor } from "../monitor/monitorActions";
import type { Dialect, Engine, ExecuteOptions, GuardBlock, ObjectRef, QueryParameter, ScriptKind, ServerMessage, Severity } from "../ipc/contract";
import { actualPlanWithRun, assertNever, dialectOf, engineTraits } from "../engines";
import { onNotify, rpc, RpcError, shell } from "../ipc/client";
import { isStreaming, ResultsDroppedError, stopAllStreams, stopStream, streamExecution } from "../ipc/frames";
import { ExecutionData, executionData } from "../results/model";
import { useExecutions } from "../store/executions";
import { useTabs, newTabId, type BottomTab, type TabState } from "../store/tabs";
import { useToolTabs } from "../store/toolTabs";
import { useProfiles } from "../store/profiles";
import { isDisconnected, obtainSecret, REPICK_HINT, useConnections } from "../store/connections";
import { needsRepick } from "../connections/fileGrants";
import { locationOf } from "../connections/auth";
import { newTabBinding } from "./tabBinding";
import { isUntitledTitle, objectLabel, tabTitleText } from "./tabTitle";
import { announce } from "../store/announce";
import { announceQueryDone } from "./announcements";
import { ensureTabSnapshot, sessionParam, tabScope } from "../store/snapshots";
import { snapshotOf, useClosedTabs } from "../store/closedTabs";
import { useSettings } from "../store/settings";
import { ask, toast, useUi } from "../store/ui";
import { createModel, disposeModel, editorForTab, executableText, getModel, languageFor, setModelLanguage } from "../editor/editors";
import { statementBoundsAt, statementTitle, undeclaredParameters, unsetUserVariables } from "../editor/completion";
import { monaco } from "../editor/monaco";
import { dropStagings, pendingIn } from "../results/staging";
import { engineGone } from "../store/engine";
import { resolvePendingEdits } from "../results/gridEdit";
import { notebookHooks } from "../notebook/hooks";

const KEEP_EXECUTIONS = 3;

function withoutKey<V>(rec: Record<number, V>, key: number): Record<number, V> {
  const out = { ...rec };
  delete out[key];
  return out;
}

function tab(id: string): TabState | undefined {
  return useTabs.getState().tabs[id];
}

function profileOf(t: TabState | undefined) {
  return useProfiles.getState().byId(t?.profileId);
}

// ---------------------------------------------------------------------------------------------------- tabs

let untitled = 0;

export interface NewTabOptions {
  profileId?: string;
  database?: string;
  text?: string;
  title?: string;
  readOnly?: boolean;
  boundObject?: TabState["boundObject"];
  filePath?: string;
  id?: string;
  activate?: boolean;
  pinned?: boolean;
  /** Keep the tab at the end of the strip (workspace restore keeps the saved order); new tabs otherwise open first, after any pinned tabs. */
  atEnd?: boolean;
  definitionOf?: TabState["definitionOf"];
  preview?: boolean;
  charts?: TabState["charts"];
}

/**
 * The database a new tab on `profileId` starts in when none was asked for: the one the most recently used tab on that
 * server is in, else the profile default, else the database the explorer's session landed in (the login's default).
 */
export function serverDatabase(profileId: string): string | undefined {
  const s = useTabs.getState();
  for (const id of s.mru) {
    const t = s.tabs[id];
    if (t?.profileId === profileId && t.database) return t.database;
  }
  return useProfiles.getState().byId(profileId)?.databaseDefault ?? useConnections.getState().servers[profileId]?.database ?? undefined;
}

/** The connection a new tab starts on (§6): the one asked for ("" = none), else the active tab's. */
export function newQueryTabBinding(profileId?: string, database?: string) {
  return newTabBinding({ profileId, database }, activeTabState(), isDisconnected, serverDatabase);
}

export function newQueryTab(o: NewTabOptions = {}): string {
  const { profileId, database } = newTabBinding(o, activeTabState(), isDisconnected, serverDatabase);
  const id = o.id ?? newTabId();
  const profile = useProfiles.getState().byId(profileId);
  createModel(id, o.text ?? "", languageFor(profile?.engine));
  useTabs.getState().add({
    id, title: o.title ?? `SQLQuery${++untitled}.sql`, profileId, database, readOnly: o.readOnly, boundObject: o.boundObject, filePath: o.filePath, pinned: o.pinned,
    definitionOf: o.definitionOf, preview: o.preview, charts: o.charts,
  });
  ensureTabSnapshot(tab(id));
  workspaceHooks.onTabAdded?.(id, o.activate !== false, !!o.atEnd);
  return id;
}

export function duplicateTab(tabId: string): string | undefined {
  const t = tab(tabId);
  if (!t) return;
  const text = getModel(tabId)?.getValue() ?? "";
  const dot = t.title.lastIndexOf(".");
  const base = dot > 0 ? t.title.slice(0, dot) : t.title;
  const ext = dot > 0 ? t.title.slice(dot) : ".sql";
  return newQueryTab({
    profileId: t.profileId ?? "",
    database: t.database,
    text,
    title: `${base} (copy)${ext}`,
  });
}

export function activeTabState(): TabState | undefined {
  const s = useTabs.getState();
  return s.activeId ? s.tabs[s.activeId] : undefined;
}

/** Wired by the Dockview workspace (adds/activates/removes panels). */
export const workspaceHooks: {
  onTabAdded?: (id: string, activate: boolean, atEnd?: boolean) => void;
  /** Opens a tool tab (ER diagram, compare) as its own panel. */
  onToolAdded?: (id: string, title: string) => void;
  onTabRemoved?: (id: string) => void;
  activate?: (id: string) => void;
  /** On-screen tab order of the strip holding `tabId` (the active strip when omitted). */
  stripOrder?: (tabId?: string) => string[];
  /** On-screen order of every tab, strip by strip. */
  allOrder?: () => string[];
  /** Moves a tab to `index` within its strip. */
  moveTab?: (id: string, index: number) => void;
  /** The strip tab that has keyboard focus, if any (keys like Ctrl+Shift+PageUp act on it). */
  focusedTab?: () => string | undefined;
  /** Gives keyboard focus to a tab in its strip. */
  focusTab?: (id: string) => void;
  /** Moves a tab into a new group to the right of its own (the tab menu's Split right). */
  splitRight?: (id: string) => void;
  /** Where a tab is: its group's id and its position in that strip. */
  placeOf?: (id: string) => { groupId: string; index: number } | undefined;
  /** Puts a tab back in a group (when it still exists) at a position. */
  place?: (id: string, groupId: string | undefined, index: number) => void;
} = {};

/** How long closing a tab waits for its cancelled query to stop before it gives up and keeps the tab. */
export const CLOSE_CANCEL_WAIT_MS = 10_000;

/** True once the tab runs nothing (or is gone); false when it is still running after `ms`. */
function whenIdle(id: string, ms: number): Promise<boolean> {
  const idle = () => !tab(id)?.busy;
  if (idle()) return Promise.resolve(true);
  return new Promise((resolve) => {
    const done = (v: boolean) => {
      clearTimeout(timer);
      unsubscribe();
      resolve(v);
    };
    const timer = setTimeout(() => done(false), ms);
    const unsubscribe = useTabs.subscribe(() => idle() && done(true));
  });
}

/**
 * What closing a tab must settle first, without ending anything behind the user's back (§6 rule 5, D20): a running
 * query is cancelled only after "Cancel the running query?" (Keep tab is the default), staged grid edits are applied or
 * discarded, and an open — or possibly open (M9) — transaction asks Commit / Roll back / Cancel. False: the tab stays.
 * Quitting (H4) settles each tab the same way, but never dead-ends (W1, W4): a query that does not stop in time no
 * longer holds the quit (the engine's shutdown ends it and the server rolls back; staged grid edits, which cannot be
 * applied while it runs, are named first), a transaction is not asked about once the engine is gone (it went with the
 * engine's connections), and a COMMIT or ROLLBACK that fails offers "Quit anyway" (the server rolls back what was not
 * committed when the connection closes).
 */
export async function settleTab(id: string, o: { quit?: boolean } = {}): Promise<boolean> {
  if (!tab(id)) return true;
  if (tab(id)!.busy) {
    if (!(await ask<boolean>((resolve) => ({ kind: "busyClose", tabId: id, quit: o.quit, resolve })))) return false;
    await cancelQuery(id);
    if (!(await whenIdle(id, CLOSE_CANCEL_WAIT_MS))) {
      if (o.quit) return confirmStagedLoss(id);
      toast("warn", `The query in "${tab(id)?.title}" has not stopped yet; the tab stays open. Try again once it has.`);
      return false;
    }
    // The run's end and its session state arrive separately: read the transaction state now, so a transaction the
    // cancelled batch left open is never closed with the session.
    const sid = tab(id)?.sessionId;
    if (sid) {
      const st = await rpc("session.state", { sessionId: sid }).catch(() => undefined);
      if (st) useTabs.getState().update(id, { txState: st.txState, txStartedAt: st.txStartedAt ?? undefined, spid: st.spid ?? undefined });
    }
  }
  const t = tab(id);
  if (!t) return true;
  if (!(await resolvePendingEdits(id))) return false;
  // An unknown state is treated as open (M9): the connection may hold uncommitted work.
  if (t.sessionId && t.txState !== "none") {
    // W1: quitting with the engine gone (dead and restarting, or failed): its connections closed with it and the server
    // rolled back what was open, so there is nothing left to commit or roll back (Roll back would fail and keep the app).
    if (o.quit && engineGone()) return true;
    const choice = await ask<"commit" | "rollback" | "cancel">((resolve) => ({ kind: "txClose", tabId: id, quit: o.quit, resolve }));
    if (choice === "cancel") return false;
    const failure = await txAction(id, choice);
    // A commit or rollback that failed leaves the transaction open: keep the tab so it can still be ended.
    const after = tab(id)?.txState;
    if (after && after !== "none") return o.quit ? confirmQuitAnyway(id, choice, failure) : false;
  }
  return true;
}

/** The text of "Quit anyway" after a COMMIT or ROLLBACK that did not end the transaction while quitting (W1). */
export function quitAnywayBody(op: "commit" | "rollback", failure?: string): string {
  const what = op === "commit" ? "COMMIT" : "ROLLBACK";
  return `${what} did not end the transaction${failure ? `: ${failure}` : "."} Quitting closes the connection, and the server rolls back whatever was not committed.`;
}

/** W1: quitting after a failed COMMIT / ROLLBACK asks once more instead of keeping Querithm open with no way out. */
function confirmQuitAnyway(id: string, op: "commit" | "rollback", failure?: string): Promise<boolean> {
  activateTab(id);
  return ask<boolean>((resolve) => ({
    kind: "confirm", title: "Quit anyway?", danger: true, ok: "Quit anyway (the server rolls back)", body: quitAnywayBody(op, failure), resolve,
  }));
}

/**
 * W4: quitting while a cancelled query still runs. Its staged grid edits cannot be applied now (the session is busy),
 * so they are named before they are lost; nothing staged: the quit goes on (the engine's shutdown ends the query).
 */
async function confirmStagedLoss(id: string): Promise<boolean> {
  const t = tab(id);
  const staged = t ? pendingIn(t.executions) : [];
  if (!staged.length) return true;
  const c = staged.reduce((a, s) => ({ u: a.u + s.counts.updates, i: a.i + s.counts.inserts, d: a.d + s.counts.deletes }), { u: 0, i: 0, d: 0 });
  return ask<boolean>((resolve) => ({
    kind: "confirm", title: "Quit and discard the staged grid edits?", danger: true, ok: "Discard and quit",
    body: `The query in "${tabTitleText(t!.title)}" has not stopped, so the grid edits staged in it (${c.u} updated, ${c.i} new and ${c.d} deleted row(s)) cannot be applied now. Quitting discards them; the server rolls back what the query had not committed.`,
    resolve,
  }));
}

/** Closes a tab once nothing would be lost (settleTab); there is no way to keep a transaction open without its tab. */
export async function closeTab(id: string, o: { batch?: string } = {}): Promise<boolean> {
  if (!(await settleTab(id))) return false;
  const t = tab(id);
  if (!t) return true;
  if (t.traceId) await rpc("trace.stop", { traceId: t.traceId }).catch(() => {});
  if (t.debugId) await rpc("debug.detach", { debugId: t.debugId }).catch(() => {});
  removeTab(id, o.batch);
  return true;
}

function removeTab(id: string, batch?: string) {
  const t = tab(id);
  if (!t) return;
  // Remember the text and the place before the model goes, so Ctrl+Shift+T can bring the tab back where it was.
  // A notebook is remembered as its document (without outputs).
  const closed = snapshotOf(t, t.notebook ? (notebookHooks.text?.(id) ?? "") : (getModel(id)?.getValue() ?? ""));
  if (closed && t.notebook) Object.assign(closed, { sql: notebookHooks.serialize?.(id) ?? "", notebook: true });
  const place = workspaceHooks.placeOf?.(id);
  if (closed) useClosedTabs.getState().push({ ...closed, batch, groupId: place?.groupId, index: place?.index });
  for (const ex of t.executions) disposeExecution(ex);
  if (t.notebook) notebookHooks.dispose?.(id);
  if (t.sessionId) void rpc("session.close", { sessionId: t.sessionId, force: false }).catch(() => {});
  workspaceHooks.onTabRemoved?.(id);
  useTabs.getState().remove(id);
  disposeModel(id);
}

export function disposeExecution(executionId: string) {
  unwatchCancel(executionId);
  dropStagings(executionId);
  // Its poll loop stops, and the shell drops the frames still coming (a run whose end already arrived has none left
  // there: discarding it would leave an entry the shell never removes).
  stopStream(executionId);
  // A view the webview wrote itself (a refusal, a lost database, a plan) has nothing in the sidecar to release.
  if (!isLocalView(executionId)) void rpc("query.close", { executionId }).catch(() => {});
  executionData.delete(executionId);
  useExecutions.getState().drop(executionId);
}

// ---------------------------------------------------------------------------------------------------- sessions

/** A tab's session is opened on first use and is bound to (profile, database) (§6). */
/** The user chose not to reconnect a disconnected server: not an error worth a toast. */
export class ReconnectDeclined extends Error {
  constructor() {
    super("Not connected.");
  }
}

/**
 * Disconnect (Object Explorer): closes the explorer session and every tab session on the server, so nothing
 * runs there again until the user agrees to reconnect. Open transactions are rolled back only after a confirm.
 */
export async function disconnectServer(profileId: string) {
  const tabs = Object.values(useTabs.getState().tabs).filter((t) => t.profileId === profileId && t.sessionId);
  if (tabs.some((t) => t.busy)) {
    toast("warn", "A query is running on this server. Cancel it before disconnecting.");
    return;
  }
  const inTx = tabs.filter((t) => t.txState === "open").length;
  const maybe = tabs.filter((t) => t.txState === "unknown").length;
  if (inTx || maybe) {
    const parts = [
      inTx && `${inTx} tab${inTx === 1 ? " has an open transaction" : "s have open transactions"}`,
      maybe && `${maybe} tab${maybe === 1 ? " may have an open transaction" : "s may have open transactions"}`,
    ].filter(Boolean);
    const n = inTx + maybe;
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm", title: "Disconnect and roll back?", danger: true, ok: "Disconnect",
      body: `${parts.join(" and ")} on this server. Disconnecting rolls ${n === 1 ? "it" : "them"} back.`,
      resolve,
    }));
    if (!ok) return;
  }
  await stopMonitorsFor(profileId);
  for (const t of tabs) {
    await rpc("session.close", { sessionId: t.sessionId!, force: true }).catch(() => {});
    useTabs.getState().update(t.id, { sessionId: undefined, txState: "none", txStartedAt: undefined, spid: undefined });
  }
  await useConnections.getState().disconnect(profileId);
}

export async function ensureSession(tabId: string): Promise<string> {
  const t = tab(tabId);
  if (!t) throw new Error("tab closed");
  if (t.sessionId) return t.sessionId;
  const profile = profileOf(t);
  if (!profile) throw new Error("Choose a connection for this tab first (Ctrl+Shift+U).");
  if (useConnections.getState().disconnected[profile.id]) {
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm", title: `Reconnect to “${profile.name}”?`,
      body: "You disconnected from this server. Connect again to run the query?", ok: "Connect", resolve,
    }));
    if (!ok) throw new ReconnectDeclined();
    useConnections.setState((s) => ({ disconnected: Object.fromEntries(Object.entries(s.disconnected).filter(([k]) => k !== profile.id)) }));
  }
  // A new session must respect expiry even when the explorer is already connected.
  const secret = await obtainSecret(profile);
  if (secret === null) throw new Error("Connection cancelled.");
  let opened;
  try {
    opened = await rpc("session.open", { profileId: profile.id, database: t.database ?? null, secret: secret ?? null });
  } catch (e) {
    // A database file whose grant is gone (moved, replaced, revoked): say how to pick it again, and the server offers it.
    if (e instanceof RpcError && needsRepick(e.appCode)) {
      useConnections.getState().markNeedsFile(profile.id, `${e.message} ${REPICK_HINT}`);
      throw new Error(`${e.message} ${REPICK_HINT}`);
    }
    throw e;
  }
  useTabs.getState().update(tabId, { sessionId: opened.sessionId, database: opened.database ?? t.database, sessionError: undefined, memoryLost: undefined });
  if (!useConnections.getState().servers[profile.id]) void useConnections.getState().connect(profile.id);
  return opened.sessionId;
}

export async function changeConnection(tabId: string, profileId: string, database?: string) {
  const t = tab(tabId);
  if (!t) return;
  if (t.txState !== "none" && !(await confirmTxSwitch(tabId))) return;
  if (t.busy) {
    toast("warn", "Cancel the running query before switching connection.");
    return;
  }
  if (t.sessionId) await rpc("session.close", { sessionId: t.sessionId, force: true }).catch(() => {});
  const profile = useProfiles.getState().byId(profileId);
  const db = database ?? profile?.databaseDefault ?? undefined;
  setModelLanguage(tabId, languageFor(profile?.engine));
  useTabs.getState().update(tabId, { profileId, database: db, sessionId: undefined, txState: "none", txStartedAt: undefined, spid: undefined });
  ensureTabSnapshot(tab(tabId));
  if (!useConnections.getState().servers[profileId]) void useConnections.getState().connect(profileId);
}

/** §6 Safe switching: warn before leaving a session with an open transaction. */
async function confirmTxSwitch(tabId: string) {
  return ask<boolean>((resolve) => ({ kind: "txSwitch", tabId, resolve }));
}

/** Changing a tab's database is explicit and visible (§6). SQL Server: one USE roundtrip; PostgreSQL: new pooled connection. */
export async function changeDatabase(tabId: string, database: string) {
  const t = tab(tabId);
  if (!t || t.database === database) return;
  if (t.busy) {
    toast("warn", "Cancel the running query before switching database.");
    return;
  }
  if (t.txState !== "none") {
    toast("warn", `Commit or roll back the ${t.txState === "open" ? "open" : "possibly open"} transaction before switching database.`);
    return;
  }
  const t0 = performance.now();
  if (t.sessionId) {
    try {
      const r = await rpc("session.changeDatabase", { sessionId: t.sessionId, database });
      useTabs.getState().update(tabId, { database: r.database });
      benchMarks.dbSwitch.push(performance.now() - t0);
    } catch (e) {
      toast("error", `Could not switch to ${database}: ${(e as Error).message}`);
      return;
    }
  } else {
    useTabs.getState().update(tabId, { database });
  }
  ensureTabSnapshot(tab(tabId));
}

export const benchMarks = { dbSwitch: [] as number[], firstBatch: [] as number[] };

// ---------------------------------------------------------------------------------------------------- execution

export type RunMode = "auto" | "all" | "statement";

export interface RunOptions {
  mode?: RunMode;
  sql?: string;
  lineOffset?: number;
  options?: Partial<ExecuteOptions>;
  bottomTab?: BottomTab;
}

async function collectParameters(sql: string, dialect: Dialect): Promise<QueryParameter[] | null | undefined> {
  const names = undeclaredParameters(sql, dialect);
  if (!names.length) return undefined;
  const values = await ask<{ name: string; value: string; isNull: boolean; type?: string }[] | null>((resolve) => ({ kind: "params", names, dialect, resolve }));
  if (!values) return null;
  return values.map((v) => ({ name: v.name, value: v.isNull ? null : v.value, isNull: v.isNull, type: v.type ?? null }));
}

async function guard(block: GuardBlock): Promise<boolean> {
  return ask<boolean>((resolve) => ({ kind: "guard", block, resolve }));
}

/** Execute (F5 / Ctrl+Enter / Ctrl+Shift+Enter). Returns the execution id, or undefined if nothing ran. */
export async function runQuery(tabId: string, o: RunOptions = {}): Promise<string | undefined> {
  const t = tab(tabId);
  if (!t) return;
  // A notebook runs its cells (Phase C3): F5 / Ctrl+Enter and the top bar's Run act on the focused cell.
  if (t.notebook) {
    await notebookHooks.run?.(tabId);
    return;
  }
  const profile = profileOf(t);
  if (!profile) {
    useUi.getState().setPalette({ mode: "connections" });
    toast("info", "Pick a connection for this tab first.");
    return;
  }
  let sql = o.sql;
  let lineOffset = o.lineOffset ?? 1;
  if (sql == null) {
    const ex = executableText(tabId);
    sql = ex.sql;
    lineOffset = ex.lineOffset;
    if (o.mode === "statement" && !ex.isSelection) {
      const model = getModel(tabId)!;
      const editor = editorForTab(tabId);
      const pos = editor?.getPosition();
      const offset = pos ? model.getOffsetAt(pos) : 0;
      const b = statementBoundsAt(model.getValue(), offset, dialectOf(profile.engine));
      sql = model.getValue().slice(b.start, b.end);
      lineOffset = model.getPositionAt(b.start).lineNumber;
    }
  }
  if (!sql.trim()) return;
  if (t.busy) {
    toast("warn", "A query is already running in this tab.");
    return;
  }
  autoTitle(tabId, profile.engine);
  // Re-running replaces the results: staged grid edits are applied or discarded first, never dropped silently.
  if (!(await resolvePendingEdits(tabId))) return;
  const model = getModel(tabId);
  const run = await executeOnTab(tabId, sql, lineOffset, o.options, () => model && monaco.editor.setModelMarkers(model, "execution", []));
  if (!run) return;
  startExecutionView(tabId, run.sessionId, run.executionId, sql, lineOffset, o.bottomTab ?? (run.options.actualPlan ? "plan" : "results"), run.notes);
  return run.executionId;
}

/** An execution the sidecar accepted. */
export interface Executed {
  sessionId: string;
  executionId: string;
  options: ExecuteOptions;
  /** Lines the webview adds to the run's Messages (info): why MySQL did not prompt, a new in-memory database. */
  notes: string[];
}

/**
 * RPC refusals of `query.execute` whose message is the reason the statement did not run (a file-policy refusal such as
 * ATTACH on SQLite or LOAD DATA LOCAL on MySQL; an in-memory database that is gone): shown in the tab's Messages as an
 * error, not only as a toast.
 */
export const VISIBLE_REFUSALS: readonly string[] = ["refused", "memoryLost"];

/** MySQL binds no parameters by name: a `@name` the script reads but never sets is a user variable, NULL until set. */
export function userVariableNote(name: string): string {
  return `MySQL: ${name} is a user variable here — set it with SET ${name} = … in the script.`;
}

/** The first run after a lost in-memory database says that its database is a new, empty one. */
export const NEW_MEMORY_DATABASE_NOTE = "A new, empty in-memory database: the previous one was lost, with its tables and data.";

/** The tab's profile keeps its database in memory: the database lives and dies with the tab's session. */
function inMemory(t: TabState | undefined): boolean {
  const p = profileOf(t);
  return !!p && locationOf(p) === "memory";
}

/**
 * Runs `sql` on the tab's session through the one guarded path (§6, §13 rule 7): the lazy session (reconnecting after
 * a sidecar restart once), the parameter prompt, and the Prod / destructive guards the sidecar answers with, asked and
 * re-sent. Query tabs and notebook cells both execute here. Undefined when nothing ran (declined, cancelled, or an
 * error the user was shown). `beforeSend` runs once the session and parameters are settled.
 */
export async function executeOnTab(
  tabId: string, sql: string, lineOffset: number, extra?: Partial<ExecuteOptions>, beforeSend?: () => void, onRefused?: (message: string) => void,
): Promise<Executed | undefined> {
  const t = tab(tabId);
  const profile = profileOf(t);
  if (!t || !profile) return;
  const wasLost = !!t.memoryLost && !t.sessionId;
  let sessionId: string;
  try {
    sessionId = await ensureSession(tabId);
  } catch (e) {
    if (e instanceof ReconnectDeclined) return;
    useTabs.getState().update(tabId, { sessionError: (e as Error).message });
    toast("error", (e as Error).message);
    return;
  }
  const engine = engineTraits(profile.engine);
  const parameters = extra?.parameters ?? (await collectParameters(sql, engine.dialect));
  if (parameters === null) return;
  const notes = unsetUserVariables(sql, engine.dialect).map(userVariableNote);
  // MySQL has no parameter prompt: say why, where the user looks for one, and keep it in the run's Messages.
  if (notes.length) toast("info", notes.join(" "));
  if (wasLost) notes.push(NEW_MEMORY_DATABASE_NOTE);
  const s = useSettings.getState();
  const options: ExecuteOptions = {
    maxRows: t.maxRows ?? s.maxRows,
    statisticsIo: engine.statisticsIo && s.statisticsIo,
    statisticsTime: engine.statisticsTime && s.statisticsTime,
    actualPlan: actualPlanWithRun(profile.engine) && t.actualPlan,
    serverOutput: tabServerOutput(t, profile.engine),
    confirmedDestructive: false,
    confirmedProd: false,
    noHistory: false,
    parameters: parameters ?? null,
    lineOffset,
    ...extra,
  };
  beforeSend?.();

  let result;
  for (let round = 0; round < 3; round++) {
    try {
      result = await rpc("query.execute", { sessionId, sql, options });
    } catch (e) {
      if (e instanceof RpcError && e.appCode === "notFound") {
        // An in-memory database went with its session: never run the text on a new, empty one as if it were the old one.
        if (inMemory(tab(tabId))) {
          markMemoryLost(tabId, MEMORY_GONE);
          onRefused?.(MEMORY_GONE);
          return;
        }
        // The session vanished (sidecar restart): reopen once.
        useTabs.getState().update(tabId, { sessionId: undefined });
        sessionId = await ensureSession(tabId);
        continue;
      }
      const message = (e as Error).message;
      if (e instanceof RpcError && e.appCode && VISIBLE_REFUSALS.includes(e.appCode)) {
        if (e.appCode === "memoryLost") useTabs.getState().update(tabId, { memoryLost: true });
        if (onRefused) onRefused(message);
        else showTabMessages(tabId, sql, lineOffset, [{ severity: "error", message }]);
      }
      toast("error", message);
      return;
    }
    if (!result.blocked) break;
    if (!(await guard(result.blocked))) return;
    if (result.blocked.reason === "prodFirstRun") options.confirmedProd = true;
    else options.confirmedDestructive = true;
    result = undefined;
  }
  const executionId = result?.executionId;
  if (!executionId) return;
  return { sessionId, executionId, options, notes };
}

/**
 * What a run sends as ExecuteOptions.serverOutput: the tab's DBMS_OUTPUT toggle (unset is on) on an engine with server
 * output; null elsewhere (the sidecar keeps the session's, and no other engine has one).
 */
export function tabServerOutput(t: Pick<TabState, "serverOutput"> | undefined, engine: Engine | null | undefined): boolean | null {
  return engineTraits(engine).serverOutput ? (t?.serverOutput ?? true) : null;
}

let localViews = 0;
const LOCAL_VIEW = "local-";
const isLocalView = (executionId: string) => executionId.startsWith(LOCAL_VIEW);

/**
 * Shows lines the webview writes itself (a refused statement, a lost in-memory database) in the tab's Messages pane as a
 * finished run of its own, so the reason stays next to the editor rather than only in a toast that fades.
 */
export function showTabMessages(tabId: string, sql: string, lineOffset: number, lines: { severity: Severity; message: string }[]) {
  const t = tab(tabId);
  if (!t) return;
  const id = `${LOCAL_VIEW}${Date.now().toString(36)}-${++localViews}`;
  const ts = Date.now();
  useExecutions.getState().put({
    id, tabId, sessionId: t.sessionId ?? "", state: "failed", startedAt: ts, elapsedMs: 0, resultSetCount: 0, truncated: false, statistics: [], plans: [], sql, lineOffset, notRun: true,
    messages: lines.map((l) => ({ executionId: id, severity: l.severity, message: l.message, batchIndex: 0, ts })),
  });
  executionData.set(id, new ExecutionData(id));
  useTabs.getState().update(tabId, { ...keepRun(tabId, id, t), activeExecutionId: id, resultIndex: 0, bottomTab: "messages", bottomOpen: true });
}

/**
 * A tab keeps its last KEEP_EXECUTIONS runs: adding `executionId` disposes the ones that fall out (sidecar execution,
 * shell queue, poll loop, rows), and a comparison with one of them ends and says so rather than vanish. Returns the
 * tab's new run list (and the ended comparison) to apply with the caller's own changes.
 */
function keepRun(tabId: string, executionId: string, t: TabState): Pick<Partial<TabState>, "executions" | "compare"> {
  const keep = [...t.executions, executionId];
  const dropped = keep.slice(0, Math.max(0, keep.length - KEEP_EXECUTIONS));
  for (const old of dropped) disposeExecution(old);
  const cur = tab(tabId) ?? t;
  const lostCompare = !!cur.compare && dropped.includes(cur.compare.executionId);
  if (lostCompare) toast("info", `The compared run was discarded: a tab keeps its last ${KEEP_EXECUTIONS} runs.`);
  return { executions: keep.slice(-KEEP_EXECUTIONS), ...(lostCompare ? { compare: undefined } : {}) };
}

/** Why an in-memory tab has no database any more: its session (and the database in it) is gone. */
export const MEMORY_GONE = "This tab's in-memory database is gone (its session closed), with its tables and data. The next run starts a new, empty database.";
/** The same, after an engine restart. */
export const MEMORY_LOST_IN_RESTART = "The Querithm engine restarted, and this tab's in-memory database went with it: its tables and data are gone. The next run starts a new, empty database.";

/**
 * An in-memory tab lost its database (engine restart, session gone): its results and schema belong to a database that
 * no longer exists, so they go; the tab is marked until its next session opens, and the reason is shown and announced
 * once. Nothing reopens behind the user's back.
 */
export function markMemoryLost(tabId: string, message: string) {
  const t = tab(tabId);
  if (!t) return;
  if (t.notebook) notebookHooks.outputsLost?.(tabId, message);
  else {
    for (const ex of t.executions) disposeExecution(ex);
    useTabs.getState().update(tabId, { executions: [], activeExecutionId: undefined, resultIndex: undefined });
  }
  useTabs.getState().update(tabId, { memoryLost: true, sessionId: undefined, txState: "none", txStartedAt: undefined, spid: undefined, busy: false });
  if (!t.notebook) showTabMessages(tabId, "", 1, [{ severity: "warning", message }]);
  announce(`${tabTitleText(t.title)}: in-memory database lost`);
}

/**
 * Registers an execution the sidecar accepted and streams its frames into an ExecutionData (the grid's source).
 * `register` runs after the view exists and before notifications that arrived early are applied (they may end it).
 * The stream starts before those are applied (W3): an early query.done then finds it streaming and arms the linger
 * timer (STREAM_LINGER_MS), so a run whose end frame was lost never polls for good.
 */
export function trackExecution(tabId: string, sessionId: string, executionId: string, sql: string, lineOffset: number, register?: () => void, notes: string[] = []): ExecutionData {
  const data = new ExecutionData(executionId);
  executionData.set(executionId, data);
  const ts = Date.now();
  useExecutions.getState().put({
    id: executionId, tabId, sessionId, state: "queued", startedAt: ts, resultSetCount: 0, truncated: false, statistics: [], plans: [], sql, lineOffset,
    messages: notes.map((message) => ({ executionId, severity: "info" as const, message, batchIndex: 0, ts })),
  });
  register?.();
  const t0 = performance.now();
  let first = true;
  const stream = streamExecution(executionId, {
    onSchema: (ri, schema) => data.get(ri).setSchema(schema),
    onBatch: (ri, batch) => {
      if (first) {
        first = false;
        benchMarks.firstBatch.push(performance.now() - t0);
      }
      data.get(ri).append(batch);
      data.notify();
    },
    onEndResultSet: (ri, truncated) => {
      data.get(ri).finish(truncated);
      data.notify();
    },
    onEnd: () => {
      data.finishStream();
      useExecutions.getState().patch(executionId, { resultSetCount: data.resultSets.length });
    },
  });
  // The stream broke off before its end (frames the shell dropped, a frame that does not decode): what arrived stays,
  // marked interrupted, and the run's Messages say why. The run itself goes on in the sidecar and still reports its end.
  stream.done.catch((e) => {
    console.warn("result stream ended", e);
    if (executionData.get(executionId) !== data) return; // disposed meanwhile
    data.finishStream();
    const message = e instanceof ResultsDroppedError ? e.message : `The results could not be read: ${(e as Error)?.message ?? e}`;
    useExecutions.getState().patch(executionId, (v) => ({
      resultSetCount: data.resultSets.length,
      messages: [...v.messages, { executionId, severity: "error" as const, message, batchIndex: 0, ts: Date.now() }],
    }));
  });
  flushEarlyEvents(executionId);
  return data;
}

/**
 * How long a run's frames may still arrive after its query.done. Every frame is written before query.done is sent, so
 * what is left is at most what the shell buffers (8 batches) plus the socket; a stream still open after this lost its
 * end frame (a data plane that reconnected, §5) and is stopped instead of polling for good.
 */
export const STREAM_LINGER_MS = 30_000;

function lingerStream(executionId: string) {
  if (!isStreaming(executionId)) return;
  setTimeout(() => {
    if (!isStreaming(executionId)) return;
    stopStream(executionId);
    executionData.get(executionId)?.finishStream();
  }, STREAM_LINGER_MS);
}

function startExecutionView(tabId: string, sessionId: string, executionId: string, sql: string, lineOffset: number, bottomTab: BottomTab, notes: string[] = []) {
  const t = tab(tabId)!;
  trackExecution(tabId, sessionId, executionId, sql, lineOffset, () => {
    useTabs.getState().update(tabId, {
      ...keepRun(tabId, executionId, t), activeExecutionId: executionId, bottomOpen: true, busy: true, resultIndex: 0, ledgerError: undefined,
      bottomTab: bottomTab === "results" && t.bottomTab === "messages" ? "results" : bottomTab, errorLines: [],
    });
  }, notes);
}

/**
 * How long after Cancel a run may still be going before the user is offered Restart engine. The sidecar closes a stuck
 * run's connection after 5 s and reports it after 10 s (§7 step 4); this catches what even that cannot reach.
 */
export const CANCEL_STUCK_MS = 15_000;

const ended = (state: string | undefined) => !state || ["done", "failed", "cancelled"].includes(state);
/** The pending "has not stopped" check per execution: cleared when the run ends or its view goes. */
const cancelWatch = new Map<string, ReturnType<typeof setTimeout>>();
function unwatchCancel(executionId: string) {
  clearTimeout(cancelWatch.get(executionId));
  cancelWatch.delete(executionId);
}

export async function cancelQuery(tabId: string) {
  const t = tab(tabId);
  // A notebook's Cancel stops the running cell and the cells queued after it (Phase C3).
  if (t?.notebook) notebookHooks.cancel?.(tabId);
  const id = t?.activeExecutionId;
  if (!id) return;
  const view = useExecutions.getState().views[id];
  if (!view || ended(view.state)) return;
  unwatchCancel(id);
  cancelWatch.set(id, setTimeout(() => {
    cancelWatch.delete(id);
    if (ended(useExecutions.getState().views[id]?.state)) return;
    const title = tabTitleText(tab(tabId)?.title ?? "A tab");
    // 8a review: the run may still end by itself (its cleanup and the drivers' cancel give up within a minute; Messages
    // says which), so the banner offers the restart rather than telling the user it is the only way out.
    useUi.getState().setBanner({
      kind: "warn",
      text: `${title}: the query has not stopped ${CANCEL_STUCK_MS / 1000} s after Cancel. It may still end by itself within a minute (Messages says what it is waiting for); Restart engine frees the tab now (every tab reconnects; open transactions are rolled back).`,
      action: { label: "Restart engine", run: () => void shell.restart() },
    });
  }, CANCEL_STUCK_MS));
  await rpc("query.cancel", { executionId: id }).catch((e) => toast("error", (e as Error).message));
}

// Notifications can arrive before query.execute's reply has been processed: buffer per execution id.
const early = new Map<string, Array<() => void>>();

function whenView(executionId: string, apply: () => void) {
  if (useExecutions.getState().views[executionId]) apply();
  else {
    const list = early.get(executionId) ?? [];
    if (list.length < 5000) list.push(apply);
    early.set(executionId, list);
    if (early.size > 50) early.delete(early.keys().next().value!);
  }
}

function flushEarlyEvents(executionId: string) {
  const list = early.get(executionId);
  early.delete(executionId);
  list?.forEach((f) => f());
}

const errorsOf = (v: { messages: ServerMessage[] } | undefined) => v?.messages.filter((m) => m.severity === "error") ?? [];

/**
 * M13: messages a run keeps. The sidecar forwards at most 20,000 server messages per run, then one "N more … were not
 * shown" line; the rest of the room is for Querithm's own few lines (a cancel that did not stop, a lost connection).
 */
export const MESSAGE_LIMIT = 20_100;

// Message arrays this module made: a batch is pushed onto them in place, so appending costs O(batch), not a copy of the
// whole log per message (O(n²) over a PRINT flood). An array made elsewhere (a view's first lines, a spread) is copied
// once and owned from then on; the view object is new on every append, so every subscriber still re-renders.
const ownedMessages = new WeakSet<ServerMessage[]>();

/** Appends one batch of a run's messages (query.messages, or a lone query.message); false when nothing was kept. */
function appendMessages(executionId: string, batch: readonly ServerMessage[]): boolean {
  const v = useExecutions.getState().views[executionId];
  if (!v || batch.length === 0 || v.messages.length >= MESSAGE_LIMIT) return false;
  let list = v.messages;
  if (!ownedMessages.has(list)) {
    list = list.slice();
    ownedMessages.add(list);
  }
  const room = MESSAGE_LIMIT - list.length;
  for (let i = 0; i < batch.length && i < room; i++) list.push(batch[i]);
  useExecutions.getState().patch(executionId, { messages: list });
  return true;
}

/**
 * A batch of a run's messages. An error never takes the view away (§12 bottom panel): earlier result sets stay shown, and
 * the error reaches the user through the ledger's red chip, the notice line, the Messages badge, the editor marker and
 * the run announcement. The editor markers are set once per batch that holds an error, not once per error.
 */
function receiveMessages(executionId: string, batch: readonly ServerMessage[]) {
  if (!appendMessages(executionId, batch) || !batch.some((m) => m.severity === "error")) return;
  const v = useExecutions.getState().views[executionId];
  if (v) markErrors(v.tabId, v.messages);
}

/** Where an error's marker starts on its line: the server's column (Oracle names one), never past the line's end. */
export function errorStartColumn(model: { getLineMaxColumn(line: number): number }, m: Pick<ServerMessage, "editorLine" | "editorColumn">): number {
  const max = model.getLineMaxColumn(m.editorLine!);
  const c = m.editorColumn ?? 0;
  return c > 0 && c < max ? c : 1;
}

function markErrors(tabId: string, messages: ServerMessage[]) {
  const model = getModel(tabId);
  if (!model) return;
  const markers = messages
    .filter((m) => m.severity === "error" && m.editorLine && m.editorLine <= model.getLineCount())
    .map((m) => ({
      // From the server's column when it named one (Oracle), else the whole line.
      startLineNumber: m.editorLine!, endLineNumber: m.editorLine!, startColumn: errorStartColumn(model, m), endColumn: model.getLineMaxColumn(m.editorLine!),
      message: m.message, severity: monaco.MarkerSeverity.Error, source: "server",
    }));
  monaco.editor.setModelMarkers(model, "execution", markers);
  useTabs.getState().update(tabId, { errorLines: markers.map((m) => m.startLineNumber) });
}

export function installExecutionListeners() {
  const ex = useExecutions.getState;
  const offs = [
    onNotify("query.queued", (e) => whenView(e.executionId, () => ex().patch(e.executionId, { state: "queued", queued: true }))),
    onNotify("query.started", (e) => whenView(e.executionId, () => ex().patch(e.executionId, { state: "running", queued: false }))),
    // M13: the sidecar sends a run's messages coalesced; the single-message form stays readable during the transition.
    onNotify("query.messages", (e) => whenView(e.executionId, () => receiveMessages(e.executionId, e.messages))),
    onNotify("query.message", (m) => whenView(m.executionId, () => receiveMessages(m.executionId, [m]))),
    onNotify("query.resultSet", (e) =>
      whenView(e.executionId, () =>
        ex().patch(e.executionId, (v) => ({
          state: "streaming", resultSetCount: Math.max(v.resultSetCount, e.resultIndex + 1),
          setAfter: { ...v.setAfter, [e.resultIndex]: v.messages.length },
          // Always (re)written: a set that arrives without edit info must never inherit an earlier set's at its index.
          ...(e.edit ? { edits: { ...v.edits, [e.resultIndex]: e.edit } } : v.edits?.[e.resultIndex] ? { edits: withoutKey(v.edits, e.resultIndex) } : {}),
        })),
      ),
    ),
    onNotify("query.plan", (e) =>
      whenView(e.executionId, () => ex().patch(e.executionId, (v) => ({ plans: [...v.plans, { batchIndex: e.batchIndex, plan: e.plan, raw: e.raw }] }))),
    ),
    onNotify("query.done", (e) =>
      whenView(e.executionId, () => {
        unwatchCancel(e.executionId);
        lingerStream(e.executionId);
        const v = ex().views[e.executionId];
        const hasError = e.state === "failed" || !!e.error || (v?.messages.some((m) => m.severity === "error") ?? false);
        const data = executionData.get(e.executionId);
        const effectiveResultSets = data?.streamEnded ? data.resultSets.length : e.resultSetCount;
        ex().patch(e.executionId, {
          state: e.state, elapsedMs: e.elapsedMs, serverElapsedMs: e.serverElapsedMs, serverCpuMs: e.serverCpuMs, affectedRows: e.affectedRows,
          truncated: e.truncated, error: e.error, statistics: e.statistics ?? [], resultSetCount: effectiveResultSets,
        });
        if (!v) return;
        const t = tab(v.tabId);
        const patch: Partial<TabState> = { busy: false };
        if (e.database && t && e.database !== t.database) patch.database = e.database;
        // The view stays where the user left it: a run without result sets shows its summary (errors, row counts,
        // PRINT output) in Results instead of switching to Messages.
        useTabs.getState().update(v.tabId, patch);
        announceQueryDone(v.tabId, e.state, e.elapsedMs, { resultSets: effectiveResultSets, errors: hasError ? errorsOf(ex().views[e.executionId]) : [] });
        if (patch.database && t?.profileId) ensureTabSnapshot(tab(v.tabId));
        markErrors(v.tabId, v.messages);
      }),
    ),
    onNotify("session.stateChanged", (s) => {
      const t = useTabs.getState().bySession(s.sessionId);
      if (!t) return;
      useTabs.getState().update(t.id, {
        txState: s.txState, txStartedAt: s.txStartedAt ?? undefined, spid: s.spid ?? undefined, busy: s.busy,
        ...(s.database && s.database !== t.database ? { database: s.database } : {}),
      });
    }),
    onNotify("auth.prompt", (e) => useUi.getState().openDialog({ kind: "authPrompt", message: e.message, url: e.verificationUrl, code: e.userCode })),
  ];
  return () => offs.forEach((o) => o());
}

// ---------------------------------------------------------------------------------------------------- transactions

/** The engine went away during COMMIT: its answer never came, so the outcome is unknown (never "try again"). */
export const COMMIT_ENGINE_STOPPED =
  "The Querithm engine stopped during COMMIT, so whether the transaction committed is unknown: the server may have rolled it back. Check the data before running the work again.";

/** Begins, commits or rolls back the tab's transaction; a failure is toasted and returned (its message). */
export async function txAction(tabId: string, op: "begin" | "commit" | "rollback"): Promise<string | undefined> {
  let sent = false;
  try {
    const sid = await ensureSession(tabId);
    sent = true;
    const st = await rpc(op === "begin" ? "tx.begin" : op === "commit" ? "tx.commit" : "tx.rollback", { sessionId: sid });
    useTabs.getState().update(tabId, { txState: st.txState, txStartedAt: st.txStartedAt ?? undefined, spid: st.spid ?? undefined });
    return undefined;
  } catch (e) {
    // The sidecar words a stopped or lost COMMIT itself (txOutcomeUnknown, txNone); only its own loss is said here.
    const engineStopped = sent && op === "commit" && e instanceof RpcError && e.appCode === "sidecarDown";
    const message = engineStopped ? COMMIT_ENGINE_STOPPED : (e as Error).message;
    toast("error", message);
    return message;
  }
}

// ---------------------------------------------------------------------------------------------------- objects

/** Opens an object's definition: editable (Modify, bound to the object) or read-only (go to definition). */
/**
 * D24: an untitled tab takes a verb-first name from its SQL at its first run ("UPDATE Invoice"), read from tokens.
 * Once only (the new name is no longer an untitled one), never over a name the user typed, and not when turned off.
 */
function autoTitle(tabId: string, engine: Engine | undefined) {
  const t = tab(tabId);
  if (!t || !useSettings.getState().autoTitle || !isUntitledTitle(t.title)) return;
  const name = statementTitle(getModel(tabId)?.getValue() ?? "", dialectOf(engine));
  if (name) useTabs.getState().update(tabId, { title: `${name}.sql` });
}

const same = (a: string | undefined, b: string | undefined) => (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

/**
 * Go to definition (D25): the tab already showing that object (a read-only definition or one open for edit) is
 * activated; otherwise the strip's preview tab shows it (italic, "Preview"), replacing the previous preview, which is
 * announced; a strip without one gets one. A preview tab is kept by double-clicking it or pinning it.
 */
export async function openReadOnlyDefinition(profileId: string, database: string, obj: { schema: string; name: string }, sql: string): Promise<string | undefined> {
  const s = useTabs.getState();
  const shows = (t: TabState | undefined) =>
    !!t && t.profileId === profileId && same(t.database, database) &&
    ((!!t.definitionOf && same(t.definitionOf.schema, obj.schema) && same(t.definitionOf.name, obj.name)) ||
      (!!t.boundObject && same(t.boundObject.schema, obj.schema) && same(t.boundObject.name, obj.name)));
  const open = s.order.map((id) => s.tabs[id]).find(shows);
  if (open) {
    activateTab(open.id);
    return open.id;
  }
  const strip = workspaceHooks.stripOrder?.() ?? s.order;
  const preview = strip.map((id) => s.tabs[id]).find((t) => t?.preview);
  const title = `${obj.name}.sql`;
  if (preview && preview.profileId === profileId && same(preview.database, database) && !preview.sessionId && !preview.busy) {
    const was = preview.title;
    getModel(preview.id)?.setValue(sql);
    useTabs.getState().update(preview.id, { title, definitionOf: { schema: obj.schema, name: obj.name } });
    activateTab(preview.id);
    announce(`Preview ${tabTitleText(was)} replaced by ${obj.name}`);
    return preview.id;
  }
  if (preview && !(await closeTab(preview.id))) return undefined;
  if (preview) announce(`Preview ${tabTitleText(preview.title)} replaced by ${obj.name}`);
  return newQueryTab({ profileId, database, text: sql, title, readOnly: true, definitionOf: { schema: obj.schema, name: obj.name }, preview: true });
}

/** Keeps a preview tab: it becomes an ordinary read-only tab the next definition does not replace. */
export function keepPreview(tabId: string) {
  if (tab(tabId)?.preview) useTabs.getState().update(tabId, { preview: false });
}

export async function openDefinition(profileId: string, database: string, obj: ObjectRef, editable: boolean) {
  try {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    const def = await rpc("meta.definition", { sessionId, object: { ...obj, profileId, database } });
    if (!editable) {
      await openReadOnlyDefinition(profileId, database, obj, def.sql);
      return;
    }
    const title = `${obj.name}${obj.kind === "packageBody" ? " BODY" : ""}.sql`;
    newQueryTab({
      profileId, database, text: def.sql, title, readOnly: !editable,
      boundObject: editable
        ? { profileId, database, objectId: obj.objectId ?? null, schema: obj.schema, name: obj.name, kind: obj.kind, definitionHash: def.definitionHash }
        : undefined,
    });
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

/** Script tabs are titled by what the script does, so a DROP tab never looks like a CREATE one (§10). */
const SCRIPT_VERB: Partial<Record<ScriptKind, string>> = { create: "CREATE", alter: "ALTER", dropCreate: "DROP and CREATE", drop: "DROP", execute: "EXEC", enable: "ENABLE", disable: "DISABLE" };

/**
 * "DROP Orders.sql", "CREATE sales.Orders.sql": the default schema (dbo, public) is left out of the title. An Oracle package
 * body says so ("CREATE APP.PKG BODY.sql"), since it shares its specification's name.
 */
export function scriptTitle(obj: Pick<ObjectRef, "schema" | "name"> & { kind?: ObjectRef["kind"] }, kind: ScriptKind, engine?: Engine): string {
  const verb = SCRIPT_VERB[kind];
  const body = obj.kind === "packageBody" ? " BODY" : "";
  return verb ? `${verb} ${objectLabel(obj.schema, obj.name, engine)}${body}.sql` : `${obj.name}${body}.sql`;
}

/** `withBody`: an Oracle package specification's script also carries its body (D4 S08); ignored for every other kind. */
export async function scriptObject(profileId: string, database: string, obj: ObjectRef, kind: ScriptKind, run = false, withBody = false) {
  try {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    const r = await rpc("meta.script", { sessionId, object: { ...obj, profileId, database }, kind, withBody });
    const id = newQueryTab({ profileId, database, text: r.sql, title: scriptTitle(obj, kind, useProfiles.getState().byId(profileId)?.engine) });
    if (run) setTimeout(() => void runQuery(id), 50);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

/**
 * Apply procedure (Ctrl+Shift+S, §10): compare the server definition hash with the one the tab was opened
 * from; on conflict show the diff and require an explicit Overwrite; then run as one execution.
 */
export async function applyProcedure(tabId: string) {
  const t = tab(tabId);
  const b = t?.boundObject;
  if (!t || !b) {
    toast("info", "This tab is not bound to a database object. Open one with Explorer → Modify.");
    return;
  }
  const sessionId = await useConnections.getState().sessionFor(b.profileId);
  let current;
  try {
    current = await rpc("meta.definition", { sessionId, object: { schema: b.schema, name: b.name, kind: b.kind, objectId: b.objectId, database: b.database, profileId: b.profileId } });
  } catch (e) {
    if (!(e instanceof RpcError && e.appCode === "noDefinition")) {
      toast("error", (e as Error).message);
      return;
    }
  }
  const local = getModel(tabId)!.getValue();
  if (current && current.definitionHash !== b.definitionHash) {
    const ok = await ask<boolean>((resolve) => ({
      kind: "diff", title: `Server definition changed: ${b.schema}.${b.name} was modified on the server since you opened this tab`,
      left: current!.sql, right: local, leftTitle: "SERVER DEFINITION", rightTitle: "YOUR LOCAL EDITS", okLabel: "Overwrite Server Version",
      language: getModel(tabId)!.getLanguageId(), resolve,
    }));
    if (!ok) return;
  }
  const execId = await runQuery(tabId, { sql: local, lineOffset: 1, bottomTab: "messages" });
  if (!execId) return;
  const applied = async (state: string) => {
    if (state !== "done") return;
    try {
      const after = await rpc("meta.definition", { sessionId, object: { schema: b.schema, name: b.name, kind: b.kind, database: b.database, profileId: b.profileId } });
      useTabs.getState().update(tabId, { boundObject: { ...b, definitionHash: after.definitionHash, objectId: after.objectId ?? b.objectId } });
      // A session-owned engine's tab refreshes its own view (temporary objects, an in-memory database).
      const own = tabScope(tab(tabId));
      const ownSession = own?.profileId === b.profileId && own.database === b.database ? own.sessionId : undefined;
      void rpc("meta.refresh", { profileId: b.profileId, database: b.database, full: false, ...sessionParam(ownSession) });
      toast("success", `${b.schema}.${b.name} applied.`);
    } catch {
      /* object renamed or dropped */
    }
  };
  // A fast apply (a view on a session that is already open, e.g. SQLite) can end before runQuery returns: its done event
  // was applied to the execution's view through the early buffer, so read the outcome there first.
  const ended = useExecutions.getState().views[execId]?.state;
  if (ended === "done" || ended === "failed" || ended === "cancelled") return void applied(ended);
  const off = onNotify("query.done", (e) => {
    if (e.executionId !== execId) return;
    off();
    void applied(e.state);
  });
}

export async function compareWithServer(tabId: string) {
  const t = tab(tabId);
  const b = t?.boundObject;
  if (!t || !b) return;
  const sessionId = await useConnections.getState().sessionFor(b.profileId);
  const current = await rpc("meta.definition", { sessionId, object: { schema: b.schema, name: b.name, kind: b.kind, database: b.database, profileId: b.profileId } });
  await ask<boolean>((resolve) => ({
    kind: "diff", title: `Compare ${b.schema}.${b.name} with the server`, left: current.sql, right: getModel(tabId)!.getValue(),
    leftTitle: "SERVER DEFINITION", rightTitle: "THIS TAB", language: getModel(tabId)!.getLanguageId(), resolve,
  }));
}

/**
 * Explain / actual plan (§11). The sidecar guards the text like a query (refusals, the Prod first run, destructive
 * statements: the guard dialog, then the request again with the confirmations) and plans it as an execution of the tab —
 * the plan arrives as query.plan, Cancel ends it, and a refusal (no actual plans here, an open DuckDB transaction) is
 * shown in Messages with its reason.
 */
export async function showPlan(tabId: string, mode: "estimated" | "actual") {
  const t = tab(tabId);
  const profile = profileOf(t);
  if (!t || !profile) return;
  if (mode === "actual") {
    const traits = engineTraits(profile.engine);
    const how = traits.actualPlan;
    switch (how) {
      case "withRun":
        // SQL Server actual plans come from executing with SET STATISTICS XML ON (§11).
        await runQuery(tabId, { options: { actualPlan: true }, bottomTab: "plan" });
        return;
      case "explainAnalyze": {
        const ok = await ask<boolean>((resolve) => ({
          kind: "confirm", title: "Run the query for an actual plan?", danger: true, ok: "Run EXPLAIN ANALYZE",
          body: traits.actualPlanConfirm ?? "EXPLAIN ANALYZE executes the statement.",
          resolve,
        }));
        if (!ok) return;
        break;
      }
      case "none":
        // The sidecar refuses actual plans here; say so instead of sending a request it rejects.
        toast("info", `${traits.shortLabel} shows the estimated plan only. Use Explain.`);
        return;
      default:
        return assertNever(how);
    }
  }
  const { sql, lineOffset } = executableText(tabId);
  if (!sql.trim()) return;
  if (tab(tabId)?.busy) {
    toast("warn", "A query is already running in this tab.");
    return;
  }
  let sessionId: string;
  try {
    sessionId = await ensureSession(tabId);
  } catch (e) {
    if (!(e instanceof ReconnectDeclined)) toast("error", (e as Error).message);
    return;
  }
  const params = { sessionId, sql, mode, confirmedRun: mode === "actual", confirmedProd: false, confirmedDestructive: false };
  let result;
  for (let round = 0; round < 3; round++) {
    try {
      result = await rpc("plan.get", params);
    } catch (e) {
      const message = (e as Error).message;
      // A refusal is the reason nothing ran: it stays next to the editor, not only in a toast that fades.
      if (e instanceof RpcError && e.appCode && PLAN_REFUSALS.includes(e.appCode)) showTabMessages(tabId, sql, lineOffset, [{ severity: "error", message }]);
      toast("error", message);
      return;
    }
    if (!result.blocked) break;
    if (!(await guard(result.blocked))) return;
    if (result.blocked.reason === "prodFirstRun") params.confirmedProd = true;
    else params.confirmedDestructive = true;
    result = undefined;
  }
  if (!result?.executionId || !tab(tabId)) return;
  startExecutionView(tabId, sessionId, result.executionId, sql, lineOffset, "plan");
}

/** plan.get refusals whose message is why the plan did not run (shown in the tab's Messages as well as a toast). */
export const PLAN_REFUSALS: readonly string[] = ["refused", "unsupported", "memoryLost"];

export async function formatTab(tabId: string) {
  const editor = editorForTab(tabId);
  if (editor) await editor.getAction("editor.action.formatDocument")?.run();
}

export async function toggleTrace(tabId: string) {
  const t = tab(tabId);
  if (!t) return;
  try {
    if (t.traceId) {
      await rpc("trace.stop", { traceId: t.traceId });
      useTabs.getState().update(tabId, { traceId: undefined });
    } else {
      const sessionId = await ensureSession(tabId);
      const r = await rpc("trace.start", { sessionId, scope: "spid" });
      useTabs.getState().update(tabId, { traceId: r.traceId, bottomTab: "runtime", runtimeTool: "trace", bottomOpen: true });
    }
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

export function setBottomTab(tabId: string, bottomTab: BottomTab) {
  useTabs.getState().update(tabId, { bottomTab, bottomOpen: true });
}

export function toggleBottom(tabId: string) {
  const t = tab(tabId);
  // Hiding a maximized panel restores the editor: a hidden panel never leaves the editor collapsed.
  if (t) useTabs.getState().update(tabId, t.bottomOpen ? { bottomOpen: false, bottomMax: false } : { bottomOpen: true });
}

/** Ctrl+Shift+R: the panel fills the tab (the editor keeps a one-line strip), or goes back to its size. */
export function toggleMaximize(tabId: string) {
  const t = tab(tabId);
  if (t) useTabs.getState().update(tabId, { bottomMax: !t.bottomMax, bottomOpen: true });
}

/** The run the panel shows (one of the 3 the tab keeps); an older run is read-only. */
export function showRun(tabId: string, executionId: string) {
  const t = tab(tabId);
  if (!t || !t.executions.includes(executionId) || t.activeExecutionId === executionId) return;
  useTabs.getState().update(tabId, { activeExecutionId: executionId, resultIndex: 0, ledgerError: undefined, compare: undefined });
}

/** Steps to the previous (-1) or next (+1) kept run. */
export function stepRun(tabId: string, delta: -1 | 1) {
  const t = tab(tabId);
  if (!t?.activeExecutionId) return;
  const next = t.executions[t.executions.indexOf(t.activeExecutionId) + delta];
  if (next) showRun(tabId, next);
}

export function activateTab(id: string) {
  workspaceHooks.activate?.(id);
}

/** Said on a run that was going when the engine restarted: nothing reports its end any more. */
export const RUN_INTERRUPTED =
  "The Querithm engine restarted while this ran, so its outcome is unknown: a statement that was running may or may not have completed on the server. Check before running it again.";
/** The same, when the engine stopped and was not restarted ("failed"). */
export const RUN_STOPPED =
  "The Querithm engine stopped while this ran, so its outcome is unknown: a statement that was running may or may not have completed on the server. Check before running it again.";
/** An in-memory tab's database after the engine stopped and was not restarted. */
export const MEMORY_LOST_IN_STOP = "The Querithm engine stopped, and this tab's in-memory database went with it: its tables and data are gone. The next run starts a new, empty database.";

/**
 * After the engine went the old engine's runs report nothing more: their poll loops stop (the shell's buffers went with
 * the engine) and each run still going is marked failed with `reason`; the rows that arrived stay, marked interrupted.
 * Returns the tabs (and tool tabs) that had one.
 */
function interruptRuns(reason: string): string[] {
  // Tool tabs' streams (a data compare, an import preview) too: what they showed stays, marked interrupted.
  for (const id of stopAllStreams()) executionData.get(id)?.finishStream();
  const tabs = new Set<string>();
  const ts = Date.now();
  for (const v of Object.values(useExecutions.getState().views)) {
    if (ended(v.state)) continue;
    unwatchCancel(v.id);
    const data = executionData.get(v.id);
    if (data && !data.streamEnded) data.finishStream();
    useExecutions.getState().patch(v.id, (cur) => ({
      state: "failed", queued: false, error: { message: reason },
      messages: [...cur.messages, { executionId: v.id, severity: "error" as const, message: reason, batchIndex: 0, ts }],
    }));
    tabs.add(v.tabId);
  }
  return [...tabs];
}

/**
 * What the engine took with it (a restart, or a stop it did not come back from): every session id is gone, and with it
 * open transactions, in-memory databases, traces, debuggers and the runs going. The tabs drop theirs; returns one line
 * per kind of loss for the banner (§4 step 3: tell the user lost transactions).
 */
function engineLost(restarted: boolean): string[] {
  const runTabs = interruptRuns(restarted ? RUN_INTERRUPTED : RUN_STOPPED);
  const interrupted = runTabs.map((id) => tab(id)?.title).filter((x): x is string => !!x);
  // Runs of tool tabs (a data compare registers its run's view under the tool's id) are said too, by the tool's title.
  const toolRuns = runTabs.flatMap((id) => (tab(id) ? [] : (useToolTabs.getState().tools[id]?.title ?? [])));
  const tabs = Object.values(useTabs.getState().tabs);
  const lostTx = tabs.filter((t) => t.txState === "open");
  const maybeTx = tabs.filter((t) => t.txState === "unknown");
  // L32: every session went with the engine, and with it what lived in it (temporary tables, SET options, variables).
  const hadSession = tabs.filter((t) => t.sessionId && !inMemory(t));
  const traced = tabs.filter((t) => t.traceId);
  const debugged = tabs.filter((t) => t.debugId);
  // Phase D0d: an in-memory database lived in its tab's session; a tab that had one lost it (a tab that never ran had none).
  const lostMemory = tabs.filter((t) => t.sessionId && inMemory(t));
  for (const t of tabs) useTabs.getState().update(t.id, { sessionId: undefined, txState: "none", txStartedAt: undefined, busy: false, traceId: undefined, debugId: undefined });
  for (const t of lostMemory) markMemoryLost(t.id, restarted ? MEMORY_LOST_IN_RESTART : MEMORY_LOST_IN_STOP);
  const lost: string[] = [];
  if (lostTx.length) lost.push(`Open transactions in ${lostTx.map((t) => t.title).join(", ")} were lost (rolled back by the server).`);
  if (maybeTx.length) lost.push(`Transactions that may have been open in ${maybeTx.map((t) => t.title).join(", ")} were lost too, if there were any (rolled back by the server).`);
  if (lostMemory.length) lost.push(`In-memory databases in ${lostMemory.map((t) => t.title).join(", ")} were lost; their next run starts an empty one.`);
  if (interrupted.length) lost.push(`The runs in progress in ${interrupted.join(", ")} were interrupted; whether a statement that was running completed is unknown.`);
  if (toolRuns.length) lost.push(`${toolRuns.join(", ")} stopped while running; run ${toolRuns.length === 1 ? "it" : "them"} again.`);
  if (traced.length) lost.push(`The traces in ${traced.map((t) => t.title).join(", ")} stopped.`);
  if (debugged.length) lost.push(`The debugging sessions in ${debugged.map((t) => t.title).join(", ")} ended.`);
  if (hadSession.length) lost.push(`${hadSession.length === 1 ? "The connected tab gets" : `The ${hadSession.length} connected tabs get`} a new session at the next run: temporary tables and session settings from before are gone.`);
  return lost;
}

/** After a sidecar restart every session id is gone; transactions are lost and the user is told (§4 step 3). */
export function onSidecarRestarted() {
  const lost = engineLost(true);
  useConnections.getState().resetSessions();
  // Nothing above: no tab had a session, so the restart took nothing a tab had (servers in the Explorer reconnect now).
  useUi.getState().setBanner({
    kind: "warn",
    text: lost.length ? `The Querithm engine restarted. ${lost.join(" ")}` : "The Querithm engine restarted. No tab was connected or running a query, so no work was lost; servers reconnect now.",
  });
}

/**
 * W1: the engine stopped and is not coming back by itself ("failed": it stopped twice in a minute, or could not be
 * started again). Its sessions are gone just as after a restart: the tabs drop theirs, so closing a tab or quitting
 * no longer asks to end a transaction nothing can reach. Returns the lines the failure banner adds (engineStatus.ts);
 * the Explorer's servers reconnect once the engine is restarted (onSidecarRestarted).
 */
export function onSidecarFailed(): string[] {
  return engineLost(false);
}

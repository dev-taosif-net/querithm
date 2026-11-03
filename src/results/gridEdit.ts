// Grid edit actions: token reads, preview, apply with the sidecar's guards, conflicts. Components call these;
// they never call data.* directly, so the Prod/destructive confirmations and conflict handling live in one place.
import type { Engine, GridApplyResult, GridConflictRow, GridPatchParams, ResultEditInfo } from "../ipc/contract";
import { knownEngine } from "../engines";
import { rpc, RpcError } from "../ipc/client";
import { useTabs } from "../store/tabs";
import { useProfiles } from "../store/profiles";
import { ask, toast } from "../store/ui";
import { useExecutions } from "../store/executions";
import { executionData, type ArrowResultSet } from "./model";
import { GridStaging, pendingIn, stagingKey, stagings } from "./staging";

export function editInfo(executionId: string, resultIndex: number): ResultEditInfo | undefined {
  return useExecutions.getState().views[executionId]?.edits?.[resultIndex];
}

/** The staging of an editable result set, created on first use. */
export function stagingFor(executionId: string, rs: ArrowResultSet, engine: Engine): GridStaging | undefined {
  const edit = editInfo(executionId, rs.index);
  if (!edit?.editable) return undefined;
  const key = stagingKey(executionId, rs.index);
  let s = stagings.get(key);
  if (!s) stagings.set(key, (s = new GridStaging(edit, rs, engine)));
  return s;
}

function sessionOf(executionId: string): string | undefined {
  return useExecutions.getState().views[executionId]?.sessionId;
}

const pendingTokenReads = new Map<GridStaging, Promise<void>>();

/**
 * Reads the version token of every edited or deleted row that has none yet, checking that the displayed values still
 * hold on the server. A row that no longer matches is marked as changed on the server (it cannot be edited until the
 * query is re-run), so a stale grid never overwrites someone else's change.
 */
export function ensureTokens(executionId: string, staging: GridStaging): Promise<void> {
  const running = pendingTokenReads.get(staging);
  if (running) return running.then(() => ensureTokens(executionId, staging));
  const need = staging.rowsNeedingTokens();
  const sessionId = sessionOf(executionId);
  if (!need.length || !sessionId) return Promise.resolve();
  const p = rpc("data.rowTokens", {
    sessionId, executionId, resultIndex: indexOf(executionId, staging), keys: need.map((n) => staging.keyOf(n.row)), expect: need.map((n) => n.expect),
  })
    .then((r) => {
      staging.setTokens(need.map((n) => n.row), r.tokens);
      const lost = r.tokens.filter((t) => t == null).length;
      if (lost) toast("warn", `${lost} edited row${lost === 1 ? " has" : "s have"} changed on the server since the query ran. Re-run the query to edit ${lost === 1 ? "it" : "them"}.`);
    })
    .finally(() => pendingTokenReads.delete(staging));
  pendingTokenReads.set(staging, p);
  return p;
}

function indexOf(executionId: string, staging: GridStaging): number {
  for (const [k, s] of stagings) if (s === staging && k.startsWith(`${executionId}:`)) return Number(k.slice(executionId.length + 1));
  return 0;
}

let tokenTimer: ReturnType<typeof setTimeout> | undefined;

/** Token reads after edits are batched (a paste of 500 cells is one roundtrip). */
export function scheduleTokens(executionId: string, staging: GridStaging) {
  clearTimeout(tokenTimer);
  tokenTimer = setTimeout(() => void ensureTokens(executionId, staging).catch((e) => toast("error", (e as Error).message)), 300);
}

/** Review SQL: the generated statements with literals (read-only). Resolves true when the user chose Apply. */
export async function reviewChanges(tabId: string, executionId: string, staging: GridStaging): Promise<boolean> {
  const patch = await preparePatch(executionId, staging, false);
  if (!patch) return false;
  try {
    const preview = await rpc("data.previewPatch", patch);
    return await ask<boolean>((resolve) => ({ kind: "gridPreview", title: titleOf(staging), sql: preview.sql, notes: preview.notes, engine: engineOf(tabId), resolve }));
  } catch (e) {
    toast("error", (e as Error).message);
    return false;
  }
}

function titleOf(s: GridStaging) {
  const c = s.counts;
  return `Changes to ${s.edit.schema}.${s.edit.table}: ${c.updates} updated, ${c.inserts} new, ${c.deletes} deleted`;
}

function engineOf(tabId: string): Engine {
  const t = useTabs.getState().tabs[tabId];
  return knownEngine(useProfiles.getState().byId(t?.profileId)?.engine);
}

async function preparePatch(executionId: string, staging: GridStaging, force: boolean): Promise<GridPatchParams | null> {
  // A commit whose outcome is unknown may have written these rows: sending them again could insert them twice (H5).
  if (staging.outcomeUnknown) {
    toast("error", staging.outcomeUnknown);
    return null;
  }
  const problems = staging.problems();
  if (problems.length) {
    toast("error", `Fix ${problems.length === 1 ? "this value" : `${problems.length} values`} first: ${problems.slice(0, 3).join("; ")}${problems.length > 3 ? "…" : ""}`);
    return null;
  }
  const sessionId = sessionOf(executionId);
  if (!sessionId) {
    toast("error", "The tab's session is closed. Re-run the query.");
    return null;
  }
  if (!force) {
    try {
      await ensureTokens(executionId, staging);
    } catch (e) {
      toast("error", (e as Error).message);
      return null;
    }
    const stale = [...staging.conflicted.keys()].filter((r) => staging.edits.has(r) || staging.deleted.has(r));
    if (stale.length) {
      const drop = await ask<boolean>((resolve) => ({
        kind: "confirm", title: "Rows changed on the server",
        body: `${stale.length} row${stale.length === 1 ? "" : "s"} you edited changed on the server after the query ran. Apply the other changes and drop the edits of ${stale.length === 1 ? "that row" : "those rows"}? (Re-run the query to see the current values.)`,
        ok: "Drop those edits", resolve,
      }));
      if (!drop) return null;
      staging.dropRows(stale);
      if (!staging.hasChanges) return null;
    }
  }
  return staging.buildPatch(sessionId, executionId, indexOf(executionId, staging), force);
}

/** The Apply in flight per staging (H6): a second Ctrl+S, a key repeat or the button joins it instead of writing again. */
const applying = new WeakMap<GridStaging, Promise<boolean>>();

/** Apply (Ctrl+S in the grid). Prod always shows the preview first, then the sidecar's guard dialogs. */
export function applyChanges(tabId: string, executionId: string, staging: GridStaging, force = false, reviewed = false): Promise<boolean> {
  const running = applying.get(staging);
  if (running) return running;
  const p = applyOnce(tabId, executionId, staging, force, reviewed).finally(() => applying.delete(staging));
  applying.set(staging, p);
  return p;
}

async function applyOnce(tabId: string, executionId: string, staging: GridStaging, force = false, reviewed = false): Promise<boolean> {
  if (!staging.hasChanges) return true;
  const t = useTabs.getState().tabs[tabId];
  // Prod and staging show the SQL before writing (Apply and Ctrl+S alike); the sidecar's ProdWrite guard still decides.
  const env = useProfiles.getState().byId(t?.profileId)?.environment;
  const reviewFirst = env === "prod" || env === "staging";
  if (reviewFirst && !force && !reviewed && !(await reviewChanges(tabId, executionId, staging))) return false;
  const patch = await preparePatch(executionId, staging, force);
  if (!patch) return false;
  let result: GridApplyResult | undefined;
  for (let round = 0; round < 3; round++) {
    try {
      result = await rpc("data.applyPatch", patch);
    } catch (e) {
      const code = e instanceof RpcError ? e.appCode : undefined;
      const message = (e as Error).message;
      // The COMMIT was cut short: the rows may be in the table. Applying the same staging again is refused until a re-run.
      if (code === "outcomeUnknown") staging.outcomeUnknown = message;
      toast(code === "cancelled" ? "warn" : "error", code && DIRECT.has(code) ? message : `Could not apply: ${message}`);
      return false;
    }
    if (!result.blocked) break;
    if (!(await ask<boolean>((resolve) => ({ kind: "guard", block: result!.blocked!, resolve })))) return false;
    if (result.blocked.reason === "destructive") patch.confirmedDestructive = true;
    else patch.confirmedProd = true;
    result = undefined;
  }
  if (!result) return false;
  if (result.ok) {
    staging.commit(patch, result);
    const n = result.appliedUpdates + result.appliedInserts + result.appliedDeletes;
    toast("success", `${n} row${n === 1 ? "" : "s"} written to ${staging.edit.schema}.${staging.edit.table}.`);
    executionData.get(executionId)?.notify();
    return true;
  }
  return handleFailure(tabId, executionId, staging, result.conflicts ?? [], result.error ?? "Nothing was written.");
}

/** Errors whose message says it all (no "Could not apply:" in front). */
const DIRECT = new Set(["resultClosed", "schemaChanged", "databaseChanged", "cancelled", "outcomeUnknown"]);

async function handleFailure(tabId: string, executionId: string, staging: GridStaging, conflicts: GridConflictRow[], error: string): Promise<boolean> {
  const errors = conflicts.filter((c) => c.reason === "error");
  if (errors.length) {
    const c = errors[0];
    toast("error", `${c.insert ? `New row ${c.rowIndex + 1}` : `Row ${c.rowIndex + 1}`}: ${c.message ?? error} Nothing was written.`);
    return false;
  }
  const choice = await ask<"overwrite" | "discard" | "cancel">((resolve) => ({ kind: "gridConflicts", conflicts, staging, resolve }));
  const changedRows = conflicts.map((c) => c.rowIndex);
  // Still inside the first Apply's in-flight guard: the retry runs directly.
  if (choice === "discard") {
    staging.dropRows(changedRows);
    staging.markConflicts(changedRows);
    return staging.hasChanges ? applyOnce(tabId, executionId, staging) : false;
  }
  if (choice === "overwrite") {
    // Rows deleted on the server cannot be overwritten; their edits are dropped first.
    const gone = conflicts.filter((c) => c.reason === "deletedOnServer").map((c) => c.rowIndex);
    if (gone.length) staging.dropRows(gone);
    return staging.hasChanges ? applyOnce(tabId, executionId, staging, true) : false;
  }
  return false;
}

/** Tab close / re-run with staged grid changes: Apply, Discard or Cancel (like open transactions, §6 rule 5). */
export async function resolvePendingEdits(tabId: string): Promise<boolean> {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return true;
  const pending = pendingIn(t.executions);
  if (!pending.length) return true;
  const c = pending.reduce((a, s) => ({ u: a.u + s.counts.updates, i: a.i + s.counts.inserts, d: a.d + s.counts.deletes }), { u: 0, i: 0, d: 0 });
  const choice = await ask<"apply" | "discard" | "cancel">((resolve) => ({ kind: "gridPending", tabId, summary: `${c.u} updated, ${c.i} new and ${c.d} deleted row(s) are staged in the grid and not written yet.`, resolve }));
  if (choice === "cancel") return false;
  if (choice === "discard") {
    for (const s of pending) s.discardAll();
    return true;
  }
  for (const [key, s] of stagings) {
    if (!pending.includes(s)) continue;
    const executionId = key.slice(0, key.lastIndexOf(":"));
    if (!(await applyChanges(tabId, executionId, s))) return false;
  }
  return true;
}

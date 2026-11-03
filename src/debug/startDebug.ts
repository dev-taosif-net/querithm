// Starts a pldebugger session on a PostgreSQL function (§11): the call runs on a target connection and the
// function's source opens in a read-only tab whose gutter maps to breakpoints.
import type { SnapObject } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { ask, toast } from "../store/ui";
import { useTabs } from "../store/tabs";
import { useDiag } from "../store/diag";
import { ensureSession, newQueryTab } from "../app/actions";
import { editorForTab, getModel } from "../editor/editors";
import { monaco } from "../editor/monaco";

function callTemplate(o: SnapObject) {
  const q = (s: string) => (/^[a-z_][a-z0-9_]*$/.test(s) ? s : `"${s.replace(/"/g, '""')}"`);
  const args = (o.params ?? []).filter((p) => !p.output || p.type).map((p) => (p.name ? `${p.name} => NULL /* ${p.type} */` : `NULL /* ${p.type} */`));
  const name = `${q(o.schema)}.${q(o.name)}`;
  return o.kind === "proc" ? `CALL ${name}(${args.join(", ")});` : `SELECT * FROM ${name}(${args.join(", ")});`;
}

/**
 * The call runs on a target connection of its own, so the sidecar guards it like a query (§13 rule 7): the first run on
 * Prod and destructive statements in the call answer `blocked`, the guard dialog asks, and the attach is sent again with
 * the confirmation. Declined: the tab says the call did not run.
 */
async function attach(sessionId: string, functionOid: number, callSql: string): Promise<string | undefined> {
  const params = { sessionId, functionOid, callSql, breakpoints: null, confirmedProd: false, confirmedDestructive: false };
  for (let round = 0; round < 3; round++) {
    const r = await rpc("debug.attach", params);
    if (r.debugId) return r.debugId;
    if (!r.blocked || !(await ask<boolean>((resolve) => ({ kind: "guard", block: r.blocked!, resolve })))) return;
    if (r.blocked.reason === "prodFirstRun") params.confirmedProd = true;
    else params.confirmedDestructive = true;
  }
}

export async function startDebug(profileId: string, database: string, o: SnapObject) {
  const sql = await ask<string | null>((resolve) => ({ kind: "debugCall", title: `Debug ${o.schema}.${o.name}`, sql: callTemplate(o), resolve }));
  if (!sql) return;
  const tabId = newQueryTab({ profileId, database, title: `🐞 ${o.name}`, text: "-- waiting for the target to reach the function…", readOnly: true });
  try {
    const sessionId = await ensureSession(tabId);
    useTabs.getState().update(tabId, { bottomTab: "runtime", runtimeTool: "debug", bottomOpen: true });
    const debugId = await attach(sessionId, o.id, sql);
    if (!debugId) {
      getModel(tabId)?.setValue("-- The debug call was not run (the confirmation was declined).");
      return;
    }
    useDiag.getState().putDebug({ debugId, tabId, functionOid: o.id, state: "attaching", stack: [], variables: [], log: [], breakpoints: [] });
    useTabs.getState().update(tabId, { debugId });
    const src = await rpc("debug.source", { debugId, functionOid: o.id });
    getModel(tabId)?.setValue(src.source);
    wireGutter(tabId, debugId, o.id);
  } catch (e) {
    toast("error", `Debugger: ${(e as Error).message}`);
  }
}

/** Gutter click toggles a breakpoint (pldbg_set_breakpoint / pldbg_drop_breakpoint). */
function wireGutter(tabId: string, debugId: string, functionOid: number) {
  const ed = editorForTab(tabId);
  if (!ed) return;
  const sub = ed.onMouseDown((e) => {
    if (ed.getModel() !== getModel(tabId)) return;
    if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && e.target.type !== monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS) return;
    const line = e.target.position?.lineNumber;
    const d = useDiag.getState().debugs[debugId];
    if (!line || !d || d.state === "finished") return;
    const on = d.breakpoints.includes(line);
    useDiag.getState().patchDebug(debugId, { breakpoints: on ? d.breakpoints.filter((l) => l !== line) : [...d.breakpoints, line] });
    void rpc(on ? "debug.dropBreakpoint" : "debug.setBreakpoint", { debugId, functionOid, line }).catch((err) => toast("error", (err as Error).message));
  });
  const off = useTabs.subscribe((s) => {
    if (!s.tabs[tabId]?.debugId) {
      sub.dispose();
      off();
    }
  });
}

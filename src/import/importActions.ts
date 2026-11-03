// Import wizard actions: picking a file through the shell (the webview only ever holds a token), a session
// of its own on the target database, and running the wizard's DDL through the same guarded path as query.execute.
import type { ExecuteOptions, QueryDoneEvent } from "../ipc/contract";
import { onNotify, onSidecarStatus, rpc, shell } from "../ipc/client";
import { isTauri, transport } from "../ipc/transport";
import { useProfiles } from "../store/profiles";
import { obtainSecret, useConnections } from "../store/connections";
import { ask } from "../store/ui";

export interface PickedFile {
  token: string;
  name: string;
  size: number;
}

/** The native dialog (app) or a file input + upload to the dev bridge (browser). Null when cancelled. */
export async function pickImportFile(): Promise<PickedFile | null> {
  if (isTauri) return transport().invoke<PickedFile | null>("import_pick_file");
  const file = await new Promise<File | null>((resolve) => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".csv,.tsv,.txt,.json,.ndjson,.jsonl,.xlsx";
    input.onchange = () => resolve(input.files?.[0] ?? null);
    input.oncancel = () => resolve(null);
    input.click();
  });
  if (!file) return null;
  const bytes = new Uint8Array(await file.arrayBuffer());
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return transport().invoke<PickedFile>("import_stage_file", { name: file.name, data: btoa(bin) });
}

/** A session of the wizard's own on (profile, database), so imports never block a query tab. */
export async function openImportSession(profileId: string, database: string): Promise<string> {
  await useConnections.getState().sessionFor(profileId); // connected, credentials loaded
  const profile = useProfiles.getState().byId(profileId);
  const secret = profile ? await obtainSecret(profile) : undefined;
  if (secret === null) throw new Error("Connection cancelled.");
  const s = await rpc("session.open", { profileId, database, secret: secret ?? null });
  return s.sessionId;
}

export async function closeImportSession(sessionId: string | undefined) {
  if (sessionId) await rpc("session.close", { sessionId, force: true }).catch(() => {});
}

/**
 * How long the wizard waits for one DDL statement before it cancels it (widening a column of a large table can take
 * minutes). A safety net: the engine stopping ends the wait at once, and the sidecar's cancel always ends the run.
 */
export const DDL_WAIT_MS = 10 * 60_000;
export const DDL_ENGINE_STOPPED = "The Querithm engine stopped while the statement ran, so whether it took effect is unknown. Check the table, then try again.";
export const DDL_TIMED_OUT = `The statement did not finish within ${DDL_WAIT_MS / 60_000} minutes, so it was cancelled.`;

/**
 * Runs DDL with the sidecar's guards (Prod first run, destructive) and waits for it, bounded (DDL_WAIT_MS, or the engine
 * stopping); returns the error, or null.
 */
export async function executeGuarded(sessionId: string, sql: string): Promise<string | null> {
  const options: ExecuteOptions = {
    maxRows: 0, statisticsIo: false, statisticsTime: false, actualPlan: false, confirmedDestructive: false, confirmedProd: false, noHistory: false,
    parameters: null, lineOffset: 1,
  };
  for (let round = 0; round < 3; round++) {
    // query.done can arrive before the reply is handled — a fast statement on a session that is already open (the
    // query tab's own SQLite session): listen before sending, and keep what arrives early.
    const early = new Map<string, QueryDoneEvent>();
    let wanted: string | undefined;
    let settle: ((e: QueryDoneEvent | string) => void) | undefined;
    let stop = () => {};
    const off = onNotify("query.done", (e) => {
      if (e.executionId === wanted && settle) settle(e);
      else if (wanted === undefined && early.size < 100) early.set(e.executionId, e);
    });
    try {
      const r = await rpc("query.execute", { sessionId, sql, options });
      if (r.blocked) {
        if (!(await ask<boolean>((resolve) => ({ kind: "guard", block: r.blocked!, resolve })))) return "Cancelled.";
        if (r.blocked.reason === "prodFirstRun") options.confirmedProd = true;
        else options.confirmedDestructive = true;
        continue;
      }
      const id = (wanted = r.executionId!);
      // Nothing reads this run's frames (its end, rows a statement returns): the shell drops them instead of holding
      // them, where a full queue would stall every tab's results (L14).
      void shell.resultsDiscard(id).catch(() => {});
      const done = early.get(id) ?? (await new Promise<QueryDoneEvent | string>((resolve) => {
        settle = resolve;
        const timer = setTimeout(() => {
          void rpc("query.cancel", { executionId: id }).catch(() => {});
          resolve(DDL_TIMED_OUT);
        }, DDL_WAIT_MS);
        // A restart loses the run's end (§4): its outcome is unknown, and the wizard says so instead of waiting.
        const offStatus = onSidecarStatus((st) => st.state !== "running" && resolve(DDL_ENGINE_STOPPED));
        stop = () => {
          clearTimeout(timer);
          offStatus();
        };
      }));
      void rpc("query.close", { executionId: id }).catch(() => {});
      if (typeof done === "string") return done;
      return done.state === "done" ? null : done.error?.message ?? "The statement failed.";
    } finally {
      stop();
      off();
    }
  }
  return "Cancelled.";
}

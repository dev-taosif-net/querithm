// Schema compare actions: opening the tab, model files through the shell (the webview only holds a token),
// and the sides as the sidecar takes them.
import type { CompareSide } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { isTauri, transport } from "../ipc/transport";
import { activeTabState } from "../app/actions";
import { openTool } from "../app/toolActions";
import { saveBytes } from "../app/saveBytes";
import { ownSessionFor, sessionParam, tabScope, toolSession } from "../store/snapshots";
import { toast } from "../store/ui";
import type { CompareTabParams, SideState } from "./schemaCompare";
import type { DataCompareTabParams } from "./dataCompare";

const asParams = (p: CompareTabParams) => p as unknown as Record<string, unknown>;

/**
 * The database a compare starts from: the one given (explorer), else the active tab's. A session-owned engine's side
 * keeps a query tab's session on it (the active tab's own, as the explorer reads it), so it reads that session's view.
 */
function startFrom(source: { profileId: string; database: string } | undefined): { profileId: string; database: string; sessionId?: string } | undefined {
  if (source) return { profileId: source.profileId, database: source.database, ...sessionParam(ownSessionFor(source.profileId, source.database)) };
  const t = activeTabState();
  return t?.profileId && t.database ? { profileId: t.profileId, database: t.database, ...sessionParam(tabScope(t)?.sessionId) } : undefined;
}

/** Ctrl+Shift+D / Explorer: the given database (else the active tab's) is the source; the target is picked in the tab. */
export function openSchemaCompare(source?: { profileId: string; database: string; schema?: string | null }) {
  const from = startFrom(source);
  openTool("schemaCompare", "Schema compare", {
    profileId: from?.profileId, database: from?.database,
    params: asParams({
      source: from ? { kind: "live", profileId: from.profileId, database: from.database, schema: source?.schema ?? null, ...sessionParam(from.sessionId) } : { kind: "live" },
      target: { kind: "live" },
    }),
  });
}

/** Explorer → table → Compare data… (that table as the source), or the active tab's database. */
export function openDataCompare(source?: { profileId: string; database: string; table?: string }) {
  const from = startFrom(source);
  const bound = sessionParam(from?.sessionId);
  const params: DataCompareTabParams = {
    source: { mode: "table", profileId: from?.profileId, database: from?.database, table: source?.table, ...bound },
    target: { mode: "table", profileId: from?.profileId, database: from?.database, ...bound },
  };
  openTool("dataCompare", "Data compare", { profileId: from?.profileId, database: from?.database, params: params as unknown as Record<string, unknown> });
}

export interface PickedModel {
  token: string;
  name: string;
  size: number;
}

/** A saved schema model: the native dialog (app) or an upload to the dev bridge (browser). Null when cancelled. */
export async function pickSchemaFile(): Promise<PickedModel | null> {
  if (isTauri) return transport().invoke<PickedModel | null>("compare_pick_file");
  const file = await new Promise<File | null>((resolve) => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".json";
    input.onchange = () => resolve(input.files?.[0] ?? null);
    input.oncancel = () => resolve(null);
    input.click();
  });
  if (!file) return null;
  const bytes = new Uint8Array(await file.arrayBuffer());
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return transport().invoke<PickedModel>("import_stage_file", { name: file.name, data: btoa(bin) });
}

/** The side as schemaCompare.* takes it: the side's session (toolSession: its tab session, else the explorer's), or the file token. */
export async function sideParams(s: SideState): Promise<CompareSide> {
  if (s.kind === "file") return { fileToken: s.token };
  if (!s.profileId || !s.database) throw new Error("Choose a connection and a database.");
  const sessionId = await toolSession(s);
  return { sessionId, database: s.database, schema: s.schema ?? null };
}

/** Saves a live side's schema as a .qschema.json file to compare against later (or to keep as a baseline). */
export async function exportSchemaModel(s: SideState) {
  if (s.kind !== "live" || !s.profileId || !s.database) return;
  try {
    const sessionId = await toolSession(s);
    const file = await rpc("schemaCompare.exportModel", { sessionId, database: s.database, schema: s.schema ?? null });
    const saved = await saveBytes(file.fileName, new TextEncoder().encode(file.json), { name: "Querithm schema", extension: "json", mime: "application/json" });
    if (saved) toast("success", `Saved ${saved}`);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

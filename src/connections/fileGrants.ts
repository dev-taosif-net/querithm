// Database files (Phase D0c): the shell's dialog picks the file and keeps a grant; the webview only ever holds the grant
// id and the file's name, never the path (§13 rule 6). Writes need the consent dialog the shell shows itself — the
// webview asks for it and learns the answer, it can never answer for the user.
import { isTauri, transport } from "../ipc/transport";

export interface PickedDatabase {
  grantId: string;
  name: string;
  size: number;
}

/** The shell's native dialog (app) or a file input + upload to the dev bridge (browser). Null when cancelled. */
export async function pickDatabaseFile(): Promise<PickedDatabase | null> {
  if (isTauri) return transport().invoke<PickedDatabase | null>("db_pick_file");
  const file = await new Promise<File | null>((resolve) => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".db,.sqlite,.sqlite3,.db3,.duckdb";
    input.onchange = () => resolve(input.files?.[0] ?? null);
    input.oncancel = () => resolve(null);
    input.click();
  });
  if (!file) return null;
  const bytes = new Uint8Array(await file.arrayBuffer());
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return transport().invoke<PickedDatabase>("db_stage_file", { name: file.name, data: btoa(bin) });
}

/** Asks the shell to show "Allow writes to this file?"; true when the user allowed it there. */
export function requestWriteAccess(grantId: string): Promise<boolean> {
  return transport().invoke<boolean>("db_grant_write", { grantId });
}

/** Back to read-only (no dialog needed to reduce access). */
export function makeReadOnly(grantId: string): Promise<void> {
  return transport().invoke<void>("db_grant_read_only", { grantId });
}

/** The relay's grant refusals: each asks the user to pick the file again. */
export const FILE_GRANT_CODES = ["fileGrant", "fileGrantUnknown", "fileGrantBound", "fileMissing", "fileMoved", "fileReplaced"] as const;

export function needsRepick(appCode: string | undefined): boolean {
  return !!appCode && (FILE_GRANT_CODES as readonly string[]).includes(appCode);
}

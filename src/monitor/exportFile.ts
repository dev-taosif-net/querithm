// Monitor exports are written by the sidecar under <data dir>/exports (it never writes into user folders). The
// webview reads that file (fs plugin, scoped to app data; the dev bridge in the browser) and saves it where the user picks.
import { isTauri, transport } from "../ipc/transport";
import { saveBytes } from "../app/saveBytes";

export async function readExport(path: string): Promise<string> {
  if (isTauri) {
    const { readTextFile } = await import("@tauri-apps/plugin-fs");
    return readTextFile(path);
  }
  return transport().invoke<string>("export_read", { path });
}

export async function saveExport(path: string, fileName: string, filter: { name: string; extension: string; mime: string }): Promise<string | null> {
  const text = await readExport(path);
  return saveBytes(fileName, new TextEncoder().encode(text), filter);
}

// Notebook files (Phase C3/C4): open and save .sqlbook files the user picks (the shell's dialog + fs plugins, as
// script files are; the sidecar never touches user folders), import Jupyter notebooks, reopen closed notebooks.
import { isTauri } from "../ipc/transport";
import { activateTab } from "../app/actions";
import { useProfiles } from "../store/profiles";
import { useTabs } from "../store/tabs";
import { toast } from "../store/ui";
import { hasOutputs, NOTEBOOK_EXTENSION, parseNotebook, type NotebookFile } from "./format";
import { parseIpynb } from "./ipynb";
import { notebookHooks } from "./hooks";
import { newNotebookTab, serializeTab, syncSources } from "./runtime";

const baseName = (path: string) => path.split(/[\\/]/).pop() ?? path;
const message = (e: unknown) => (e instanceof Error ? e.message : String(e));

interface OpenAs {
  title: string;
  filePath?: string;
  /** Overrides the file's connection (reopening a closed tab keeps the tab's). */
  profileId?: string;
  database?: string;
  dirty?: boolean;
  activate?: boolean;
  atEnd?: boolean;
  id?: string;
  pinned?: boolean;
}

/**
 * Opens a parsed notebook in a tab. A connection that is not on this computer (or was deleted) leaves the notebook
 * disconnected with a notice; the file's warnings are shown under the toolbar.
 */
export function openParsed(nb: NotebookFile, warnings: string[], o: OpenAs): string {
  const notices = [...warnings];
  let profileId = o.profileId;
  let database = o.database;
  if (profileId === undefined) {
    const saved = nb.connection;
    if (saved && useProfiles.getState().byId(saved.profileId)) {
      profileId = saved.profileId;
      database = saved.database ?? undefined;
    } else {
      profileId = "";
      if (saved) notices.unshift("The connection this notebook was saved with is not on this computer. Pick a connection (Ctrl+Shift+U) to run it.");
    }
  }
  return newNotebookTab({
    id: o.id, title: o.title, filePath: o.filePath, profileId, database, cells: nb.cells, saveOutputs: hasOutputs(nb), notices,
    activate: o.activate, atEnd: o.atEnd, pinned: o.pinned, dirty: o.dirty,
  });
}

/** Reads a .sqlbook document into a tab; null (with the reason shown) when it cannot be opened. */
export function openNotebookText(text: string, o: OpenAs, byteLength?: number): string | null {
  const r = parseNotebook(text, byteLength);
  if (!r.ok) {
    toast("error", `Could not open ${o.title}: ${r.reason}`);
    return null;
  }
  return openParsed(r.notebook, r.warnings, o);
}

async function pick(name: string, extensions: string[]): Promise<{ path?: string; name: string; text: string; size?: number } | null> {
  if (!isTauri) {
    return new Promise((resolve) => {
      const input = document.createElement("input");
      input.type = "file";
      input.accept = extensions.map((e) => `.${e}`).join(",");
      input.onchange = async () => {
        const file = input.files?.[0];
        resolve(file ? { name: file.name, text: await file.text(), size: file.size } : null);
      };
      input.click();
    });
  }
  const { open } = await import("@tauri-apps/plugin-dialog");
  const path = await open({ multiple: false, filters: [{ name, extensions }] });
  if (!path || Array.isArray(path)) return null;
  const { readTextFile } = await import("@tauri-apps/plugin-fs");
  return { path, name: baseName(path), text: await readTextFile(path) };
}

/** Opens a .sqlbook file at `path` (or activates the tab that already shows it). */
export async function openNotebookPath(path: string): Promise<string | null> {
  const open = Object.values(useTabs.getState().tabs).find((t) => t.notebook && t.filePath === path);
  if (open) {
    activateTab(open.id);
    return open.id;
  }
  try {
    const { readTextFile } = await import("@tauri-apps/plugin-fs");
    return openNotebookText(await readTextFile(path), { title: baseName(path), filePath: path });
  } catch (e) {
    toast("error", `Could not read ${baseName(path)}: ${message(e)}`);
    return null;
  }
}

/** File ▸ Open notebook… */
export async function openNotebookFile(): Promise<string | null> {
  try {
    if (isTauri) {
      const { open } = await import("@tauri-apps/plugin-dialog");
      const path = await open({ multiple: false, filters: [{ name: "Querithm notebook", extensions: [NOTEBOOK_EXTENSION] }] });
      return path && !Array.isArray(path) ? openNotebookPath(path) : null;
    }
    const f = await pick("Querithm notebook", [NOTEBOOK_EXTENSION]);
    if (!f) return null;
    return openNotebookText(f.text, { title: f.name, filePath: f.path }, f.size);
  } catch (e) {
    toast("error", `Could not open the notebook: ${message(e)}`);
    return null;
  }
}

/**
 * Save (Ctrl+S) / Save as: writes the document to the user's file. A failed write (disk full, no permission) keeps the
 * tab modified and the recovery copy in the data folder untouched, and says why.
 */
export async function saveNotebook(tabId: string, as = false): Promise<boolean> {
  const t = useTabs.getState().tabs[tabId];
  if (!t?.notebook) return false;
  syncSources(tabId);
  const text = serializeTab(tabId);
  if (text == null) return false;
  const name = t.title.endsWith(`.${NOTEBOOK_EXTENSION}`) ? t.title : `${t.title}.${NOTEBOOK_EXTENSION}`;
  if (!isTauri) {
    const url = URL.createObjectURL(new Blob([text], { type: "application/json" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
    useTabs.getState().update(tabId, { dirty: false });
    return true;
  }
  let path = as ? undefined : t.filePath;
  try {
    if (!path) {
      const { save } = await import("@tauri-apps/plugin-dialog");
      path = (await save({ defaultPath: t.filePath ?? name, filters: [{ name: "Querithm notebook", extensions: [NOTEBOOK_EXTENSION] }] })) ?? undefined;
      if (!path) return false;
    }
    const { writeTextFile } = await import("@tauri-apps/plugin-fs");
    await writeTextFile(path, text);
  } catch (e) {
    toast("error", `Could not save ${path ? baseName(path) : name}: ${message(e)}. Your changes are still here (unsaved) and the recovery copy is intact.`);
    return false;
  }
  useTabs.getState().update(tabId, { filePath: path, title: baseName(path), dirty: false });
  toast("success", `Saved ${path}`);
  return true;
}

/** File ▸ Import Jupyter notebook…: `%%sql` code cells become SQL cells. */
export async function importIpynb(): Promise<string | null> {
  try {
    const f = await pick("Jupyter notebook", ["ipynb"]);
    if (!f) return null;
    const r = parseIpynb(f.text);
    if (!r.ok) {
      toast("error", `Could not import ${f.name}: ${r.reason}`);
      return null;
    }
    const notices = r.skipped ? [`${r.skipped} cell${r.skipped === 1 ? " was" : "s were"} skipped: only %%sql code cells and markdown cells are imported.`] : [];
    if (!r.cells.length) notices.push("The notebook had no %%sql or markdown cells.");
    return newNotebookTab({ title: f.name.replace(/\.ipynb$/i, `.${NOTEBOOK_EXTENSION}`), cells: r.cells, notices, dirty: true });
  } catch (e) {
    toast("error", `Could not import the notebook: ${message(e)}`);
    return null;
  }
}

notebookHooks.reopen = (o) => openNotebookText(o.text, { title: o.title, filePath: o.filePath, profileId: o.profileId, database: o.database, dirty: o.dirty, activate: o.activate, atEnd: o.atEnd }) ?? undefined;

// Local scripts (§10): tabs autosave to app-data/scripts/<tabId>.sql 2 s after a change and restore on launch with
// their bound profile and database; notebooks (Phase C3) to <tabId>.sqlbook the same way. The Object Explorer's servers reconnect in the background (§16). Save As / Open
// use the shell's dialog + fs plugins.
// M23: every recovery copy and the workspace are written to a temporary name and renamed into place, so a crash or a
// second writer never leaves a cut file; the workspace's previous good copy is kept as workspace.json.bak and read when
// the file does not parse; a script no saved tab names is offered back ("Recover N unsaved scripts"); a write that fails
// says so (at most once a minute). Only the app instance that owns the workspace (the first one, src-tauri instance.rs)
// restores and writes it; a second window starts with a new tab. W5: a second window keeps `scripts/<window>.live`
// fresh (every LIVE_BEAT_MS, listing the tabs whose copies it writes), and the owner never offers — or removes — the
// copies a live window lists; a beat older than LIVE_STALE_MS belongs to a window that is gone, and goes.
import { isTauri } from "../ipc/transport";
import { useTabs, type TabState } from "../store/tabs";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { flushSettings, useSettings } from "../store/settings";
import { useUi, toast } from "../store/ui";
import { getModel, onModelContent, editorForTab } from "../editor/editors";
import { newQueryTab, activeTabState, workspaceHooks } from "./actions";
import { restoreExpansion, savedExpansion, useExplorer } from "../explorer/explorerState";
import { cleanCharts } from "../results/resultViews";
import { cleanLayout } from "../results/resultLayout";
import { notebookOf, useNotebooks } from "../notebook/store";
import { markDirty, newNotebookTab, ownerOfKey, serializeTab } from "../notebook/runtime";
import { openNotebookPath, openNotebookText, saveNotebook } from "../notebook/files";
import { getAppInfo } from "./appInfo";

const DIR = "querithm/scripts";
const WORKSPACE = "workspace.json";
const BACKUP = "workspace.json.bak";

/** False in a second app window (M23): it neither restores nor writes the saved tabs. Set by restoreWorkspace. */
let owner = true;

interface SavedTab {
  id: string;
  title: string;
  profileId?: string;
  database?: string;
  boundObject?: TabState["boundObject"];
  filePath?: string;
  readOnly?: boolean;
  cursorLine?: number;
  pinned?: boolean;
  /** Grid or chart per result index and the chart specs (never the data). */
  charts?: TabState["charts"];
  /** The tab's own layout for several result sets (unset = the Settings default). */
  resultLayout?: TabState["resultLayout"];
  /** A notebook: its document is the recovery copy <id>.sqlbook. */
  notebook?: boolean;
  /** A notebook changed since it was last saved to its file. */
  dirty?: boolean;
}

interface WorkspaceFile {
  version: 1;
  tabs: SavedTab[];
  activeId?: string;
  explorerWidth?: number;
  /** Profile ids of the Object Explorer's servers, in order (ids only: secrets stay in the keyring). */
  servers?: string[];
  /** The Explorer's expanded rows, by name (server, database, folder, object; §10), restored with the servers. */
  explorerExpanded?: string[];
}

let fsModule: Promise<typeof import("@tauri-apps/plugin-fs")> | undefined;
/** The fs plugin, loaded once (restore reads, the backup and the recovery listing run side by side). */
function fs() {
  fsModule ??= import("@tauri-apps/plugin-fs");
  return fsModule;
}

/** A tab's recovery copy: `sql` for a query tab, `sqlbook` for a notebook (same folder, so no new fs permission). */
type Ext = "sql" | "sqlbook";
const storageKey = (id: string, ext: Ext) => (ext === "sql" ? `querithm.script.${id}` : `querithm.notebook.${id}`);

/** W5: how often a second window says it is still running, and when a silent one counts as gone (a crash). */
export const LIVE_BEAT_MS = 60_000;
export const LIVE_STALE_MS = 3 * LIVE_BEAT_MS;
/** A rename that fails is tried again this many times, this far apart (an antivirus or indexer briefly holding the file). */
export const RENAME_RETRIES = 3;
export const RENAME_RETRY_MS = 100;

let tmpSeq = 0;
/** A temporary name in the same folder; its time lets a later start remove what a crash left (`.<ms>-<n>.tmp`). */
const tmpName = (name: string) => `${DIR}/${name}.${Date.now()}-${++tmpSeq}.tmp`;

/**
 * Writes `scripts/<name>` whole or not at all: the text goes to a temporary file of its own (never shared with another
 * write or instance), which is then renamed over the old file. A failure leaves the old file and removes the temporary.
 */
async function writeAtomic(name: string, text: string) {
  const f = await fs();
  const base = { baseDir: f.BaseDirectory.Data };
  await f.mkdir(DIR, { ...base, recursive: true }).catch(() => {});
  const tmp = tmpName(name);
  try {
    await f.writeTextFile(tmp, text, base);
    for (let attempt = 0; ; attempt++) {
      try {
        await f.rename(tmp, `${DIR}/${name}`, { oldPathBaseDir: f.BaseDirectory.Data, newPathBaseDir: f.BaseDirectory.Data });
        break;
      } catch (e) {
        // Windows refuses a rename while another program has the file open (an antivirus scan, the search indexer):
        // a few short retries, then the failure as before (the old file stays, the temporary goes).
        if (attempt >= RENAME_RETRIES) throw e;
        await new Promise((resolve) => setTimeout(resolve, RENAME_RETRY_MS));
      }
    }
  } catch (e) {
    await f.remove(tmp, base).catch(() => {});
    throw e;
  }
}

async function readText(name: string): Promise<string | null> {
  const f = await fs();
  try {
    return await f.readTextFile(`${DIR}/${name}`, { baseDir: f.BaseDirectory.Data });
  } catch {
    return null;
  }
}

const AUTOSAVE_WARN_MS = 60_000;
let lastWarned = -Infinity;
/** M23: a write that failed says so — non-modal, at most once a minute — instead of losing the work silently. */
function autosaveFailed(e: unknown) {
  console.error("[autosave]", e);
  const now = Date.now();
  if (now - lastWarned < AUTOSAVE_WARN_MS) return;
  lastWarned = now;
  const raw = e instanceof Error ? e.message : String(e);
  // The fs plugin's message names the whole path; the reason after "error:" is what the user can act on.
  const reason = raw.includes("error:") ? raw.slice(raw.lastIndexOf("error:") + 6).trim() : raw;
  toast("warn", `Autosave failed (${reason || "unknown error"}). Your tabs stay open; Querithm keeps trying. Save important scripts with Save As.`);
}

/** Test hook: forget the last warning time, the owner flag and this window's liveness beat. */
export function resetPersistence() {
  lastWarned = -Infinity;
  owner = true;
  liveIds.clear();
  windowId = newWindowId();
}

// ---- W5: a second window's liveness beat

const newWindowId = () => `w${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;
/** This window's beat file name (`<id>.live`, beside the copies: no other fs permission). */
let windowId = newWindowId();
/** The tabs whose recovery copies this (second) window has written: its beat lists them. */
const liveIds = new Set<string>();

interface LiveBeat {
  at: number;
  ids: string[];
}

/** Says "this window runs and owns these copies" (a second window only; the owner's tabs are in its workspace). */
async function writeBeat() {
  if (owner || !isTauri) return;
  const beat: LiveBeat = { at: Date.now(), ids: [...liveIds] };
  await writeAtomic(`${windowId}.live`, JSON.stringify(beat));
}

/** A second window that quits: its copies are then the next owner's to offer back. */
async function removeBeat() {
  if (owner || !isTauri) return;
  const f = await fs();
  await f.remove(`${DIR}/${windowId}.live`, { baseDir: f.BaseDirectory.Data }).catch(() => {});
}

/**
 * The copies other windows still run with (their beat is fresh): never offered back or removed. A beat that went stale
 * belongs to a window that is gone (it crashed) and is removed; its copies are offered like any other.
 */
async function liveScriptIds(now = Date.now()): Promise<Set<string>> {
  const live = new Set<string>();
  if (!isTauri) return live;
  const f = await fs();
  const entries = await f.readDir(DIR, { baseDir: f.BaseDirectory.Data }).catch(() => []);
  for (const e of entries) {
    if (!e.isFile || !/^[A-Za-z0-9_-]+\.live$/.test(e.name)) continue;
    let beat: Partial<LiveBeat> | null = null;
    try {
      beat = JSON.parse(await f.readTextFile(`${DIR}/${e.name}`, { baseDir: f.BaseDirectory.Data }));
    } catch {
      beat = null; // unreadable: treated as gone
    }
    const at = typeof beat?.at === "number" ? beat.at : -Infinity;
    if (now - at < LIVE_STALE_MS && Array.isArray(beat?.ids)) {
      for (const id of beat.ids) if (typeof id === "string") live.add(id);
    } else void f.remove(`${DIR}/${e.name}`, { baseDir: f.BaseDirectory.Data }).catch(() => {});
  }
  return live;
}

async function writeScript(id: string, text: string, ext: Ext = "sql") {
  if (!isTauri) return void localStorage.setItem(storageKey(id, ext), text);
  // A second window lists a copy in its beat before the copy exists, so the owner never sees it unlisted.
  if (!owner && !liveIds.has(id)) {
    liveIds.add(id);
    await writeBeat();
  }
  await writeAtomic(`${id}.${ext}`, text);
}

async function readScript(id: string, ext: Ext = "sql"): Promise<string | null> {
  if (!isTauri) return localStorage.getItem(storageKey(id, ext));
  return readText(`${id}.${ext}`);
}

async function removeScript(id: string, only?: Ext) {
  if (!isTauri) {
    if (only !== "sqlbook") localStorage.removeItem(storageKey(id, "sql"));
    if (only !== "sql") localStorage.removeItem(storageKey(id, "sqlbook"));
    return;
  }
  const f = await fs();
  if (only !== "sqlbook") await f.remove(`${DIR}/${id}.sql`, { baseDir: f.BaseDirectory.Data }).catch(() => {});
  if (only !== "sql") await f.remove(`${DIR}/${id}.sqlbook`, { baseDir: f.BaseDirectory.Data }).catch(() => {});
}

/** Writes a notebook's recovery copy. */
async function writeNotebookCopy(id: string) {
  const text = serializeTab(id);
  if (text != null) await writeScript(id, text, "sqlbook");
}

/** A second window never writes the workspace: the first one's tabs stay what the next launch restores. */
async function writeWorkspace(w: WorkspaceFile) {
  if (!owner) return;
  const text = JSON.stringify(w, null, 1);
  if (!isTauri) return void localStorage.setItem("querithm.workspace", text);
  await writeAtomic(WORKSPACE, text);
}

function parseWorkspace(text: string | null): WorkspaceFile | null {
  if (text == null) return null;
  try {
    const w = JSON.parse(text);
    return w && typeof w === "object" && !Array.isArray(w) ? (w as WorkspaceFile) : null;
  } catch {
    return null;
  }
}

/**
 * The saved workspace: the file when it parses — then also kept as the backup, the previous good copy — else the
 * backup. `damaged`: a file was there but could not be read.
 */
async function readWorkspace(): Promise<{ w: WorkspaceFile | null; damaged: boolean; fromBackup: boolean }> {
  const main = isTauri ? await readText(WORKSPACE) : localStorage.getItem("querithm.workspace");
  const w = parseWorkspace(main);
  if (w) {
    const keep = main!;
    if (!isTauri) localStorage.setItem("querithm.workspace.bak", keep);
    else void writeAtomic(BACKUP, keep).catch(autosaveFailed);
    return { w, damaged: false, fromBackup: false };
  }
  const bak = parseWorkspace(isTauri ? await readText(BACKUP) : localStorage.getItem("querithm.workspace.bak"));
  return { w: bak, damaged: main != null, fromBackup: !!bak };
}

function snapshotWorkspace(): WorkspaceFile {
  const s = useTabs.getState();
  return {
    version: 1,
    activeId: s.activeId,
    explorerWidth: useUi.getState().explorerWidth,
    // On-screen order (tabs can be dragged), then any tab the strip does not show yet.
    tabs: [...new Set([...(workspaceHooks.allOrder?.() ?? []), ...s.order])]
      .map((id) => s.tabs[id])
      .filter((t) => t && !t.title.startsWith("🐞"))
      .map((t) => ({
        id: t.id, title: t.title, profileId: t.profileId, database: t.database, boundObject: t.boundObject, filePath: t.filePath, readOnly: t.readOnly,
        pinned: t.pinned || undefined,
        charts: t.charts,
        resultLayout: t.resultLayout,
        notebook: t.notebook || undefined,
        dirty: (t.notebook && t.dirty) || undefined,
        cursorLine: editorForTab(t.id)?.getPosition()?.lineNumber,
      })),
    servers: [...useConnections.getState().order],
    explorerExpanded: savedExpansion(),
  };
}

/**
 * Reconnects the servers the Object Explorer showed when the app closed, without blocking startup and without
 * asking: a server whose password is not in the keyring (or needs an SSH answer) shows an error with a Connect item.
 * Deleted profiles and malformed entries are skipped.
 */
function reconnectServers(ids: unknown) {
  if (!Array.isArray(ids)) return;
  const profiles = useProfiles.getState();
  const valid = [...new Set(ids.filter((id): id is string => typeof id === "string" && !!profiles.byId(id)))];
  for (const id of valid) void useConnections.getState().connect(id, { quiet: true });
}

export async function restoreWorkspace(): Promise<boolean> {
  owner = await getAppInfo().then((i) => i.workspaceOwner !== false, () => true);
  if (!owner) {
    toast("info", "Another Querithm window has your saved tabs open; this window starts with a new tab.");
    return false;
  }
  const { w, damaged, fromBackup } = await readWorkspace();
  if (fromBackup) toast("warn", "The saved tabs could not be read; the previous copy of them was restored.");
  else if (damaged) toast("warn", "The saved tabs could not be read. Unsaved scripts are offered for recovery.");
  const tabs = Array.isArray(w?.tabs) ? w.tabs.filter((t): t is SavedTab => !!t && typeof t.id === "string") : [];
  // Off the startup path: scripts no saved tab names, and what a crash left of a write.
  void offerOrphans(new Set(tabs.map((t) => t.id))).catch((e) => console.error("[recovery]", e));
  reconnectServers(w?.servers);
  // After the servers are back in the Explorer (connecting), so their expansion is not pruned as of servers gone.
  restoreExpansion(w?.explorerExpanded);
  if (!w || !tabs.length) return false;
  for (const t of tabs) {
    if (t.notebook) {
      await restoreNotebook(t, t.id === w.activeId);
      continue;
    }
    const text = (await readScript(t.id)) ?? "";
    newQueryTab({
      id: t.id, title: t.title, profileId: t.profileId, database: t.database, boundObject: t.boundObject, filePath: t.filePath, readOnly: t.readOnly, pinned: t.pinned, text,
      charts: cleanCharts(t.charts), resultLayout: cleanLayout(t.resultLayout), activate: t.id === w.activeId, atEnd: true,
    });
  }
  if (w.activeId && useTabs.getState().tabs[w.activeId]) useTabs.getState().setActive(w.activeId);
  if (w.explorerWidth) useUi.getState().setExplorer({ explorerWidth: w.explorerWidth });
  return true;
}

type Script = { id: string; ext: Ext };

/** The recovery copies on disk (browser: in storage). */
async function listScripts(): Promise<Script[]> {
  const parse = (name: string): Script | null => {
    const m = /^([A-Za-z0-9_-]+)\.(sql|sqlbook)$/.exec(name);
    return m ? { id: m[1], ext: m[2] as Ext } : null;
  };
  if (!isTauri) {
    const out: Script[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i) ?? "";
      if (k.startsWith("querithm.script.")) out.push({ id: k.slice(16), ext: "sql" });
      else if (k.startsWith("querithm.notebook.")) out.push({ id: k.slice(18), ext: "sqlbook" });
    }
    return out;
  }
  const f = await fs();
  const entries = await f.readDir(DIR, { baseDir: f.BaseDirectory.Data }).catch(() => []);
  // A temporary file a crash left mid-write (its name carries its time) goes after an hour; a newer one may be a write
  // still going on.
  const now = Date.now();
  for (const e of entries) {
    const t = e.isFile ? /\.(\d{13})-\d+\.tmp$/.exec(e.name) : null;
    if (t && now - Number(t[1]) > 3_600_000) void f.remove(`${DIR}/${e.name}`, { baseDir: f.BaseDirectory.Data }).catch(() => {});
  }
  return entries.filter((e) => e.isFile).map((e) => parse(e.name)).filter((x): x is Script => !!x);
}

/**
 * M23: scripts no saved tab names — a tab opened just before a crash, a second window's tabs, a workspace that could
 * not be read — are offered back instead of staying on disk unseen. Empty ones hold nothing to recover and go.
 */
async function offerOrphans(saved: Set<string>) {
  const found: (Script & { text: string })[] = [];
  const live = await liveScriptIds();
  for (const s of await listScripts()) {
    if (saved.has(s.id) || live.has(s.id)) continue;
    const text = await readScript(s.id, s.ext);
    if (text == null) continue;
    if (!text.trim()) void removeScript(s.id, s.ext);
    else found.push({ ...s, text });
  }
  if (!found.length) return;
  const n = found.length;
  const what = `${n} unsaved script${n === 1 ? "" : "s"}`;
  showBannerWhenFree({
    kind: "warn",
    text: `${what} from an earlier session ${n === 1 ? "was" : "were"} not restored.`,
    action: { label: `Recover ${what}`, run: () => void recoverOrphans(found) },
  });
}

/** Opens each as a new tab (a new id: the copy is this window's from now on), then removes the old copy. */
async function recoverOrphans(found: (Script & { text: string })[]) {
  const ui = useUi.getState();
  if (ui.banner?.text.includes("from an earlier session")) ui.setBanner(null);
  let i = 0;
  for (const s of found) {
    i++;
    const title = found.length === 1 ? "Recovered" : `Recovered ${i}`;
    const opened = s.ext === "sqlbook" ? openNotebookText(s.text, { title: `${title}.sqlbook`, dirty: true }) : newQueryTab({ text: s.text, title: `${title}.sql` });
    if (opened) await removeScript(s.id, s.ext);
  }
  toast("success", `Recovered ${found.length} script${found.length === 1 ? "" : "s"}.`);
}

/** The banner slot holds one message: one that is showing (a store reset, the engine) is not replaced, this waits for it. */
function showBannerWhenFree(b: NonNullable<ReturnType<typeof useUi.getState>["banner"]>) {
  if (!useUi.getState().banner) return useUi.getState().setBanner(b);
  const off = useUi.subscribe((st) => {
    if (st.banner) return;
    off();
    st.setBanner(b);
  });
}

/** A notebook tab from its recovery copy; an empty notebook that says so when the copy is missing or unreadable. */
async function restoreNotebook(t: SavedTab, activate: boolean) {
  const profileId = t.profileId && useProfiles.getState().byId(t.profileId) ? t.profileId : "";
  const o = { id: t.id, title: t.title, filePath: t.filePath, profileId, database: profileId ? t.database : undefined, dirty: t.dirty, activate, atEnd: true, pinned: t.pinned };
  const text = await readScript(t.id, "sqlbook");
  if (text != null && openNotebookText(text, o)) return;
  newNotebookTab({ ...o, notices: [text == null ? "The recovery copy of this notebook was not found; it opened empty." : "The recovery copy of this notebook could not be read; it opened empty."] });
}

/** Set while autosave is installed: writes what still waits on its debounce (scripts, notebooks), then the workspace. */
let flushPending: (() => Promise<void>) | undefined;

/**
 * Quitting (H4): every write still waiting on its debounce goes out now — the last 2 s of a script's text, a notebook's
 * recovery copy — then the workspace and the settings. Resolves when they are written (failures are logged, not thrown).
 */
export async function flushAutosave(): Promise<void> {
  // Before autosave is installed the workspace was not restored yet: writing it then would replace the saved tabs.
  if (flushPending) await flushPending();
}

export function installAutosave() {
  const timers = new Map<string, ReturnType<typeof setTimeout>>();
  let wsTimer: ReturnType<typeof setTimeout> | undefined;
  const saveWorkspaceSoon = () => {
    clearTimeout(wsTimer);
    wsTimer = setTimeout(() => void writeWorkspace(snapshotWorkspace()).catch(autosaveFailed), 1000);
  };
  const notebookTimers = new Map<string, ReturnType<typeof setTimeout>>();
  const saveNotebookSoon = (id: string) => {
    if (!useSettings.getState().autosave) return;
    clearTimeout(notebookTimers.get(id));
    notebookTimers.set(id, setTimeout(() => {
      notebookTimers.delete(id);
      if (useTabs.getState().tabs[id]) void writeNotebookCopy(id).catch(autosaveFailed);
    }, 2000));
  };
  const offContent = onModelContent((tabId, model) => {
    // A notebook cell's model: the notebook changed.
    const owner = ownerOfKey(tabId);
    if (owner) {
      markDirty(owner);
      saveNotebookSoon(owner);
      return;
    }
    const t = useTabs.getState().tabs[tabId];
    // A file-backed tab differs from its file until saved again (bulk close asks before losing it).
    if (t?.filePath && !t.dirty) useTabs.getState().update(tabId, { dirty: true });
    if (!useSettings.getState().autosave) return;
    if (!t || t.title.startsWith("🐞")) return;
    clearTimeout(timers.get(tabId));
    timers.set(
      tabId,
      setTimeout(() => {
        timers.delete(tabId);
        if (!model.isDisposed()) void writeScript(tabId, model.getValue()).catch(autosaveFailed);
      }, 2000),
    );
  });
  /** Writes the scripts and notebooks still waiting on their debounce, now. */
  const flushScripts = () => {
    const writes: Promise<void>[] = [];
    for (const [id, timer] of timers) {
      clearTimeout(timer);
      const m = getModel(id);
      if (m && !m.isDisposed() && useTabs.getState().tabs[id]) writes.push(writeScript(id, m.getValue()).catch(autosaveFailed));
    }
    timers.clear();
    for (const [id, timer] of notebookTimers) {
      clearTimeout(timer);
      if (useTabs.getState().tabs[id]) writes.push(writeNotebookCopy(id).catch(autosaveFailed));
    }
    notebookTimers.clear();
    return Promise.all(writes);
  };
  let prevOrder = useTabs.getState().order;
  const offTabs = useTabs.subscribe((s) => {
    if (s.order !== prevOrder) {
      for (const id of prevOrder) if (!s.order.includes(id)) void removeScript(id);
      // New tabs (from files, definitions, history) get their first autosave immediately.
      for (const id of s.order) {
        if (prevOrder.includes(id)) continue;
        setTimeout(() => {
          const m = getModel(id);
          if (m) void writeScript(id, m.getValue()).catch(autosaveFailed);
          else if (notebookOf(id)) void writeNotebookCopy(id).catch(autosaveFailed);
        }, 0);
      }
      prevOrder = s.order;
    }
    saveWorkspaceSoon();
  });
  // Connecting or disconnecting a server changes what the next launch reconnects.
  const offServers = useConnections.subscribe((s, prev) => {
    if (s.order !== prev.order) saveWorkspaceSoon();
  });
  const offExplorer = useExplorer.subscribe((s, prev) => {
    if (s.expanded !== prev.expanded) saveWorkspaceSoon();
  });
  // Notebook cells added, moved, deleted, run or re-charted: the recovery copy follows.
  const offNotebooks = useNotebooks.subscribe((s, prev) => {
    for (const id of Object.keys(s.docs)) {
      const d = s.docs[id];
      const p = prev.docs[id];
      if (p && (d.cells !== p.cells || d.saveOutputs !== p.saveOutputs)) saveNotebookSoon(id);
    }
  });
  const flush = () => {
    void writeWorkspace(snapshotWorkspace()).catch(autosaveFailed);
    void flushSettings();
    void flushScripts();
  };
  window.addEventListener("beforeunload", flush);
  // W5: a second window's beat, at once and then every minute, while it runs.
  const beatTimer = !owner && isTauri ? setInterval(() => void writeBeat().catch((e) => console.error("[autosave] liveness beat", e)), LIVE_BEAT_MS) : undefined;
  void writeBeat().catch((e) => console.error("[autosave] liveness beat", e));
  const pending = async () => {
    await flushScripts();
    clearTimeout(wsTimer);
    await writeWorkspace(snapshotWorkspace()).catch(autosaveFailed);
    await flushSettings().catch(() => {});
    // W5: a second window that quits stops saying it runs: its copies are offered back at the owner's next start.
    clearInterval(beatTimer);
    await removeBeat();
  };
  flushPending = pending;
  return () => {
    clearInterval(beatTimer);
    offContent();
    offTabs();
    offServers();
    offExplorer();
    offNotebooks();
    window.removeEventListener("beforeunload", flush);
    if (flushPending === pending) flushPending = undefined;
  };
}

/** Writes every tab's text and the workspace now, whatever the autosave setting: the app is about to restart (update). */
export async function saveWorkspaceNow() {
  const s = useTabs.getState();
  for (const id of s.order) {
    const m = getModel(id);
    if (m && !s.tabs[id]?.title.startsWith("🐞")) await writeScript(id, m.getValue());
    else if (s.tabs[id]?.notebook) await writeNotebookCopy(id);
  }
  await writeWorkspace(snapshotWorkspace());
  await flushSettings();
}

/** Save As writes a user file through the Rust fs plugin (§10). */
export async function saveAs(tabId?: string) {
  const t = tabId ? useTabs.getState().tabs[tabId] : activeTabState();
  if (t?.notebook) return void (await saveNotebook(t.id, true));
  const model = t && getModel(t.id);
  if (!t || !model) return;
  if (!isTauri) {
    const url = URL.createObjectURL(new Blob([model.getValue()], { type: "text/sql" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = t.title.endsWith(".sql") ? t.title : `${t.title}.sql`;
    a.click();
    return;
  }
  const { save } = await import("@tauri-apps/plugin-dialog");
  const path = await save({ defaultPath: t.filePath ?? t.title, filters: [{ name: "SQL", extensions: ["sql", "psql", "txt"] }] });
  if (!path) return;
  const f = await fs();
  await f.writeTextFile(path, model.getValue());
  useTabs.getState().update(t.id, { filePath: path, title: path.split(/[\\/]/).pop() ?? t.title, dirty: false });
  toast("success", `Saved ${path}`);
}

export async function saveFile() {
  const t = activeTabState();
  if (!t) return;
  if (t.notebook) return void (await saveNotebook(t.id));
  if (!t.filePath || !isTauri) return saveAs(t.id);
  const model = getModel(t.id);
  if (!model) return;
  try {
    const f = await fs();
    await f.writeTextFile(t.filePath, model.getValue());
    useTabs.getState().update(t.id, { dirty: false });
    toast("success", `Saved ${t.filePath}`);
  } catch {
    // A tab restored across app restarts has no dialog-granted fs scope for its external filePath yet; prompt once via Save As.
    await saveAs(t.id);
  }
}

export async function openFile() {
  if (!isTauri) {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = ".sql,.psql,.txt,.sqlbook";
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;
      if (file.name.toLowerCase().endsWith(".sqlbook")) openNotebookText(await file.text(), { title: file.name }, file.size);
      else newQueryTab({ text: await file.text(), title: file.name });
    };
    input.click();
    return;
  }
  const { open } = await import("@tauri-apps/plugin-dialog");
  const path = await open({ multiple: false, filters: [{ name: "SQL", extensions: ["sql", "psql", "pgsql", "txt"] }, { name: "Querithm notebook", extensions: ["sqlbook"] }] });
  if (!path || Array.isArray(path)) return;
  if (path.toLowerCase().endsWith(".sqlbook")) return void (await openNotebookPath(path));
  const f = await fs();
  const text = await f.readTextFile(path);
  newQueryTab({ text, title: path.split(/[\\/]/).pop(), filePath: path });
}

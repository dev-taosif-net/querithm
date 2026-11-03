// Find usages (Shift+F12) and rename (F2): what is under the cursor comes from the sidecar (AST / tokens
// against its snapshot); usages fill the tab's Usages panel; renames go through a previewed, guarded script.
import type { ObjectRef, OpenTabText, RefactorTarget, TabEdits, UsageHit } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { activateTab, newQueryTab, openReadOnlyDefinition, setBottomTab } from "../app/actions";
import { editorForTab, getModel } from "../editor/editors";
import { useTabs } from "../store/tabs";
import { profileDialect as dialectOf } from "../store/profiles";
import { useConnections } from "../store/connections";
import { useUsages } from "../store/usages";
import { toast, useUi } from "../store/ui";
import { targetKindOf } from "./refactor";

/** The text of every open tab on (profile, database): searched as it is now. */
export function openTabsFor(profileId: string, database: string): OpenTabText[] {
  const s = useTabs.getState();
  return s.order
    .map((id) => s.tabs[id])
    .filter((t) => t && t.profileId === profileId && t.database === database && !t.readOnly)
    .flatMap((t) => {
      const m = getModel(t.id);
      return m ? [{ tabId: t.id, title: t.title, text: m.getValue() }] : [];
    });
}

async function symbolAtCursor(tabId: string) {
  const tab = useTabs.getState().tabs[tabId];
  const model = getModel(tabId);
  const pos = editorForTab(tabId)?.getPosition();
  if (!tab || !model || !pos) return null;
  const sessionId = tab.profileId && tab.database ? await useConnections.getState().sessionFor(tab.profileId).catch(() => null) : null;
  const offset = model.getOffsetAt(pos);
  const r = await rpc("refactor.symbolAt", { sessionId, database: tab.database ?? null, dialect: dialectOf(tab.profileId), text: model.getValue(), offset });
  return { tab, model, offset, ...r };
}

/** Shift+F12. */
export async function findUsagesAtCursor(tabId: string) {
  try {
    const s = await symbolAtCursor(tabId);
    if (!s) return;
    if (s.local?.length) {
      const name = s.model.getValue().slice(s.local[0].offset, s.local[0].offset + s.local[0].length);
      const spans = s.local.map((sp) => {
        const p = s.model.getPositionAt(sp.offset);
        return { ...sp, line: p.lineNumber, column: p.column, snippet: s.model.getLineContent(p.lineNumber).trim() };
      });
      useUsages.getState().set(tabId, { local: { name, spans }, running: false });
      setBottomTab(tabId, "usages");
      return;
    }
    if (!s.target) return void toast("info", "No database object, column or local name under the cursor.");
    await runFindUsages(tabId, s.tab.profileId!, s.tab.database!, s.target);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

export async function runFindUsages(tabId: string, profileId: string, database: string, target: RefactorTarget) {
  useUsages.getState().set(tabId, { target, running: true });
  setBottomTab(tabId, "usages");
  try {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    const result = await rpc("refactor.findUsages", { sessionId, database, target, tabs: openTabsFor(profileId, database) });
    useUsages.getState().set(tabId, { target: result.target, result, running: false });
  } catch (e) {
    useUsages.getState().set(tabId, { target, running: false, error: (e as Error).message });
  }
}

/** F2: a local symbol renames in the editor; a schema object or column opens the previewed rename. */
export async function renameAtCursor(tabId: string) {
  try {
    const s = await symbolAtCursor(tabId);
    if (!s) return;
    if (s.local?.length) {
      const original = s.model.getValue().slice(s.local[0].offset, s.local[0].offset + s.local[0].length);
      useUi.getState().openDialog({ kind: "rename", local: { tabId, spans: s.local, original, dialect: dialectOf(s.tab.profileId) } });
      return;
    }
    if (!s.target) return void toast("info", "No database object, column or local name under the cursor.");
    useUi.getState().openDialog({ kind: "rename", profileId: s.tab.profileId!, database: s.tab.database!, target: s.target, fromTabId: tabId });
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

/** A query tab on (profile, database) to show usages in: the active one when it matches, else a new one. */
function tabFor(profileId: string, database: string): string {
  const s = useTabs.getState();
  const active = s.activeId ? s.tabs[s.activeId] : undefined;
  if (active && active.profileId === profileId && active.database === database) return active.id;
  return newQueryTab({ profileId, database });
}

export function findUsagesOf(profileId: string, database: string, obj: { schema: string; name: string; kind: ObjectRef["kind"]; column?: string }) {
  const target: RefactorTarget = obj.column
    ? { kind: "column", schema: obj.schema, name: obj.name, column: obj.column }
    : { kind: targetKindOf(obj.kind), schema: obj.schema, name: obj.name, column: null };
  void runFindUsages(tabFor(profileId, database), profileId, database, target);
}

export function renameOf(profileId: string, database: string, obj: { schema: string; name: string; kind: ObjectRef["kind"]; column?: string }) {
  const target: RefactorTarget = obj.column
    ? { kind: "column", schema: obj.schema, name: obj.name, column: obj.column }
    : { kind: targetKindOf(obj.kind), schema: obj.schema, name: obj.name, column: null };
  useUi.getState().openDialog({ kind: "rename", profileId, database, target });
}

/** Jumps to a hit: the tab's text, or the object's definition opened read-only at that line. */
export async function revealHit(hit: UsageHit, profileId?: string, database?: string) {
  const select = (tabId: string) => {
    activateTab(tabId);
    const tryReveal = (left: number) => {
      const ed = editorForTab(tabId);
      const m = getModel(tabId);
      if (!ed || !m) return left > 0 ? void setTimeout(() => tryReveal(left - 1), 50) : undefined;
      const a = m.getPositionAt(hit.offset);
      const b = m.getPositionAt(hit.offset + hit.length);
      ed.setSelection({ startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column });
      ed.revealLineInCenter(a.lineNumber);
      ed.focus();
    };
    tryReveal(20);
  };
  if (hit.tabId) return select(hit.tabId);
  if (!hit.object || !profileId || !database) return;
  try {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    const def = await rpc("meta.definition", { sessionId, object: { ...hit.object, profileId, database } });
    const id = await openReadOnlyDefinition(profileId, database, hit.object, def.sql);
    if (id) select(id);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

/** The rename's edits to open tabs, each tab as one undoable edit. */
export function applyTabEdits(tabs: TabEdits[]) {
  for (const t of tabs) {
    const m = getModel(t.tabId);
    if (!m) continue;
    const ops = t.edits.map((e) => {
      const a = m.getPositionAt(e.offset);
      const b = m.getPositionAt(e.offset + e.length);
      return { range: { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column }, text: e.text };
    });
    m.pushStackElement();
    m.pushEditOperations([], ops, () => null);
    m.pushStackElement();
  }
}

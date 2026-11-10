// Object Explorer (§10, §12): the servers in use and pinned profiles, lazy levels from the cached snapshot, a
// virtualized WAI-ARIA tree (one Tab stop, arrow keys, type-ahead), search scoped to the active tab's database,
// per-kind context menus, drag a table into the editor. Rows come from tree.ts; this file draws and drives them.
import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent, type KeyboardEvent, type MouseEvent } from "react";
import type { EnvironmentKind, Profile, SnapObject } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { useConnections } from "../store/connections";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { sessionParam, tabScope, useSnapshots } from "../store/snapshots";
import { repickFile } from "../connections/fileAccess";
import { schemaGroupKey, useSettings } from "../store/settings";
import { useTabs } from "../store/tabs";
import { toast, useUi } from "../store/ui";
import { EngineLogo } from "../connections/EngineLogo";
import { DEFAULT_ENGINE, engineTraits, ENGINES } from "../engines";
import { offeredEngines } from "../connections/engineInfo";
import { Icon, type IconName } from "../app/icons";
import { DropMenu, type MenuEntry } from "../app/ToolbarMenu";
import { readableOnAll, useThemeKey } from "../app/tabStyle";
import { findUsagesOf, renameOf } from "../refactor/refactorActions";
import { explorerAction, explorerHooks, useExplorerWidth } from "./explorerWindow";
import { useExplorer } from "./explorerState";
import { pruneExpanded, takeNewlyConnected } from "./expansion";
import { menuFor, sessionReason } from "./menus";
import { bindActiveTab, columnList, columnName, connectFromExplorer, copyText, insertName, insertText, openObject, qualified } from "./objectActions";
import {
  ancestorsOf, buildTree, CATEGORIES, catKey, dbKey, objKey, schemaKey, searchRows, searchScope, serverKey, sysKey,
  type DetailsCell, type Engine, type KindFilter, type RowOf, type TreeRow,
} from "./tree";

export { generateDmlTemplate, modifyTrigger } from "./objectActions";

const ROW = 24;
/** Rows are rendered in blocks: scrolling inside a block changes nothing React sees (§12: no render per scroll frame). */
const BLOCK = 16;
/** Where an environment label is drawn: rows at rest, hovered, selected, search headers and the sticky path, menus. */
const SURFACES = ["var(--q-panel)", "var(--q-hover)", "var(--q-row-sel)", "var(--q-panel-2)", "var(--q-raised)"];
const ENV_TEXT: Record<EnvironmentKind, string> = { local: "LOCAL", dev: "DEV", test: "TEST", staging: "STAGE", prod: "PROD" };

/** Text the keyboard or a drag acts on: a row's name. */
function rowName(r: TreeRow, profiles: Map<string, Profile>): string {
  switch (r.t) {
    case "server": return profiles.get(r.profileId)?.name ?? r.profileId;
    case "db": return r.db;
    case "schema": return r.schema;
    case "cat": return CATEGORIES.find((c) => c.id === r.cat)!.label;
    case "obj": return r.obj.name;
    case "leaf": return r.text;
    case "env": return envLabel[r.env];
    default: return "";
  }
}

interface Handlers {
  select(i: number): void;
  click(i: number, e: MouseEvent): void;
  caret(i: number): void;
  activate(i: number): void;
  menu(i: number, x: number, y: number): void;
  drag(i: number, e: DragEvent): void;
  clearFilter(catKey: string): void;
  showAll(kind: KindFilter): void;
}

interface RowView {
  profiles: Map<string, Profile>;
  envText: Map<string, string>;
  compact: boolean;
  narrow: boolean;
  wide: boolean;
  indent: number;
}

export function Explorer() {
  const profilesList = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const servers = useConnections((s) => s.servers);
  const order = useConnections((s) => s.order);
  const entries = useSnapshots((s) => s.entries);
  const pinned = useSettings((s) => s.pinnedProfiles);
  const showAll = useSettings((s) => s.explorerShowAll);
  const groupByEnv = useSettings((s) => s.explorerGroupByEnv);
  const schemaGroups = useSettings((s) => s.explorerSchemaGroups);
  const hoverOpen = useSettings((s) => s.explorerHoverOpen);
  const width = useExplorerWidth();
  const ex = useExplorer();
  const activeProfile = useTabs((s) => (s.activeId ? s.tabs[s.activeId]?.profileId : undefined));
  const activeDb = useTabs((s) => (s.activeId ? s.tabs[s.activeId]?.database : undefined));
  const activeSession = useTabs((s) => (s.activeId ? s.tabs[s.activeId]?.sessionId : undefined));
  const hasTab = useTabs((s) => !!s.activeId);
  const themeKey = useThemeKey();
  const searchBox = useRef<HTMLInputElement>(null);
  const viewBtn = useRef<HTMLButtonElement>(null);
  const kindBtn = useRef<HTMLButtonElement>(null);
  const treeRef = useRef<HTMLDivElement>(null);
  const [popup, setPopup] = useState<"view" | "kind" | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number; items: MenuEntry[]; header: { env?: EnvironmentKind; profileId?: string; text: string }; label: string } | null>(null);
  const menuAnchor = useRef<HTMLElement | null>(null);
  const compact = width < 260, narrow = width < 240, wide = width >= 400;

  const profiles = useMemo(() => new Map(profilesList.map((p) => [p.id, p])), [profilesList]);
  const envText = useMemo(() => {
    const m = new Map<string, string>();
    for (const p of profilesList) {
      const c = envColor(p, groups);
      m.set(p.id, readableOnAll(c, SURFACES) ?? c);
    }
    return m;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profilesList, groups, themeKey]);

  useEffect(() => {
    explorerHooks.focusSearch = () => searchBox.current?.focus();
    return () => void (explorerHooks.focusSearch = undefined);
  }, []);

  // A server the user connects opens one level (its databases); a background reconnect (startup, engine restart)
  // restores the saved expansion instead. A server that leaves the Explorer forgets its expansion.
  const seen = useRef(new Set<string>());
  useEffect(() => {
    const st = useExplorer.getState();
    const live = order.filter((id) => servers[id]);
    const fresh = takeNewlyConnected(seen.current, live, (id) => servers[id]?.status === "connected").filter((id) => !servers[id]?.quiet);
    st.setExpanded((prev) => {
      const pruned = pruneExpanded(prev, live);
      if (!fresh.length) return pruned;
      const next = new Set(pruned);
      for (const id of fresh) next.add(serverKey(id));
      return next;
    });
  }, [order, servers]);

  const active = activeProfile && activeDb ? { profileId: activeProfile, db: activeDb } : undefined;
  const tree = useMemo(
    () => buildTree({ profiles: profilesList, servers, order, pinned, showAll, groupByEnv, collapsedEnvs: ex.collapsedEnvs, expanded: ex.expanded, entries, details: ex.details, schemaGroups, kind: ex.kind, folderFilters: ex.folderFilters, active }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [profilesList, servers, order, pinned, showAll, groupByEnv, ex.collapsedEnvs, ex.expanded, entries, ex.details, schemaGroups, ex.kind, ex.folderFilters, active?.profileId, active?.db],
  );

  // Keys and indexes of expanded objects are read lazily, again after a refresh replaced the object's record.
  useEffect(() => {
    for (const n of tree.needDetails) loadDetails(n.key, n.profileId, n.db, n.obj);
  }, [tree.needDetails]);

  // An expanded database in view loads its catalog once its server is connected; a restored key under a collapsed
  // server or System databases folder waits until it is shown (each read borrows a pooled connection).
  useEffect(() => {
    for (const n of tree.needCatalog) {
      if (servers[n.profileId]?.status === "connected" && !useSnapshots.getState().get(n.profileId, n.db)) useSnapshots.getState().ensure(n.profileId, n.db);
    }
  }, [tree.needCatalog, servers]);

  // ---- search, scoped to the active tab's database (never another server)
  const query = ex.query;
  const searching = !!query.trim();
  const scope = useMemo(() => {
    if (!hasTab) return searchScope(undefined, servers, entries);
    // A session-owned engine's tab (SQLite) searches its own session's view; an in-memory one has none before it opens.
    const tab = { profileId: activeProfile, database: activeDb, sessionId: activeSession };
    const own = tabScope(tab);
    return searchScope({ ...tab, sessionId: own?.sessionId, noDatabaseYet: !!activeProfile && !!activeDb && !own }, servers, entries);
    // profilesList: tabScope reads the tab's engine from the profiles store.
  }, [hasTab, activeProfile, activeDb, activeSession, servers, entries, profilesList]);
  useEffect(() => {
    if (searching && scope.state === "loading") useSnapshots.getState().ensure(scope.profileId, scope.db, scope.sessionId);
  }, [searching, scope]);
  const scopeEngine = "profileId" in scope ? profiles.get(scope.profileId)?.engine : undefined;
  const results = useMemo(
    () => (searching && scope.state === "ready" ? searchRows(scope.profileId, scope.db, scopeEngine ?? DEFAULT_ENGINE, scope.objects, query, ex.kind, ex.uncapped) : null),
    [searching, scope, scopeEngine, query, ex.kind, ex.uncapped],
  );
  const rows = results ? results.rows : searching ? [] : tree.rows;

  // ---- selection
  const selIndex = useMemo(() => (ex.selected ? rows.findIndex((r) => r.key === ex.selected) : -1), [rows, ex.selected]);
  // A selected row that disappeared (collapse, disconnect, filter) hands the selection to its nearest surviving ancestor.
  const lastSel = useRef<{ key?: string; rows: TreeRow[] }>({ rows: [] });
  useLayoutEffect(() => {
    const prev = lastSel.current;
    if (ex.selected && selIndex < 0 && prev.key === ex.selected) {
      const at = prev.rows.findIndex((r) => r.key === ex.selected);
      const up = at >= 0 ? ancestorsOf(prev.rows, at).reverse().map((i) => prev.rows[i].key).find((k) => rows.some((r) => r.key === k)) : undefined;
      useExplorer.getState().set({ selected: up });
    }
    lastSel.current = { key: ex.selected, rows };
  }, [rows, ex.selected, selIndex]);

  const rowsRef = useRef(rows);
  rowsRef.current = rows;
  const reveal = useRef<string | null>(null);

  const select = useCallback((i: number) => {
    const r = rowsRef.current[i];
    if (r) useExplorer.getState().set({ selected: r.key });
  }, []);

  const toggleRow = useCallback((r: TreeRow, open?: boolean) => {
    const st = useExplorer.getState();
    switch (r.t) {
      case "env": {
        const next = new Set(st.collapsedEnvs);
        if (open ?? next.has(r.env)) next.delete(r.env);
        else next.add(r.env);
        st.set({ collapsedEnvs: next });
        return;
      }
      case "server":
        if (r.status === "off") return void ((open ?? true) && connectFromExplorer(r.profileId));
        st.toggle(r.key, open);
        return;
      case "db":
        if (st.toggle(r.key, open) && !useSnapshots.getState().get(r.profileId, r.db)) useSnapshots.getState().ensure(r.profileId, r.db);
        return;
      case "sub": {
        const now = st.toggle(r.key, open);
        // Keys / indexes that failed are read again when reopened.
        const cell = st.details[r.objKey];
        if (now && (r.sub === "keys" || r.sub === "indexes") && cell && typeof cell.d === "string" && cell.d !== "loading") {
          const { [r.objKey]: _, ...rest } = st.details;
          st.set({ details: rest });
        }
        return;
      }
      default:
        if (r.expandable) st.toggle(r.key, open);
    }
  }, []);

  const activate = useCallback((r: TreeRow) => {
    switch (r.t) {
      case "server":
        if (r.status === "off" || r.status === "error") void connectFromExplorer(r.profileId);
        else toggleRow(r);
        return;
      case "db": return bindActiveTab(r.profileId, r.db);
      case "obj": return void (whenConnected(r.profileId) && openObject(r.profileId, r.db, r.obj));
      case "action":
        if (r.action === "reconnect") void connectFromExplorer(r.profileId);
        else if (r.action === "editProfile") useUi.getState().openDialog({ kind: "profile", profileId: r.profileId });
        else if (r.action === "repick") void repickFile(r.profileId);
        else if (r.action === "retryMeta" && r.db) void useSnapshots.getState().refresh(r.profileId, r.db, false);
        else if (r.action === "retryDetails" && r.objKey && r.obj && r.db) loadDetails(r.objKey, r.profileId, r.db, r.obj, true);
        return;
      case "header":
        if (r.capped && r.kind) useExplorer.getState().set({ uncapped: new Set([...useExplorer.getState().uncapped, r.kind]) });
        return;
      default:
        if (r.expandable) toggleRow(r);
    }
  }, [toggleRow]);

  // Opens every level down to a search result and clears whatever would hide it: a collapsed environment group, the
  // kind filter, its folder's filter. A trigger is under its table's (or view's) Triggers folder.
  const revealObject = useCallback((r: RowOf<"obj">) => {
    const st = useExplorer.getState();
    const p = useProfiles.getState().byId(r.profileId);
    const sysdb = useConnections.getState().servers[r.profileId]?.databases.find((d) => d.name === r.db)?.isSystem && p?.databaseDefault?.toLowerCase() !== r.db.toLowerCase();
    const grouped = useSettings.getState().explorerSchemaGroups.includes(schemaGroupKey(r.profileId, r.db));
    const owner = r.obj.kind === "trigger" && r.obj.parentObjectId != null ? useSnapshots.getState().get(r.profileId, r.db)?.index?.byId.get(r.obj.parentObjectId) : undefined;
    const target = owner ?? r.obj;
    const cat = CATEGORIES.find((c) => c.kinds.includes(target.kind))?.id;
    const keys = [serverKey(r.profileId), dbKey(r.profileId, r.db)];
    if (sysdb) keys.push(sysKey(r.profileId));
    if (grouped) keys.push(schemaKey(r.profileId, r.db, target.schema));
    const ck = cat ? catKey(r.profileId, r.db, cat, grouped ? target.schema : undefined) : undefined;
    if (ck) keys.push(ck);
    if (owner) keys.push(objKey(r.profileId, r.db, owner), `${objKey(r.profileId, r.db, owner)}:triggers`);
    st.setExpanded((prev) => new Set([...prev, ...keys]));
    const key = owner ? objKey(r.profileId, r.db, r.obj, owner) : r.key.replace(/^r:/, "");
    const envs = new Set(st.collapsedEnvs);
    if (p) envs.delete(p.environment);
    reveal.current = key;
    st.set({
      query: "", selected: key, collapsedEnvs: envs,
      kind: st.kind === "all" || st.kind === cat ? st.kind : "all",
      folderFilters: ck && st.folderFilters[ck] ? { ...st.folderFilters, [ck]: "" } : st.folderFilters,
    });
  }, []);

  const openMenu = useCallback((i: number, x: number, y: number) => {
    const r = rowsRef.current[i];
    if (!r) return;
    useExplorer.getState().set({ selected: r.key });
    const items = menuFor(r, { reveal: r.t === "obj" && r.key.startsWith("r:") ? () => revealObject(r) : undefined, parent: r.t === "obj" && r.obj.kind === "trigger" ? triggerOwner(r, rowsRef.current) : undefined });
    if (!items) return;
    menuAnchor.current = treeRef.current;
    setMenu({ x, y, items, ...menuTarget(r, rowsRef.current, i) });
  }, [revealObject]);

  const handlers = useMemo<Handlers>(() => ({
    select,
    click(i, e) {
      select(i);
      const r = rowsRef.current[i];
      if (!r || e.detail > 1) return; // the second click of a double-click activates instead of toggling back
      if (r.t === "server" && (r.status === "off" || r.status === "error")) return; // selecting never dials a server
      if (r.t === "obj" && r.key.startsWith("r:")) return;
      if (r.t === "action" || r.t === "header") return activate(r);
      if (r.expandable) toggleRow(r);
    },
    caret(i) {
      const r = rowsRef.current[i];
      if (r) toggleRow(r);
    },
    activate(i) {
      const r = rowsRef.current[i];
      if (!r || r.t === "action" || r.t === "header") return;
      // The double-click's first click already opened or closed a folder; toggling it again would undo that.
      if (folderOnly(r)) return;
      activate(r);
    },
    menu: openMenu,
    drag(i, e) {
      const r = rowsRef.current[i];
      if (!r) return;
      const engine = r.t === "obj" || r.t === "sub" ? useProfiles.getState().byId(r.profileId)?.engine : r.t === "leaf" && r.profileId ? useProfiles.getState().byId(r.profileId)?.engine : undefined;
      let text = "";
      if (r.t === "obj") text = e.altKey ? columnList(r.obj, engine) : qualified(r.obj, engine);
      else if (r.t === "sub" && r.sub === "columns") text = columnList(r.obj, engine);
      else if (r.t === "leaf" && r.drag) text = r.drag;
      if (text) e.dataTransfer.setData("text/plain", text);
      else e.preventDefault();
    },
    clearFilter(key) {
      const st = useExplorer.getState();
      st.set({ folderFilters: { ...st.folderFilters, [key]: "" } });
    },
    showAll(kind) {
      useExplorer.getState().set({ uncapped: new Set([...useExplorer.getState().uncapped, kind]) });
    },
  }), [select, activate, toggleRow, openMenu]);

  // ---- keyboard on the tree (one Tab stop; the active row is aria-activedescendant)
  const typeahead = useRef({ text: "", at: 0 });
  const onTreeKey = (e: KeyboardEvent) => {
    const rs = rowsRef.current;
    if (!rs.length) return;
    const i = selIndex >= 0 ? selIndex : 0;
    const r = rs[i];
    const page = Math.max(1, Math.floor((treeRef.current?.clientHeight ?? 400) / ROW) - 1);
    const go = (n: number) => {
      e.preventDefault();
      select(Math.max(0, Math.min(rs.length - 1, n)));
    };
    const ctrl = e.ctrlKey || e.metaKey;
    if (e.key === "ArrowDown") return go(selIndex < 0 ? 0 : i + 1);
    if (e.key === "ArrowUp") return go(i - 1);
    if (e.key === "Home") return go(0);
    if (e.key === "End") return go(rs.length - 1);
    if (e.key === "PageDown") return go(i + page);
    if (e.key === "PageUp") return go(i - page);
    if (selIndex < 0) return go(0);
    if (e.key === "ArrowRight") {
      e.preventDefault();
      if (r.expandable && !r.expanded) return toggleRow(r, true);
      if (r.t === "server" && r.status === "off") return toggleRow(r, true);
      if (r.expanded && rs[i + 1]?.parent === i) select(i + 1);
      return;
    }
    if (e.key === "ArrowLeft") {
      e.preventDefault();
      if (r.expanded) return toggleRow(r, false);
      if (r.parent >= 0) select(r.parent);
      return;
    }
    if (e.key === "Enter" && ctrl) {
      e.preventDefault();
      if (r.t === "obj") insertName(r.obj, r.profileId, e.altKey);
      else if (r.t === "leaf" && r.column) insertText(columnName(r.column, r.profileId ? useProfiles.getState().byId(r.profileId)?.engine : undefined));
      return;
    }
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      return activate(r);
    }
    if ((e.key === "F10" && e.shiftKey) || e.key === "ContextMenu") {
      e.preventDefault();
      const el = treeRef.current?.querySelector<HTMLElement>(`[data-row="${i}"]`);
      const b = el?.getBoundingClientRect();
      return openMenu(i, (b?.left ?? 0) + 24, (b?.bottom ?? 0) + 2);
    }
    if (e.key === "F2" && (r.t === "obj" || (r.t === "leaf" && r.column))) {
      e.preventDefault();
      const o = r.t === "obj" ? r.obj : r.obj!;
      if (o.kind !== "synonym" && o.kind !== "trigger" && whenConnected(r.profileId!)) renameOf(r.t === "obj" ? r.profileId : r.profileId!, r.db!, { schema: o.schema, name: o.name, kind: o.kind, column: r.t === "leaf" ? r.column : undefined });
      return;
    }
    if (e.key === "F12" && e.shiftKey && (r.t === "obj" || (r.t === "leaf" && r.column))) {
      e.preventDefault();
      const o = r.t === "obj" ? r.obj : r.obj!;
      if (o.kind !== "synonym" && o.kind !== "trigger" && whenConnected(r.profileId!)) findUsagesOf(r.t === "obj" ? r.profileId : r.profileId!, r.db!, { schema: o.schema, name: o.name, kind: o.kind, column: r.t === "leaf" ? r.column : undefined });
      return;
    }
    if (e.key === "F5") {
      e.preventDefault();
      if (r.t === "server" && r.status === "connected") void useConnections.getState().refreshDatabases(r.profileId).catch((e) => toast("error", `Could not refresh databases: ${e instanceof Error ? e.message : String(e)}. Press F5 to retry.`));
      else if ("db" in r && r.db && "profileId" in r && r.profileId) void useSnapshots.getState().refresh(r.profileId, r.db, false);
      return;
    }
    if (ctrl && e.key.toLowerCase() === "c" && !e.shiftKey && !e.altKey) {
      e.preventDefault();
      const name = r.t === "obj" ? qualified(r.obj, useProfiles.getState().byId(r.profileId)?.engine) : rowName(r, profiles);
      if (name) void copyText(name);
      return;
    }
    // Typing inside a folder of objects filters it in place; elsewhere it jumps to the next row that starts so.
    const cat = r.t === "cat" ? i : ancestorsOf(rs, i).reverse().find((a) => rs[a].t === "cat");
    const st = useExplorer.getState();
    if (cat !== undefined && cat >= 0 && rs[cat].t === "cat" && (rs[cat] as RowOf<"cat">).count > 0) {
      const ck = rs[cat].key;
      const cur = st.folderFilters[ck] ?? "";
      if (e.key === "Escape" && cur) {
        e.preventDefault();
        e.stopPropagation(); // the flyout's Esc closes only once the filter is gone
        st.set({ folderFilters: { ...st.folderFilters, [ck]: "" }, selected: ck });
        return;
      }
      if (e.key === "Backspace" && cur) {
        e.preventDefault();
        st.set({ folderFilters: { ...st.folderFilters, [ck]: cur.slice(0, -1) } });
        return;
      }
      if (e.key.length === 1 && !ctrl && !e.altKey && e.key !== " ") {
        e.preventDefault();
        st.set({ folderFilters: { ...st.folderFilters, [ck]: cur + e.key }, selected: ck });
        st.toggle(ck, true);
        return;
      }
    }
    if (e.key.length === 1 && !ctrl && !e.altKey && e.key !== " ") {
      const now = Date.now();
      const t = typeahead.current;
      t.text = now - t.at > 700 ? e.key.toLowerCase() : t.text + e.key.toLowerCase();
      t.at = now;
      for (let n = 1; n <= rs.length; n++) {
        const j = (i + (t.text.length > 1 ? 0 : 1) + n - 1) % rs.length;
        if (rowName(rs[j], profiles).toLowerCase().startsWith(t.text)) {
          e.preventDefault();
          return select(j);
        }
      }
    }
  };

  // Keeps the selected row in view after keyboard moves, a reveal or a filter: a new selection only. Rows changing above
  // it (another server connecting, a metadata patch) leave the view where the user scrolled it.
  const inView = useRef<string | undefined>(undefined);
  useLayoutEffect(() => {
    const el = treeRef.current;
    if (!el || selIndex < 0) return;
    if (inView.current === ex.selected && reveal.current !== ex.selected) return;
    inView.current = ex.selected;
    const top = selIndex * ROW;
    const stickyPad = top > ROW ? ROW : 0;
    if (top - stickyPad < el.scrollTop) el.scrollTop = Math.max(0, top - stickyPad);
    else if (top + ROW > el.scrollTop + el.clientHeight) el.scrollTop = top + ROW - el.clientHeight;
    if (reveal.current === ex.selected) reveal.current = null;
  }, [selIndex, ex.selected]);

  // ---- search box keys
  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    const first = results?.rows.findIndex((r) => r.t === "obj") ?? -1;
    if (e.key === "Escape") {
      if (query) {
        e.preventDefault();
        useExplorer.getState().set({ query: "" });
      }
      return;
    }
    if (e.key === "ArrowDown" && first >= 0) {
      e.preventDefault();
      select(first);
      treeRef.current?.focus();
      return;
    }
    if (e.key === "Enter" && first >= 0 && results) {
      e.preventDefault();
      const r = results.rows[first] as RowOf<"obj">;
      if (e.ctrlKey || e.metaKey) insertName(r.obj, r.profileId, e.altKey);
      else if (whenConnected(r.profileId)) openObject(r.profileId, r.db, r.obj);
    }
  };

  const view = useMemo<RowView>(() => ({ profiles, envText, compact, narrow, wide, indent: narrow ? 8 : 12 }), [profiles, envText, compact, narrow, wide]);
  const noProfiles = !profilesList.length;
  const noServers = !noProfiles && !tree.shown.length && !searching;
  const engines = new Set(tree.shown.map((id) => profiles.get(id)?.engine));

  return (
    <div className="h-full bg-panel border-r border-line flex flex-col" data-q-explorer data-focus-region="explorer">
      {/* 40 px, as tall as the tab strip: its bottom line continues the strip's across the window. */}
      <div role="toolbar" aria-label="Object Explorer" className="h-[40px] pl-3 pr-1.5 border-b border-line flex items-center gap-0.5 shrink-0">
        <span className="flex-1 min-w-0 truncate text-[11px] font-bold tracking-wider text-muted">{compact ? "EXPLORER" : "OBJECT EXPLORER"}</span>
        <button
          className="q-tb !h-6 !min-w-6 !px-0"
          aria-label={noProfiles ? "New connection…" : "Connect…"}
          title={noProfiles ? "New connection…" : "Connect… (saved profiles, or a new one)"}
          onClick={() => useUi.getState().openDialog({ kind: noProfiles ? "profile" : "connect" })}
        >
          <Icon name="plus" size={15} />
        </button>
        <button ref={viewBtn} className="q-tb !h-6 !min-w-6 !px-0" aria-label="View options" aria-haspopup="menu" aria-expanded={popup === "view"} title="View options" onClick={() => setPopup((p) => (p === "view" ? null : "view"))}>
          <Icon name="more" size={15} />
        </button>
        <WindowButtons />
      </div>
      {popup === "view" && <DropMenu anchor={viewBtn} label="View options" items={viewItems(active, hoverOpen, () => revealActive(active))} onClose={() => setPopup(null)} />}

      {!noProfiles && (
        <div className="px-2 pt-[5px] shrink-0 border-b border-line">
          <div className={`h-[28px] flex items-center gap-1.5 pl-2 pr-0.5 rounded-md border border-line-strong focus-within:border-accent ${scope.state === "noTab" || scope.state === "noConn" || scope.state === "noDb" ? "border-dashed bg-panel" : "bg-bg"}`}>
            <Icon name="search" size={14} className="text-muted" />
            <input
              ref={searchBox}
              data-q-explorer-search
              className="flex-1 min-w-0 bg-transparent text-[12px] text-fg placeholder:text-muted border-line-strong"
              style={{ outline: "none" }}
              aria-label={`Search objects in ${scopeName(scope, profiles)}`}
              aria-describedby="q-explorer-scope"
              placeholder={placeholderOf(scope, profiles)}
              value={query}
              onChange={(e) => useExplorer.getState().set({ query: e.target.value, uncapped: new Set() })}
              onKeyDown={onSearchKey}
            />
            {query && (
              <button className="w-5 h-5 flex items-center justify-center rounded text-muted hover:text-fg hover:bg-hover" title="Clear search (Esc)" aria-label="Clear search" onClick={() => { useExplorer.getState().set({ query: "" }); searchBox.current?.focus(); }}>
                <Icon name="close" size={12} />
              </button>
            )}
            <button ref={kindBtn} className={`relative w-[22px] h-[22px] flex items-center justify-center rounded hover:bg-hover ${ex.kind !== "all" ? "text-accent" : "text-muted"}`} aria-label={ex.kind === "all" ? "Filter by kind" : `Kind filter: ${kindLabel(ex.kind)}`} title={ex.kind === "all" ? "Filter by kind" : `Kind filter: ${kindLabel(ex.kind)}`} aria-haspopup="menu" aria-expanded={popup === "kind"} onClick={() => setPopup((p) => (p === "kind" ? null : "kind"))}>
              <Icon name="filter" size={14} />
              {ex.kind !== "all" && <span aria-hidden className="absolute top-[3px] right-[3px] w-1.5 h-1.5 rounded-full bg-accent" />}
            </button>
          </div>
          {popup === "kind" && <DropMenu anchor={kindBtn} label="Filter by kind" items={kindItems(ex.kind, scopeEngine ?? (engines.size === 1 ? ([...engines][0] as Engine) : undefined))} onClose={() => setPopup(null)} />}
          <ScopeLine scope={scope} profiles={profiles} envText={envText} kind={ex.kind} count={results?.total} searching={searching} compact={compact} onReveal={() => revealActive(active)} />
        </div>
      )}

      {noProfiles ? (
        <FirstRun />
      ) : noServers ? (
        <NoServers saved={profilesList.length} />
      ) : searching && !results ? (
        <SearchMessage scope={scope} profiles={profiles} />
      ) : searching && results && !results.rows.length ? (
        <Message icon="search" title={`No ${ex.kind === "all" ? "objects match" : `${kindLabel(ex.kind).toLowerCase()} match`} “${query.trim()}”`} sub={`Searched ${scopeName(scope, profiles)}.`} action={ex.kind !== "all" ? { label: "Search all kinds", run: () => useExplorer.getState().set({ kind: "all" }) } : undefined} />
      ) : (
        <TreeView rows={rows} selIndex={selIndex} view={view} handlers={handlers} treeRef={treeRef} onKeyDown={onTreeKey} label={searching ? `Search results in ${scopeName(scope, profiles)}` : "Object Explorer"} />
      )}
      {menu && (
        <DropMenu
          anchor={menuAnchor}
          at={{ x: menu.x, y: menu.y }}
          label={menu.label}
          header={<MenuHeader env={menu.header.env} color={menu.header.profileId ? envText.get(menu.header.profileId) : undefined} text={menu.header.text} />}
          items={menu.items}
          onClose={() => setMenu(null)}
        />
      )}
    </div>
  );

  function revealActive(a: typeof active) {
    if (!a) return;
    const st = useExplorer.getState();
    const p = useProfiles.getState().byId(a.profileId);
    const d = useConnections.getState().servers[a.profileId]?.databases.find((x) => x.name === a.db);
    const keys = [serverKey(a.profileId)];
    if (d?.isSystem && p?.databaseDefault?.toLowerCase() !== a.db.toLowerCase()) keys.push(sysKey(a.profileId));
    st.setExpanded((prev) => new Set([...prev, ...keys]));
    st.set({ query: "", selected: dbKey(a.profileId, d?.name ?? a.db) });
    requestAnimationFrame(() => treeRef.current?.focus());
  }

  function loadDetails(key: string, profileId: string, db: string, o: SnapObject, force = false) {
    const st = useExplorer.getState();
    const cur = st.details[key];
    if (!force && cur && cur.o === o && cur.d === "loading") return;
    const ask = { o, d: "loading" as const };
    st.set({ details: { ...st.details, [key]: ask } });
    // A reply lands only on the request that asked for it: a late one for an older record (or a disconnected server)
    // must not overwrite a newer read.
    const done = (d: DetailsCell["d"]) => {
      const now = useExplorer.getState().details;
      if (now[key] === ask) useExplorer.getState().set({ details: { ...now, [key]: { o, d } } });
    };
    // The active SQLite tab's own view (Phase D0d): its temporary tables, or its in-memory database, are read through
    // its session; the sidecar answers shared objects from the shared snapshot either way.
    const s = useTabs.getState();
    const own = tabScope(s.activeId ? s.tabs[s.activeId] : undefined);
    const sessionId = own?.sessionId && own.profileId === profileId && own.database === db ? own.sessionId : undefined;
    rpc("meta.objectDetails", { profileId, database: db, objectId: o.id, ...sessionParam(sessionId) })
      .then((r) => done(r))
      .catch((e) => done((e as Error).message || "Could not be loaded."));
  }
}

function kindLabel(k: KindFilter) {
  return k === "all" ? "All kinds" : k === "triggers" ? "Triggers" : CATEGORIES.find((c) => c.id === k)!.label;
}

function kindItems(current: KindFilter, engine: Engine | undefined): MenuEntry[] {
  const set = (k: KindFilter) => () => useExplorer.getState().set({ kind: k });
  return [
    { label: "All kinds", run: set("all"), checked: current === "all" },
    "sep",
    ...CATEGORIES.map((c) => ({ label: c.label, run: set(c.id), checked: current === c.id, unavailable: c.id === "synonyms" && engine && !ENGINES[engine].synonyms ? `${ENGINES[engine].shortLabel} has no synonyms` : undefined })),
    { label: "Triggers", detail: "search only", run: set("triggers"), checked: current === "triggers" },
  ];
}

function viewItems(active: { profileId: string; db: string } | undefined, hoverOpen: boolean, reveal: () => void): MenuEntry[] {
  const s = useSettings.getState();
  const key = active ? schemaGroupKey(active.profileId, active.db) : "";
  const grouped = !!active && s.explorerSchemaGroups.includes(key);
  return [
    { label: "Group by environment", checked: s.explorerGroupByEnv, run: () => s.set({ explorerGroupByEnv: !s.explorerGroupByEnv }) },
    { label: "Show all saved profiles", checked: s.explorerShowAll, run: () => s.set({ explorerShowAll: !s.explorerShowAll }) },
    {
      label: "Group active database by schema", checked: grouped, unavailable: active ? undefined : "No active database",
      run: () => s.set({ explorerSchemaGroups: grouped ? s.explorerSchemaGroups.filter((k) => k !== key) : [...s.explorerSchemaGroups, key] }),
    },
    "sep",
    { label: "Reveal active database", run: reveal, unavailable: active ? undefined : "No active database" },
    { label: "Collapse all", run: () => useExplorer.getState().setExpanded(() => new Set()) },
    "sep",
    { label: "Open on hover", detail: "when auto-hidden", checked: hoverOpen, run: () => s.set({ explorerHoverOpen: !hoverOpen }) },
  ];
}

function placeholderOf(scope: ReturnType<typeof searchScope>, profiles: Map<string, Profile>) {
  if (scope.state === "noTab") return "Search needs a query tab";
  if (scope.state === "noConn") return "Search needs a connection";
  if (scope.state === "noDb") return "Search needs a database";
  return `Search ${scope.db} on ${profiles.get(scope.profileId)?.name ?? scope.profileId}…`;
}

function scopeName(scope: ReturnType<typeof searchScope>, profiles: Map<string, Profile>) {
  if (!("profileId" in scope)) return "the active tab";
  const server = profiles.get(scope.profileId)?.name ?? scope.profileId;
  return "db" in scope ? `${server} › ${scope.db}` : server;
}

/** A session-bound action on a server that is reconnecting or failed says why instead of dialing a second time. */
function whenConnected(profileId: string): boolean {
  const why = sessionReason(profileId);
  if (why) toast("info", `${why}.`);
  return !why;
}

/** Rows whose default action is only to open or close (a double-click must not toggle them back). */
function folderOnly(r: TreeRow): boolean {
  if (r.t === "server") return r.status === "connected" || r.status === "connecting";
  return r.t !== "db" && r.t !== "obj" && r.expandable;
}

/** The table or view a trigger belongs to: its tree parent, or (a search result) the catalog's record. */
function triggerOwner(r: RowOf<"obj">, rows: readonly TreeRow[]): SnapObject | undefined {
  const up = r.parent >= 0 ? rows[r.parent] : undefined;
  if (up?.t === "sub") return up.obj;
  return r.obj.parentObjectId != null ? useSnapshots.getState().get(r.profileId, r.db)?.index?.byId.get(r.obj.parentObjectId) : undefined;
}

/** What a context menu acts on, for its header line and its accessible name. */
function menuTarget(r: TreeRow, rows: readonly TreeRow[], i: number): { header: { env?: EnvironmentKind; profileId?: string; text: string }; label: string } {
  const profileId = "profileId" in r ? (r.profileId as string | undefined) : undefined;
  const p = profileId ? useProfiles.getState().byId(profileId) : undefined;
  const server = p?.name ?? "";
  const db = "db" in r ? (r.db as string | undefined) : undefined;
  let what = "";
  if (r.t === "obj") what = `${r.obj.schema}.${r.obj.name}`;
  else if (r.t === "schema") what = r.schema;
  else if (r.t === "cat") what = CATEGORIES.find((c) => c.id === r.cat)!.label;
  else if (r.t === "leaf") {
    const owner = ancestorsOf(rows, i).map((a) => rows[a]).find((x): x is RowOf<"obj"> => x.t === "obj");
    what = owner ? `${owner.obj.schema}.${owner.obj.name}.${r.text}` : r.text;
  }
  const status = r.t === "server" ? (r.status === "error" ? "connection failed" : r.status === "off" ? "not connected" : r.status === "connecting" ? "connecting" : "") : "";
  const text = [server, db].filter(Boolean).join(" › ") + (what ? ` · ${what}` : "") + (status ? ` · ${status}` : "");
  const env = p?.environment;
  return { header: { env, profileId, text }, label: `Actions for ${env ? `${ENV_TEXT[env]} ` : ""}${text}` };
}

function MenuHeader({ env, color, text }: { env?: EnvironmentKind; color?: string; text: string }) {
  return (
    <>
      {env && <EnvTag env={env} color={color ?? `var(--q-env-${env})`} />}
      <span className="min-w-0 truncate">{text}</span>
    </>
  );
}

function EnvTag({ env, color }: { env: EnvironmentKind; color: string }) {
  return (
    <span className="shrink-0 flex items-center gap-[3px] text-[10px] font-bold tracking-wide" style={{ color }}>
      {env === "prod" && <Icon name="shield" size={11} />}
      {ENV_TEXT[env]}
    </span>
  );
}

function ScopeLine({ scope, profiles, envText, kind, count, searching, compact, onReveal }: {
  scope: ReturnType<typeof searchScope>; profiles: Map<string, Profile>; envText: Map<string, string>; kind: KindFilter; count?: number; searching: boolean; compact: boolean; onReveal: () => void;
}) {
  const p = "profileId" in scope ? profiles.get(scope.profileId) : undefined;
  let right = "";
  if (searching && scope.state === "ready") right = count === undefined ? "" : `${count.toLocaleString("en-US")} match${count === 1 ? "" : "es"}${scope.stale ? " (cached)" : ""}`;
  else if (searching && scope.state === "loading") right = "Reading catalog…";
  // What the search box searches: the active tab's database, named "in ENV server › database" so it does not read as
  // the server being browsed below (the sticky path names that one).
  const where = p && "db" in scope ? `Searches the active tab's database: ${ENV_TEXT[p.environment]} ${p.name} › ${scope.db}` : undefined;
  return (
    <div id="q-explorer-scope" aria-live="polite" title={where} className="h-[22px] flex items-center gap-[5px] px-0.5 text-[11px] text-muted whitespace-nowrap overflow-hidden">
      {scope.state === "noTab" ? (
        <span className="truncate">No query tab is active</span>
      ) : scope.state === "noConn" ? (
        <span className="truncate">The active tab has no connection (Ctrl+Shift+U)</span>
      ) : scope.state === "noDb" ? (
        <span className="truncate">The active tab has no database (Ctrl+U)</span>
      ) : (
        <>
          {scope.state === "server" && <Icon name="alert" size={12} className="text-err" />}
          {scope.state === "loading" && <Icon name="spinner" size={12} className="animate-spin motion-reduce:animate-none" />}
          <span className="shrink-0">in</span>
          {p && <EnvTag env={p.environment} color={envText.get(p.id) ?? `var(--q-env-${p.environment})`} />}
          {p && <span className="shrink-0 text-muted"><EngineLogo engine={p.engine} size={12} mono /></span>}
          <span className="min-w-[3ch] truncate">{p?.name ?? ("profileId" in scope ? scope.profileId : "")}</span>
          {"db" in scope && <span className="shrink-0">›</span>}
          {"db" in scope && <span className="min-w-[3ch] truncate font-semibold">{scope.db}</span>}
        </>
      )}
      {kind !== "all" && (
        <button className="shrink-0 h-4 pl-1.5 pr-1 rounded flex items-center gap-[3px] bg-accent/15 text-accent text-[10.5px]" aria-label={`Clear kind filter ${kindLabel(kind)}`} onClick={() => useExplorer.getState().set({ kind: "all" })}>
          {kindLabel(kind)}
          <Icon name="close" size={9} />
        </button>
      )}
      <span className="flex-1" />
      {right && <span className="shrink-0">{right}</span>}
      {scope.state === "server" &&
        (compact ? (
          <button className="shrink-0 w-[18px] h-[18px] flex items-center justify-center rounded border border-line-strong text-fg hover:bg-hover" aria-label="Reconnect" title="Reconnect" onClick={() => void connectFromExplorer(scope.profileId)}>
            <Icon name="reconnect" size={11} />
          </button>
        ) : (
          <button className="shrink-0 h-[18px] px-1.5 rounded border border-line-strong text-fg text-[11px] hover:bg-hover" onClick={() => void connectFromExplorer(scope.profileId)}>
            Reconnect
          </button>
        ))}
      {!compact && "db" in scope && scope.state !== "server" && (
        <button className="shrink-0 w-[18px] h-[18px] flex items-center justify-center rounded text-muted hover:text-fg hover:bg-hover" aria-label="Reveal the active database in the tree" title="Reveal the active database in the tree" onClick={onReveal}>
          <Icon name="locate" size={12} />
        </button>
      )}
    </div>
  );
}

function Message({ icon, title, sub, action, tone = "muted" }: { icon: IconName; title: string; sub?: string; action?: { label: string; run: () => void }; tone?: "muted" | "err" }) {
  return (
    <div className="flex flex-col items-center gap-2 px-4 pt-7 pb-3 text-center">
      <Icon name={icon} size={22} className={tone === "err" ? "text-err" : "text-muted"} />
      <div className="text-[12px] font-semibold text-fg">{title}</div>
      {sub && <div className="text-[11.5px] leading-snug text-muted max-w-[230px] break-words">{sub}</div>}
      {action && (
        <button className="q-btn mt-1" onClick={action.run}>
          {action.label}
        </button>
      )}
    </div>
  );
}

function SearchMessage({ scope, profiles }: { scope: ReturnType<typeof searchScope>; profiles: Map<string, Profile> }) {
  const name = "profileId" in scope ? (profiles.get(scope.profileId)?.name ?? scope.profileId) : "";
  switch (scope.state) {
    case "noTab": return <Message icon="search" title="Search works on the active query tab’s database" sub="Open a query tab on a database to search it." />;
    case "noConn": return <Message icon="plug" title="The active tab has no connection" sub="Connect it (Ctrl+Shift+U) to search its catalog." />;
    case "noDb": return <Message icon="database" title="The active tab has no database" sub="Choose one with Ctrl+U to search it." />;
    case "loading": return <Message icon="spinner" title={`Reading the catalog of ${scope.db}…`} sub="Results appear as soon as it is loaded." />;
    case "error": return <Message icon="alert" tone="err" title={`${scope.db}’s catalog could not be read`} sub={scope.error} action={{ label: "Retry", run: () => void useSnapshots.getState().refresh(scope.profileId, scope.db, false, scope.sessionId) }} />;
    case "server":
      return (
        <Message
          icon="plug"
          tone={scope.status === "error" ? "err" : "muted"}
          title={scope.status === "connecting" ? `Connecting to ${name}…` : `${name} isn’t connected`}
          sub={scope.status === "error" ? `${sentence(scope.error ?? "The connection failed.")} Its catalog can be searched once it reconnects.` : "Its catalog can be searched once it is connected."}
          action={scope.status === "connecting" ? undefined : { label: scope.status === "error" ? "Reconnect" : "Connect", run: () => void connectFromExplorer(scope.profileId) }}
        />
      );
    default: return null;
  }
}

const sentence = (t: string) => (/[.!?…]$/.test(t.trim()) ? t.trim() : `${t.trim()}.`);

/** "SQL Server or PostgreSQL": every engine this build connects to. */
/**
 * The engines every build offers (SQLite and MySQL stay behind the developer setting, so the empty Explorer does not
 * name them): "SQL Server or PostgreSQL".
 */
const FIRST_RUN_ENGINES = offeredEngines(null);
const ENGINE_NAMES = new Intl.ListFormat("en", { type: "disjunction" }).format(FIRST_RUN_ENGINES.map((e) => ENGINES[e].shortLabel));

function FirstRun() {
  return (
    <div className="flex-1 flex flex-col items-center justify-center gap-2.5 px-5 py-6 text-center">
      <div className="flex gap-2.5 text-muted">
        {FIRST_RUN_ENGINES.map((e) => <EngineLogo key={e} engine={e} size={26} mono />)}
      </div>
      <div className="text-[13px] font-semibold text-fg">No connections yet</div>
      <div className="text-[11.5px] leading-snug text-muted max-w-[220px]">Add a {ENGINE_NAMES} connection. Passwords stay in your OS keyring.</div>
      <button className="q-btn q-btn-primary h-[28px] px-3.5 text-[12px]" onClick={() => useUi.getState().openDialog({ kind: "profile" })}>
        New connection…
      </button>
    </div>
  );
}

function NoServers({ saved }: { saved: number }) {
  return (
    <Message
      icon="plug"
      title="No servers in use"
      sub={`${saved.toLocaleString("en-US")} saved connection${saved === 1 ? "" : "s"}. Connect one, or pin a profile to keep it here.`}
      action={{ label: "Connect…", run: () => useUi.getState().openDialog({ kind: "connect" }) }}
    />
  );
}

// ------------------------------------------------------------------------------------------------- the tree view

/** Renders of the tree view, read by the bench (§14 metric 33: scrolling must not render per frame). */
export const treeStats = { renders: 0 };

function TreeView({ rows, selIndex, view, handlers, treeRef, onKeyDown, label }: {
  rows: TreeRow[]; selIndex: number; view: RowView; handlers: Handlers; treeRef: React.RefObject<HTMLDivElement | null>; onKeyDown: (e: KeyboardEvent) => void; label: string;
}) {
  treeStats.renders++;
  const [block, setBlock] = useState(0);
  const [height, setHeight] = useState(400);
  const [sticky, setSticky] = useState("");
  const [focused, setFocused] = useState(false);
  const state = useRef({ block: 0, sticky: "" });
  const pointer = useRef(false);
  const rowsRef = useRef(rows);
  rowsRef.current = rows;
  const prevKeysRef = useRef<Set<string> | null>(null);
  const freshKeys = useMemo(() => {
    const prev = prevKeysRef.current;
    const next = new Set(rows.map((r) => r.key));
    prevKeysRef.current = next;
    if (!prev || prev.size === 0) return null;
    const added = new Set<string>();
    for (const r of rows) {
      if (!prev.has(r.key)) added.add(r.key);
    }
    return added.size > 0 && added.size <= 80 ? added : null;
  }, [rows]);

  useEffect(() => {
    const el = treeRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setHeight(el.clientHeight || 400));
    ro.observe(el);
    return () => ro.disconnect();
  }, [treeRef]);

  // The scroll position lives in the DOM; React hears of it only when the rendered block or the sticky path changes.
  const sync = useCallback(() => {
    const el = treeRef.current;
    if (!el) return;
    const top = el.scrollTop;
    const b = Math.floor(top / ROW / BLOCK);
    if (b !== state.current.block) {
      state.current.block = b;
      setBlock(b);
    }
    // The path of the row under the sticky row, shown while its top-level ancestor is scrolled away.
    const first = Math.floor(top / ROW) + 1;
    const chain = first > 1 && rowsRef.current[first] ? ancestorsOf(rowsRef.current, first) : [];
    const s = chain.length && chain[0] < first - 1 ? chain.join(",") : "";
    if (s !== state.current.sticky) {
      state.current.sticky = s;
      setSticky(s);
    }
  }, [treeRef]);
  useLayoutEffect(sync, [rows, sync]);

  const start = Math.max(0, (block - 1) * BLOCK);
  const end = Math.min(rows.length, start + Math.ceil(height / ROW) + 3 * BLOCK);
  const indices: number[] = [];
  for (let i = start; i < end; i++) indices.push(i);
  if (selIndex >= 0 && (selIndex < start || selIndex >= end)) indices.push(selIndex); // aria-activedescendant must exist

  const chain = sticky ? sticky.split(",").map(Number).filter((n) => rows[n]) : [];
  return (
    <div className="flex-1 min-h-0 relative">
      {chain.length > 0 && <StickyPath rows={rows} chain={chain} view={view} onJump={(i) => { handlers.select(i); const el = treeRef.current; if (el) el.scrollTop = i * ROW; }} />}
      <div
        ref={treeRef}
        role="tree"
        data-q-explorer-tree
        aria-label={label}
        tabIndex={0}
        aria-activedescendant={selIndex >= 0 ? `qx-row-${selIndex}` : undefined}
        className="h-full overflow-y-auto relative text-[12px]"
        style={{ outline: "none" }}
        onScroll={sync}
        onKeyDown={onKeyDown}
        onPointerDown={() => (pointer.current = true)}
        onPointerUp={() => (pointer.current = false)}
        onFocus={(e) => {
          if (e.target !== e.currentTarget) return;
          setFocused(true);
          // Focus from a press selects the pressed row (its click does); selecting the first row here would scroll the
          // tree to the top under the pointer and lose the click.
          if (pointer.current) return;
          // The selection the store holds now (↓ from the search box sets it just before focusing), not this render's.
          const sel = useExplorer.getState().selected;
          if ((!sel || !rowsRef.current.some((r) => r.key === sel)) && rowsRef.current.length) handlers.select(0);
        }}
        onBlur={(e) => e.target === e.currentTarget && setFocused(false)}
      >
        <div style={{ height: rows.length * ROW, position: "relative" }}>
          {indices.map((i) => (
            <Row key={rows[i].key} row={rows[i]} index={i} selected={i === selIndex} focused={focused && i === selIndex} fresh={!!freshKeys?.has(rows[i].key)} view={view} handlers={handlers} />
          ))}
        </div>
      </div>
    </div>
  );
}

function StickyPath({ rows, chain, view, onJump }: { rows: TreeRow[]; chain: number[]; view: RowView; onJump: (i: number) => void }) {
  return (
    // A sticky header: it covers the row scrolled under it, by design (the layout audit reads it as its own layer).
    <div aria-hidden data-layout-layer="sticky" className="absolute top-0 left-0 right-2 z-10 h-[24px] flex items-center gap-[5px] px-2 bg-panel-2 border-b border-line text-[11px] text-muted whitespace-nowrap overflow-hidden">
      {chain.map((i, n) => {
        const r = rows[i];
        const p = "profileId" in r ? view.profiles.get(r.profileId as string) : undefined;
        return (
          <span key={r.key} className={`flex items-center gap-[5px] ${n === 0 ? "min-w-0" : "shrink-0"}`}>
            {n > 0 && <span>›</span>}
            <button tabIndex={-1} className="flex items-center gap-1 min-w-0 text-fg hover:underline" onClick={() => onJump(i)}>
              {r.t === "server" && p && <EnvTag env={p.environment} color={view.envText.get(p.id) ?? `var(--q-env-${p.environment})`} />}
              <span className="truncate">{rowName(r, view.profiles) || (r.t === "sysdbs" ? "System databases" : r.t === "sub" ? r.sub : "")}</span>
            </button>
          </span>
        );
      })}
    </div>
  );
}

const ICON_OF: Partial<Record<string, IconName>> = { table: "table", view: "view", proc: "proc", func: "func", tvf: "func", type: "type", sequence: "sequence", synonym: "synonym", trigger: "trigger" };
const OBJ_COLOR: Record<string, string> = {
  table: "text-obj-table", view: "text-obj-view", proc: "text-obj-proc", func: "text-obj-func", tvf: "text-obj-func", type: "text-obj-type", sequence: "text-obj-sequence", synonym: "text-obj-synonym", trigger: "text-obj-proc",
};
const TONE: Record<string, string> = { warn: "text-warn", muted: "text-muted", ident: "text-ident", "obj.index": "text-obj-index", "obj.table": "text-obj-table" };

const Row = memo(function Row({ row: r, index: i, selected, focused, fresh, view, handlers }: {
  row: TreeRow; index: number; selected: boolean; focused: boolean; fresh?: boolean; view: RowView; handlers: Handlers;
}) {
  const style: CSSProperties = { top: i * ROW, height: ROW, paddingLeft: 6 + (r.level - 1) * view.indent };
  const label = ariaLabelOf(r, view);
  const header = r.t === "header";
  const common = {
    id: `qx-row-${i}`,
    "data-row": i,
    role: "treeitem",
    "aria-level": r.level,
    "aria-setsize": r.size,
    "aria-posinset": r.pos,
    "aria-expanded": r.expandable || (r.t === "server" && r.status === "off") ? r.expanded : undefined,
    "aria-selected": selected,
    "aria-busy": (r.t === "server" && (r.status === "connecting" || r.refreshing)) || (r.t === "note" && r.busy) || (r.t === "db" && (r.state === "loading" || r.state === "refreshing")) || undefined,
    "aria-label": label,
    style,
    onClick: (e: MouseEvent) => handlers.click(i, e),
    onDoubleClick: () => handlers.activate(i),
    onContextMenu: (e: MouseEvent) => {
      e.preventDefault();
      handlers.menu(i, e.clientX, e.clientY);
    },
  };
  const cls = `q-tree-row ${fresh ? "q-tree-row-reveal " : ""}group absolute left-0 right-0 flex items-center gap-1.5 pr-2 whitespace-nowrap overflow-hidden ${header ? "bg-panel-2" : selected ? "bg-row-sel q-hl shadow-[inset_2px_0_0_var(--q-accent)]" : "hover:bg-hover"} ${focused ? "outline outline-2 -outline-offset-2 outline-accent" : ""}`;
  const caret = r.expandable || (r.t === "server" && r.status === "off") ? (
    <span aria-hidden className="w-3 shrink-0 flex items-center justify-center text-muted" onClick={(e) => { e.stopPropagation(); handlers.select(i); handlers.caret(i); }}>
      <Icon name="chevron" size={10} strokeWidth={2.5} className={`q-tree-caret ${r.expanded ? "" : "-rotate-90"}`} />
    </span>
  ) : (
    <span aria-hidden className="w-3 shrink-0" />
  );
  const draggable = r.t === "obj" || (r.t === "leaf" && !!r.drag) || (r.t === "sub" && r.sub === "columns");
  return (
    <div {...common} className={cls} draggable={draggable || undefined} onDragStart={draggable ? (e) => handlers.drag(i, e) : undefined}>
      {header ? null : caret}
      <RowBody r={r} view={view} handlers={handlers} />
    </div>
  );
});

function RowBody({ r, view, handlers }: { r: TreeRow; view: RowView; handlers: Handlers }) {
  switch (r.t) {
    case "env":
      return (
        <>
          <span aria-hidden className="w-[7px] h-[7px] rounded-full shrink-0" style={{ background: `var(--q-env-${r.env})` }} />
          <span className="text-[10.5px] font-bold tracking-wider flex-1 truncate">{ENV_TEXT[r.env]}</span>
          <span className="text-[10px] text-muted q-mono shrink-0">{r.count}</span>
        </>
      );
    case "server": {
      const p = view.profiles.get(r.profileId);
      return (
        <>
          <span aria-hidden className="shrink-0 text-muted"><EngineLogo engine={p?.engine} mono /></span>
          <span className={`flex-1 min-w-0 truncate font-semibold ${r.status === "off" ? "text-muted" : "text-fg"}`}>{p?.name ?? r.profileId}</span>
          {r.status === "off" && (
            <span aria-hidden className={`shrink-0 h-[18px] px-1.5 rounded border border-line-strong text-fg text-[11px] items-center gap-1 ${view.wide ? "flex" : "hidden group-hover:flex group-aria-selected:flex"}`} onClick={(e) => { e.stopPropagation(); void connectFromExplorer(r.profileId); }}>
              <Icon name="plug" size={11} />
              Connect
            </span>
          )}
          {p && <EnvTag env={p.environment} color={view.envText.get(p.id) ?? `var(--q-env-${p.environment})`} />}
          {(r.status === "connecting" || r.refreshing) && (
            <span className="shrink-0 flex items-center gap-1 text-accent text-[11px]">
              {!view.compact && (r.refreshing ? "Refreshing…" : "Connecting…")}
              <Icon name="spinner" size={13} className="animate-spin motion-reduce:animate-none" />
            </span>
          )}
          {r.status === "error" && (
            <span className="shrink-0 flex items-center gap-1 text-err text-[11px]" title={r.error}>
              {!view.compact && "Failed"}
              <Icon name="alert" size={13} />
            </span>
          )}
          {r.status === "off" && <Icon name="ring" size={13} className="text-muted" />}
        </>
      );
    }
    case "db":
      return (
        <>
          <Icon name="database" size={14} className={r.active ? "text-accent" : "text-muted"} />
          <span className={`flex-1 min-w-0 truncate ${r.active ? "font-bold text-fg" : r.state === "offline" ? "text-muted" : "text-fg"}`}>{r.db}</span>
          {r.active && !view.narrow && <span className="shrink-0 h-4 px-[5px] rounded text-[9.5px] font-bold tracking-wide leading-4 text-accent bg-accent/15">ACTIVE</span>}
          {r.state === "offline" && <span className="shrink-0 text-[10.5px] text-muted">{(r.dbState ?? "offline").toUpperCase()}</span>}
          {(r.state === "loading" || r.state === "refreshing") && (
            <span className="shrink-0 flex items-center gap-1 text-[11px] text-muted">
              {!view.compact && (r.state === "loading" ? "Loading…" : "Refreshing…")}
              <Icon name="spinner" size={12} className="animate-spin motion-reduce:animate-none" />
            </span>
          )}
          {r.state === "stale" && (
            <span className="shrink-0 flex items-center gap-1 text-[11px] text-warn" title={`Refresh failed: ${r.error ?? "unknown error"}. Showing the cached catalog.`}>
              {!view.compact && `cached${r.refreshedAt ? ` ${clock(r.refreshedAt)}` : ""}`}
              <Icon name="alert" size={12} />
            </span>
          )}
          {r.state === "error" && <Icon name="alert" size={12} className="text-err" />}
        </>
      );
    case "sysdbs":
      return (
        <>
          <Icon name="folder" size={14} className="text-muted" />
          <span className="flex-1 min-w-0 truncate text-fg">System databases</span>
          <span className="shrink-0 text-[10px] text-muted q-mono">{r.count}</span>
        </>
      );
    case "schema":
      return (
        <>
          <Icon name="schema" size={14} className="text-muted" />
          <span className="flex-1 min-w-0 truncate q-mono text-[11.5px] text-fg">{r.schema}</span>
          <span className="shrink-0 text-[10px] text-muted q-mono">{r.count.toLocaleString("en-US")}</span>
        </>
      );
    case "cat": {
      const c = CATEGORIES.find((x) => x.id === r.cat)!;
      return (
        <>
          <Icon name={c.icon as IconName} size={14} className={OBJ_COLOR[c.kinds[0]]} />
          <span className={`flex-1 min-w-0 truncate ${r.count ? "text-fg" : "text-muted"}`}>{c.label}</span>
          <span className="shrink-0 text-[10px] text-muted q-mono">{r.shown !== undefined ? `${r.shown.toLocaleString("en-US")} / ${r.count.toLocaleString("en-US")}` : r.count.toLocaleString("en-US")}</span>
        </>
      );
    }
    case "filter":
      return (
        <span className="flex-1 min-w-0 h-5 flex items-center gap-[5px] pl-1.5 pr-1 rounded border border-accent bg-bg">
          <Icon name="filter" size={11} className="text-muted" />
          <span className="flex-1 min-w-0 truncate q-mono text-[11.5px] text-fg">{r.text}</span>
          <span className="shrink-0 text-[10.5px] text-muted">{`${r.shown.toLocaleString("en-US")} of ${r.total.toLocaleString("en-US")}`}</span>
          <span aria-hidden className="shrink-0 text-muted hover:text-fg" title="Clear filter (Esc)" onClick={(e) => { e.stopPropagation(); handlers.clearFilter(r.catKey); }}>
            <Icon name="close" size={10} />
          </span>
        </span>
      );
    case "obj":
      return (
        <>
          <Icon name={ICON_OF[r.obj.kind] ?? "table"} size={14} className={OBJ_COLOR[r.obj.kind]} />
          <span className="flex-1 min-w-0 truncate q-mono text-[11.5px] text-fg" title={`${r.obj.schema}.${r.obj.name} (${r.obj.kind})`}>
            {!r.bare && <span className="text-muted">{r.obj.schema}.</span>}
            {r.obj.name}
          </span>
          {r.obj.note && <span className={`shrink-0 text-[10.5px] ${/invalid/.test(r.obj.note) ? "text-warn" : "text-muted"}`}>{r.obj.note}</span>}
        </>
      );
    case "sub":
      return <span className="flex-1 min-w-0 truncate text-muted">{SUB_TEXT[r.sub]}</span>;
    case "leaf":
      return (
        <>
          <Icon name={r.icon as IconName} size={12} className={TONE[r.tone] ?? "text-muted"} />
          <span className="shrink-0 max-w-[70%] truncate q-mono text-[11.5px] text-fg" title={`${r.text} ${r.detail ?? ""}`}>{r.text}</span>
          {r.detail && <span className="min-w-0 truncate q-mono text-[10.5px] text-muted" title={`${r.text} ${r.detail}`}>{r.detail}</span>}
        </>
      );
    case "note":
      return (
        <span className={`flex-1 min-w-0 flex items-center gap-1.5 ${r.tone === "err" ? "text-err" : r.tone === "warn" ? "text-warn" : "text-muted"}`} title={r.text}>
          {r.busy && <Icon name="spinner" size={12} className="animate-spin motion-reduce:animate-none" />}
          <span className="truncate">{r.text}</span>
        </span>
      );
    case "action":
      return (
        <>
          <Icon name={r.action === "editProfile" || r.action === "repick" ? "edit" : "reconnect"} size={13} className={r.action === "editProfile" || r.action === "repick" ? "text-muted" : "text-accent"} />
          <span className={`flex-1 min-w-0 truncate ${r.action === "editProfile" || r.action === "repick" ? "text-fg" : "text-accent"}`}>{r.label}</span>
        </>
      );
    case "header":
      return (
        <>
          <span className="pl-1 flex-1 min-w-0 truncate text-[10px] font-bold tracking-wider text-muted">{r.text}</span>
          {r.capped && r.kind && (
            <span aria-hidden className="shrink-0 text-[11px] text-accent hover:underline" onClick={(e) => { e.stopPropagation(); handlers.showAll(r.kind!); }}>
              Show all
            </span>
          )}
        </>
      );
  }
}

const SUB_TEXT = { columns: "Columns", keys: "Keys", indexes: "Indexes", params: "Parameters", triggers: "Triggers" } as const;

function clock(ms: number) {
  const d = new Date(ms);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/** The row as a screen reader hears it: kind, name, state ("PROD, Primary Billing, SQL Server, connection failed"). */
function ariaLabelOf(r: TreeRow, view: RowView): string {
  switch (r.t) {
    case "server": {
      const p = view.profiles.get(r.profileId);
      const st = r.status === "error" ? `connection failed: ${r.error ?? ""}` : r.status === "off" ? "not connected" : r.status;
      return `${p ? ENV_TEXT[p.environment] : ""}, ${p?.name ?? r.profileId}, ${engineTraits(p?.engine).shortLabel}, ${st}${r.pinned ? ", pinned" : ""}`;
    }
    case "db":
      return `Database ${r.db}${r.active ? ", active in tab" : ""}${r.state === "stale" ? ", refresh failed, cached catalog" : r.state === "offline" ? `, ${(r.dbState ?? "offline").toLowerCase()}` : r.state === "loading" ? ", loading" : ""}`;
    case "cat": return `${CATEGORIES.find((c) => c.id === r.cat)!.label}, ${r.shown !== undefined ? `${r.shown} of ${r.count}` : r.count}${r.filter ? `, filtered by ${r.filter}` : ""}`;
    case "obj": return `${r.obj.kind} ${r.obj.schema}.${r.obj.name}`;
    case "leaf": return `${r.text}${r.detail ? `, ${r.detail}` : ""}`;
    case "filter": return `Filter ${r.text}, ${r.shown} of ${r.total}`;
    case "schema": return `Schema ${r.schema}, ${r.count} objects`;
    case "sysdbs": return `System databases, ${r.count}`;
    case "sub": return SUB_TEXT[r.sub];
    case "note": return r.text;
    case "action": return r.label;
    case "header": return r.text;
    case "env": return `${envLabel[r.env]}, ${r.count} server${r.count === 1 ? "" : "s"}`;
  }
}

/** Visual Studio tool-window title buttons: pushpin (docked ↔ auto-hide) and close. */
function WindowButtons() {
  const autoHide = useSettings((s) => s.explorerAutoHide);
  return (
    <>
      <button className="q-tb !h-6 !min-w-6 !px-0" title={autoHide ? "Dock (pin)" : "Auto hide"} aria-label={autoHide ? "Dock (pin)" : "Auto hide"} aria-pressed={!autoHide} onClick={() => explorerAction("togglePin")}>
        <Icon name="pin" size={14} className={autoHide ? "rotate-90" : ""} />
      </button>
      <button className="q-tb !h-6 !min-w-6 !px-0" title="Close (Ctrl+B to reopen)" aria-label="Close (Ctrl+B to reopen)" onClick={() => explorerAction("close")}>
        <Icon name="close" size={13} />
      </button>
    </>
  );
}


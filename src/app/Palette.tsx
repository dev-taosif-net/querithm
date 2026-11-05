// Command palette (Ctrl+Shift+P), Go to object (Ctrl+T), Change database (Ctrl+U), Change connection (Ctrl+Shift+U).
// Lists every command with its shortcut, plus recent connections and scripts (§12).
import { useEffect, useMemo, useRef, useState } from "react";
import { useUi, toast, type Toast } from "../store/ui";
import { useActiveTab } from "../store/tabs";
import { engineBadge, envColor, envLabel, useProfiles } from "../store/profiles";
import { dialectOf } from "../engines";
import { useConnections } from "../store/connections";
import { tabSnapKey, useSnapshots } from "../store/snapshots";
import { commands, displayKeys, keysFor } from "./commands";
import { activateTab, changeConnection, changeDatabase, newQueryTab, openDefinition, scriptObject } from "./actions";
import { useTabs } from "../store/tabs";
import { TabList } from "./TabList";
import { Icon } from "./icons";
import { snippetsFor, useSnippets } from "../snippets/snippetStore";

interface Item {
  id: string;
  label: string;
  detail?: string;
  hint?: string;
  color?: string;
  run: () => void;
}

function score(label: string, q: string): number {
  if (!q) return 1;
  const l = label.toLowerCase();
  if (l.startsWith(q)) return 100 - l.length / 100;
  const i = l.indexOf(q);
  if (i >= 0) return 60 - i / 10;
  // subsequence
  let j = 0;
  for (const ch of l) if (ch === q[j]) j++;
  return j === q.length ? 20 : 0;
}

export function Palette() {
  const palette = useUi((s) => s.palette);
  const tab = useActiveTab();
  const profiles = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const servers = useConnections((s) => s.servers);
  const disconnected = useConnections((s) => (tab?.profileId ? !!s.disconnected[tab.profileId] : false));
  // A session-owned engine's tab (SQLite) searches its own session's view (Phase D0d).
  const snapKey = tabSnapKey(tab);
  const snap = useSnapshots((s) => (snapKey ? s.entries[snapKey] : undefined));
  const tabs = useTabs((s) => s.tabs);
  const snippetLib = useSnippets();
  const [q, setQ] = useState("");
  const [sel, setSel] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const [mode, setMode] = useState(palette?.mode ?? "commands");

  useEffect(() => {
    if (!palette) return;
    setMode(palette.mode);
    setQ(palette.initial ?? (palette.mode === "commands" ? ">" : ""));
    setSel(0);
    setTimeout(() => input.current?.focus(), 0);
    // Opening the list reconnects a server that was never connected, never one the user disconnected.
    if (palette.mode === "databases" && tab?.profileId && !servers[tab.profileId] && !disconnected) void useConnections.getState().connect(tab.profileId);
    if (palette.mode === "snippets") void useSnippets.getState().load();
  }, [palette]);

  const close = () => useUi.getState().setPalette(null);

  const items: Item[] = useMemo(() => {
    if (!palette) return [];
    const query = q.replace(/^>/, "").trim().toLowerCase();
    const effective = q.startsWith(">") ? "commands" : mode === "commands" ? "objects" : mode;
    let list: Item[] = [];
    if (effective === "commands") {
      list = commands.map((c) => ({ id: c.id, label: c.title, detail: c.category, hint: keysFor(c).map(displayKeys).join("  "), run: () => void c.run() }));
    } else if (effective === "objects") {
      const objs = disconnected ? [] : (snap?.snapshot?.objects ?? []);
      list = objs.map((o) => ({
        id: `o${o.id}`,
        label: `${o.schema}.${o.name}`,
        detail: o.kind,
        hint: o.kind === "table" || o.kind === "view" ? "Enter: select top 1000" : "Enter: modify",
        run: () => {
          if (!tab?.profileId || !tab.database) return;
          const ref = { schema: o.schema, name: o.name, kind: o.kind, objectId: o.id };
          if (o.kind === "table" || o.kind === "view") void scriptObject(tab.profileId, tab.database, ref, "selectTop", true);
          else void openDefinition(tab.profileId, tab.database, ref, true);
        },
      }));
      // Open tabs are also navigable.
      list.push(...Object.values(tabs).map((t) => ({ id: `t${t.id}`, label: t.title, detail: "open tab", run: () => activateTab(t.id) })));
      if (!objs.length && !query) list.unshift({ id: "none", label: disconnected ? "Disconnected: connect the server to search its objects" : tab?.profileId ? "Metadata is loading…" : "Connect the tab to a database to search objects", run: () => {} });
    } else if (effective === "databases") {
      const conn = tab?.profileId && !disconnected ? servers[tab.profileId] : undefined;
      list = (conn?.status === "connected" ? conn.databases : []).map((d) => ({
        id: d.name, label: d.name, detail: d.isSystem ? "system" : d.state ?? "", hint: d.name === tab?.database ? "current" : undefined,
        run: () => tab && void changeDatabase(tab.id, d.name),
      }));
      if (disconnected) {
        const pid = tab!.profileId!;
        list = [{ id: "c", label: "Disconnected. Connect to list databases", detail: "Enter: connect", run: () => void useConnections.getState().connect(pid).then((ok) => ok && useUi.getState().setPalette({ mode: "databases" })) }];
      } else if (conn?.status === "error") list = [{ id: "c", label: conn.error ?? "Connection failed", run: () => {} }];
      else if (conn?.status !== "connected") list = [{ id: "c", label: "Connecting…", run: () => {} }];
    } else if (effective === "snippets") {
      const dialect = dialectOf(profiles.find((p) => p.id === tab?.profileId)?.engine);
      list = snippetsFor(snippetLib.snippets, dialect).map((s) => ({
        id: s.id, label: `${s.prefix} — ${s.title}`, detail: s.builtIn ? "built-in" : s.tags.join(", "), hint: "Enter: insert",
        run: () => void import("../snippets/snippetActions").then((m) => m.insertSnippet(s)),
      }));
      if (!list.length) list = [{ id: "none", label: snippetLib.error ?? (snippetLib.loaded ? "No snippets yet: use \"Save selection as snippet\"" : "Loading snippets…"), run: () => {} }];
    } else if (effective === "connections") {
      list = profiles.map((p) => ({
        id: p.id, label: `[${engineBadge(p)}] ${envLabel[p.environment]} - ${p.name}`, detail: `${p.host ?? ""}${p.port ? `:${p.port}` : ""}`, color: envColor(p, groups),
        hint: servers[p.id]?.status === "connected" ? "connected" : undefined,
        run: () => {
          if (tab) void changeConnection(tab.id, p.id);
          else newQueryTab({ profileId: p.id });
        },
      }));
      list.push({ id: "new", label: "+ New connection profile…", run: () => useUi.getState().openDialog({ kind: "profile" }) });
    }
    return list
      .map((i) => ({ i, s: score(i.label, query) }))
      .filter((x) => x.s > 0)
      .sort((a, b) => b.s - a.s)
      .slice(0, 200)
      .map((x) => x.i);
  }, [palette, q, mode, snap?.version, profiles, servers, disconnected, tab?.id, tab?.database, tabs, snippetLib]);

  if (!palette) return null;
  if (palette.mode === "tabs") return <TabList onClose={close} only={palette.only} />;
  const placeholder =
    mode === "commands" ? "Type a command…" : mode === "objects" ? `Go to object in ${tab?.database ?? "…"} (type > for commands)` : mode === "databases" ? "Switch database…" : mode === "snippets" ? "Insert snippet…" : "Switch connection…";
  const run = (i: Item | undefined) => {
    if (!i) return;
    close();
    try {
      i.run();
    } catch (e) {
      toast("error", String(e));
    }
  };
  return (
    <div className="q-modal-backdrop fixed inset-0 z-40" onMouseDown={(e) => e.target === e.currentTarget && close()}>
      <div className="q-palette-in absolute left-1/2 top-[64px] -translate-x-1/2 w-[620px] max-w-[92vw] bg-raised border border-line rounded-lg shadow-2xl overflow-hidden">
        <input
          ref={input}
          className="w-full bg-panel px-3 py-2.5 text-[13px] text-fg outline-none border-b border-line-strong"
          placeholder={placeholder}
          value={q}
          onChange={(e) => {
            setQ(e.target.value);
            setSel(0);
          }}
          onKeyDown={(e) => {
            if (e.key === "Escape") close();
            else if (e.key === "ArrowDown") { e.preventDefault(); setSel((s) => Math.min(items.length - 1, s + 1)); }
            else if (e.key === "ArrowUp") { e.preventDefault(); setSel((s) => Math.max(0, s - 1)); }
            else if (e.key === "Enter") { e.preventDefault(); run(items[sel]); }
          }}
        />
        <div className="max-h-[50vh] overflow-y-auto py-1">
          {items.map((i, k) => (
            <div
              key={i.id}
              ref={(el) => { if (k === sel && el) el.scrollIntoView({ block: "nearest" }); }}
              className={`px-3 py-1.5 flex items-center justify-between gap-3 cursor-default ${k === sel ? "bg-accent/25 q-hl" : "hover:bg-hover"}`}
              onMouseEnter={() => setSel(k)}
              onMouseDown={(e) => { e.preventDefault(); run(i); }}
            >
              <span className="truncate" style={i.color ? { color: i.color } : undefined}>
                <span className="text-fg q-mono text-[12px]" style={i.color ? { color: i.color } : undefined}>{i.label}</span>
                {i.detail && <span className="ml-2 text-muted text-[11px]">{i.detail}</span>}
              </span>
              {i.hint && <span className="q-kbd shrink-0">{i.hint}</span>}
            </div>
          ))}
          {!items.length && <div className="px-3 py-3 text-muted">No matches.</div>}
        </div>
      </div>
    </div>
  );
}

const TOAST_TONE: Record<Toast["kind"], string> = {
  error: "bg-err/15 border-err/50 text-err",
  warn: "bg-warn/15 border-warn/50 text-warn",
  success: "bg-ok/15 border-ok/50 text-ok",
  info: "bg-raised border-line text-fg",
};
/** Said before an error or a warning, so its kind does not rest on its color alone. */
const TOAST_PREFIX: Partial<Record<Toast["kind"], string>> = { error: "Error: ", warn: "Warning: " };

/**
 * The toasts (M20): screen readers hear each one through the app's Announcer (store/ui); this list is what is seen.
 * Their time stops while the pointer is over them or focus is inside them; each has a Dismiss button, and a click on
 * one that selects no text dismisses it too.
 */
export function Toasts() {
  const toasts = useUi((s) => s.toasts);
  const box = useRef<HTMLDivElement>(null);
  const hold = useUi.getState().holdToasts;
  // A dismissed toast took focus with it (no blur follows a removed element): the time runs again.
  useEffect(() => {
    if (!box.current?.contains(document.activeElement)) hold("focus", false);
  }, [toasts, hold]);
  const dismiss = (id: number, fromKeyboard: boolean) => {
    const next = toasts[toasts.findIndex((t) => t.id === id) + 1] ?? toasts.at(-2);
    useUi.getState().dismissToast(id);
    if (fromKeyboard && next && next.id !== id) requestAnimationFrame(() => box.current?.querySelector<HTMLElement>(`[data-q-toast="${next.id}"] button`)?.focus());
  };
  return (
    <div
      ref={box}
      role="region"
      aria-label="Notifications"
      className="fixed right-4 bottom-8 z-50 space-y-2 w-[380px]"
      onPointerEnter={() => hold("pointer", true)}
      onPointerLeave={() => hold("pointer", false)}
      onFocus={() => hold("focus", true)}
      onBlur={(e) => !box.current?.contains(e.relatedTarget as Node | null) && hold("focus", false)}
    >
      {toasts.map((t) => (
        <div
          key={t.id}
          data-q-toast={t.id}
          onClick={(e) => {
            if (e.target instanceof Element && e.target.closest("button")) return;
            if (window.getSelection()?.isCollapsed !== false) useUi.getState().dismissToast(t.id);
          }}
          className={`q-toast-in pl-3 pr-1 py-1.5 rounded-lg shadow-xl border text-[12px] select-text cursor-default flex items-start gap-2 ${TOAST_TONE[t.kind]}`}
          style={{ backdropFilter: "blur(6px)" }}
        >
          <span className="flex-1 min-w-0 py-0.5 break-words">
            {TOAST_PREFIX[t.kind] && <span className="sr-only">{TOAST_PREFIX[t.kind]}</span>}
            {t.text}
            {(t.count ?? 1) > 1 && (
              <>
                <span aria-hidden className="ml-1.5 opacity-75 tabular-nums">×{t.count}</span>
                <span className="sr-only">, {t.count} times</span>
              </>
            )}
          </span>
          <button
            type="button"
            className="shrink-0 w-6 h-6 flex items-center justify-center rounded opacity-70 hover:opacity-100 hover:bg-hover focus-visible:opacity-100"
            aria-label="Dismiss"
            title="Dismiss"
            onClick={(e) => dismiss(t.id, e.detail === 0)}
          >
            <Icon name="close" size={12} strokeWidth={2.2} />
          </button>
        </div>
      ))}
    </div>
  );
}

/** The one-line notice under the top bar (engine stopped or restarted, a damaged file); read at once when it appears (store/ui). */
export function Banner() {
  const banner = useUi((s) => s.banner);
  if (!banner) return null;
  return (
    <div
      role="region"
      aria-label={banner.kind === "error" ? "Error notice" : "Notice"}
      className={`q-banner-in min-h-[30px] py-1 px-4 flex items-center justify-between gap-3 text-xs shrink-0 border-b ${banner.kind === "error" ? "bg-err/15 border-err/40 text-err" : "bg-warn/15 border-warn/40 text-warn"}`}
    >
      <span className="min-w-0 select-text">{banner.text}</span>
      <div className="flex gap-2 shrink-0">
        {banner.action && <button className="q-btn h-[22px]" onClick={banner.action.run}>{banner.action.label}</button>}
        <button className="q-btn q-btn-ghost h-[22px]" onClick={() => useUi.getState().setBanner(null)}>Dismiss</button>
      </div>
    </div>
  );
}

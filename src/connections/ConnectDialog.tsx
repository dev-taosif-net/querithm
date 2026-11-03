// [+ Connect] profile picker (mockup screen 4, modal A): compact rows, search, environment filter, pins, edit/delete.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import type { Profile } from "../ipc/contract";
import { Modal } from "../app/Modal";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { EngineLogo } from "./EngineLogo";
import { useConnections } from "../store/connections";
import { useSettings } from "../store/settings";
import { useUi } from "../store/ui";
import { activateTab, changeConnection, newQueryTab } from "../app/actions";
import { tabForNewConnection } from "../app/tabActions";
import { useTabs } from "../store/tabs";
import { buildSections, ENV_FILTERS, envCounts, filterProfiles, togglePin, type EnvFilter } from "./profileList";
import { confirmDeleteProfile } from "./deleteProfile";

export function ConnectDialog({ onClose }: { onClose: () => void }) {
  const profiles = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const servers = useConnections((s) => s.servers);
  const pinned = useSettings((s) => s.pinnedProfiles);
  const ui = useUi.getState();
  const [text, setText] = useState("");
  const [env, setEnv] = useState<EnvFilter>("all");
  const [activeId, setActiveId] = useState<string>();
  const listRef = useRef<HTMLDivElement>(null);

  const counts = useMemo(() => envCounts(profiles), [profiles]);
  const filtered = useMemo(() => filterProfiles(profiles, text, env), [profiles, text, env]);
  const { sections, flat } = useMemo(() => buildSections(filtered, pinned), [filtered, pinned]);
  const active = flat.find((p) => p.id === activeId) ?? flat[0];

  useEffect(() => {
    if (active) listRef.current?.querySelector(`[data-profile="${CSS.escape(active.id)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);

  const connect = async (p: Profile) => {
    onClose();
    const ok = await useConnections.getState().connect(p.id);
    if (!ok) return;
    // An unconnected tab (the active one, else an empty spare) takes the connection instead of a new tab piling up.
    const reuse = tabForNewConnection();
    if (reuse) {
      await changeConnection(reuse, p.id);
      activateTab(reuse);
    } else if (!useTabs.getState().activeId) newQueryTab({ profileId: p.id });
  };
  const edit = (p: Profile) => {
    onClose();
    ui.openDialog({ kind: "profile", profileId: p.id });
  };
  const pin = (p: Profile) => useSettings.getState().set({ pinnedProfiles: togglePin(useSettings.getState().pinnedProfiles, p.id) });

  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.defaultPrevented || !flat.length) return;
    const i = active ? flat.indexOf(active) : -1;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const next = e.key === "ArrowDown" ? Math.min(flat.length - 1, i + 1) : Math.max(0, i - 1);
      setActiveId(flat[next].id);
    } else if (e.key === "Enter" && active && servers[active.id]?.status !== "connected") {
      e.preventDefault();
      void connect(active);
    }
  };

  const filtering = text.trim() !== "" || env !== "all";

  return (
    <Modal
      title="Connect to database profile"
      subtitle="Connected and pinned servers appear in the Object Explorer"
      onClose={onClose}
      width={620}
      bodyClassName="flex flex-col min-h-0"
      footer={
        <>
          <span className="mr-auto self-center text-[10.5px] text-muted">
            {filtering ? `${flat.length} of ${profiles.length} profiles` : `${profiles.length} profile${profiles.length === 1 ? "" : "s"}`} · ↑↓ select · Enter connect
          </span>
          <button className="q-btn" onClick={onClose}>Close</button>
          <button className="q-btn q-btn-primary" onClick={() => { onClose(); ui.openDialog({ kind: "profile" }); }}>+ New Profile</button>
        </>
      }
    >
      {profiles.length > 0 && (
        <div className="px-4 pt-3 pb-2 space-y-2 border-b border-line">
          <div className="relative">
            <span className="absolute left-2 top-1/2 -translate-y-1/2 text-muted pointer-events-none"><SearchIcon /></span>
            <input
              autoFocus
              className="q-input w-full h-7 pl-7"
              placeholder="Search name, host, group, database or engine…"
              aria-label="Search profiles"
              value={text}
              onChange={(e) => { setText(e.target.value); setActiveId(undefined); }}
              onKeyDown={onSearchKey}
            />
          </div>
          <div className="flex flex-wrap gap-1" role="radiogroup" aria-label="Filter by environment">
            <EnvChip label="All" count={profiles.length} on={env === "all"} onClick={() => setEnv("all")} />
            {ENV_FILTERS.map((e) => (
              <EnvChip key={e} label={envLabel[e]} count={counts[e]} color={`var(--q-env-${e})`} on={env === e} disabled={!counts[e]} onClick={() => { setEnv(env === e ? "all" : e); setActiveId(undefined); }} />
            ))}
          </div>
        </div>
      )}
      <div ref={listRef} className="px-4 py-3 overflow-auto min-h-0 max-h-[52vh] space-y-2.5">
        {!profiles.length && <div className="text-muted py-6 text-center">No saved profiles yet. Create one to connect to SQL Server or PostgreSQL.</div>}
        {profiles.length > 0 && !flat.length && (
          <div className="text-muted py-6 text-center space-y-2">
            <div>No profiles match{text.trim() ? ` “${text.trim()}”` : ""}{env !== "all" ? ` in ${envLabel[env]}` : ""}.</div>
            <button className="q-btn" onClick={() => { setText(""); setEnv("all"); }}>Clear filters</button>
          </div>
        )}
        {sections.map((s) => (
          <div key={s.key} className="space-y-1">
            <div className="text-[10px] font-bold text-muted uppercase px-1 flex items-center gap-1">
              {s.key === "pinned" && <PinIcon filled />}
              {s.title}
            </div>
            {s.groups.map(({ group, profiles: ps }) => (
              <div key={group} className="space-y-1">
                {group && <div className="text-[10px] text-muted px-1">{group}</div>}
                {ps.map((p) => (
                  <ProfileRow
                    key={p.id}
                    p={p}
                    color={envColor(p, groups)}
                    status={servers[p.id]?.status}
                    pinned={pinned.includes(p.id)}
                    active={p.id === active?.id}
                    onSelect={() => setActiveId(p.id)}
                    onConnect={() => void connect(p)}
                    onEdit={() => edit(p)}
                    onDelete={() => void confirmDeleteProfile(p)}
                    onPin={() => pin(p)}
                  />
                ))}
              </div>
            ))}
          </div>
        ))}
      </div>
    </Modal>
  );
}

function ProfileRow({
  p, color, status, pinned, active, onSelect, onConnect, onEdit, onDelete, onPin,
}: {
  p: Profile; color: string; status?: string; pinned: boolean; active: boolean;
  onSelect: () => void; onConnect: () => void; onEdit: () => void; onDelete: () => void; onPin: () => void;
}) {
  const connected = status === "connected";
  return (
    <div
      data-profile={p.id}
      className={`group h-9 pl-1 pr-1.5 rounded-md border flex items-center justify-between gap-2 hover:bg-raised ${active ? "ring-1 ring-accent/60" : ""}`}
      style={{ borderColor: connected ? `color-mix(in srgb, ${color} 45%, transparent)` : "var(--q-line)", background: connected ? "var(--q-raised)" : undefined }}
      onClick={onSelect}
      onDoubleClick={() => !connected && onConnect()}
    >
      <div className="flex items-center gap-1.5 min-w-0">
        <IconButton title={pinned ? "Unpin" : "Pin to top"} onClick={onPin} className={pinned ? "text-accent" : "text-muted opacity-0 group-hover:opacity-100 focus:opacity-100"}>
          <PinIcon filled={pinned} />
        </IconButton>
        <EngineLogo engine={p.engine} size={15} />
        <span className="font-bold text-[11px]" style={{ color }}>{envLabel[p.environment]}</span>
        <span className="text-muted">-</span>
        <span className="font-semibold text-fg truncate">{p.name}</span>
        <span className="text-muted truncate q-mono text-[10.5px]">{p.host}{p.port ? `:${p.port}` : ""}</span>
      </div>
      <div className="flex items-center gap-1 shrink-0">
        <IconButton title="Edit profile" onClick={onEdit} className="text-muted hover:text-fg">
          <EditIcon />
        </IconButton>
        <IconButton title="Delete profile and its data" onClick={onDelete} className="text-muted hover:text-err">
          <TrashIcon />
        </IconButton>
        <div className="w-[78px] flex justify-end">
          {connected ? (
            <span className="text-[10px] px-2 py-0.5 bg-ok/15 text-ok rounded font-medium">Connected</span>
          ) : status === "connecting" ? (
            <span className="text-[10px] text-muted">Connecting…</span>
          ) : (
            <button className="q-btn q-btn-primary h-[24px]" onClick={onConnect}>Connect</button>
          )}
        </div>
      </div>
    </div>
  );
}

function EnvChip({ label, count, color, on, disabled, onClick }: { label: string; count: number; color?: string; on: boolean; disabled?: boolean; onClick: () => void }) {
  return (
    <button
      role="radio"
      aria-checked={on}
      disabled={disabled}
      onClick={onClick}
      className={`h-[22px] px-2 rounded-full border text-[10.5px] font-medium inline-flex items-center gap-1.5 disabled:opacity-40 ${on ? "bg-accent/15 border-accent text-fg q-hl" : "border-line text-muted hover:bg-hover"}`}
    >
      {color && <span className="w-1.5 h-1.5 rounded-full" style={{ background: color }} />}
      {label}
      <span className="text-muted">{count}</span>
    </button>
  );
}

function IconButton({ title, onClick, className = "", children }: { title: string; onClick: () => void; className?: string; children: ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      aria-label={title}
      className={`w-7 h-7 inline-flex items-center justify-center rounded hover:bg-hover ${className}`}
      onClick={(e) => { e.stopPropagation(); onClick(); }}
      onDoubleClick={(e) => e.stopPropagation()}
    >
      {children}
    </button>
  );
}

const svg = { width: 15, height: 15, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": true } as const;

function SearchIcon() {
  return <svg {...svg} width={13} height={13}><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>;
}
function EditIcon() {
  return <svg {...svg}><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></svg>;
}
function TrashIcon() {
  return <svg {...svg}><path d="M3 6h18" /><path d="M8 6V4h8v2" /><path d="M19 6l-1 14H6L5 6" /><path d="M10 11v6M14 11v6" /></svg>;
}
function PinIcon({ filled = false }: { filled?: boolean }) {
  return (
    <svg {...svg} width={13} height={13} fill={filled ? "currentColor" : "none"}>
      <path d="M9 4h6l-1 6 3 3H7l3-3Z" />
      <path d="M12 16v5" />
    </svg>
  );
}

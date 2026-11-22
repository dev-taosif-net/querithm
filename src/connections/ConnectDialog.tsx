// [+ Connect] profile picker (§6, §16 "Connect dialog (2026-11-22)"). One Enter target: ↑↓, typing or a click moves it,
// hover never does, and the footer says what Enter will do. Production is the only loud row (a solid PROD tag and a
// stripe); colour means environment, and status colour sits on a glyph. A row shows Edit, ⋯ and its action only when
// hovered or active; Delete lives in the row menu. Search gives one ranked list. Organize holds Select, groups.
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent as ReactMouseEvent, type ReactNode } from "react";
import type { Engine, EnvironmentKind, Profile } from "../ipc/contract";
import { BusySpinner } from "../app/LoadingStatus";
import { Modal } from "../app/Modal";
import { DropMenu, type MenuEntry } from "../app/ToolbarMenu";
import { activateTab, changeConnection, disconnectServer, newQueryTab } from "../app/actions";
import { tabForNewConnection } from "../app/tabActions";
import { ENGINES, knownEngine } from "../engines";
import { announce } from "../store/announce";
import { useConnections } from "../store/connections";
import { envLabel, useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { useTabs } from "../store/tabs";
import { ask, toast, useUi } from "../store/ui";
import { locationOf } from "./auth";
import { confirmDeleteProfile } from "./deleteProfile";
import { EngineLogo } from "./EngineLogo";
import {
  buildSections, defaultActiveId, ENV_FILTERS, ENV_NAMES, envCounts, filterProfiles, matchParts, nameSuffix, prodNameHint,
  profileAddress, ROW_ACTION, rowState, searchTerms, togglePin, type ProfileSection, type RowState,
} from "./profileList";

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** Row and sticky header heights: the list's height is set from them once, when the dialog opens. */
const ROW_H = 32;
const HEAD_H = 32;

/** Environment words in their AA text colours (tokens.css --q-env-*-fg); PROD alone is a filled tag. */
const ENV_TEXT: Record<EnvironmentKind, string> = {
  local: "text-env-local-fg",
  dev: "text-env-dev-fg",
  test: "text-env-test-fg",
  staging: "text-env-staging-fg",
  prod: "bg-env-prod-fill text-white border border-transparent",
};

export function ConnectDialog({ onClose, focus }: { onClose: () => void; focus?: string }) {
  const profiles = useProfiles((s) => s.profiles);
  const loaded = useProfiles((s) => s.loaded);
  const servers = useConnections((s) => s.servers);
  const pinned = useSettings((s) => s.pinnedProfiles);
  const showGroups = useSettings((s) => s.showGroups);
  const muted = useSettings((s) => s.prodHintMuted);
  const ui = useUi.getState();
  // Taken once, when the dialog opens: a connect made from it (Ctrl+Enter) must not re-sort the list under the next
  // Enter, a chip never disappears while the dialog is open, and the list keeps its height while you type.
  const [recent] = useState(() => useSettings.getState().recentProfiles);
  const [chipEnvs] = useState(() => ENV_FILTERS.filter((e) => useProfiles.getState().profiles.some((p) => p.environment === e)));
  const [listHeight] = useState(() => {
    const s = useSettings.getState();
    const all = buildSections(useProfiles.getState().profiles, s.pinnedProfiles, { showGroups: s.showGroups });
    return Math.max(220, all.flat.length * ROW_H + (all.sections.length > 1 ? all.sections.length * HEAD_H : 0) + 16);
  });
  const [text, setText] = useState("");
  const [envs, setEnvs] = useState<EnvironmentKind[]>([]);
  // The row the user picked; undefined follows the default (the search's top match, else defaultActiveId).
  const [picked, setPicked] = useState<string | undefined>(undefined);
  const beforeSearch = useRef<string | undefined>(undefined);
  const [menu, setMenu] = useState<{ id: string; at: { x: number; y: number } } | null>(null);
  const [organize, setOrganize] = useState(false);
  // ↑↓ moved the active row: hover styles wait for the pointer to move (rows scroll under a still pointer).
  const [keyboardNav, setKeyboardNav] = useState(false);
  // Select mode (bulk move, §6): rows tick instead of connecting; the selection survives filtering.
  const [selecting, setSelecting] = useState(false);
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set());
  const anchor = useRef<string | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const organizeRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const uid = useId();
  const listId = `${uid}-list`;
  const targetId = `${uid}-target`;
  const optionId = (id: string) => `${uid}-opt-${id}`;

  const searching = searchTerms(text).length > 0;
  const filtering = searching || envs.length > 0;
  const counts = useMemo(() => envCounts(profiles), [profiles]);
  const filtered = useMemo(() => filterProfiles(profiles, text, envs), [profiles, text, envs]);
  const { sections, flat } = useMemo(() => buildSections(filtered, pinned, { showGroups, query: text, recent }), [filtered, pinned, showGroups, text, recent]);
  const fallback = useMemo(() => (searching ? flat[0]?.id : defaultActiveId(flat, { recent, pinned, focus })), [searching, flat, recent, pinned, focus]);
  // A pick the filter hid leaves no active row: Enter never jumps to a row the user did not choose.
  const activeId = picked === undefined ? fallback : flat.some((p) => p.id === picked) ? picked : undefined;
  const active = activeId ? flat.find((p) => p.id === activeId) : undefined;
  const stateOf = (p: Profile): RowState => rowState(p, servers[p.id]);
  // Ids of profiles deleted meanwhile drop out of the selection.
  const chosenProfiles = useMemo(() => profiles.filter((p) => chosen.has(p.id)), [profiles, chosen]);
  const hidden = chosenProfiles.filter((p) => !flat.includes(p)).length;
  const chosenProd = chosenProfiles.filter((p) => p.environment === "prod").length;

  useEffect(() => {
    if (activeId) listRef.current?.querySelector(`[data-profile="${CSS.escape(activeId)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [activeId]);

  // What a filter left, said once typing pauses (the visible count is in the search field).
  useEffect(() => {
    if (!filtering) return;
    const prod = flat.filter((p) => p.environment === "prod").length;
    const t = setTimeout(() => announce(flat.length ? `${flat.length} of ${plural(profiles.length, "profile")} shown${prod ? `, ${prod} Production` : ""}` : "No profiles match"), 500);
    return () => clearTimeout(t);
  }, [filtering, flat, profiles.length]);

  const onText = (value: string) => {
    const was = searchTerms(text).length > 0;
    const now = searchTerms(value).length > 0;
    if (!was && now) beforeSearch.current = activeId;
    setText(value);
    // Each keystroke makes the top match active; clearing the search brings back the row from before it.
    if (now) setPicked(undefined);
    else if (was) setPicked(beforeSearch.current);
    setMenu(null);
  };
  const toggleEnv = (e: EnvironmentKind) => setEnvs((cur) => (cur.includes(e) ? cur.filter((x) => x !== e) : [...cur, e]));

  const check = (p: Profile, range = false) => {
    setPicked(p.id);
    setChosen((cur) => {
      const next = new Set(cur);
      const from = range && anchor.current ? flat.findIndex((x) => x.id === anchor.current) : -1;
      const to = flat.indexOf(p);
      if (from >= 0 && to >= 0) {
        // Shift: the range takes the anchor's state, as in a file list.
        const on = cur.has(anchor.current!);
        for (const x of flat.slice(Math.min(from, to), Math.max(from, to) + 1)) on ? next.add(x.id) : next.delete(x.id);
      } else if (!next.delete(p.id)) next.add(p.id);
      return next;
    });
    if (!range) anchor.current = p.id;
  };
  const checkAll = (ps: readonly Profile[], on: boolean) =>
    setChosen((cur) => {
      const next = new Set(cur);
      for (const p of ps) on ? next.add(p.id) : next.delete(p.id);
      return next;
    });
  const stopSelecting = () => {
    setSelecting(false);
    setChosen(new Set());
    anchor.current = null;
  };
  const moveChosen = () => ui.openDialog({ kind: "moveToGroup", profileIds: chosenProfiles.map((p) => p.id) });
  const ungroupChosen = async () => {
    const ids = chosenProfiles.filter((p) => p.groupName).map((p) => p.id);
    if (!ids.length) return;
    const prod = chosenProfiles.filter((p) => p.groupName && p.environment === "prod").length;
    if (prod && !(await ask<boolean>((resolve) => ({
      kind: "confirm", title: `Remove ${plural(ids.length, "connection")} from their group?`, ok: "Remove from group", resolve,
      body: `${plural(prod, "Production connection")} ${prod === 1 ? "is" : "are"} among them. They go back to their usual place in the list; environment, passwords and open tabs don’t change.`,
    })))) return;
    try {
      await useProfiles.getState().assignGroup(ids, null);
    } catch (e) {
      toast("error", e instanceof Error ? e.message : String(e));
    }
  };

  const backToSearch = () => requestAnimationFrame(() => searchRef.current?.focus());
  const pending = useRef(false);
  /**
   * The row's action, as its button and Enter name it: Connect (or Retry) closes the dialog and connects, then an
   * unconnected tab takes the connection; New query opens a tab on a connected server. `keepOpen` (Ctrl+Enter) leaves
   * the dialog open, so several servers can be connected in a row.
   */
  const run = async (p: Profile, keepOpen = false) => {
    const state = rowState(p, useConnections.getState().servers[p.id]);
    if (state === "unavailable" || state === "connecting") return;
    if (state === "connected") {
      if (!keepOpen) onClose();
      newQueryTab({ profileId: p.id });
      return;
    }
    if (pending.current) return;
    pending.current = true;
    if (!keepOpen) onClose();
    try {
      const ok = await useConnections.getState().connect(p.id);
      if (keepOpen) {
        announce(ok ? `${p.name} connected` : `${p.name} did not connect`);
        return;
      }
      if (!ok) return;
      // An unconnected tab (the active one, else an empty spare) takes the connection instead of a new tab piling up.
      const reuse = tabForNewConnection();
      if (reuse) {
        await changeConnection(reuse, p.id);
        activateTab(reuse);
      } else if (!useTabs.getState().activeId) newQueryTab({ profileId: p.id });
    } finally {
      pending.current = false;
    }
  };
  const cancelConnect = (p: Profile) => void disconnectServer(p.id);
  const openEditor = (d: { profileId?: string; copyOf?: string; preset?: Engine }) => {
    onClose();
    ui.openDialog({ kind: "profile", ...d, fromConnect: true });
  };
  const pin = (p: Profile) => useSettings.getState().set({ pinnedProfiles: togglePin(useSettings.getState().pinnedProfiles, p.id) });
  const identity = (p: Profile) => [ENV_NAMES[p.environment], profileAddress(p).full, p.groupName].filter(Boolean).join(" · ");
  const copy = (value: string) => void navigator.clipboard.writeText(value).then(() => toast("success", "Copied."), () => toast("error", "Could not copy to the clipboard."));
  const openMenu = (p: Profile, at: { x: number; y: number }) => {
    setPicked(p.id);
    setOrganize(false);
    setMenu({ id: p.id, at });
  };
  const openMenuFromKeyboard = (p: Profile) => {
    const r = listRef.current?.querySelector(`[data-profile="${CSS.escape(p.id)}"]`)?.getBoundingClientRect();
    openMenu(p, { x: r ? Math.max(4, r.right - 300) : 0, y: r ? r.bottom : 0 });
  };

  const rowMenu = (p: Profile): MenuEntry[] => {
    const state = stateOf(p);
    const where = locationOf(p);
    const isPinned = pinned.includes(p.id);
    const items: MenuEntry[] = [];
    if (state === "connecting") items.push({ label: "Cancel connecting", run: () => cancelConnect(p), bold: true });
    else if (state === "unavailable") items.push({ label: "Connect", run: () => {}, unavailable: p.unsupported ?? "Not available in this version" });
    else items.push({ label: ROW_ACTION[state]!, run: () => void run(p), bold: true, hint: "Enter" });
    if (state === "connected" || state === "failed") items.push({ label: "Disconnect", run: () => void disconnectServer(p.id), detail: state === "failed" ? "stops retrying" : undefined });
    items.push(
      "sep",
      { label: "Edit…", run: () => openEditor({ profileId: p.id }), hint: "F2" },
      { label: "Duplicate…", run: () => openEditor({ copyOf: p.id }) },
      { label: isPinned ? "Unpin" : "Pin", run: () => { pin(p); backToSearch(); }, detail: isPinned ? undefined : "first here and kept in Object Explorer" },
      { label: p.groupName ? "Move to group…" : "Add to group…", run: () => ui.openDialog({ kind: "moveToGroup", profileIds: [p.id] }) },
    );
    if (where !== "memory") items.push({ label: where === "file" ? "Copy file name" : "Copy address", run: () => { copy(profileAddress(p).full); backToSearch(); } });
    if (prodNameHint(p) && !muted.includes(p.id))
      items.push({ label: "Don’t warn for this profile", run: () => { useSettings.getState().set({ prodHintMuted: [...useSettings.getState().prodHintMuted, p.id] }); backToSearch(); }, detail: "its name says prod" });
    items.push("sep", { label: "Delete profile…", run: () => void confirmDeleteProfile(p, identity(p)) });
    return items;
  };

  const organizeMenu: MenuEntry[] = [
    { label: selecting ? "Stop selecting" : "Select profiles…", run: () => { if (selecting) stopSelecting(); else setSelecting(true); backToSearch(); }, detail: selecting ? undefined : "to move them into a group" },
    { label: "Manage groups…", run: () => ui.openDialog({ kind: "groups" }) },
    "sep",
    { label: "Show groups", checked: showGroups, run: () => { useSettings.getState().set({ showGroups: !showGroups }); backToSearch(); } },
  ];

  /** Esc, one step at a time: an open menu, Select mode, the search and chips, then the dialog. True when it did a step. */
  const escape = (): boolean => {
    if (menu || organize) {
      setMenu(null);
      setOrganize(false);
      return true;
    }
    if (selecting) {
      stopSelecting();
      return true;
    }
    if (text || envs.length) {
      if (searchTerms(text).length) setPicked(beforeSearch.current);
      setText("");
      setEnvs([]);
      return true;
    }
    return false;
  };

  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.defaultPrevented || e.nativeEvent.isComposing) return;
    const i = active ? flat.indexOf(active) : -1;
    const moveTo = (n: number) => {
      e.preventDefault();
      if (!flat.length) return;
      setPicked(flat[Math.max(0, Math.min(flat.length - 1, n))].id);
      setKeyboardNav(true);
    };
    if (e.key === "ArrowDown") moveTo(i < 0 ? 0 : i + 1);
    else if (e.key === "ArrowUp") moveTo(i < 0 ? flat.length - 1 : i - 1);
    else if (e.key === "PageDown") moveTo(i < 0 ? 0 : i + 10);
    else if (e.key === "PageUp") moveTo(i < 0 ? 0 : i - 10);
    else if (e.key === "Home" && e.ctrlKey) moveTo(0);
    else if (e.key === "End" && e.ctrlKey) moveTo(flat.length - 1);
    else if (e.key === "Enter") {
      e.preventDefault();
      if (!active) return;
      // In Select mode Enter ticks the row; it never connects.
      if (selecting) check(active, e.shiftKey);
      else void run(active, e.ctrlKey);
    } else if (e.key === "F2" && active && !selecting) {
      e.preventDefault();
      openEditor({ profileId: active.id });
    } else if ((e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey)) && active && !selecting) {
      e.preventDefault();
      openMenuFromKeyboard(active);
    } else if (e.key === "Escape") {
      // The search box is a combobox (aria-expanded), so the Modal leaves its Esc to it.
      e.preventDefault();
      e.stopPropagation();
      if (!escape()) onClose();
    }
  };

  const shownChips = ENV_FILTERS.filter((e) => chipEnvs.includes(e) || envs.includes(e));
  const menuProfile = menu ? profiles.find((p) => p.id === menu.id) : undefined;

  const footer = selecting ? (
    <div role="toolbar" aria-label="Selection" className="w-full flex items-center gap-2">
      <span className="self-center font-semibold">{chosenProfiles.length} selected</span>
      {chosenProd > 0 && <span className="self-center text-[11px] font-semibold text-env-prod-fg">· {chosenProd} Production</span>}
      {hidden > 0 && <span className="self-center text-[11px] text-muted">· {hidden} hidden by filter</span>}
      <button className="text-accent text-[11px] hover:underline" onClick={() => checkAll(flat, true)}>Select all shown</button>
      <span className="mr-auto" />
      <button className="q-btn" disabled={!chosenProfiles.some((p) => p.groupName)} onClick={() => void ungroupChosen()}>Remove from group</button>
      <button className="q-btn q-btn-primary" disabled={!chosenProfiles.length} onClick={moveChosen}>Move to group…</button>
      <button className="q-btn" onClick={stopSelecting}>Done</button>
    </div>
  ) : !loaded ? (
    <span className="mr-auto self-center text-[11px] text-muted">Connect is unavailable until the saved profiles load.</span>
  ) : !profiles.length ? (
    <span className="mr-auto self-center text-[11px] text-muted">Passwords go to the system keyring, never into Querithm’s own files.</span>
  ) : (
    <>
      <TargetLine id={targetId} p={active} state={active ? stateOf(active) : "idle"} error={active ? servers[active.id]?.error : undefined} hint={!!active && prodNameHint(active) && !muted.includes(active.id)} listed={flat.length} />
      <button className="q-btn h-[28px] shrink-0" onClick={() => openEditor({})}>+ New profile</button>
    </>
  );

  return (
    <Modal
      title="Connect to a database"
      onClose={onClose}
      closeButton
      onEscape={escape}
      width={680}
      bodyClassName="@container flex flex-col min-h-0"
      footer={footer}
    >
      {!loaded ? (
        <div className="p-3 space-y-2" style={{ height: 260 }}>
          <div role="alert" className="flex items-center gap-2.5 px-3 py-2.5 border border-line rounded-md bg-panel-2">
            <WarnIcon />
            <span className="flex-1"><span className="font-semibold">Couldn’t load saved profiles.</span> The Querithm engine may still be starting.</span>
            <button className="q-btn" onClick={() => void useProfiles.getState().load().catch((e) => toast("error", `Saved profiles could not be read: ${e instanceof Error ? e.message : String(e)}`))}>Retry</button>
          </div>
        </div>
      ) : !profiles.length ? (
        <FirstRun onServer={() => openEditor({})} onFile={() => openEditor({ preset: "sqlite" })} />
      ) : (
        <>
          <div className="px-4 pt-3 pb-2.5 space-y-2 border-b border-line">
            <div className="flex gap-2">
              <div className="relative flex-1 min-w-0">
                <span className="absolute left-2.5 top-1/2 -translate-y-1/2 text-muted pointer-events-none"><SearchIcon /></span>
                <input
                  ref={searchRef}
                  autoFocus
                  role="combobox"
                  aria-expanded="true"
                  aria-controls={listId}
                  aria-autocomplete="list"
                  aria-activedescendant={active ? optionId(active.id) : undefined}
                  aria-describedby={targetId}
                  className={`q-input w-full h-[30px] pl-8 ${filtering ? "pr-20" : ""}`}
                  placeholder="Search name, host, group, database or engine"
                  aria-label="Search profiles"
                  value={text}
                  onChange={(e) => onText(e.target.value)}
                  onKeyDown={onSearchKey}
                />
                {/* Only while a search or chip narrows the list (owner: no standing counts or key hints in the dialog). */}
                {filtering && (
                  <span aria-hidden className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[11px] text-muted tabular-nums pointer-events-none" data-testid="profile-count">
                    {flat.length} of {profiles.length}
                  </span>
                )}
              </div>
              <button
                ref={organizeRef}
                type="button"
                className={`q-btn h-[30px] ${selecting || organize ? "border-fg" : ""}`}
                aria-haspopup="menu"
                aria-expanded={organize}
                aria-pressed={selecting || undefined}
                onClick={() => {
                  setMenu(null);
                  setOrganize((o) => !o);
                }}
              >
                Organize
                <ChevronIcon />
              </button>
            </div>
            {(shownChips.length >= 2 || envs.length > 0) && (
              <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by environment">
                <Chip label="All" name={`All environments, ${plural(profiles.length, "profile")}`} count={profiles.length} on={!envs.length} onClick={() => setEnvs([])} />
                {shownChips.map((e) => (
                  <Chip key={e} label={envLabel[e]} name={`${ENV_NAMES[e]}, ${plural(counts[e], "profile")}`} count={counts[e]} on={envs.includes(e)} onClick={() => toggleEnv(e)} />
                ))}
              </div>
            )}
          </div>
          <div
            ref={listRef}
            id={listId}
            role="listbox"
            aria-label="Saved profiles"
            aria-multiselectable={selecting || undefined}
            data-kbd={keyboardNav || undefined}
            onPointerMove={() => keyboardNav && setKeyboardNav(false)}
            className="q-clist px-2 pb-2 overflow-auto min-h-0 scroll-pt-[30px]"
            style={{ height: `min(${listHeight}px, 56vh)` }}
          >
            {!flat.length && (
              <div className="h-full flex flex-col items-center justify-center gap-2 text-center px-6">
                <div className="text-[13px] font-semibold text-fg">
                  {searching ? `No profiles match “${text.trim()}”` : "No profiles"}
                  {envs.length ? ` in ${envs.map((e) => envLabel[e]).join(", ")}` : ""}
                </div>
                <div className="text-muted">Search looks at name, host, port, group, user, database, environment and engine.</div>
                <div className="flex items-center gap-2 text-muted text-[11px]">
                  <button className="q-btn" onClick={() => { setText(""); setEnvs([]); backToSearch(); }}>Clear search</button>
                  <span><kbd className="q-keycap">Esc</kbd> also clears it</span>
                </div>
              </div>
            )}
            {sections.map((s) => (
              <Section
                key={`${envs.join()}:${s.key}`}
                s={s}
                showHead={!(sections.length === 1 && s.kind !== "matches")}
                selecting={selecting}
                allChosen={s.profiles.length > 0 && s.profiles.every((p) => chosen.has(p.id))}
                onCheckAll={(on) => checkAll(s.profiles, on)}
              >
                {s.profiles.map((p) => (
                  <ProfileRow
                    key={p.id}
                    p={p}
                    id={optionId(p.id)}
                    state={stateOf(p)}
                    error={servers[p.id]?.error}
                    active={p.id === activeId}
                    pinned={pinned.includes(p.id)}
                    suffix={nameSuffix(p, profiles, s.kind === "group")}
                    hint={prodNameHint(p) && !muted.includes(p.id)}
                    terms={searchTerms(text)}
                    selecting={selecting}
                    checked={chosen.has(p.id)}
                    onCheck={(range) => check(p, range)}
                    onPick={() => {
                      setPicked(p.id);
                      setMenu(null);
                    }}
                    onRun={() => void run(p)}
                    onCancel={() => cancelConnect(p)}
                    onEdit={() => openEditor({ profileId: p.id })}
                    onMenu={(at) => openMenu(p, at)}
                  />
                ))}
              </Section>
            ))}
          </div>
        </>
      )}
      {organize && <DropMenu anchor={organizeRef} label="Organize" items={organizeMenu} onClose={() => setOrganize(false)} />}
      {menu && menuProfile && (
        <DropMenu
          anchor={searchRef}
          at={menu.at}
          label={`Actions for ${menuProfile.name}`}
          header={<><EnvTag env={menuProfile.environment} /><span className="font-semibold text-fg truncate">{menuProfile.name}</span></>}
          items={rowMenu(menuProfile)}
          onClose={() => setMenu(null)}
        />
      )}
    </Modal>
  );
}

function Section({ s, showHead, selecting, allChosen, onCheckAll, children }: {
  s: ProfileSection; showHead: boolean; selecting: boolean; allChosen: boolean; onCheckAll: (on: boolean) => void; children: ReactNode;
}) {
  const counted = s.kind === "pinned" || s.kind === "group";
  const label = s.kind === "matches" ? s.title : `${s.title}, ${plural(s.profiles.length, "profile")}${counted && s.prod ? `, ${s.prod} Production` : ""}`;
  return (
    <div className="q-settings-page-in mt-1.5" role="group" aria-label={label} data-section={s.kind}>
      {showHead && (
        <div className="sticky top-0 z-[1] h-[26px] bg-panel flex items-center gap-1.5 pl-[15px] pr-2 text-[11px] font-semibold text-muted">
          {selecting && s.kind === "group" && (
            <input type="checkbox" aria-label={`Select all in ${s.title}`} checked={allChosen} onChange={() => onCheckAll(!allChosen)} />
          )}
          {s.kind === "pinned" && <PinIcon filled />}
          {s.kind === "group" && <FolderIcon />}
          <span className="truncate">{s.title}</span>
          {s.kind !== "matches" && (
            <span className="ml-auto shrink-0 font-normal tabular-nums">
              {s.profiles.length}
              {counted && s.prod > 0 && <span className="font-semibold text-env-prod-fg"> · {s.prod} Production</span>}
            </span>
          )}
        </div>
      )}
      {children}
    </div>
  );
}

function ProfileRow({ p, id, state, error, active, pinned, suffix, hint, terms, selecting, checked, onCheck, onPick, onRun, onCancel, onEdit, onMenu }: {
  p: Profile; id: string; state: RowState; error?: string; active: boolean; pinned: boolean; suffix: string; hint: boolean; terms: string[];
  /** Select mode: the row ticks (Shift: a range) instead of connecting. */
  selecting: boolean; checked: boolean; onCheck: (range: boolean) => void;
  onPick: () => void; onRun: () => void; onCancel: () => void; onEdit: () => void; onMenu: (at: { x: number; y: number }) => void;
}) {
  const address = profileAddress(p);
  const engine = ENGINES[knownEngine(p.engine)].label;
  const action = state === "connecting" ? "Cancel" : ROW_ACTION[state];
  const word = state === "connected" ? "Connected" : state === "failed" ? "Failed" : state === "connecting" ? "Connecting…" : null;
  const label = [ENV_NAMES[p.environment], p.name, suffix, engine, address.full, word ?? (state === "unavailable" ? "not available" : null), hint ? "name suggests Production" : null].filter(Boolean).join(", ");
  const tip = state === "failed" ? error : state === "unavailable" ? (p.unsupported ?? undefined) : undefined;
  const menuAt = (e: ReactMouseEvent<HTMLElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    return { x: r.left, y: r.bottom + 2 };
  };
  return (
    <div
      id={id}
      role="option"
      aria-selected={selecting ? checked : active}
      aria-label={label}
      data-profile={p.id}
      data-active={active || undefined}
      data-selecting={selecting || undefined}
      title={tip}
      className="q-crow relative flex items-center gap-2 min-h-[32px] pr-1.5 rounded-r-md text-fg @max-[540px]:min-h-[44px]"
      // Focus stays in the search box (the combobox), whatever is clicked.
      onMouseDown={(e) => e.preventDefault()}
      onClick={(e) => (selecting ? onCheck(e.shiftKey) : onPick())}
      onDoubleClick={() => !selecting && onRun()}
      onContextMenu={(e) => {
        e.preventDefault();
        if (!selecting) onMenu({ x: e.clientX, y: e.clientY });
      }}
    >
      <span aria-hidden data-prod={p.environment === "prod" || undefined} className={`q-crow-stripe self-stretch w-[3px] shrink-0 ${p.environment === "prod" ? "bg-env-prod" : ""}`} />
      <span className="w-5 shrink-0 flex items-center justify-center text-muted">
        {selecting ? (
          <input
            type="checkbox" tabIndex={-1} aria-label={`Select ${p.name}`} checked={checked}
            onClick={(e) => e.stopPropagation()}
            onChange={(e) => onCheck((e.nativeEvent as MouseEvent).shiftKey ?? false)}
          />
        ) : (
          <span title={engine} className="inline-flex">
            <EngineLogo engine={p.engine} size={14} mono decorative />
          </span>
        )}
      </span>
      <EnvTag env={p.environment} />
      <span className="flex-1 min-w-0 flex items-center gap-2.5 @max-[540px]:flex-col @max-[540px]:items-start @max-[540px]:gap-0 @max-[540px]:py-1">
        <span className="min-w-0 max-w-[62%] @max-[540px]:max-w-full flex items-center gap-1.5">
          <span className="font-semibold truncate"><Marked text={p.name} terms={terms} /></span>
          {pinned && <span className="text-muted shrink-0 inline-flex" title="Pinned: first here and kept in Object Explorer"><PinIcon filled size={11} /></span>}
          {hint && (
            <span className="text-warn shrink-0 inline-flex" title={`The name says prod, but this profile is tagged ${envLabel[p.environment].toUpperCase()}. Edit it to change the environment.`}>
              <WarnIcon size={13} />
            </span>
          )}
          {suffix && <span className="text-muted text-[11px] truncate">· {suffix}</span>}
        </span>
        <span className="flex-1 min-w-0 @max-[540px]:w-full flex items-center gap-1.5 q-mono text-[11px] text-muted" title={address.full}>
          <span className="truncate"><Marked text={address.host} terms={terms} /></span>
          {address.port && <span className="shrink-0">{address.port}</span>}
          {p.ssh && <span className="shrink-0 h-4 px-1 inline-flex items-center border border-line rounded-[3px] font-sans text-[10px] font-semibold tracking-wide" title="Through an SSH tunnel">SSH</span>}
        </span>
      </span>
      <span className="q-crow-actions shrink-0 flex items-center gap-0.5">
        <IconButton title="Edit profile (F2)" label={`Edit ${p.name}`} className="@max-[540px]:hidden" onClick={onEdit}>
          <EditIcon />
        </IconButton>
        <IconButton title="More actions (Shift+F10)" label={`More actions for ${p.name}`} onClick={(e) => onMenu(menuAt(e))}>
          <MoreIcon />
        </IconButton>
      </span>
      <span className="w-[112px] @max-[540px]:w-[88px] shrink-0 flex items-center justify-end gap-1.5">
        {state === "connected" && <span className="text-ok inline-flex" aria-hidden><Dot /></span>}
        {state === "failed" && <span className="text-err inline-flex" aria-hidden><FailedIcon /></span>}
        {state === "connecting" && <BusySpinner />}
        {word && <span className="q-crow-word text-[11px] whitespace-nowrap" data-yields={action ? "" : undefined}>{word}</span>}
        {state === "unavailable" && <span className="h-[18px] px-1.5 inline-flex items-center border border-line rounded-[3px] text-[11px] text-muted whitespace-nowrap">Unavailable</span>}
        {action && !selecting && (
          <button
            type="button"
            tabIndex={-1}
            className={`q-crow-btn q-btn ${active && state !== "connecting" ? "q-btn-primary" : "bg-transparent border-line-strong hover:bg-hover"}`}
            title={state === "connecting" ? "Stop connecting" : `${action} (Enter)`}
            onClick={(e) => {
              e.stopPropagation();
              if (state === "connecting") onCancel();
              else onRun();
            }}
          >
            {action}
          </button>
        )}
      </span>
    </div>
  );
}

/** What Enter will do, in the footer: "↵ Connect [PROD] orders-db · app@db-prod-01:1433 · orders · SSH". */
function TargetLine({ id, p, state, error, hint, listed }: { id: string; p: Profile | undefined; state: RowState; error?: string; hint: boolean; listed: number }) {
  if (!p) return <span id={id} className="mr-auto self-center truncate text-muted">{listed ? "Type, or press ↓ to choose a profile" : ""}</span>;
  const verb = ROW_ACTION[state];
  const address = profileAddress(p);
  const where = locationOf(p) === "server" ? `${p.username ? `${p.username}@` : ""}${address.full}` : address.full;
  const detail =
    state === "failed" ? error || "Could not connect"
    : state === "connecting" ? "Connecting… Cancel on the row stops it"
    : state === "unavailable" ? p.unsupported || "Not available in this version"
    : [where, p.databaseDefault, p.ssh ? "SSH" : null].filter(Boolean).join(" · ");
  return (
    <div id={id} className="mr-auto flex-1 min-w-0 flex items-center gap-1.5 self-center whitespace-nowrap overflow-hidden" title={`${verb ? `${verb} ` : ""}${ENV_NAMES[p.environment]} ${p.name} · ${detail}`}>
      {verb && (
        <>
          <kbd className="q-keycap" aria-hidden>↵</kbd>
          <span className="font-semibold shrink-0">{verb}</span>
        </>
      )}
      <EnvTag env={p.environment} />
      <span className="font-semibold shrink-0 max-w-[40%] truncate">{p.name}</span>
      {hint && (
        <span className="shrink-0 inline-flex items-center gap-1 text-warn text-[11px]">
          <WarnIcon size={12} />
          name says prod
        </span>
      )}
      <span className={`truncate ${state === "failed" ? "text-err" : "text-muted"}`}>· {detail}</span>
    </div>
  );
}

function FirstRun({ onServer, onFile }: { onServer: () => void; onFile: () => void }) {
  return (
    <div className="flex flex-col items-center justify-center text-center gap-2 px-6 py-10">
      <span className="text-muted"><DatabaseIcon /></span>
      <div className="text-[14px] font-semibold text-fg mt-1">No saved profiles yet</div>
      <p className="text-muted max-w-[380px] leading-relaxed m-0">Save a server or a database file once. After that it is one search and Enter away.</p>
      <div className="grid grid-cols-2 gap-3 mt-3 w-[460px] max-w-full">
        <button type="button" autoFocus className="flex flex-col items-start gap-1 p-3.5 rounded-lg border border-accent-solid bg-panel text-left hover:bg-hover" onClick={onServer}>
          <span className="text-accent"><ServerIcon /></span>
          <span className="text-[13px] font-semibold text-fg">Connect to a server…</span>
          <span className="text-[11px] text-muted">SQL Server · PostgreSQL · MySQL / MariaDB · Oracle</span>
        </button>
        <button type="button" className="flex flex-col items-start gap-1 p-3.5 rounded-lg border border-line-strong bg-panel text-left hover:bg-hover" onClick={onFile}>
          <span className="text-accent"><FileIcon /></span>
          <span className="text-[13px] font-semibold text-fg">Open a database file…</span>
          <span className="text-[11px] text-muted">SQLite · DuckDB</span>
        </button>
      </div>
    </div>
  );
}

/** The environment as a word: PROD a filled tag, the others text in their AA colour (§12: never colour alone). */
function EnvTag({ env }: { env: EnvironmentKind }) {
  if (!envLabel[env]) return null;
  return (
    <span data-env={env} className={`q-env-tag w-11 h-[18px] shrink-0 inline-flex items-center justify-center rounded-[3px] text-[11px] font-semibold tracking-[0.04em] leading-none ${ENV_TEXT[env]}`}>
      {envLabel[env].toUpperCase()}
    </span>
  );
}

function Marked({ text, terms }: { text: string; terms: readonly string[] }) {
  if (!terms.length) return <>{text}</>;
  return (
    <>
      {matchParts(text, terms).map((part, i) =>
        part.hit ? <mark key={i} className="bg-transparent text-fg underline underline-offset-2 decoration-[1.5px]">{part.text}</mark> : <span key={i}>{part.text}</span>,
      )}
    </>
  );
}

function Chip({ label, name, count, on, onClick }: { label: string; name: string; count: number; on: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      aria-label={name}
      onClick={onClick}
      className={`h-6 pl-2 pr-2.5 rounded-full border text-[11px] inline-flex items-center gap-1.5 transition-colors duration-150 ${on ? "bg-raised border-fg text-fg font-semibold" : "border-line text-fg font-medium hover:bg-hover"}`}
    >
      <span aria-hidden className={`inline-flex w-2.5 ${on ? "" : "invisible"}`}><CheckIcon /></span>
      {label}
      <span className="text-muted font-normal tabular-nums">{count}</span>
    </button>
  );
}

function IconButton({ title, label, onClick, className = "", children }: { title: string; label: string; onClick: (e: ReactMouseEvent<HTMLButtonElement>) => void; className?: string; children: ReactNode }) {
  return (
    <button
      type="button"
      tabIndex={-1}
      title={title}
      aria-label={label}
      className={`w-6 h-6 inline-flex items-center justify-center rounded text-muted hover:text-fg hover:bg-hover ${className}`}
      onClick={(e) => {
        e.stopPropagation();
        onClick(e);
      }}
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
function ChevronIcon() {
  return <svg {...svg} width={11} height={11} strokeWidth={2.4}><path d="m6 9 6 6 6-6" /></svg>;
}
function CheckIcon() {
  return <svg {...svg} width={10} height={10} strokeWidth={3}><path d="M20 6 9 17l-5-5" /></svg>;
}
function EditIcon() {
  return <svg {...svg} width={14} height={14}><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></svg>;
}
function MoreIcon() {
  return <svg {...svg} width={14} height={14} stroke="none" fill="currentColor"><circle cx="5" cy="12" r="1.8" /><circle cx="12" cy="12" r="1.8" /><circle cx="19" cy="12" r="1.8" /></svg>;
}
function FolderIcon() {
  return <svg {...svg} width={13} height={13} className="shrink-0"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" /></svg>;
}
function PinIcon({ filled = false, size = 12 }: { filled?: boolean; size?: number }) {
  return (
    <svg {...svg} width={size} height={size} className="shrink-0">
      <path d="M9 4h6l-1 6 3 3H7l3-3Z" fill={filled ? "currentColor" : "none"} />
      <path d="M12 16v5" />
    </svg>
  );
}
function WarnIcon({ size = 16 }: { size?: number }) {
  return <svg {...svg} width={size} height={size} strokeWidth={2.2} className="shrink-0 text-warn"><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4M12 17h.01" /></svg>;
}
/** Status glyphs are drawn in currentColor, so forced colours keep them (a background dot would vanish). */
function Dot() {
  return <svg width={8} height={8} viewBox="0 0 8 8" aria-hidden><circle cx="4" cy="4" r="3.5" fill="currentColor" /></svg>;
}
function FailedIcon() {
  return <svg width={10} height={10} viewBox="0 0 10 10" aria-hidden><path d="M5 .8 9.6 9H.4Z" fill="currentColor" /></svg>;
}
function DatabaseIcon() {
  return <svg {...svg} width={34} height={34} strokeWidth={1.6}><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5" /><path d="M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3" /></svg>;
}
function ServerIcon() {
  return <svg {...svg} width={18} height={18}><rect x="3" y="4" width="18" height="7" rx="2" /><rect x="3" y="13" width="18" height="7" rx="2" /><path d="M7 7.5h.01M7 16.5h.01" /></svg>;
}
function FileIcon() {
  return <svg {...svg} width={18} height={18}><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" /><path d="M14 3v5h5" /></svg>;
}

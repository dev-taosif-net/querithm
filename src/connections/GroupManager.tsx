// The group manager (§6): make a group, rename it, see and change its members (bulk add and remove), delete it.
// Every change is one sidecar call and one transaction; the store changes only from the reply, so a refused or failed
// call leaves the dialog as it was, with the reason. Groups have no color and no environment: nothing here changes a
// connection's environment, password or open tabs.
import { useEffect, useId, useMemo, useState } from "react";
import type { Profile } from "../ipc/contract";
import { Modal } from "../app/Modal";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { toast } from "../store/ui";
import { confirmDeleteGroup } from "./groupActions";
import { cleanGroupName, compareGroupNames, findGroup } from "./groupNames";

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;
const message = (e: unknown) => (e instanceof Error ? e.message : String(e));
const byName = (a: Profile, b: Profile) => a.name.localeCompare(b.name, undefined, { sensitivity: "base", numeric: true });

function EnvName({ p }: { p: Profile }) {
  return <span className="font-bold text-[11px] w-[44px] shrink-0" style={{ color: envColor(p) }}>{envLabel[p.environment]}</span>;
}

export function GroupManagerDialog({ focus, onClose }: { focus?: string; onClose: () => void }) {
  const groups = useProfiles((s) => s.groups);
  const profiles = useProfiles((s) => s.profiles);
  const showGroups = useSettings((s) => s.showGroups);
  const fid = useId();
  // The list as the sidecar has it now (another window may have changed it); a failure keeps what is shown.
  useEffect(() => void useProfiles.getState().refreshGroups(), []);
  const sorted = useMemo(() => [...groups].sort((a, b) => compareGroupNames(a.name, b.name)), [groups]);
  const [query, setQuery] = useState("");
  const shown = sorted.filter((g) => !query.trim() || g.name.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  const [selected, setSelected] = useState<string | null>(focus ?? null);
  // A group renamed or deleted elsewhere falls back to the first one.
  const sel = sorted.find((g) => g.name === selected) ?? sorted[0];
  const members = useMemo(() => (sel ? profiles.filter((p) => p.groupName === sel.name).sort(byName) : []), [profiles, sel]);
  const prodIn = (name: string) => profiles.filter((p) => p.groupName === name && p.environment === "prod").length;

  // ---- New
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const [newError, setNewError] = useState<string | null>(null);
  const create = async () => {
    const c = cleanGroupName(newName);
    if (c.error || !c.name) return setNewError(c.error ?? "Enter the group name.");
    try {
      await useProfiles.getState().createGroup(c.name);
      setCreating(false);
      setNewName("");
      setSelected(findGroup(useProfiles.getState().groups, c.name)?.name ?? c.name);
    } catch (e) {
      setNewError(message(e));
    }
  };

  // ---- Rename (the selected group's name field)
  const [draft, setDraft] = useState(sel?.name ?? "");
  const [renameError, setRenameError] = useState<string | null>(null);
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  const [adding, setAdding] = useState(false);
  const [pick, setPick] = useState<ReadonlySet<string>>(new Set());
  const [pickQuery, setPickQuery] = useState("");
  useEffect(() => {
    setDraft(sel?.name ?? "");
    setRenameError(null);
    setChecked(new Set());
    setAdding(false);
    setPick(new Set());
  }, [sel?.name]);
  const draftClean = cleanGroupName(draft);
  const canRename = !!sel && !!draftClean.name && !draftClean.error && draftClean.name !== sel.name;
  const rename = async () => {
    if (!sel) return;
    if (draftClean.error || !draftClean.name) return setRenameError(draftClean.error ?? "Enter the group name.");
    if (draftClean.name === sel.name) return;
    try {
      const r = await useProfiles.getState().renameGroup(sel.name, draftClean.name);
      setSelected(r.renamed?.to ?? draftClean.name);
    } catch (e) {
      setRenameError(message(e));
    }
  };

  // ---- Members: remove the ticked ones, or add others (bulk move into this group)
  const removeChecked = async () => {
    if (!sel) return;
    const ids = members.filter((m) => checked.has(m.id)).map((m) => m.id);
    if (!ids.length) return;
    try {
      await useProfiles.getState().assignGroup(ids, null);
      setChecked(new Set());
    } catch (e) {
      toast("error", message(e));
    }
  };
  const candidates = useMemo(() => {
    const q = pickQuery.trim().toLocaleLowerCase();
    return profiles
      .filter((p) => p.groupName !== sel?.name)
      .filter((p) => !q || p.name.toLocaleLowerCase().includes(q) || (p.host ?? "").toLocaleLowerCase().includes(q) || (p.groupName ?? "").toLocaleLowerCase().includes(q))
      .sort(byName);
  }, [profiles, sel, pickQuery]);
  const addPicked = async () => {
    if (!sel || !pick.size) return;
    try {
      const r = await useProfiles.getState().assignGroup([...pick], sel.name);
      setAdding(false);
      setPick(new Set());
      if (r.missing?.length) toast("warn", `${plural(r.missing.length, "connection")} ${r.missing.length === 1 ? "was" : "were"} deleted elsewhere and skipped.`);
    } catch (e) {
      toast("error", message(e));
    }
  };

  // ---- Delete: the count the user saw goes along; a different count asks again with the real one.
  const remove = async () => {
    if (sel && (await confirmDeleteGroup(sel.name))) setSelected(null);
  };

  const toggle = (set: ReadonlySet<string>, id: string) => {
    const next = new Set(set);
    if (!next.delete(id)) next.add(id);
    return next;
  };

  return (
    <Modal
      title="Groups"
      subtitle="Group connections; connections without a group stay where they are"
      onClose={onClose}
      width={780}
      bodyClassName="flex min-h-0 h-[440px]"
      footer={
        <>
          <label className="mr-auto flex items-center gap-2">
            <input type="checkbox" checked={showGroups} onChange={(e) => useSettings.getState().set({ showGroups: e.target.checked })} />
            Show groups in the Object Explorer and the Connect dialog
          </label>
          <button className="q-btn" onClick={onClose}>Close</button>
        </>
      }
    >
      <div className="w-[260px] shrink-0 border-r border-line p-3 flex flex-col gap-2 min-h-0">
        <div className="flex gap-1.5">
          <button className="q-btn" onClick={() => { setCreating(true); setNewError(null); }}>+ New</button>
        </div>
        {creating && (
          <div className="flex flex-col gap-1">
            <div className="flex gap-1.5">
              <input
                autoFocus aria-label="New group name" className="q-input h-7 flex-1 min-w-0" value={newName} placeholder="New group name"
                onChange={(e) => { setNewName(e.target.value); setNewError(null); }}
                onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); void create(); } if (e.key === "Escape") { e.stopPropagation(); setCreating(false); } }}
                aria-invalid={newError ? true : undefined} aria-describedby={newError ? `${fid}-new-error` : undefined}
              />
              <button className="q-btn q-btn-primary" onClick={() => void create()}>Create</button>
            </div>
            {newError && <span id={`${fid}-new-error`} className="text-[11px] text-err">{newError}</span>}
          </div>
        )}
        {groups.length > 0 && <input aria-label="Search groups" className="q-input h-7" placeholder="Search groups" value={query} onChange={(e) => setQuery(e.target.value)} />}
        <ul className="flex flex-col gap-0.5 overflow-auto min-h-0" aria-label="Groups">
          {!groups.length && <li className="text-muted py-4 text-center">No groups yet. Make one with New, or type a group name in a connection's profile.</li>}
          {shown.map((g) => {
            const prod = prodIn(g.name);
            return (
              <li key={g.name}>
                <button
                  className={`w-full h-8 px-2 rounded-md flex items-center gap-2 text-left hover:bg-raised ${g.name === sel?.name ? "bg-raised ring-1 ring-accent/60" : ""}`}
                  aria-current={g.name === sel?.name ? "true" : undefined}
                  onClick={() => setSelected(g.name)}
                >
                  <span className="truncate flex-1">{g.name}</span>
                  <span className="text-[11px] text-muted shrink-0">
                    {g.members ? g.members : "empty"}
                    {prod ? <span style={{ color: "var(--q-env-prod)" }}> · {prod} Prod</span> : null}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      </div>

      <div className="flex-1 min-w-0 p-4 flex flex-col gap-3 min-h-0">
        {!sel ? (
          <div className="text-muted m-auto">Pick a group, or make one with New.</div>
        ) : (
          <>
            <div className="flex flex-col gap-1">
              <label htmlFor={`${fid}-name`} className="text-muted">Name</label>
              <div className="flex gap-2">
                <input
                  id={`${fid}-name`} className="q-input h-7 flex-1 min-w-0" value={draft}
                  onChange={(e) => { setDraft(e.target.value); setRenameError(null); }}
                  onKeyDown={(e) => { if (e.key === "Enter" && canRename) { e.preventDefault(); void rename(); } }}
                  aria-invalid={renameError || draftClean.error ? true : undefined} aria-describedby={renameError || draftClean.error ? `${fid}-rename-error` : undefined}
                />
                <button className="q-btn" disabled={!canRename} onClick={() => void rename()}>Rename</button>
              </div>
              {(renameError ?? draftClean.error) && <span id={`${fid}-rename-error`} className="text-[11px] text-err">{renameError ?? draftClean.error}</span>}
            </div>

            {!adding ? (
              <div className="flex flex-col gap-1.5 min-h-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="text-muted flex-1">Members ({members.length})</span>
                  <button className="q-btn" disabled={!members.some((m) => checked.has(m.id))} onClick={() => void removeChecked()}>
                    Remove from group{checked.size ? ` (${members.filter((m) => checked.has(m.id)).length})` : ""}
                  </button>
                  <button className="q-btn q-btn-primary" onClick={() => { setAdding(true); setPick(new Set()); setPickQuery(""); }}>Add connections…</button>
                </div>
                <ul className="border border-line rounded-md overflow-auto min-h-0" aria-label={`Members of ${sel.name}`}>
                  {!members.length && <li className="text-muted py-4 text-center">No connections in this group yet.</li>}
                  {members.map((p) => (
                    <li key={p.id} className="border-b border-line last:border-b-0">
                      <label className="h-8 px-2.5 flex items-center gap-2">
                        <input type="checkbox" checked={checked.has(p.id)} onChange={() => setChecked((c) => toggle(c, p.id))} />
                        <EnvName p={p} />
                        <span className="font-semibold truncate">{p.name}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              </div>
            ) : (
              <div className="flex flex-col gap-1.5 min-h-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="text-muted flex-1">Add connections to “{sel.name}”</span>
                  <button className="q-btn" onClick={() => setAdding(false)}>Cancel</button>
                  <button className="q-btn q-btn-primary" disabled={!pick.size} onClick={() => void addPicked()}>Add {pick.size ? plural(pick.size, "connection") : ""}</button>
                </div>
                <input autoFocus aria-label="Search connections" className="q-input h-7" placeholder="Search name, host or group" value={pickQuery} onChange={(e) => setPickQuery(e.target.value)} />
                <ul className="border border-line rounded-md overflow-auto min-h-0" aria-label="Connections to add">
                  {!candidates.length && <li className="text-muted py-4 text-center">No other connections.</li>}
                  {candidates.map((p) => (
                    <li key={p.id} className="border-b border-line last:border-b-0">
                      <label className="h-8 px-2.5 flex items-center gap-2">
                        <input type="checkbox" checked={pick.has(p.id)} onChange={() => setPick((c) => toggle(c, p.id))} />
                        <EnvName p={p} />
                        <span className="font-semibold truncate">{p.name}</span>
                        <span className="ml-auto shrink-0 text-[11px] text-muted">{p.groupName ? `in ${p.groupName}` : "no group"}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <div className="flex items-center gap-2 border-t border-line pt-3">
              <span className="flex-1 text-[11px] text-muted">Deleting only ungroups its members. No connection is deleted.</span>
              <button className="q-btn text-err" onClick={() => void remove()}>Delete group…</button>
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}

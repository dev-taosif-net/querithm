// Bulk move (§6): put the chosen connections in a group, an existing one or a new one typed here. One group.assign
// call, one sidecar transaction; a connection deleted meanwhile is skipped and said so. Nothing about a connection
// changes but its group: environment, color, passwords and open tabs stay as they are.
import { useId, useMemo, useState } from "react";
import type { Profile } from "../ipc/contract";
import { Modal } from "../app/Modal";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { toast } from "../store/ui";
import { plural } from "../app/plural";
import { cleanGroupName, compareGroupNames, describeGroupChoice } from "./groupNames";

export function MoveToGroupDialog({ profileIds, onClose }: { profileIds: string[]; onClose: () => void }) {
  const all = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const fid = useId();
  // The connections as they are now: one deleted while the dialog is open drops out of the list.
  const profiles = useMemo(() => profileIds.map((id) => all.find((p) => p.id === id)).filter((p): p is Profile => !!p), [all, profileIds]);
  const sorted = useMemo(() => [...groups].sort((a, b) => compareGroupNames(a.name, b.name)), [groups]);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const clean = cleanGroupName(name);
  const choice = describeGroupChoice(groups, name);
  const prod = profiles.filter((p) => p.environment === "prod").length;

  const move = async () => {
    if (busy || !choice || !profiles.length) return;
    setBusy(true);
    setError(null);
    try {
      const r = await useProfiles.getState().assignGroup(profiles.map((p) => p.id), choice.name);
      onClose();
      const moved = r.profiles.length;
      const skipped = r.missing?.length ?? 0;
      const head = moved ? `Moved ${plural(moved, "connection")} to “${choice.name}”.` : `Already in “${choice.name}”.`;
      toast(skipped ? "warn" : "info", skipped ? `${head} ${plural(skipped, "connection")} ${skipped === 1 ? "was" : "were"} deleted elsewhere and skipped.` : head);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const hintId = `${fid}-hint`;
  const errorId = `${fid}-error`;
  return (
    <Modal
      title={`Move ${plural(profiles.length, "connection")} to a group`}
      onClose={onClose}
      onEnter={() => void move()}
      width={480}
      footer={
        <>
          <button className="q-btn" onClick={onClose}>Cancel</button>
          <button className="q-btn q-btn-primary" disabled={!choice || !profiles.length || busy} onClick={() => void move()}>
            Move {plural(profiles.length, "connection")}
          </button>
        </>
      }
    >
      <div className="space-y-3">
        <div className="flex flex-col gap-1">
          <label htmlFor={`${fid}-group`} className="text-muted">Group</label>
          <input
            id={`${fid}-group`} autoFocus className="q-input w-full h-7" list={`${fid}-groups`} value={name} placeholder="Pick a group or type a new name"
            onChange={(e) => { setName(e.target.value); setError(null); }}
            onBlur={() => choice && choice.name !== name && setName(choice.name)}
            aria-invalid={clean.error || error ? true : undefined}
            aria-describedby={clean.error || error ? errorId : choice ? hintId : undefined}
          />
          <datalist id={`${fid}-groups`}>
            {sorted.map((g) => <option key={g.name} value={g.name} label={plural(g.members, "connection")} />)}
          </datalist>
          {clean.error || error ? (
            <span id={errorId} className="text-[11px] text-err">{clean.error ?? error}</span>
          ) : choice ? (
            <span id={hintId} className="text-[11px] text-muted">{choice.text}</span>
          ) : null}
        </div>
        <div className="flex flex-col gap-1">
          <span className="text-muted">Connections</span>
          {profiles.length ? (
            <ul className="border border-line rounded-md max-h-[220px] overflow-auto" aria-label="Connections to move">
              {profiles.map((p) => (
                <li key={p.id} className="h-8 px-2.5 flex items-center gap-2 border-b border-line last:border-b-0">
                  <span className="font-bold text-[11px]" style={{ color: envColor(p) }}>{envLabel[p.environment]}</span>
                  <span className="font-semibold truncate">{p.name}</span>
                  <span className="ml-auto shrink-0 text-[11px] text-muted">{p.groupName ? `from ${p.groupName}` : "no group"}</span>
                </li>
              ))}
            </ul>
          ) : (
            <span className="text-muted">These connections no longer exist.</span>
          )}
        </div>
        <p className="text-[11px] text-muted">
          Environment, passwords and open tabs don’t change.{prod ? ` ${plural(prod, "Production connection")} ${prod === 1 ? "stays" : "stay"} Production.` : ""}
        </p>
      </div>
    </Modal>
  );
}

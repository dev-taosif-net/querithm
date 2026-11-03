// Settings → Keyboard shortcuts (§12 Keyboard model): every command, bound or not, grouped by category and searchable.
// A key cap is a button: Enter or a click records a replacement for that key, Delete removes it, "Add key" records one
// more. A recorded shortcut must pass shortcutProblem and must not belong to another command (Reassign moves it).
import { Fragment, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { commands, keysFor, type Command } from "../app/commands";
import { canonical, displayKeys, eventToShortcut, isChord, isMac, joinBinding, shortcutProblem, spokenKeys } from "../app/shortcuts";
import { KeyCaps } from "../app/controls";
import { Icon } from "../app/icons";
import { useSettings } from "../store/settings";
import { PageHeader } from "./SettingRow";

/** Keys this platform can press: MacCtrl exists on macOS only. */
const shownKeys = (c: Command) => keysFor(c).filter((k) => isMac || !k.includes("MacCtrl"));
const norm = (t: string) => t.toLowerCase().replace(/\s+/g, " ").trim();

/** Commands whose title, category or shortcuts contain every word of the query. */
export function searchCommands(query: string): Command[] {
  const words = norm(query).split(" ").filter(Boolean);
  if (!words.length) return [];
  return commands.filter((c) => {
    const keys = shownKeys(c);
    const hay = norm([c.title, c.category ?? "", ...keys, ...keys.map(displayKeys)].join(" "));
    return words.every((w) => hay.includes(w));
  });
}

/** The command that already uses a shortcut, other than `except`. */
function ownerOf(sc: string, except: string): Command | undefined {
  const c = canonical(sc);
  return commands.find((x) => x.id !== except && keysFor(x).some((k) => !isChord(k) && canonical(k) === c));
}

/** Stores a command's keys, or drops the override when they equal the defaults. */
function storeKeys(c: Command, keys: string[]) {
  const kb = { ...useSettings.getState().keybindings };
  const same = keys.length === (c.keys ?? []).length && keys.every((k, i) => canonical(k) === canonical(c.keys![i]));
  if (same) delete kb[c.id];
  else kb[c.id] = joinBinding(keys);
  useSettings.getState().set({ keybindings: kb });
}

type Slot = { cmd: string; index: number | "add" };
const slotId = (cmd: string, index: number | "add") => `kb-${cmd}-${index}`;

export function KeyboardPage({ query, setQuery, announce }: { query: string; setQuery: (q: string) => void; announce: (t: string) => void }) {
  const keybindings = useSettings((s) => s.keybindings);
  const [recording, setRecording] = useState<Slot | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [conflict, setConflict] = useState<{ sc: string; owner: Command } | null>(null);
  const [askReset, setAskReset] = useState(false);
  const reassignRef = useRef<HTMLButtonElement>(null);
  const shown = useMemo(() => (query.trim() ? searchCommands(query) : commands), [query, keybindings]); // eslint-disable-line react-hooks/exhaustive-deps
  const categories = [...new Set(shown.map((c) => c.category ?? "Other"))];
  const overridden = Object.keys(keybindings).length;

  useEffect(() => {
    if (conflict) reassignRef.current?.focus();
  }, [conflict]);

  const focusLater = (id: string) => requestAnimationFrame(() => document.getElementById(id)?.focus());
  const stop = () => {
    setRecording(null);
    setProblem(null);
    setConflict(null);
  };
  const start = (cmd: Command, index: number | "add") => {
    setRecording({ cmd: cmd.id, index });
    setProblem(null);
    setConflict(null);
    announce(`Recording a shortcut for ${cmd.title}. Press the keys, or Escape to cancel.`);
    focusLater(slotId(cmd.id, index));
  };
  const apply = (cmd: Command, index: number | "add", sc: string) => {
    const keys = shownKeys(cmd);
    const hiddenKeys = keysFor(cmd).filter((k) => !keys.includes(k));
    const next = index === "add" ? [...keys, sc] : keys.map((k, i) => (i === index ? sc : k));
    const unique = next.filter((k, i) => next.findIndex((x) => canonical(x) === canonical(k)) === i);
    storeKeys(cmd, [...unique, ...hiddenKeys]);
    stop();
    announce(`${cmd.title}: ${spokenKeys(sc)}.`);
    focusLater(slotId(cmd.id, unique.findIndex((k) => canonical(k) === canonical(sc))));
  };
  const remove = (cmd: Command, index: number) => {
    const keys = keysFor(cmd);
    const k = shownKeys(cmd)[index];
    storeKeys(cmd, keys.filter((x) => x !== k));
    stop();
    announce(`${spokenKeys(k)} removed from ${cmd.title}.`);
    focusLater(slotId(cmd.id, "add"));
  };
  const reassign = () => {
    if (!recording || !conflict) return;
    const cmd = commands.find((c) => c.id === recording.cmd)!;
    storeKeys(conflict.owner, keysFor(conflict.owner).filter((k) => isChord(k) || canonical(k) !== canonical(conflict.sc)));
    announce(`${spokenKeys(conflict.sc)} moved from ${conflict.owner.title}.`);
    apply(cmd, recording.index, conflict.sc);
  };
  const onRecordKey = (e: ReactKeyboardEvent, cmd: Command, index: number | "add") => {
    if (["Control", "Shift", "Alt", "Meta", "AltGraph", "OS"].includes(e.key) || e.nativeEvent.isComposing) return;
    if (e.key === "Tab") {
      stop(); // leaving the recorder never records Tab
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    if (e.key === "Escape") {
      stop();
      announce("Recording cancelled.");
      focusLater(index === "add" || index >= shownKeys(cmd).length ? slotId(cmd.id, "add") : slotId(cmd.id, index));
      return;
    }
    const sc = eventToShortcut(e.nativeEvent);
    const why = shortcutProblem(sc);
    if (why) {
      setProblem(why);
      announce(why);
      return;
    }
    const owner = ownerOf(sc, cmd.id);
    if (owner) {
      setConflict({ sc, owner });
      setProblem(null);
      announce(`${spokenKeys(sc)} is used by ${owner.title}. Reassign it, or Cancel.`);
      return;
    }
    apply(cmd, index, sc);
  };

  return (
    <>
      <PageHeader title="Keyboard shortcuts">
        {overridden > 0 &&
          (askReset ? (
            <span className="inline-flex items-center gap-2 text-[11px] text-fg">
              Reset all {overridden} changed {overridden === 1 ? "command" : "commands"} to their default shortcuts?
              <button
                type="button"
                className="q-btn q-btn-primary"
                autoFocus
                onClick={() => {
                  useSettings.getState().set({ keybindings: {} });
                  setAskReset(false);
                  announce("All shortcuts reset to the defaults.");
                }}
              >
                Reset all
              </button>
              <button type="button" className="q-btn" onClick={() => setAskReset(false)}>
                Cancel
              </button>
            </span>
          ) : (
            <button type="button" className="q-btn" onClick={() => setAskReset(true)}>
              Reset all shortcuts
            </button>
          ))}
      </PageHeader>
      <div className="relative mb-3">
        <Icon name="search" size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-muted pointer-events-none" />
        <input
          type="search"
          data-search="keys"
          aria-label="Search shortcuts by command or key"
          placeholder="Search by command or key"
          className="q-input h-7 w-full pl-7"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>
      <p id="kb-help" className="sr-only">
        Enter changes this shortcut. Delete removes it.
      </p>
      {!shown.length && <div className="text-[12px] text-muted py-6 text-center">No command or shortcut matches “{query}”.</div>}
      <table className="w-full border-collapse text-[12px]">
        <caption className="sr-only">Commands and their shortcuts</caption>
        {categories.map((cat) => (
          <tbody key={cat}>
            <tr>
              <th scope="rowgroup" colSpan={2} className="sticky top-[-20px] z-10 bg-panel text-left text-[11px] font-semibold uppercase tracking-wide text-muted pt-4 pb-1.5 border-b border-line">
                {cat}
              </th>
            </tr>
            {shown
              .filter((c) => (c.category ?? "Other") === cat)
              .map((c) => {
                const keys = shownKeys(c);
                const changed = c.id in keybindings;
                const rec = recording?.cmd === c.id ? recording : null;
                return (
                  <tr key={c.id} className="border-b border-line align-middle" data-command={c.id}>
                    <th scope="row" className="py-1.5 pr-4 text-left font-normal text-fg">
                      <span className="inline-flex items-center gap-2">
                        {c.title}
                        {changed && <span className="w-1.5 h-1.5 rounded-full bg-muted" title="Changed from the default" aria-hidden />}
                        {changed && <span className="sr-only">(changed)</span>}
                      </span>
                    </th>
                    <td className="py-1.5 text-right">
                      <span className="inline-flex flex-wrap items-center justify-end gap-1.5">
                        {keys.map((k, i) => (
                          <Fragment key={i}>
                            {i > 0 && (
                              <span className="text-[10.5px] text-muted" aria-hidden>
                                or
                              </span>
                            )}
                            {isChord(k) ? (
                              <span className="inline-flex items-center gap-1" title="A two-key shortcut handled by the editor">
                                <KeyCaps keys={k} />
                              </span>
                            ) : rec && rec.index === i ? (
                              <Recorder id={slotId(c.id, i)} cmd={c} onKey={(e) => onRecordKey(e, c, i)} onBlur={() => !conflict && stop()} onRemove={() => remove(c, i)} />
                            ) : (
                              <button
                                id={slotId(c.id, i)}
                                type="button"
                                className="rounded-[4px] p-px hover:bg-hover"
                                aria-label={`${c.title}: ${spokenKeys(k)}`}
                                aria-describedby="kb-help"
                                onClick={() => start(c, i)}
                                onKeyDown={(e) => {
                                  if (e.key === "Delete" || e.key === "Backspace") {
                                    e.preventDefault();
                                    remove(c, i);
                                  }
                                }}
                              >
                                <KeyCaps keys={k} />
                              </button>
                            )}
                          </Fragment>
                        ))}
                        {!keys.length && !rec && <span className="text-[11px] text-muted">Not set</span>}
                        {rec && rec.index === "add" ? (
                          <Recorder id={slotId(c.id, "add")} cmd={c} onKey={(e) => onRecordKey(e, c, "add")} onBlur={() => !conflict && stop()} />
                        ) : (
                          <button
                            id={slotId(c.id, "add")}
                            type="button"
                            className="q-btn q-btn-ghost w-6 !px-0 justify-center text-muted hover:text-fg"
                            aria-label={`Add a shortcut for ${c.title}`}
                            title="Add a shortcut"
                            onClick={() => start(c, "add")}
                          >
                            <Icon name="plus" size={12} />
                          </button>
                        )}
                        {changed && (
                          <button
                            type="button"
                            className="q-btn q-btn-ghost w-6 !px-0 justify-center text-muted hover:text-fg"
                            aria-label={`Reset ${c.title} to its default shortcuts`}
                            title="Reset to the default"
                            onClick={() => {
                              const kb = { ...useSettings.getState().keybindings };
                              delete kb[c.id];
                              useSettings.getState().set({ keybindings: kb });
                              announce(`${c.title} reset to its default shortcuts.`);
                              focusLater(slotId(c.id, "add"));
                            }}
                          >
                            <Icon name="undo" size={12} />
                          </button>
                        )}
                      </span>
                      {rec && (problem || conflict) && (
                        <div className="flex items-center justify-end gap-2 mt-1 text-[11px]" role="presentation">
                          {problem && <span className="text-err">{problem}</span>}
                          {conflict && (
                            <>
                              <span className="text-warn">
                                {displayKeys(conflict.sc)} is used by {conflict.owner.title}.
                              </span>
                              <button ref={reassignRef} type="button" className="q-btn" onMouseDown={(e) => e.preventDefault()} onClick={reassign}>
                                Reassign
                              </button>
                              <button
                                type="button"
                                className="q-btn"
                                onMouseDown={(e) => e.preventDefault()}
                                onClick={() => {
                                  stop();
                                  announce("Recording cancelled.");
                                  focusLater(slotId(c.id, "add"));
                                }}
                              >
                                Cancel
                              </button>
                            </>
                          )}
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
          </tbody>
        ))}
      </table>
    </>
  );
}

/** The armed slot. aria-expanded="true" tells the dialog that Escape belongs to it (cancel, not close). */
function Recorder({ id, cmd, onKey, onBlur, onRemove }: { id: string; cmd: Command; onKey: (e: ReactKeyboardEvent) => void; onBlur: () => void; onRemove?: () => void }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <button
        id={id}
        type="button"
        aria-expanded="true"
        aria-label={`Recording a shortcut for ${cmd.title}. Press the keys, or Escape to cancel.`}
        className="h-6 px-2 rounded-[4px] border border-dashed border-accent bg-row-sel q-hl text-[11px] text-fg"
        onKeyDown={onKey}
        onBlur={onBlur}
      >
        Press keys · Esc cancels
      </button>
      {onRemove && (
        <button type="button" className="q-btn" onMouseDown={(e) => e.preventDefault()} onClick={onRemove}>
          Remove key
        </button>
      )}
    </span>
  );
}

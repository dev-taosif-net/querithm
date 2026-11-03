// Form controls shared by Settings and other dialogs (settings redesign 2026-10-30). Every control is a native input,
// so labels, keyboard behaviour and screen-reader support come from the platform; styling lives in tokens.css.
import { useEffect, useState } from "react";
import { EngineLogo } from "../connections/EngineLogo";
import { ENGINES } from "../engines";
import type { Engine } from "../ipc/contract";
import { isMac, spokenKeys, splitShortcut } from "./shortcuts";

/**
 * An on/off switch: a native checkbox with role="switch". `disabledWhy` keeps it focusable (aria-disabled) so the
 * reason linked by `describedBy` is still read; a change is ignored while it is set.
 */
export function Switch({
  id, checked, onChange, disabledWhy, describedBy, label,
}: { id?: string; checked: boolean; onChange: (on: boolean) => void; disabledWhy?: string | null; describedBy?: string; label?: string }) {
  return (
    <input
      id={id}
      type="checkbox"
      role="switch"
      className="q-switch"
      checked={checked}
      aria-checked={checked}
      aria-label={label}
      aria-disabled={disabledWhy ? true : undefined}
      aria-describedby={describedBy}
      onChange={(e) => {
        if (!disabledWhy) onChange(e.target.checked);
      }}
    />
  );
}

/** One choice of a few, all visible: native radio inputs (arrow keys move and select), drawn as a segmented control. */
export function Segmented<T extends string>({
  name, value, options, onChange, labelledBy, describedBy, disabledWhy,
}: {
  name: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void; labelledBy?: string; describedBy?: string; disabledWhy?: string | null;
}) {
  return (
    <div role="radiogroup" aria-labelledby={labelledBy} aria-describedby={describedBy} aria-disabled={disabledWhy ? true : undefined} className="inline-flex p-0.5 gap-0.5 rounded-md bg-panel-2 border border-line">
      {options.map((o) => (
        <label key={o.value} className="relative">
          <input
            type="radio"
            name={name}
            value={o.value}
            checked={value === o.value}
            onChange={() => !disabledWhy && onChange(o.value)}
            className="peer sr-only"
          />
          <span className="h-6 px-2.5 flex items-center rounded text-[11px] font-medium text-muted hover:text-fg peer-checked:bg-raised peer-checked:text-fg peer-checked:ring-1 peer-checked:ring-line-strong peer-focus-visible:outline-2 peer-focus-visible:outline-accent peer-focus-visible:outline-offset-1 peer-focus-visible:outline">
            {o.label}
          </span>
        </label>
      ))}
    </div>
  );
}

/**
 * A whole number in [min, max]. What is typed stays a draft: a valid value applies at once (live preview), an empty or
 * out-of-range one is flagged and never stored, and leaving the field puts the last valid value back.
 */
export function NumberField({
  id, value, min, max, onCommit, describedBy, unit, width = 72, disabled = false, label,
}: {
  id: string; value: number; min: number; max: number; onCommit: (n: number) => void; describedBy?: string; unit?: string; width?: number; disabled?: boolean; label?: string;
}) {
  const [draft, setDraft] = useState(String(value));
  const [editing, setEditing] = useState(false);
  useEffect(() => {
    if (!editing) setDraft(String(value));
  }, [value, editing]);
  const parse = (t: string) => {
    const n = Number(t);
    return t.trim() !== "" && Number.isInteger(n) && n >= min && n <= max ? n : null;
  };
  const invalid = parse(draft) === null;
  const errId = `${id}-err`;
  const fmt = (n: number) => n.toLocaleString("en-US");
  return (
    <span className="inline-flex flex-col items-end">
      <span className="inline-flex items-center gap-1.5">
        <input
          id={id}
          type="number"
          inputMode="numeric"
          min={min}
          max={max}
          step={1}
          disabled={disabled}
          aria-label={label}
          aria-invalid={invalid || undefined}
          aria-describedby={[describedBy, invalid ? errId : ""].filter(Boolean).join(" ") || undefined}
          className={`q-input h-7 text-right tabular-nums ${invalid ? "!border-err" : ""} disabled:opacity-45`}
          style={{ width }}
          value={draft}
          onFocus={() => setEditing(true)}
          onChange={(e) => {
            setDraft(e.target.value);
            const n = parse(e.target.value);
            if (n !== null && n !== value) onCommit(n);
          }}
          onBlur={() => {
            setEditing(false);
            setDraft(String(value));
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") setDraft(String(parse(draft) ?? value));
          }}
        />
        {unit && <span className="text-[11px] text-muted">{unit}</span>}
      </span>
      {invalid && !disabled && (
        <span id={errId} className="text-[11px] text-err mt-0.5">
          Allowed: {fmt(min)}–{fmt(max)}
        </span>
      )}
    </span>
  );
}

/** One shortcut as key caps ("Ctrl" "Shift" "F", or ⌘ ⇧ F on macOS); screen readers hear the keys as words. */
export function KeyCaps({ keys }: { keys: string }) {
  return (
    <span className="inline-flex items-center gap-[3px]" aria-label={spokenKeys(keys)} role="img">
      {capsOf(keys).map((c, i) => (
        <kbd key={i} className="q-keycap" aria-hidden>
          {c}
        </kbd>
      ))}
    </span>
  );
}

const MAC_MODS: Record<string, string> = { Ctrl: "⌘", MacCtrl: "⌃", Alt: "⌥", Shift: "⇧" };

/** A chord ("Ctrl+K Ctrl+C") shows both strokes. */
export function capsOf(keys: string): string[] {
  return keys.trim().split(/\s+/).flatMap((stroke) => {
    const { mods, key } = splitShortcut(stroke);
    return [...mods.map((m) => (isMac ? (MAC_MODS[m] ?? m) : m === "MacCtrl" ? "Ctrl" : m)), key];
  });
}

/** "Works on SQL Server only" chip: the monochrome engine mark and the engine's name. */
export function EngineChip({ engine }: { engine: Engine }) {
  return (
    <span className="inline-flex items-center gap-1 h-[18px] px-1.5 border border-line rounded text-[10px] font-medium text-muted whitespace-nowrap">
      <EngineLogo engine={engine} mono size={12} decorative />
      {ENGINES[engine].shortLabel}
    </span>
  );
}

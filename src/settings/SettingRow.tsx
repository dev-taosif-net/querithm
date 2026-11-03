// One setting as a row (label and description left, control right) and a page of them under group headings.
// Each row subscribes to its own values only, so a change re-renders that row, not the dialog.
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useShallow } from "zustand/react/shallow";
import { defaultSettings, settingLimits, useSettings, type Settings } from "../store/settings";
import { explorerAction } from "../explorer/explorerWindow";
import { modeOf, type ExplorerMode } from "../explorer/layoutState";
import { EngineChip, NumberField, Segmented, Switch } from "../app/controls";
import { Icon } from "../app/icons";
import { syntaxThemeById, type SyntaxThemeId } from "../editor/syntaxTheme";
import { SyntaxThemePicker } from "./SyntaxThemePicker";
import { defaultsOf, isModified, pages, settingDefs, type PageId, type SettingDef } from "./registry";

const set = (patch: Partial<Settings>) => useSettings.getState().set(patch);
const modeNow = (s: Settings) => modeOf({ visible: s.explorerVisible, autoHide: s.explorerAutoHide });
const EXPLORER_MODES: { value: ExplorerMode; label: string }[] = [
  { value: "pinned", label: "Docked" },
  { value: "autoHide", label: "Auto hide" },
  { value: "hidden", label: "Closed" },
];

export const controlId = (id: SettingDef["id"]) => `setting-${id}`;

/** Puts focus on a row's control: the input itself, or the checked choice of a radio group. */
export function focusControl(id: SettingDef["id"]) {
  const el = document.getElementById(controlId(id));
  const target = el?.matches("input") ? el : el?.querySelector<HTMLElement>("input:checked, input:not([disabled])");
  target?.focus();
}

/** The default in words, for the reset button ("Reset Font size to 13 px"). */
function defaultText(def: SettingDef): string {
  const v = defaultSettings[def.keys[0]];
  switch (def.control.kind) {
    case "switch":
      return v ? "on" : "off";
    case "segmented":
      return def.control.options.find((o) => o.value === v)?.label ?? String(v);
    case "number":
      return `${v} ${def.control.unit}`;
    case "rowLimit":
      return `${(v as number).toLocaleString("en-US")} rows`;
    case "explorerMode":
      return EXPLORER_MODES.find((m) => m.value === modeNow(defaultSettings))!.label;
    case "syntaxTheme":
      return syntaxThemeById(v as string).label;
  }
}

export function resetRow(def: SettingDef) {
  if (def.id === "explorerMode") explorerAction({ mode: modeNow(defaultSettings) });
  else set(defaultsOf(def));
}

/** The last number of rows used before "No limit" was ticked, so unticking it brings that number back. */
let lastRowLimit = defaultSettings.maxRows;

function RowLimit({ id, describedBy }: { id: string; describedBy: string }) {
  const v = useSettings((s) => s.maxRows);
  if (v > 0) lastRowLimit = v;
  const none = v === 0;
  return (
    <span className="inline-flex items-start gap-3">
      <NumberField
        id={id}
        label="Rows fetched per result set"
        value={none ? lastRowLimit : v}
        min={1}
        max={settingLimits.maxRows.max}
        unit="rows"
        width={104}
        disabled={none}
        describedBy={describedBy}
        onCommit={(n) => set({ maxRows: n })}
      />
      <label className="inline-flex items-center gap-1.5 h-7 text-[12px] text-fg">
        <input type="checkbox" checked={none} aria-describedby={describedBy} onChange={(e) => set({ maxRows: e.target.checked ? 0 : lastRowLimit })} />
        No limit
      </label>
    </span>
  );
}

export function SettingRow({ def, announce }: { def: SettingDef; announce: (text: string) => void }) {
  const st = useSettings(
    useShallow((s) => ({
      value: def.id === "explorerMode" ? modeNow(s) : (s[def.keys[0]] as unknown),
      modified: isModified(def, s),
      why: def.disabledWhy?.(s) ?? null,
      warning: def.warning?.(s) ?? null,
      note: def.note?.(s) ?? null,
    })),
  );
  const id = controlId(def.id);
  const labelId = `${id}-label`;
  const ids = { desc: `${id}-desc`, why: `${id}-why`, warning: `${id}-warn`, note: `${id}-note`, modified: `${id}-mod` };
  const describedBy = [ids.desc, st.why && ids.why, st.warning && ids.warning, st.note && ids.note, st.modified && ids.modified].filter(Boolean).join(" ");
  const key = def.keys[0];
  const c = def.control;
  let control: ReactNode;
  switch (c.kind) {
    case "switch":
      control = <Switch id={id} checked={!!st.value} disabledWhy={st.why} describedBy={describedBy} onChange={(on) => set({ [key]: on })} />;
      break;
    case "segmented":
      control = (
        <span id={id}>
          <Segmented name={id} value={String(st.value)} options={c.options} labelledBy={labelId} describedBy={describedBy} disabledWhy={st.why} onChange={(v) => set({ [key]: v } as Partial<Settings>)} />
        </span>
      );
      break;
    case "number":
      control = <NumberField id={id} value={st.value as number} min={c.min} max={c.max} unit={c.unit} width={c.width} describedBy={describedBy} onCommit={(n) => set({ [key]: n })} />;
      break;
    case "rowLimit":
      control = <RowLimit id={id} describedBy={describedBy} />;
      break;
    case "explorerMode":
      control = (
        <span id={id}>
          <Segmented name={id} value={st.value as ExplorerMode} options={EXPLORER_MODES} labelledBy={labelId} describedBy={describedBy} onChange={(mode) => explorerAction({ mode })} />
        </span>
      );
      break;
    case "syntaxTheme":
      control = (
        <div id={id}>
          <SyntaxThemePicker name={id} value={st.value as SyntaxThemeId} labelledBy={labelId} describedBy={describedBy} onChange={(v) => set({ syntaxTheme: v })} />
        </div>
      );
      break;
  }
  const labelled = c.kind === "switch" || c.kind === "number";
  // The theme cards need the row's width: they sit under the label instead of beside it.
  const wide = c.kind === "syntaxTheme";
  const reset = () => {
    resetRow(def);
    announce(`${def.label} reset to ${defaultText(def)}.`);
    requestAnimationFrame(() => focusControl(def.id));
  };
  return (
    <div className={`group grid ${wide ? "grid-cols-[minmax(0,1fr)_auto]" : "grid-cols-[minmax(0,1fr)_auto] items-center"} gap-x-6 py-2.5 min-h-[44px]`} data-setting={def.id}>
      <div className="min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          {labelled ? (
            <label id={labelId} htmlFor={id} className="text-[12px] font-medium text-fg">
              {def.label}
            </label>
          ) : (
            <span id={labelId} className="text-[12px] font-medium text-fg">
              {def.label}
            </span>
          )}
          {def.engine && <EngineChip engine={def.engine.only} />}
          {st.modified && (
            <span className="w-1.5 h-1.5 rounded-full bg-muted" title="Changed from the default" aria-hidden />
          )}
          {st.modified && (
            <span id={ids.modified} className="sr-only">
              Changed from the default.
            </span>
          )}
        </div>
        <div id={ids.desc} className="text-[11px] leading-[15px] text-muted max-w-[460px] mt-0.5">
          {def.description}
          {def.engine && <> {def.engine.other}</>}
        </div>
        {st.why && (
          <div id={ids.why} className="text-[11px] leading-[15px] text-muted max-w-[460px] mt-1 italic">
            {st.why}
          </div>
        )}
        {st.note && (
          <div id={ids.note} className="text-[11px] leading-[15px] text-muted max-w-[460px] mt-1">
            {st.note}
          </div>
        )}
        {st.warning && (
          <div id={ids.warning} className="flex items-start gap-1.5 text-[11px] leading-[15px] text-warn max-w-[460px] mt-1">
            <Icon name="alert" size={12} className="mt-px" />
            {st.warning}
          </div>
        )}
      </div>
      <div className="flex items-center gap-1.5 justify-end">
        {st.modified && (
          <button
            type="button"
            className="q-btn q-btn-ghost w-6 !px-0 justify-center text-muted hover:text-fg opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
            aria-label={`Reset ${def.label} to the default, ${defaultText(def)}`}
            title={`Reset to the default (${defaultText(def)})`}
            onClick={reset}
          >
            <Icon name="undo" size={12} />
          </button>
        )}
        {!wide && control}
      </div>
      {wide && <div className="col-span-2">{control}</div>}
    </div>
  );
}

/** "Restore defaults" for a page: asks once inline, then resets every changed row on it. */
function RestoreDefaults({ defs, announce }: { defs: SettingDef[]; announce: (text: string) => void }) {
  const changed = useSettings(useShallow((s) => defs.filter((d) => isModified(d, s)).map((d) => d.id)));
  const [asking, setAsking] = useState(false);
  const confirmRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (asking) confirmRef.current?.focus();
  }, [asking]);
  useEffect(() => {
    if (!changed.length) setAsking(false);
  }, [changed.length]);
  if (!changed.length) return null;
  const n = changed.length;
  if (!asking)
    return (
      <button type="button" className="q-btn" onClick={() => setAsking(true)}>
        Restore defaults
      </button>
    );
  return (
    <span className="inline-flex items-center gap-2 text-[11px] text-fg">
      Reset {n} {n === 1 ? "setting" : "settings"} on this page?
      <button
        ref={confirmRef}
        type="button"
        className="q-btn q-btn-primary"
        onClick={() => {
          for (const d of defs) if (changed.includes(d.id)) resetRow(d);
          setAsking(false);
          announce(`${n} ${n === 1 ? "setting" : "settings"} reset to the defaults.`);
        }}
      >
        Reset
      </button>
      <button type="button" className="q-btn" onClick={() => setAsking(false)}>
        Cancel
      </button>
    </span>
  );
}

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="flex items-center gap-3 min-h-7 mb-3">
      <h2 className="text-[13px] font-semibold text-fg">{title}</h2>
      <span className="ml-auto">{children}</span>
    </div>
  );
}

export function GroupedRows({ defs, announce }: { defs: SettingDef[]; announce: (text: string) => void }) {
  const groups = [...new Set(defs.map((d) => d.group))];
  const base = useId();
  return (
    <>
      {groups.map((g, i) => (
        <section key={g} aria-labelledby={`${base}-${i}`} className={i ? "mt-6" : ""}>
          <h3 id={`${base}-${i}`} className="text-[11px] font-semibold uppercase tracking-wide text-muted pb-1.5 border-b border-line">
            {g}
          </h3>
          <div className="divide-y divide-line">
            {defs
              .filter((d) => d.group === g)
              .map((d) => (
                <SettingRow key={d.id} def={d} announce={announce} />
              ))}
          </div>
        </section>
      ))}
    </>
  );
}

/** A page of plain setting rows (every page but Keyboard shortcuts and About). */
export function SettingsPage({ page, announce }: { page: PageId; announce: (text: string) => void }) {
  const defs = settingDefs.filter((d) => d.page === page);
  return (
    <>
      <PageHeader title={pages.find((p) => p.id === page)!.title}>
        <RestoreDefaults defs={defs} announce={announce} />
      </PageHeader>
      <GroupedRows defs={defs} announce={announce} />
    </>
  );
}

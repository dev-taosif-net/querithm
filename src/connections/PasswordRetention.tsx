import { useEffect, useId, useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import { createPortal } from "react-dom";
import type { Profile } from "../ipc/contract";

const PRESETS = [15, 60, 480, 1440, 10080, 43200];
const UNITS = { minutes: 1, hours: 60, days: 1440 };
type Unit = keyof typeof UNITS;

/**
 * The four policies as the user reads them: a one-line hint under the profile editor's select, and the longer
 * explanation its info button shows. Matches src-tauri/src/keyring.rs: `once` and `session` live in memory only,
 * `duration` and `forever` in the OS keyring (`duration` as a timed entry checked before every use).
 */
const OPTIONS = [
  {
    value: "once", label: "Until disconnected", where: "Memory only",
    hint: "Forgotten as soon as you disconnect. Never saved to disk.",
    details: "Querithm holds the password in memory only while you are connected. When you disconnect or close the app, it is forgotten, and you type it again next time. A good choice for shared computers and production servers.",
  },
  {
    value: "session", label: "Until app closes", where: "Memory only",
    hint: "Forgotten when you close Querithm. Never saved to disk.",
    details: "The password stays in memory while Querithm is open, so you can disconnect and reconnect without typing it again. Closing Querithm forgets it.",
  },
  {
    value: "duration", label: "For a set duration", where: "Password vault",
    hint: "Saved in your system’s password vault until the time is up.",
    details: "Saved in your system’s password vault for the time you choose, from 1 minute up to 1 year. It survives restarting Querithm. When the time is up, Querithm stops using it and asks you again; connections that are already open keep working.",
  },
  {
    value: "forever", label: "Always remember", where: "Password vault",
    hint: "Saved in your system’s password vault until you remove it.",
    details: "Saved in your system’s password vault and used every time you connect, even after restarting. It stays until you click Forget next to the password or delete this connection.",
  },
] as const;

/** The one-line hint for a policy; no policy is a legacy profile, which means `forever` (§ password retention). */
export function retentionHint(policy: string | null | undefined): string {
  return (OPTIONS.find((o) => o.value === (policy ?? "forever")) ?? OPTIONS[3]).hint;
}

/** What each policy means, said once (the full form's note). */
function explain(policy: string): string {
  return policy === "forever" ? "Saved securely on this device until you choose to forget it." : policy === "once" ? "You’ll be asked again after disconnecting or closing the app." : policy === "session" ? "You’ll be asked again the next time you open the app." : "Saved securely on this device, including after restarting the app. You’ll be asked again when the duration ends. Existing connections stay open.";
}

const HELP_WIDTH = 380;
/** Room the popover needs below the button before it opens above instead (its four entries plus the footer). */
const HELP_HEIGHT = 330;

/**
 * The info button beside the compact select: a disclosure whose panel explains all four options, the current one
 * marked. Focus stays on the button (the panel takes no focus and keeps a click from moving it), so Escape there closes
 * the panel and not the dialog (Modal leaves Escape to an element with aria-expanded="true"). It closes on Escape, on a
 * click outside, on Tab away, and when anything scrolls or the window resizes, since the panel is placed once on open.
 *
 * The panel is portalled to <body>: the editor's sections play `q-profile-reveal` with `fill-mode: both`, and a filled
 * transform animation makes the section the containing block of a fixed descendant (Chromium / WebView2), so a panel
 * left inside it was placed at viewport coordinates measured from the section — off to the right, under a scrollbar.
 */
function RetentionHelp({ policy }: { policy: string }) {
  const [pos, setPos] = useState<CSSProperties | null>(null);
  const btn = useRef<HTMLButtonElement>(null);
  const pop = useRef<HTMLDivElement>(null);
  const id = useId();
  const open = pos !== null;

  const show = () => {
    const r = btn.current!.getBoundingClientRect();
    const width = Math.min(HELP_WIDTH, window.innerWidth - 16);
    const left = Math.max(8, Math.min(r.left - 12, window.innerWidth - width - 8));
    const below = window.innerHeight - r.bottom;
    // The side with room (or the roomier one), never taller than that side: a short window scrolls the panel itself.
    setPos(below < HELP_HEIGHT + 8 && r.top > below
      ? { left, width, bottom: window.innerHeight - r.top + 6, maxHeight: r.top - 14 }
      : { left, width, top: r.bottom + 6, maxHeight: below - 14 });
  };

  useEffect(() => {
    if (!open) return;
    const away = (e: Event) => {
      if (!btn.current?.contains(e.target as Node) && !pop.current?.contains(e.target as Node)) setPos(null);
    };
    const close = () => setPos(null);
    window.addEventListener("mousedown", away, true);
    window.addEventListener("scroll", away, true);
    window.addEventListener("resize", close);
    return () => {
      window.removeEventListener("mousedown", away, true);
      window.removeEventListener("scroll", away, true);
      window.removeEventListener("resize", close);
    };
  }, [open]);

  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape" && open) {
      e.preventDefault();
      setPos(null);
    }
  };

  return <>
    <button
      ref={btn} type="button" aria-label="What do these options mean?" title="What do these options mean?"
      aria-expanded={open} aria-controls={open ? `${id}-help` : undefined}
      className={`shrink-0 w-7 h-7 flex items-center justify-center rounded hover:bg-hover hover:text-fg ${open ? "text-accent bg-hover" : "text-muted"}`}
      onClick={() => (open ? setPos(null) : show())} onKeyDown={onKey} onBlur={() => setPos(null)}
    >
      <svg width={15} height={15} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden>
        <circle cx="12" cy="12" r="10" /><path d="M12 16v-4" /><path d="M12 8h.01" />
      </svg>
    </button>
    {open && createPortal(
      <div
        ref={pop} id={`${id}-help`} role="region" aria-labelledby={`${id}-title`}
        className="q-popover-in fixed z-50 overflow-y-auto rounded-md border border-line bg-panel shadow-xl p-3 text-[11.5px] leading-snug" style={pos}
        onMouseDown={(e) => e.preventDefault()}
      >
        <div id={`${id}-title`} className="text-[12px] font-semibold text-fg mb-2">How long Querithm remembers your password</div>
        <dl className="space-y-1">
          {OPTIONS.map((o) => (
            <div key={o.value} className={`rounded px-2 py-1.5 ${o.value === policy ? "bg-hover" : ""}`} aria-current={o.value === policy ? "true" : undefined}>
              <dt className="flex items-center gap-2 text-fg font-medium">
                {o.label}
                <span className="px-1.5 rounded-full border border-line text-[10px] font-normal text-muted">{o.where}</span>
                {o.value === policy && <span className="ml-auto text-[10.5px] font-normal text-accent">Selected</span>}
              </dt>
              <dd className="m-0 mt-0.5 text-muted">{o.details}</dd>
            </div>
          ))}
        </dl>
        <p className="mt-2 pt-2 border-t border-line text-muted">
          Your system’s password vault is Windows Credential Manager, the macOS Keychain, or the Secret Service keyring on Linux. Querithm never writes passwords to its own files or logs.
        </p>
      </div>,
      document.body,
    )}
  </>;
}

/**
 * Remember-password policy. `compact` (the profile editor's one-row form) keeps the same controls and labels on one
 * line: the select as wide as the editor's password box with an info button beside it, the duration controls after
 * them; the one-line hint is the caller's (`retentionHint`, pointed at by `describedBy`), so it sits under the row like
 * the editor's other hints. The default form is unchanged.
 */
export function PasswordRetention({ profile, onChange, compact = false, describedBy }: { profile: Profile; onChange: (patch: Partial<Profile>) => void; compact?: boolean; describedBy?: string }) {
  const policy = profile.passwordRetention ?? "forever";
  const minutes = profile.passwordRetentionMinutes ?? 60;
  const [custom, setCustom] = useState(!PRESETS.includes(minutes));
  const [unit, setUnit] = useState<Unit>(minutes >= 1440 && minutes % 1440 === 0 ? "days" : minutes >= 60 && minutes % 60 === 0 ? "hours" : "minutes");
  if (compact) {
    const pick = (v: string) => {
      const isCustom = v === "custom";
      setCustom(isCustom);
      if (isCustom) { setUnit("days"); onChange({ passwordRetentionMinutes: 7 * 1440 }); }
      else onChange({ passwordRetentionMinutes: Number(v) });
    };
    return <div className="flex flex-wrap items-center gap-1.5 min-w-0">
      {/* 354px = the password box's 320px, the gap and the 28px button: the select lines up with the box above it. */}
      <div className="flex items-center gap-1.5 w-full max-w-[354px] min-w-0">
        <select className="q-input q-select h-8 flex-1 min-w-0" aria-label="Remember password" aria-describedby={describedBy} value={policy}
          onChange={(e) => onChange({ passwordRetention: e.target.value, passwordRetentionMinutes: minutes })}>
          {OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
        <RetentionHelp policy={policy} />
      </div>
      {policy === "duration" && <>
        <select className="q-input q-select h-8" aria-label="Duration" value={custom ? "custom" : minutes} onChange={(e) => pick(e.target.value)}>
          <option value={15}>15 minutes</option><option value={60}>1 hour</option><option value={480}>8 hours</option>
          <option value={1440}>1 day</option><option value={10080}>7 days</option><option value={43200}>30 days</option>
          <option value="custom">Custom duration</option>
        </select>
        {custom && <>
          <input className="q-input h-8 w-16" aria-label="Length" type="number" min={1} max={Math.floor(525600 / UNITS[unit])} step={1} value={minutes / UNITS[unit]} onChange={(e) => onChange({ passwordRetentionMinutes: Number(e.target.value) * UNITS[unit] })} />
          <select className="q-input q-select h-8" aria-label="Unit" value={unit} onChange={(e) => {
            const next = e.target.value as Unit;
            setUnit(next);
            onChange({ passwordRetentionMinutes: minutes / UNITS[unit] * UNITS[next] });
          }}><option value="minutes">Minutes</option><option value="hours">Hours</option><option value="days">Days</option></select>
        </>}
      </>}
    </div>;
  }
  return <div className="space-y-2">
    <label className="block text-muted text-xs">Remember password
      <select className="q-input w-full mt-1" value={policy} onChange={(e) => onChange({ passwordRetention: e.target.value, passwordRetentionMinutes: minutes })}>
        {OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
    {policy === "duration" && <div className="space-y-2">
      <label className="block text-xs text-muted">Duration
        <select className="q-input w-full mt-1" value={custom ? "custom" : minutes} onChange={(e) => {
          const isCustom = e.target.value === "custom";
          setCustom(isCustom);
          if (isCustom) { setUnit("days"); onChange({ passwordRetentionMinutes: 7 * 1440 }); }
          else onChange({ passwordRetentionMinutes: Number(e.target.value) });
        }}>
          <option value={15}>15 minutes</option><option value={60}>1 hour</option><option value={480}>8 hours</option>
          <option value={1440}>1 day</option><option value={10080}>7 days</option><option value={43200}>30 days</option>
          <option value="custom">Custom duration</option>
        </select>
      </label>
      {custom && <div className="flex gap-2">
        <label className="text-xs text-muted flex-1">Length
          <input className="q-input w-full mt-1" type="number" min={1} max={Math.floor(525600 / UNITS[unit])} step={1} value={minutes / UNITS[unit]} onChange={(e) => onChange({ passwordRetentionMinutes: Number(e.target.value) * UNITS[unit] })} />
        </label>
        <label className="text-xs text-muted flex-1">Unit
          <select className="q-input w-full mt-1" value={unit} onChange={(e) => {
            const next = e.target.value as Unit;
            setUnit(next);
            onChange({ passwordRetentionMinutes: minutes / UNITS[unit] * UNITS[next] });
          }}><option value="minutes">Minutes</option><option value="hours">Hours</option><option value="days">Days</option></select>
        </label>
      </div>}
    </div>}
    <p className="text-[10.5px] text-muted">{explain(policy)}</p>
  </div>;
}

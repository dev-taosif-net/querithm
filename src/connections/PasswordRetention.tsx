import { useState } from "react";
import type { Profile } from "../ipc/contract";

const PRESETS = [15, 60, 480, 1440, 10080, 43200];
const UNITS = { minutes: 1, hours: 60, days: 1440 };
type Unit = keyof typeof UNITS;

export function PasswordRetention({ profile, onChange }: { profile: Profile; onChange: (patch: Partial<Profile>) => void }) {
  const policy = profile.passwordRetention ?? "forever";
  const minutes = profile.passwordRetentionMinutes ?? 60;
  const [custom, setCustom] = useState(!PRESETS.includes(minutes));
  const [unit, setUnit] = useState<Unit>(minutes >= 1440 && minutes % 1440 === 0 ? "days" : minutes >= 60 && minutes % 60 === 0 ? "hours" : "minutes");
  return <div className="space-y-2">
    <label className="block text-muted text-xs">Remember password
      <select className="q-input w-full mt-1" value={policy} onChange={(e) => onChange({ passwordRetention: e.target.value, passwordRetentionMinutes: minutes })}>
        <option value="once">Until disconnected</option>
        <option value="session">Until app closes</option>
        <option value="duration">For a set duration</option>
        <option value="forever">Always remember</option>
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
    <p className="text-[10.5px] text-muted">{policy === "forever" ? "Saved securely on this device until you choose to forget it." : policy === "once" ? "You’ll be asked again after disconnecting or closing the app." : policy === "session" ? "You’ll be asked again the next time you open the app." : "Saved securely on this device, including after restarting the app. You’ll be asked again when the duration ends. Existing connections stay open."}</p>
  </div>;
}

// A fake store.db for the settings tests: settings.get, settings.set and settings.patch, with the sidecar's merge rules
// (SettingsStore.Patch, sidecar/Querithm.Core/Store/Stores.cs): members of `remove` dropped, then `setWhenNew` when the
// setting held no members (absent, {} or not an object), then `set`; members already stored keep their value and place.
import type { SettingEntry, SettingsGetParams, SettingsPatchParams } from "../../ipc/contract";

export const UI_SETTINGS = "ui.settings";

/** The stored value after a patch, as the sidecar computes it. */
export function applySettingsPatch(stored: string | undefined, p: SettingsPatchParams): string {
  let obj: Record<string, unknown> = {};
  try {
    const v = stored === undefined ? undefined : JSON.parse(stored);
    if (v && typeof v === "object" && !Array.isArray(v)) obj = v;
  } catch {
    // unreadable: holds nothing to keep
  }
  const wasEmpty = Object.keys(obj).length === 0;
  for (const name of p.remove ?? []) delete obj[name];
  if (wasEmpty) for (const [name, json] of Object.entries(p.setWhenNew ?? {})) obj[name] = JSON.parse(json);
  for (const [name, json] of Object.entries(p.set ?? {})) obj[name] = JSON.parse(json);
  return JSON.stringify(obj);
}

export function fakeSettingsDb(initial: Record<string, unknown> | null) {
  const entries = new Map<string, string>();
  if (initial) entries.set(UI_SETTINGS, JSON.stringify(initial));
  return {
    entries,
    /** An rpc mock's implementation for the three settings methods (anything else answers undefined). */
    handle: async (method: string, arg?: unknown): Promise<unknown> => {
      if (method === "settings.get") {
        const keys = (arg as SettingsGetParams | undefined)?.keys;
        return [...entries].filter(([k]) => !keys || keys.includes(k)).map(([key, valueJson]): SettingEntry => ({ key, valueJson }));
      }
      if (method === "settings.set") {
        const e = arg as SettingEntry;
        entries.set(e.key, e.valueJson);
      }
      if (method === "settings.patch") {
        const p = arg as SettingsPatchParams;
        entries.set(p.key, applySettingsPatch(entries.get(p.key), p));
      }
      return undefined;
    },
    /** ui.settings as stored, or null when absent. */
    read: (): Record<string, unknown> | null => {
      const json = entries.get(UI_SETTINGS);
      return json === undefined ? null : JSON.parse(json);
    },
    /** Another window (or another build) writes ui.settings whole. */
    write: (o: Record<string, unknown>) => void entries.set(UI_SETTINGS, JSON.stringify(o)),
  };
}

/** The ui.settings patches an rpc mock received, in order. */
export const patchesOf = (calls: unknown[][]) =>
  calls.filter((c) => c[0] === "settings.patch" && (c[1] as SettingsPatchParams).key === UI_SETTINGS).map((c) => c[1] as SettingsPatchParams);

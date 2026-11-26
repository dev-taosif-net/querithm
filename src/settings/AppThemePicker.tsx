// Settings → Appearance → Color theme (plans/app-themes §7.1): "Single theme" shows one App Theme; "Match system" keeps a
// dark pick and a light pick and follows the OS between them. The theme in use is always one of the two picks, so the
// light/dark toggle command flips between them. Cards are native radios (arrow keys move and apply at once) and draw
// their mini window from the theme's own data, never from var(--q-*), which would be the active theme's.
import { useSyncExternalStore } from "react";
import { useSettings } from "../store/settings";
import { APP_THEMES, type AppTheme, type ThemeMode } from "../theme/appThemes";

const osQuery = () => (typeof window !== "undefined" && window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null);
function subscribeOs(onChange: () => void) {
  const mq = osQuery();
  mq?.addEventListener("change", onChange);
  return () => mq?.removeEventListener("change", onChange);
}
const osDarkNow = () => osQuery()?.matches ?? true;

/** Three lines of SQL in the theme's own Default palette: keyword, identifier, function, schema, number, comment. */
function MiniWindow({ t }: { t: AppTheme }) {
  const u = t.ui, s = t.sql;
  const kw = { color: s.keyword, fontWeight: 700 } as const;
  // As the editor draws it: Italic comments off is upright here too.
  const comment = useSettings((st) => st.italicComments) ? "italic" : "normal";
  return (
    <span aria-hidden className="block rounded border overflow-hidden" style={{ background: u.bg, borderColor: u.line }}>
      <span className="flex h-[14px]" style={{ background: u.panel }}>
        <span className="px-2 text-[8px] leading-[14px]" style={{ color: u.fg, background: u.bg, boxShadow: `inset 0 2px 0 ${u.accent}` }}>report.sql</span>
      </span>
      <span className="q-mono block text-[9.5px] leading-[14px] py-1 whitespace-pre">
        <span className="block"><span style={{ color: u.muted }}> 1 </span><span style={kw}>SELECT</span> <span style={{ color: s.ident }}>o.id</span><span style={{ color: u.fg }}>, </span><span style={{ color: s.function }}>COUNT</span><span style={{ color: s.bracket1 }}>(</span><span style={{ color: s.operator }}>*</span><span style={{ color: s.bracket1 }}>)</span></span>
        <span className="block" style={{ background: u.panel }}><span style={{ color: u.fg }}> 2 </span><span style={kw}>FROM</span> <span style={{ color: s.schema }}>sales.</span><span style={{ color: s.ident }}>orders</span> <span style={{ color: s.ident }}>o</span></span>
        <span className="block"><span style={{ color: u.muted }}> 3 </span><span style={kw}>WHERE</span> <span style={{ color: s.ident }}>total</span> <span style={{ color: s.operator }}>&gt;</span> <span style={{ color: s.number }}>100</span> <span style={{ color: s.comment, fontStyle: comment }}>-- big</span></span>
      </span>
    </span>
  );
}

function Card({ t, name, checked, inUse, onPick }: { t: AppTheme; name: string; checked: boolean; inUse: boolean; onPick: () => void }) {
  const nameId = `${name}-${t.id}-name`;
  const descId = `${name}-${t.id}-desc`;
  return (
    <label className="relative block cursor-pointer" data-app-theme-card={t.id}>
      <input type="radio" name={name} value={t.id} checked={checked} onChange={onPick} aria-labelledby={nameId} aria-describedby={descId} className="peer sr-only" />
      <span className="flex flex-col gap-1.5 h-full p-2 rounded-md border border-line bg-panel-2 hover:border-line-strong peer-checked:border-accent peer-checked:ring-1 peer-checked:ring-accent peer-focus-visible:outline peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent">
        <MiniWindow t={t} />
        <span className="flex items-center gap-2 min-w-0">
          <span id={nameId} className="text-[12px] font-medium text-fg truncate">{t.name}</span>
          {t.id === "dark" && <span className="text-[10px] text-muted border border-line rounded px-1 shrink-0">Default</span>}
          {inUse && <span className="text-[10px] text-accent border border-accent rounded px-1 shrink-0">In use</span>}
        </span>
        <span id={descId} className="text-[11px] leading-[15px] text-muted">{t.description}</span>
      </span>
    </label>
  );
}

/**
 * The cards of one mode. `role`: a radiogroup of its own (Match system: one pick per mode), or a group inside the one
 * radiogroup of Single theme, whose nine cards share one radio name and so are one choice.
 */
function Group({ title, mode, name, value, shown, onPick, role = "radiogroup" }: { title: string; mode: ThemeMode; name: string; value: string; shown: string; onPick: (t: AppTheme) => void; role?: "radiogroup" | "group" }) {
  // By mode as well: in Single theme both groups share `name`, and one id twice made the Light group read as "Dark".
  const titleId = `${name}-${mode}-title`;
  return (
    <div role={role} aria-labelledby={titleId} className="mt-2">
      <div id={titleId} className="text-[11px] font-semibold text-muted mb-1.5">{title}</div>
      <div className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-2">
        {APP_THEMES.filter((t) => t.mode === mode).map((t) => (
          <Card key={t.id} t={t} name={name} checked={value === t.id} inUse={shown === t.id} onPick={() => onPick(t)} />
        ))}
      </div>
    </div>
  );
}

export function AppThemePicker({ name, labelledBy, describedBy }: { name: string; labelledBy: string; describedBy: string }) {
  const theme = useSettings((s) => s.theme);
  const darkTheme = useSettings((s) => s.darkTheme);
  const lightTheme = useSettings((s) => s.lightTheme);
  const osDark = useSyncExternalStore(subscribeOs, osDarkNow, osDarkNow);
  const set = useSettings.getState().set;
  const shownMode: ThemeMode = theme === "system" ? (osDark ? "dark" : "light") : theme;
  const shown = shownMode === "dark" ? darkTheme : lightTheme;
  const follow = theme === "system";
  return (
    <div role="group" aria-labelledby={labelledBy} aria-describedby={describedBy}>
      <div role="radiogroup" aria-label="Theme mode" className="inline-flex rounded-md border border-line-strong overflow-hidden text-[12px]">
        {([["single", "Single theme"], ["system", "Match system"]] as const).map(([v, label]) => {
          const on = v === "system" ? follow : !follow;
          // The radio is hidden: its keyboard focus shows on the segment, inset (the bar clips what is outside), in the
          // label color on the accent fill, where an accent ring would be the fill's own hue.
          return (
            <label key={v} className="relative inline-flex cursor-pointer">
              <input type="radio" name={`${name}-mode`} className="peer sr-only" checked={on} onChange={() => set({ theme: v === "system" ? "system" : shownMode })} />
              <span className={`px-3 h-7 inline-flex items-center peer-focus-visible:outline peer-focus-visible:outline-2 peer-focus-visible:-outline-offset-4 ${on ? "bg-accent-solid text-accent-fg font-semibold peer-focus-visible:outline-accent-fg" : "text-fg hover:bg-ghost-hover peer-focus-visible:outline-accent"}`}>
                {label}
              </span>
            </label>
          );
        })}
      </div>
      {follow ? (
        <>
          <Group title="When the system is dark" mode="dark" name={`${name}-dark`} value={darkTheme} shown={shown} onPick={(t) => set({ darkTheme: t.id })} />
          <Group title="When the system is light" mode="light" name={`${name}-light`} value={lightTheme} shown={shown} onPick={(t) => set({ lightTheme: t.id })} />
        </>
      ) : (
        <div role="radiogroup" aria-label="Single theme">
          <Group role="group" title="Dark" mode="dark" name={`${name}-single`} value={shownMode === "dark" ? darkTheme : ""} shown={shown} onPick={(t) => set({ theme: "dark", darkTheme: t.id })} />
          <Group role="group" title="Light" mode="light" name={`${name}-single`} value={shownMode === "light" ? lightTheme : ""} shown={shown} onPick={(t) => set({ theme: "light", lightTheme: t.id })} />
        </div>
      )}
    </div>
  );
}

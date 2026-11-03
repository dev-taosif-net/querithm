// Settings → Appearance → Syntax colors: one card per syntax theme with a short preview in the current mode.
// Native radios (arrow keys move and apply at once); the preview is pre-split text, so no editor is created.
import { useSyncExternalStore } from "react";
import { DEFAULT_SYNTAX_THEME, PREVIEW, roleColor, SYNTAX_THEMES, type SyntaxThemeId } from "../editor/syntaxTheme";

function subscribeMode(onChange: () => void) {
  const mo = new MutationObserver(onChange);
  mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  return () => mo.disconnect();
}
const currentMode = () => (document.documentElement.dataset.theme === "light" ? "light" : "dark");

const STYLE_CSS = (s: string | undefined) => ({
  fontWeight: s?.includes("bold") ? 700 : undefined,
  fontStyle: s?.includes("italic") ? "italic" : undefined,
  textDecoration: s?.includes("underline") ? "underline" : undefined,
});

export function SyntaxThemePicker({
  name, value, onChange, labelledBy, describedBy,
}: { name: string; value: SyntaxThemeId; onChange: (id: SyntaxThemeId) => void; labelledBy: string; describedBy: string }) {
  const mode = useSyncExternalStore(subscribeMode, currentMode, currentMode);
  return (
    <div role="radiogroup" aria-labelledby={labelledBy} aria-describedby={describedBy} className="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-2 mt-2">
      {SYNTAX_THEMES.map((t) => {
        const p = t[mode];
        const nameId = `${name}-${t.id}-name`;
        const descId = `${name}-${t.id}-desc`;
        return (
          <label key={t.id} className="relative block cursor-pointer" data-syntax-theme={t.id}>
            <input
              type="radio"
              name={name}
              value={t.id}
              checked={value === t.id}
              onChange={() => onChange(t.id)}
              aria-labelledby={nameId}
              aria-describedby={descId}
              className="peer sr-only"
            />
            <span className="flex flex-col gap-1.5 h-full p-2 rounded-md border border-line bg-panel-2 hover:border-line-strong peer-checked:border-accent peer-checked:ring-1 peer-checked:ring-accent peer-focus-visible:outline-2 peer-focus-visible:outline-accent peer-focus-visible:outline-offset-2 peer-focus-visible:outline">
              <span className="flex items-center gap-2">
                <span id={nameId} className="text-[12px] font-medium text-fg">{t.label}</span>
                {t.id === DEFAULT_SYNTAX_THEME && <span className="text-[10px] text-muted border border-line rounded px-1">Default</span>}
              </span>
              <span id={descId} className="text-[11px] leading-[15px] text-muted">
                {t.description}
              </span>
              <pre aria-hidden className="q-mono m-0 px-2 py-1.5 rounded bg-bg border border-line text-[11px] leading-[17px] overflow-hidden whitespace-pre">
                {PREVIEW.map((line, i) => (
                  <div key={i}>
                    {line.map((tok, j) => (
                      <span
                        key={j}
                        style={{
                          color: tok.role === "plain" ? "var(--q-fg)" : tok.role === "paren" ? p.bracket1 : roleColor(p, tok.role, "var(--q-fg)"),
                          ...(tok.role === "plain" || tok.role === "paren" ? {} : STYLE_CSS(t.fontStyle[tok.role])),
                        }}
                      >
                        {tok.text}
                      </span>
                    ))}
                  </div>
                ))}
              </pre>
            </span>
          </label>
        );
      })}
    </div>
  );
}

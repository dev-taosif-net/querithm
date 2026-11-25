// Settings → Appearance → Syntax colors: Default (the color theme's own palette, plans/app-themes §7.2) first, then the
// six syntax themes, which keep their colors on every color theme. Each card previews a few lines in the current mode.
// Native radios (arrow keys move and apply at once); the preview is pre-split text, so no editor is created.
import { useSyncExternalStore } from "react";
import { PREVIEW, roleColor, SYNTAX_THEMES, type FontStyle, type SyntaxChoice, type SyntaxPalette, type SyntaxRole } from "../editor/syntaxTheme";
import { appSqlPalette, resolveDocumentAppTheme, resolveSyntax } from "../theme/resolve";
import { useSettings } from "../store/settings";

/** The document's theme changes by its mode (data-theme) and its App Theme (data-app-theme). */
function subscribeTheme(onChange: () => void) {
  const mo = new MutationObserver(onChange);
  mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-app-theme"] });
  return () => mo.disconnect();
}
const currentAppTheme = () => resolveDocumentAppTheme(document.documentElement).id;

const STYLE_CSS = (s: string | undefined) => ({
  fontWeight: s?.includes("bold") ? 700 : undefined,
  fontStyle: s?.includes("italic") ? "italic" : undefined,
  textDecoration: s?.includes("underline") ? "underline" : undefined,
});

interface Card {
  id: SyntaxChoice;
  label: string;
  description: string;
  palette: SyntaxPalette;
  fontStyle: Partial<Record<SyntaxRole, FontStyle>>;
}

export function SyntaxThemePicker({
  name, value, onChange, labelledBy, describedBy,
}: { name: string; value: SyntaxChoice; onChange: (id: SyntaxChoice) => void; labelledBy: string; describedBy: string }) {
  useSyncExternalStore(subscribeTheme, currentAppTheme, currentAppTheme);
  // Font styles as the editor draws them: Italic comments off draws every preview's comment upright too.
  const upright = !useSettings((s) => s.italicComments);
  const app = resolveDocumentAppTheme(document.documentElement);
  const fontStyleOf = (id: SyntaxChoice) => resolveSyntax(id, app, upright).fontStyle;
  const cards: Card[] = [
    {
      id: "default",
      label: "Default",
      description: app.legacy
        ? `${app.name} uses the Standard colors plus its own for NULL, CTE names and schemas.`
        : `Follows the color theme: ${app.name}'s own colors.`,
      palette: appSqlPalette(app),
      fontStyle: fontStyleOf("default"),
    },
    ...SYNTAX_THEMES.map((t) => ({ id: t.id, label: t.label, description: t.description, palette: t[app.mode], fontStyle: fontStyleOf(t.id) })),
  ];
  return (
    <div role="radiogroup" aria-labelledby={labelledBy} aria-describedby={describedBy} className="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-2 mt-2">
      {cards.map((t) => {
        const p = t.palette;
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
                {t.id === "default" && <span className="text-[10px] text-muted border border-line rounded px-1">Default</span>}
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

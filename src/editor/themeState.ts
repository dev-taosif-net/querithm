// What the editor theme decided, shared between monaco.ts (which applies the theme) and editors.ts (which owns the editor
// options and the models), without either importing the other (plans/app-themes §4.3).

/** The warning squiggle's own mark when a theme sets none: Monaco's amber on dark, a deeper amber that reaches 3:1 on light. */
export const WARN_MARK_FALLBACK = { dark: "#cca700", light: "#a87000" } as const;

let semanticOn = false;
let onPaletteChange: () => void = () => {};

/** Whether the palette in use colors CTE names and schema prefixes; semantic tokens are requested only then. */
export const semanticWanted = () => semanticOn;
export function setSemanticWanted(on: boolean) {
  semanticOn = on;
}

/** Runs when the palette changes (the editor layer drops semantic tokens drawn with the previous colors). */
export function setPaletteChangeHook(fn: () => void) {
  onPaletteChange = fn;
}
export const paletteChanged = () => onPaletteChange();

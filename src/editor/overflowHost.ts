// Monaco's fixed overflow widgets (suggest list, hover, parameter hints) are placed in viewport coordinates, but
// Dockview's `contain: layout` makes the panel their containing block, so they landed offset by the explorer width
// and tab strip. Hosting them under <body> keeps them at the cursor; z-index 30 sits above the dock, below modals (z-40).

/** A body-level container for one editor's overflow widgets; `.monaco-editor` scopes the theme variables and widget CSS. */
export function createOverflowHost(doc: Pick<Document, "createElement" | "body">): HTMLDivElement {
  const el = doc.createElement("div");
  el.className = "monaco-editor querithm-overflow-widgets";
  Object.assign(el.style, { position: "absolute", top: "0", left: "0", width: "0", height: "0", zIndex: "30" });
  doc.body.appendChild(el);
  return el;
}

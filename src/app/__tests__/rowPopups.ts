// Popups opened from a bottom-panel row must render outside it: the row is 28 px with overflow hidden (PanelRow), so a
// popup drawn inside shows a sliver or nothing in the app. jsdom does no layout, so this checks where the popup sits;
// the UI smoke's layout audit checks what is painted (popup-clipped).
import { fireEvent } from "@testing-library/react";
import { expect } from "vitest";

const POPUP = '[role="menu"], [role="listbox"], [role="dialog"]';

/** Opens each popup trigger of the panel rows in turn, checks its popup is outside every row and closes it with Escape.
 *  Returns the labels of the triggers it opened. */
export function openRowPopups(): string[] {
  const labels: string[] = [];
  for (const t of document.querySelectorAll<HTMLElement>("[data-q-panel-row] [aria-haspopup]:not([aria-disabled='true'])")) {
    const label = t.getAttribute("aria-label") ?? t.textContent ?? "";
    fireEvent.click(t);
    expect(t.getAttribute("aria-expanded"), `${label} opens`).toBe("true");
    const popups = [...document.querySelectorAll(POPUP)];
    expect(popups.length, `${label} shows a popup`).toBeGreaterThan(0);
    for (const p of popups) expect(p.closest("[data-q-panel-row]"), `${label}: its popup sits inside the panel row, which clips it`).toBeNull();
    fireEvent.keyDown(document.body, { key: "Escape" });
    expect(t.getAttribute("aria-expanded"), `${label} closes on Escape`).toBe("false");
    labels.push(label);
  }
  return labels;
}

import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { isTopModal, pushModal } from "./modalStack";

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"]), [contenteditable="true"]';

/** Controls that Enter activates by themselves: the dialog's Enter shortcut never runs over them (M19). */
const ACTIVATES = [
  "button", "a[href]", "select", "summary",
  ...["checkbox", "radio", "button", "submit", "reset", "file"].map((t) => `input[type="${t}"]`),
  ...["button", "link", "menuitem", "menuitemcheckbox", "menuitemradio", "option", "checkbox", "radio", "switch", "tab"].map((r) => `[role="${r}"]`),
].join(", ");

/** Tab stops inside a dialog, in order; Monaco's hidden input counts as its editor's one stop. */
export function tabStops(root: HTMLElement): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(FOCUSABLE)].filter((el) => !el.closest("[hidden], [inert]") && el.tabIndex >= 0);
}

/**
 * A modal dialog (WAI-ARIA dialog pattern): named by its title, focus moves into it, Tab and Shift+Tab wrap inside it,
 * and closing returns focus to whatever had it before, unless the dialog already moved focus somewhere else.
 * Enter runs `onEnter` unless a button, link, select or option has focus: that control takes Enter itself, so a focused
 * Cancel cancels.
 */
export function Modal({
  title, subtitle, children, footer, onClose, width = 520, danger = false, alert = danger, onEnter, onEscape, bodyClassName = "p-4 overflow-auto",
}: {
  title: ReactNode; subtitle?: ReactNode; children: ReactNode; footer?: ReactNode; onClose: () => void; width?: number; danger?: boolean;
  /**
   * An alert dialog (WAI-ARIA alertdialog; every danger dialog is one): it opens on its `[data-modal-cancel]` control
   * (unless a field took focus first), and Enter confirms only from a text field, never from the dialog itself, so
   * Enter right after it opens never confirms (M19).
   */
  alert?: boolean;
  onEnter?: () => void;
  /** Runs before Escape closes the dialog; return true when it handled the key (cleared a search, cancelled a recording). */
  onEscape?: () => boolean;
  bodyClassName?: string;
}) {
  const token = useRef({}).current;
  const box = useRef<HTMLDivElement>(null);
  const id = useId();
  // Read while rendering: by the time effects run, a child's autoFocus has already moved focus into the dialog.
  const [opener] = useState(() => (typeof document === "undefined" ? null : (document.activeElement as HTMLElement | null)));
  // One listener for the dialog's life that reads the latest handlers: re-subscribing after every render left a gap
  // in which a key ran the previous render's onClose (Escape closing an import that had just started).
  const handlers = useRef({ onClose, onEnter, onEscape, alert });
  useLayoutEffect(() => {
    handlers.current = { onClose, onEnter, onEscape, alert };
  });
  useEffect(() => pushModal(token), [token]);
  useLayoutEffect(() => {
    const el = box.current;
    if (el && !el.contains(document.activeElement)) {
      const cancel = handlers.current.alert ? el.querySelector<HTMLElement>("[data-modal-cancel]:not([disabled])") : null;
      (cancel ?? el).focus({ preventScroll: true });
    }
    return () => {
      const a = document.activeElement;
      if (opener?.isConnected && (!a || a === document.body || el?.contains(a))) opener.focus({ preventScroll: true });
    };
  }, [opener]);
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (!isTopModal(token)) return;
      const { onClose, onEnter, onEscape, alert } = handlers.current;
      const target = e.target as HTMLElement;
      // An open popup inside the dialog (aria-expanded) closes itself first.
      if (e.key === "Escape" && !target.closest?.('[aria-expanded="true"]')) {
        e.preventDefault();
        e.stopPropagation();
        if (!onEscape?.()) onClose();
      } else if (e.key === "Enter" && onEnter && !e.isComposing && !(target instanceof HTMLTextAreaElement) && !target.closest?.(".monaco-editor")) {
        // An IME confirming a composition sends Enter too (isComposing): that Enter is the input method's, never the dialog's.
        const inside = target instanceof Node && !!box.current?.contains(target);
        // Left to the focused control (not prevented): a button's own activation runs, Cancel cancels.
        if (inside && target.closest(ACTIVATES)) return;
        e.preventDefault();
        if (!alert || (inside && target instanceof HTMLInputElement)) onEnter();
      } else if (e.key === "Tab" && !e.ctrlKey && !e.altKey && !e.metaKey && box.current && !target.closest?.(".monaco-editor")) {
        // Tab wraps at the ends; inside an editor Tab indents, and Monaco's own Ctrl+M / F6 lets the user leave.
        const stops = tabStops(box.current);
        if (!stops.length) return;
        const inside = target instanceof Node && box.current.contains(target);
        const i = stops.indexOf(target);
        if (!inside || (e.shiftKey && (i === 0 || target === box.current))) {
          e.preventDefault();
          stops[e.shiftKey ? stops.length - 1 : 0].focus();
        } else if (!e.shiftKey && i === stops.length - 1) {
          e.preventDefault();
          stops[0].focus();
        }
      }
    };
    window.addEventListener("keydown", h, true);
    return () => window.removeEventListener("keydown", h, true);
  }, [token]);
  return (
    <div className="fixed inset-0 z-40 bg-black/50 flex items-center justify-center p-6" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={box}
        role={alert ? "alertdialog" : "dialog"}
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-describedby={subtitle ? `${id}-subtitle` : undefined}
        tabIndex={-1}
        className={`bg-panel rounded-xl shadow-2xl overflow-hidden flex flex-col max-h-full outline-none ${danger ? "border-2 border-err shadow-[0_0_50px_rgba(239,68,68,0.3)]" : "border border-line"}`}
        style={{ width, maxWidth: "100%" }}
      >
        <div className={`px-4 py-3 ${danger ? "bg-err-solid text-white" : "border-b border-line"}`}>
          {/* Shown as written: titles carry identifiers and names ("Rename @OrderId", "Execute dbo.usp_Get"), which a
              text-transform would change. */}
          <div id={`${id}-title`} className={`text-xs font-bold ${danger ? "" : "text-fg"}`} data-modal-title>{title}</div>
          {subtitle && <div id={`${id}-subtitle`} className={`text-[11px] mt-0.5 ${danger ? "text-white" : "text-muted"}`}>{subtitle}</div>}
        </div>
        <div className={`${bodyClassName} text-xs`}>{children}</div>
        {footer && <div className="px-4 py-3 bg-panel-2 border-t border-line flex justify-end gap-2.5">{footer}</div>}
      </div>
    </div>
  );
}

export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-[11px] font-semibold text-muted">{label}</span>
      {children}
      {hint && <span className="block text-[10.5px] text-muted">{hint}</span>}
    </label>
  );
}

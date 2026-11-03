// Drop-down menus of the top bar's split buttons and the Object Explorer's context menus (WAI-ARIA menu): opens under
// its trigger (or at a point), ↑/↓/Home/End move between items, Enter/Space activates, → opens a submenu and ← or Esc
// closes it, Esc or Tab closes the menu and Esc returns focus to the trigger. An unavailable item stays focusable and
// says why instead of only greying out.
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import { useDismiss } from "./dismiss";

export type MenuEntry =
  | {
      label: string;
      run: () => void;
      /** Secondary text after the label ("runs the query"). */
      detail?: string;
      /** Shortcut shown at the right. */
      hint?: string;
      /** Why the item is unavailable; set, the item is shown but does nothing. */
      unavailable?: string;
      /** A checkable item (menuitemcheckbox). */
      checked?: boolean;
      /** The default action (what a double-click or Enter does), drawn bold. */
      bold?: boolean;
      /** Items of a submenu (one level); the item opens it instead of running. */
      sub?: MenuEntry[];
      /** Pointer or focus entering (true) / leaving (false) the item. */
      onHighlight?: (on: boolean) => void;
    }
  | "sep";

const ITEMS = '[role^="menuitem"]';

/** Each open menu box (portaled to the body) and the trigger it belongs to. */
const owners = new WeakMap<Element, RefObject<HTMLElement | null>>();

/** The trigger of the open menu that holds `node`, if any: a panel that closes when focus or a press leaves it (the
 *  Explorer's auto-hide flyout) counts a menu opened from inside it as inside. */
export function menuTriggerOf(node: Node | null): HTMLElement | null {
  const box = node instanceof Element ? node.closest('[role="menu"]') : null;
  return (box && owners.get(box)?.current) ?? null;
}

function MenuItems({ items, onClose, subOpen, onSub, itemRefs }: {
  items: MenuEntry[]; onClose: () => void; subOpen?: number | null; onSub?: (i: number | null) => void; itemRefs?: Record<number, HTMLButtonElement | null>;
}) {
  return items.map((it, i) =>
    it === "sep" ? (
      <div key={i} role="separator" className="my-1 mx-1.5 border-t border-line" />
    ) : (
      <button
        key={i}
        ref={(el) => {
          if (itemRefs) itemRefs[i] = el;
        }}
        type="button"
        tabIndex={-1}
        role={it.checked === undefined ? "menuitem" : "menuitemcheckbox"}
        aria-checked={it.checked}
        aria-disabled={it.unavailable ? true : undefined}
        aria-haspopup={it.sub ? "menu" : undefined}
        aria-expanded={it.sub ? subOpen === i : undefined}
        // The visible parts sit in separate spans, which a computed name would run together ("runCtrl+L").
        aria-label={[it.label, it.detail, it.unavailable].filter(Boolean).join(", ")}
        aria-keyshortcuts={it.hint || undefined}
        className={`q-hl h-[28px] px-2.5 rounded flex items-center gap-2 text-left shrink-0 focus:outline-none ${it.bold ? "font-semibold" : ""} ${it.unavailable ? "text-muted" : "text-fg hover:bg-active focus:bg-active"} ${it.sub && subOpen === i ? "bg-active" : ""}`}
        onPointerEnter={() => {
          it.onHighlight?.(true);
          onSub?.(it.sub && !it.unavailable ? i : null);
        }}
        onPointerLeave={() => it.onHighlight?.(false)}
        onFocus={() => it.onHighlight?.(true)}
        onBlur={() => it.onHighlight?.(false)}
        onClick={() => {
          if (it.unavailable) return;
          if (it.sub) return onSub?.(i);
          onClose();
          it.run();
        }}
      >
        {it.checked !== undefined && (
          <span aria-hidden className={`w-3.5 h-3.5 rounded-[3px] flex items-center justify-center shrink-0 ${it.checked ? "bg-accent-solid text-accent-fg" : `border border-line-strong ${it.unavailable ? "border-dashed" : ""}`}`}>
            {it.checked && (
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round">
                <path d="M20 6L9 17l-5-5" />
              </svg>
            )}
          </span>
        )}
        <span className="flex-1 whitespace-nowrap">
          {it.label}
          {it.detail && <span className="text-muted text-[11px] font-normal"> · {it.detail}</span>}
        </span>
        {it.unavailable ? <span className="text-muted text-[11px] font-normal pl-4 whitespace-nowrap">{it.unavailable}</span> : it.hint && <span className="q-kbd font-normal pl-4 whitespace-nowrap">{it.hint}</span>}
        {it.sub && (
          <svg aria-hidden width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" className="shrink-0 text-muted -rotate-90">
            <path d="M6 9l6 6 6-6" />
          </svg>
        )}
      </button>
    ),
  );
}

/** Moves focus between the items of one menu box with ↑/↓/Home/End; true when the key was handled. */
function moveFocus(box: HTMLElement | null, e: KeyboardEvent): boolean {
  const all = [...(box?.querySelectorAll<HTMLElement>(ITEMS) ?? [])];
  const i = all.indexOf(document.activeElement as HTMLElement);
  const to = (n: number) => {
    e.preventDefault();
    all[(n + all.length) % all.length]?.focus();
    return true;
  };
  if (e.key === "ArrowDown") return to(i + 1);
  if (e.key === "ArrowUp") return to(i - 1);
  if (e.key === "Home") return to(0);
  if (e.key === "End") return to(all.length - 1);
  return false;
}

export function DropMenu({ anchor, at, label, header, items, onClose }: {
  /** The trigger: the menu opens under it, and focus returns to it when the menu is dismissed from the keyboard. */
  anchor: RefObject<HTMLElement | null>;
  /** Opens at this point instead of under the anchor (a context menu), kept inside the window. */
  at?: { x: number; y: number };
  label: string;
  /** A line above the items, not focusable: what a context menu acts on. */
  header?: ReactNode;
  items: MenuEntry[];
  onClose: () => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const subBox = useRef<HTMLDivElement>(null);
  const itemRefs = useRef<Record<number, HTMLButtonElement | null>>({});
  const [pos, setPos] = useState<{ left: number; top: number; maxHeight?: number }>({ left: 0, top: 0 });
  const [sub, setSub] = useState<number | null>(null);
  const [subPos, setSubPos] = useState<{ left: number; top: number }>({ left: 0, top: 0 });
  // A dismissal while focus is in the menu (Esc, a press outside) gives focus back to the trigger; the press that
  // caused it then moves focus on as usual.
  const dismiss = () => {
    const inside = !!box.current?.contains(document.activeElement) || !!subBox.current?.contains(document.activeElement);
    onClose();
    if (inside) anchor.current?.focus();
  };
  const closeSub = (focusParent: boolean) => {
    const parent = sub === null ? null : itemRefs.current[sub];
    setSub(null);
    if (focusParent) parent?.focus();
  };
  // Escape inside an open submenu closes only the submenu.
  useDismiss(true, [box, subBox, anchor], dismiss, () => {
    if (sub === null || !subBox.current?.contains(document.activeElement)) return false;
    closeSub(true);
    return true;
  });

  useLayoutEffect(() => {
    const width = box.current?.offsetWidth ?? 0;
    const height = box.current?.offsetHeight ?? 0;
    const maxHeight = window.innerHeight - 8;
    if (at) {
      setPos({ left: Math.max(4, Math.min(at.x, window.innerWidth - width - 4)), top: Math.max(4, Math.min(at.y, window.innerHeight - Math.min(height, maxHeight) - 4)), maxHeight });
    } else {
      const r = anchor.current?.getBoundingClientRect();
      if (!r) return;
      setPos({ left: Math.max(4, Math.min(r.left, window.innerWidth - width - 4)), top: r.bottom + 4 });
    }
    box.current?.querySelector<HTMLElement>(ITEMS)?.focus();
    // The point's coordinates, not the object: a caller re-rendering with a new {x, y} must not refocus the first item.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [anchor, at?.x, at?.y]);

  useLayoutEffect(() => {
    for (const el of [box.current, subBox.current]) if (el) owners.set(el, anchor);
  }, [anchor, sub]);

  useLayoutEffect(() => {
    if (sub === null) return;
    const r = itemRefs.current[sub]?.getBoundingClientRect();
    const w = subBox.current?.offsetWidth ?? 0;
    const h = subBox.current?.offsetHeight ?? 0;
    if (r) {
      // To the right of the item, flipped to its left when the window ends first.
      const left = r.right + 2 + w > window.innerWidth - 4 ? Math.max(4, r.left - w - 2) : r.right + 2;
      setSubPos({ left, top: Math.max(4, Math.min(r.top - 4, window.innerHeight - h - 4)) });
    }
  }, [sub]);

  // Whatever the pointer or focus was highlighting is released when the menu goes.
  const latest = useRef(items);
  latest.current = items;
  useEffect(() => () => latest.current.forEach((it) => it !== "sep" && it.onHighlight?.(false)), []);

  const openSub = (i: number, focus: boolean) => {
    setSub(i);
    if (focus) requestAnimationFrame(() => subBox.current?.querySelector<HTMLElement>(ITEMS)?.focus());
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if (moveFocus(box.current, e)) return;
    const i = Object.entries(itemRefs.current).find(([, el]) => el === document.activeElement)?.[0];
    const it = i === undefined ? undefined : items[Number(i)];
    if (e.key === "ArrowRight" && it && it !== "sep" && it.sub && !it.unavailable) {
      e.preventDefault();
      openSub(Number(i), true);
    } else if ((e.key === "Enter" || e.key === " ") && it && it !== "sep" && it.sub && !it.unavailable) {
      e.preventDefault();
      openSub(Number(i), true);
    } else if (e.key === "Tab") onClose();
  };
  const onSubKeyDown = (e: KeyboardEvent) => {
    e.stopPropagation();
    if (moveFocus(subBox.current, e)) return;
    if (e.key === "ArrowLeft") {
      e.preventDefault();
      closeSub(true);
    } else if (e.key === "Tab") onClose();
  };

  const subItems = sub === null ? null : (items[sub] as Exclude<MenuEntry, "sep">).sub;
  return createPortal(
    <>
      <div
        ref={box}
        role="menu"
        aria-label={label}
        className="fixed z-50 bg-raised border border-line rounded-lg shadow-2xl p-1 min-w-[260px] text-[12px] flex flex-col overflow-y-auto"
        style={pos}
        onKeyDown={onKeyDown}
        onContextMenu={(e) => e.preventDefault()}
      >
        {header && <div className="h-[26px] px-2.5 flex items-center gap-1.5 text-[11px] text-muted whitespace-nowrap overflow-hidden shrink-0 border-b border-line mb-1">{header}</div>}
        <MenuItems items={items} onClose={onClose} subOpen={sub} onSub={(n) => (n === null ? setSub(null) : openSub(n, false))} itemRefs={itemRefs.current} />
      </div>
      {subItems && (
        <div
          ref={subBox}
          role="menu"
          aria-label={(items[sub!] as Exclude<MenuEntry, "sep">).label}
          className="fixed z-50 bg-raised border border-line rounded-lg shadow-2xl p-1 min-w-[200px] text-[12px] flex flex-col"
          style={subPos}
          onKeyDown={onSubKeyDown}
          onContextMenu={(e) => e.preventDefault()}
        >
          <MenuItems items={subItems} onClose={onClose} />
        </div>
      )}
    </>,
    document.body,
  );
}

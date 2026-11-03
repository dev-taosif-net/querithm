// Building blocks every bottom-panel view shares (§12 bottom panel): one row height, one footer, one way to fold the
// row at narrow widths, segmented controls, menu buttons and the statement chips. Sizes come from here only, so the
// views cannot drift apart again (28/30/32 px rows before the redesign).
import { createContext, useContext, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode, type RefObject } from "react";
import { DropMenu, type MenuEntry } from "./ToolbarMenu";
import { Icon, type IconName } from "./icons";

/** wide ≥ 1100 px: ledger and tools share one row · mid ≥ 760 px: each gets a row · narrow: the ledger folds to one chip. */
export type PanelWidth = "wide" | "mid" | "narrow";
export const PANEL_WIDE = 1100;
export const PANEL_MID = 760;

export const widthBucket = (px: number): PanelWidth => (px >= PANEL_WIDE ? "wide" : px >= PANEL_MID ? "mid" : "narrow");

export const PanelWidthContext = createContext<PanelWidth>("wide");
export const usePanelWidth = () => useContext(PanelWidthContext);

/**
 * The panel's width bucket, measured by a ResizeObserver: it re-renders only when the bucket changes (a resize that
 * stays in the bucket costs nothing), never per frame or per batch. Layout reflows with the same DOM in every bucket,
 * so the Tab order never depends on the width.
 */
export function useWidthBucket(ref: RefObject<HTMLElement | null>): PanelWidth {
  const [bucket, setBucket] = useState<PanelWidth>("wide");
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const measure = (w: number) => w > 0 && setBucket(widthBucket(w));
    measure(el.getBoundingClientRect().width);
    const ro = new ResizeObserver((entries) => measure(entries[0].contentRect.width));
    ro.observe(el);
    return () => ro.disconnect();
  }, [ref]);
  return bucket;
}

/** The ledger + tools row: one 28 px row when wide, two below 1100 px; never wraps (overflow goes into ⋯ menus).
 *  It clips what overflows it, so a popup opened from inside must render outside it (DropMenu, or a portal to the body
 *  with fixed position): `openRowPopups` in the panel tests and the smoke's popup-clipped audit check that. */
export function PanelRow({ ledger, tools, label, fill }: { ledger?: ReactNode; tools?: ReactNode; label: string; fill?: boolean }) {
  const width = usePanelWidth();
  const row = "h-[28px] px-2 flex items-center gap-1.5 border-b border-line shrink-0 min-w-0 overflow-hidden whitespace-nowrap";
  if (width === "mid" && ledger && tools) {
    return (
      <>
        <div className={row} data-q-panel-row="ledger">{ledger}</div>
        <div className={row} role="toolbar" aria-label={label} data-q-panel-row="tools">{fill ? tools : <div className="ml-auto flex items-center gap-1.5 min-w-0">{tools}</div>}</div>
      </>
    );
  }
  return (
    <div className={row} data-q-panel-row="both">
      {ledger}
      {tools && (
        <div className={`${fill ? "flex-1" : "ml-auto"} flex items-center gap-1.5 min-w-0`} role="toolbar" aria-label={label}>
          {tools}
        </div>
      )}
    </div>
  );
}

/** The 22 px footer: the selected item's state on the left, its numbers on the right. */
export function PanelFooter({ left, right }: { left?: ReactNode; right?: ReactNode }) {
  return (
    <div className="h-[22px] px-2 border-t border-line bg-panel flex items-center justify-between gap-3 text-[11px] text-muted shrink-0 whitespace-nowrap overflow-hidden" data-q-panel-footer>
      <div className="flex items-center gap-2 min-w-0 truncate">{left}</div>
      <div className="flex items-center gap-2 min-w-0">{right}</div>
    </div>
  );
}

export interface SegOption<T extends string> {
  value: T;
  label: string;
  icon?: IconName;
  /** Shown but not chosen: why it is unavailable (the option keeps its place). */
  unavailable?: string;
  title?: string;
}

/** A segmented control: a group of toggle buttons, one pressed. Icons only when `compact`, the name stays spoken. */
export function Seg<T extends string>({ label, value, options, onChange, compact }: {
  label: string; value: T; options: SegOption<T>[]; onChange: (v: T) => void; compact?: boolean;
}) {
  return (
    <div role="group" aria-label={label} className="flex items-stretch h-[22px] rounded border border-line-strong overflow-hidden shrink-0">
      {options.map((o, i) => {
        const on = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            aria-pressed={on}
            aria-label={compact ? o.label : undefined}
            title={o.unavailable ?? o.title ?? (compact ? o.label : undefined)}
            className={`px-2 text-[11px] flex items-center gap-1 ${i ? "border-l border-line" : ""} ${on ? (o.unavailable ? "bg-raised text-fg font-semibold" : "bg-accent-solid text-white font-semibold") : o.unavailable ? "text-muted hover:bg-hover" : "text-fg hover:bg-hover"}`}
            onClick={() => !on && onChange(o.value)}
          >
            {o.unavailable ? <Icon name="unavailable" size={12} strokeWidth={2} /> : o.icon && <Icon name={o.icon} size={12} strokeWidth={2} />}
            {!compact && o.label}
          </button>
        );
      })}
    </div>
  );
}

/** A button that opens a menu under itself. */
export function MenuButton({ label, title, icon, items, className = "", children, disabled }: {
  label: string; title?: string; icon?: IconName; items: () => MenuEntry[]; className?: string; children?: ReactNode; disabled?: string;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLButtonElement>(null);
  return (
    <>
      <button
        ref={ref}
        type="button"
        className={`q-btn h-[22px] px-2 shrink-0 ${className}`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={children ? undefined : label}
        aria-disabled={disabled ? true : undefined}
        title={disabled ?? title ?? label}
        onClick={() => !disabled && setOpen((o) => !o)}
      >
        {icon && <Icon name={icon} size={13} strokeWidth={2} />}
        {children}
        {children && <Icon name="chevron" size={11} strokeWidth={2} />}
      </button>
      {open && <DropMenu anchor={ref} label={label} items={items()} onClose={() => setOpen(false)} />}
    </>
  );
}

/** A face button and its ▾ menu as two real buttons (Copy ▾, Copy all ▾, Copy XML ▾). */
export function SplitButton({ face, onFace, title, icon, menuLabel, items, compact }: {
  face: string; onFace: () => void; title?: string; icon?: IconName; menuLabel: string; items: () => MenuEntry[]; compact?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const caret = useRef<HTMLButtonElement>(null);
  return (
    <div className="flex items-stretch shrink-0">
      <button type="button" className="q-btn h-[22px] px-2 rounded-r-none" title={title ?? face} aria-label={compact ? face : undefined} onClick={onFace}>
        {icon && <Icon name={icon} size={13} strokeWidth={2} />}
        {!compact && face}
      </button>
      <button
        ref={caret}
        type="button"
        className="q-btn h-[22px] !px-1 rounded-l-none border-l-0"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={menuLabel}
        title={menuLabel}
        onClick={() => setOpen((o) => !o)}
      >
        <Icon name="chevron" size={11} strokeWidth={2} />
      </button>
      {open && <DropMenu anchor={caret} label={menuLabel} items={items()} onClose={() => setOpen(false)} />}
    </div>
  );
}

/** A 24 px icon button in the panel's chrome (header actions, edit undo/redo). */
export function PanelIconButton({ icon, label, onClick, pressed, disabled }: {
  icon: IconName; label: string; onClick: () => void; pressed?: boolean; disabled?: string;
}) {
  return (
    <button
      type="button"
      className="w-6 h-6 rounded flex items-center justify-center text-muted hover:text-fg hover:bg-hover shrink-0"
      aria-label={label}
      aria-pressed={pressed}
      aria-disabled={disabled ? true : undefined}
      title={disabled ?? label}
      onClick={() => !disabled && onClick()}
    >
      <Icon name={icon} size={14} strokeWidth={2} />
    </button>
  );
}

/** One ledger chip as drawn: number, glyph, text, muted detail and an optional signed change. */
export interface ChipModel {
  key: string;
  n: string;
  icon: IconName;
  text: string;
  meta?: string;
  /** "+4" / "−3": the change since the previous run; the sign carries it, not the color. */
  delta?: string;
  error?: boolean;
  /** The chip's spoken name ("Result set 1, 1,234 rows"). */
  aria: string;
  title?: string;
}

/**
 * The statement ledger (§12 bottom panel): a tablist of chips, one Tab stop, ←/→/Home/End move and select. Ctrl+click
 * (or the chip's menu: Shift+F10, the context-menu key or a right-click) offers the chip's extra actions such as
 * Compare. Below 760 px it folds into one chip that opens the list, with the first error chip pinned beside it so an
 * error never leaves the screen. While `locked` (staged edits) the chips say why and do nothing.
 */
export function Chips({ label, chips, selected, compared, onSelect, menuFor, locked }: {
  label: string;
  chips: ChipModel[];
  selected?: string;
  /** The chip shown in the second pane of a comparison. */
  compared?: string;
  onSelect: (key: string, e: { ctrl: boolean }) => void;
  menuFor?: (key: string) => MenuEntry[];
  locked?: string;
}) {
  const width = usePanelWidth();
  const [menu, setMenu] = useState<{ key: string; at?: { x: number; y: number } } | null>(null);
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});
  const listBtn = useRef<HTMLButtonElement>(null);
  const [listOpen, setListOpen] = useState(false);
  if (!chips.length) return null;
  const cur = chips.find((c) => c.key === selected) ?? chips[0];

  if (locked) {
    return (
      <span className="flex items-center gap-1.5 h-[22px] px-2 rounded border border-line text-[11.5px] text-muted shrink-0" title={locked} data-q-ledger-locked>
        <Icon name="lock" size={12} strokeWidth={2} />
        {cur.text}
      </span>
    );
  }

  if (width === "narrow") {
    const firstError = chips.find((c) => c.error && c.key !== cur.key);
    return (
      <div className="flex items-center gap-1 min-w-0" data-q-ledger>
        <button
          ref={listBtn}
          type="button"
          className="flex items-center gap-1.5 h-[22px] pl-1 pr-1.5 rounded border border-accent bg-row-sel text-[11.5px] font-semibold text-fg min-w-0"
          aria-haspopup="menu"
          aria-expanded={listOpen}
          aria-label={`${label}: ${cur.aria}. ${chips.length} in all`}
          onClick={() => setListOpen((o) => !o)}
        >
          <ChipBadge chip={cur} on />
          <span className="truncate">{chips.length > 1 ? `${chips.indexOf(cur) + 1} of ${chips.length} · ` : ""}{cur.text}</span>
          <Icon name="chevron" size={11} strokeWidth={2} />
        </button>
        {firstError && (
          <button type="button" className="flex items-center gap-1 h-[22px] px-1.5 rounded border border-err/60 text-err text-[11.5px] shrink-0" aria-label={firstError.aria} title={firstError.title ?? firstError.aria} onClick={() => onSelect(firstError.key, { ctrl: false })}>
            <Icon name="errorCircle" size={12} strokeWidth={2} />
            {chips.filter((c) => c.error).length}
          </button>
        )}
        {listOpen && (
          <DropMenu
            anchor={listBtn}
            label={label}
            items={chips.map((c) => ({ label: `${c.n} · ${c.text}`, detail: c.meta, checked: c.key === cur.key, run: () => onSelect(c.key, { ctrl: false }) }))}
            onClose={() => setListOpen(false)}
          />
        )}
      </div>
    );
  }

  const move = (e: KeyboardEvent, i: number) => {
    const to = e.key === "ArrowRight" ? i + 1 : e.key === "ArrowLeft" ? i - 1 : e.key === "Home" ? 0 : e.key === "End" ? chips.length - 1 : null;
    if (e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey)) {
      e.preventDefault();
      if (menuFor) setMenu({ key: chips[i].key });
      return;
    }
    if (to == null) return;
    e.preventDefault();
    const c = chips[Math.max(0, Math.min(chips.length - 1, to))];
    refs.current[c.key]?.focus();
    onSelect(c.key, { ctrl: false });
  };
  const click = (e: MouseEvent, key: string) => onSelect(key, { ctrl: e.ctrlKey || e.metaKey });

  return (
    <div role="tablist" aria-label={label} className="flex items-center gap-1 min-w-0 overflow-hidden" data-q-ledger>
      {chips.map((c, i) => {
        const on = c.key === cur.key;
        const cmp = c.key === compared;
        return (
          <button
            key={c.key}
            ref={(el) => void (refs.current[c.key] = el)}
            type="button"
            role="tab"
            aria-selected={on}
            tabIndex={on ? 0 : -1}
            aria-label={c.aria + (cmp ? ", compared" : "")}
            title={c.title}
            data-q-chip={c.key}
            className={`flex items-center gap-1.5 h-[22px] pl-0.5 pr-2 rounded border text-[11.5px] shrink-0 ${cmp ? "border-dashed" : ""} ${
              c.error ? (on ? "border-err bg-err/10 text-err font-semibold q-hl" : "border-err/60 text-err hover:bg-err/10 q-hl") : on ? "border-accent bg-row-sel text-fg font-semibold" : cmp ? "border-accent text-fg hover:bg-hover" : "border-line text-fg hover:bg-hover"
            }`}
            onClick={(e) => click(e, c.key)}
            onKeyDown={(e) => move(e, i)}
            onContextMenu={(e) => {
              if (!menuFor) return;
              e.preventDefault();
              setMenu({ key: c.key, at: { x: e.clientX, y: e.clientY } });
            }}
          >
            <ChipBadge chip={c} on={on} />
            <Icon name={c.icon} size={12} strokeWidth={2} />
            <span>{c.text}</span>
            {c.meta && <span className="text-muted font-normal">{c.meta}</span>}
            {c.delta && <span className="text-muted font-normal q-mono text-[10.5px]" title="Change since the previous run of the same SQL">{c.delta}</span>}
          </button>
        );
      })}
      {menu && menuFor && (
        <DropMenu
          anchor={{ current: refs.current[menu.key] ?? null }}
          at={menu.at}
          label={`${chips.find((c) => c.key === menu.key)?.aria ?? ""}: actions`}
          items={menuFor(menu.key)}
          onClose={() => setMenu(null)}
        />
      )}
    </div>
  );
}

function ChipBadge({ chip, on }: { chip: ChipModel; on: boolean }) {
  return (
    <span
      aria-hidden
      className={`min-w-[16px] h-[16px] px-[3px] rounded-[3px] flex items-center justify-center q-mono text-[10px] font-semibold ${
        chip.error ? "bg-err/15 text-err" : on ? "bg-accent-solid text-white" : "bg-raised text-muted"
      }`}
    >
      {chip.n}
    </span>
  );
}

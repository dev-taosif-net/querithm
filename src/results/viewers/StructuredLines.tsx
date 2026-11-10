// Formatted JSON / XML lines (Phase C2): a windowed list (only the lines in view are in the DOM, re-rendered once per
// block of 16 lines) with fold toggles and a case-insensitive search. Every line is a React text node: values are never
// HTML.
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { foldedText, findMatches, lineCount, lineText, reveal, visibleLines, type StructuredView } from "./lines";

const LINE_PX = 18;
const BLOCK = 16;
/** Characters of one line drawn; longer lines end in "…" (Copy copies the whole value). */
const MAX_LINE_CHARS = 4000;

function Highlighted({ text, needle, current }: { text: string; needle: string; current: boolean }) {
  if (!needle) return <>{text}</>;
  const lower = text.toLowerCase();
  const parts: React.ReactNode[] = [];
  let from = 0;
  for (let at = lower.indexOf(needle); at >= 0; at = lower.indexOf(needle, at + needle.length)) {
    if (at > from) parts.push(text.slice(from, at));
    parts.push(
      <mark key={at} className={`rounded-sm text-fg ${current ? "bg-warn/45" : "bg-warn/25"}`}>
        {text.slice(at, at + needle.length)}
      </mark>,
    );
    from = at + needle.length;
  }
  if (from < text.length) parts.push(text.slice(from));
  return <>{parts}</>;
}

export function StructuredLines({ view, label }: { view: StructuredView; label: string }) {
  const [collapsed, setCollapsed] = useState<ReadonlySet<number>>(() => new Set());
  const [query, setQuery] = useState("");
  const [hit, setHit] = useState(0);
  const [block, setBlock] = useState(0);
  const [height, setHeight] = useState(400);
  const box = useRef<HTMLDivElement>(null);
  const pendingLine = useRef<number | null>(null);

  // A new view starts unfolded at the top. Reset during render, not in a passive effect: a mount effect that runs late
  // (a busy main thread) would undo a fold the user made right after the view appeared.
  const [shownView, setShownView] = useState(view);
  if (shownView !== view) {
    setShownView(view);
    setCollapsed(new Set());
    setBlock(0);
  }
  useEffect(() => {
    if (box.current) box.current.scrollTop = 0;
  }, [view]);

  const visible = useMemo(() => visibleLines(view, collapsed), [view, collapsed]);
  const needle = query.toLowerCase();
  const matches = useMemo(() => findMatches(view, query), [view, query]);
  const current = matches.lines.length ? matches.lines[Math.min(hit, matches.lines.length - 1)] : -1;

  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    setHeight(el.clientHeight || 400);
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => setHeight(el.clientHeight || 400));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // A search hit (or a reveal) scrolls once the folds hiding it are open and the visible list is rebuilt.
  useLayoutEffect(() => {
    const line = pendingLine.current;
    const el = box.current;
    if (line == null || !el) return;
    pendingLine.current = null;
    let lo = 0, hi = visible.length - 1;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (visible[mid] < line) lo = mid + 1;
      else hi = mid;
    }
    const top = lo * LINE_PX;
    if (top < el.scrollTop || top > el.scrollTop + el.clientHeight - LINE_PX * 2) el.scrollTop = Math.max(0, top - el.clientHeight / 3);
    setBlock(Math.floor(el.scrollTop / LINE_PX / BLOCK));
  }, [visible]);

  // Typing a search shows its first hit.
  useEffect(() => {
    if (!matches.lines.length) return;
    pendingLine.current = matches.lines[0];
    setCollapsed((c) => {
      const next = reveal(view, c, matches.lines[0]);
      return next !== c ? next : new Set(c);
    });
  }, [matches, view]);

  const goTo = (k: number) => {
    if (!matches.lines.length) return;
    const i = ((k % matches.lines.length) + matches.lines.length) % matches.lines.length;
    setHit(i);
    pendingLine.current = matches.lines[i];
    const next = reveal(view, collapsed, matches.lines[i]);
    if (next !== collapsed) setCollapsed(next);
    else setCollapsed(new Set(collapsed)); // rebuild so the scroll effect runs
  };

  const toggle = (line: number) => {
    const next = new Set(collapsed);
    if (next.has(line)) next.delete(line);
    else next.add(line);
    setCollapsed(next);
  };

  const foldAll = (fold: boolean) => {
    if (!fold) return setCollapsed(new Set());
    const all = new Set<number>();
    // Fold the second level down so the outline stays readable (the root stays open).
    for (let i = 0; i < lineCount(view); i++) if (view.foldEnd[i] > i && view.depth[i] === 1) all.add(i);
    setCollapsed(all);
  };

  const rows = Math.ceil(height / LINE_PX);
  const first = Math.max(0, (block - 1) * BLOCK);
  const last = Math.min(visible.length, (block + 1) * BLOCK + rows + BLOCK);
  const digits = String(lineCount(view)).length;

  return (
    <div className="flex-1 flex flex-col min-h-0">
      <div className="flex items-center gap-1.5 px-2 py-1 border-b border-line shrink-0" role="search">
        <input
          className="q-input h-[22px] flex-1 min-w-0"
          placeholder={`Search ${label}…`}
          aria-label={`Search ${label}`}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setHit(0);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              goTo(e.shiftKey ? hit - 1 : hit + 1);
            } else if (e.key === "Escape" && query) {
              e.stopPropagation();
              setQuery("");
            }
          }}
        />
        <span className="text-[11px] text-muted whitespace-nowrap" aria-live="polite">
          {query ? (matches.total ? `${Math.min(hit, matches.lines.length - 1) + 1} of ${matches.lines.length} lines · ${matches.total} hits` : "No match") : `${lineCount(view).toLocaleString()} lines`}
        </span>
        <button className="q-btn h-[22px] px-1.5" disabled={!matches.lines.length} onClick={() => goTo(hit - 1)} title="Previous match (Shift+Enter)" aria-label="Previous match">↑</button>
        <button className="q-btn h-[22px] px-1.5" disabled={!matches.lines.length} onClick={() => goTo(hit + 1)} title="Next match (Enter)" aria-label="Next match">↓</button>
        <button className="q-btn h-[22px] px-1.5" onClick={() => foldAll(collapsed.size === 0)} title={collapsed.size ? "Unfold all" : "Fold the second level"}>
          {collapsed.size ? "Unfold" : "Fold"}
        </button>
      </div>
      <div
        ref={box}
        className="flex-1 min-h-0 overflow-auto q-mono text-[11.5px] leading-[18px] text-fg relative focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        tabIndex={0}
        role="region"
        aria-label={`Formatted ${label}`}
        onScroll={(e) => {
          const b = Math.floor(e.currentTarget.scrollTop / LINE_PX / BLOCK);
          if (b !== block) setBlock(b);
        }}
      >
        <div style={{ height: visible.length * LINE_PX, position: "relative", minWidth: "100%" }}>
          <div style={{ position: "absolute", top: first * LINE_PX, left: 0, right: 0 }}>
            {Array.from(visible.subarray(first, last), (line) => {
              const foldable = view.foldEnd[line] > line;
              const folded = foldable && collapsed.has(line);
              let text = folded ? foldedText(view, line) : lineText(view, line);
              const cut = text.length > MAX_LINE_CHARS;
              if (cut) text = `${text.slice(0, MAX_LINE_CHARS)}…`;
              return (
                <div key={line} className={`flex whitespace-pre h-[18px] ${line === current ? "bg-hover" : ""}`} data-line={line} title={cut ? "Line shortened here; Copy copies the whole value" : undefined}>
                  <span className="text-muted select-none text-right pr-1.5 shrink-0" style={{ width: `${digits + 1.5}ch` }}>{line + 1}</span>
                  <span className="w-4 shrink-0 select-none">
                    {foldable && (
                      <button
                        tabIndex={-1}
                        className="w-4 h-[18px] text-muted hover:text-fg leading-none"
                        aria-expanded={!folded}
                        aria-label={folded ? `Unfold line ${line + 1}` : `Fold line ${line + 1}`}
                        onClick={() => toggle(line)}
                      >
                        {folded ? "▸" : "▾"}
                      </button>
                    )}
                  </span>
                  <span className="pr-3">
                    <Highlighted text={text} needle={needle} current={line === current} />
                  </span>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}

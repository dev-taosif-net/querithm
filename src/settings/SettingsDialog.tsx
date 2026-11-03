// Settings (settings redesign 2026-10-30, ARCHITECTURE §12): a modal with a page rail on the left (vertical tablist,
// selection follows focus) and one search box over every page. Changes apply at once and are saved in the background;
// the footer says whether they were saved. Loaded on first open.
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Modal } from "../app/Modal";
import { Icon, type IconName } from "../app/icons";
import { retrySettingsSave, useSettingsStatus } from "../store/settings";
import { pages, searchPages, searchSettings, type PageId, type SettingDef } from "./registry";
import { controlId, GroupedRows, SettingsPage } from "./SettingRow";
import { KeyboardPage, searchCommands } from "./KeyboardPage";
import { AboutPage, useAppInfo } from "./AboutPage";

const ICONS: Record<PageId, IconName> = {
  appearance: "appearance",
  editor: "code",
  formatting: "format",
  execution: "play",
  tabs: "tabs",
  explorer: "database",
  keyboard: "keyboard",
  about: "info",
};

/** Reopening Settings shows the page used last in this session. */
let lastPage: PageId = "appearance";

interface Results {
  rows: SettingDef[];
  commands: number;
  pageHits: PageId[];
  count: Partial<Record<PageId, number>>;
  total: number;
}

function search(query: string): Results {
  const rows = searchSettings(query);
  const commands = searchCommands(query).length;
  const pageHits = searchPages(query).map((p) => p.id);
  const count: Partial<Record<PageId, number>> = {};
  for (const r of rows) count[r.page] = (count[r.page] ?? 0) + 1;
  if (commands) count.keyboard = (count.keyboard ?? 0) + commands;
  for (const p of pageHits) count[p] ??= 0;
  return { rows, commands, pageHits, count, total: rows.length + commands };
}

function SaveStatus() {
  const { save, error, loadFailed } = useSettingsStatus();
  if (save === "failed")
    return (
      <span className="mr-auto inline-flex items-center gap-2 text-[11px] text-err min-w-0">
        <Icon name="alert" size={12} />
        <span className="truncate" title={error}>
          Not saved{error ? `: ${error}` : ""}
        </span>
        <button type="button" className="q-btn" onClick={() => void retrySettingsSave()}>
          Retry
        </button>
      </span>
    );
  return (
    <span className="mr-auto text-[11px] text-muted">
      {save === "pending" || save === "saving"
        ? "Saving…"
        : loadFailed
          ? "Saved settings could not be read; changes apply now and are saved once they can be."
          : "Changes apply at once and are saved automatically."}
    </span>
  );
}

export function SettingsDialog({ close, page: initialPage, focus }: { close: () => void; page?: PageId; focus?: string }) {
  const [page, setPage] = useState<PageId>(initialPage ?? lastPage);
  const [query, setQuery] = useState("");
  const [keysQuery, setKeysQuery] = useState("");
  const [status, setStatus] = useState("");
  const info = useAppInfo();
  const base = useId();
  const tabId = (p: PageId) => `${base}-tab-${p}`;
  const panelId = `${base}-panel`;
  const searchBox = useRef<HTMLInputElement>(null);
  const searching = query.trim().length > 0;
  const results = useMemo(() => (searching ? search(query) : null), [query, searching]);

  useEffect(() => {
    lastPage = page;
  }, [page]);

  // Opened at a page (About, Keyboard shortcuts): focus that page's tab, or the named control; otherwise the search box.
  useEffect(() => {
    const t = requestAnimationFrame(() => {
      if (focus) document.getElementById(focus.startsWith("setting-") ? focus : controlId(focus as SettingDef["id"]))?.focus();
      else if (initialPage) document.getElementById(tabId(initialPage))?.focus();
      else searchBox.current?.focus();
    });
    return () => cancelAnimationFrame(t);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Search results are announced once typing pauses (WCAG 4.1.3), never per keystroke.
  useEffect(() => {
    if (!results) return;
    const t = setTimeout(() => {
      const pagesHit = Object.keys(results.count).length;
      setStatus(
        results.total || pagesHit
          ? `${results.total} ${results.total === 1 ? "result" : "results"} on ${pagesHit} ${pagesHit === 1 ? "page" : "pages"}.`
          : `No settings match “${query.trim()}”.`,
      );
    }, 500);
    return () => clearTimeout(t);
  }, [results, query]);

  // A status message is re-announced even when the same text repeats.
  const announce = (text: string) => {
    setStatus("");
    requestAnimationFrame(() => setStatus(text));
  };

  const open = (p: PageId, opts: { focusTab?: boolean } = {}) => {
    setPage(p);
    if (p === "keyboard" && searching) setKeysQuery(query);
    setQuery("");
    if (opts.focusTab) requestAnimationFrame(() => document.getElementById(tabId(p))?.focus());
  };

  const railKeys = (e: KeyboardEvent) => {
    const i = pages.findIndex((p) => p.id === page);
    const to = e.key === "ArrowDown" ? i + 1 : e.key === "ArrowUp" ? i - 1 : e.key === "Home" ? 0 : e.key === "End" ? pages.length - 1 : null;
    if (to === null) return;
    e.preventDefault();
    open(pages[(to + pages.length) % pages.length].id, { focusTab: true });
  };

  // Escape clears a search box that has text before it closes the dialog.
  const onEscape = () => {
    const a = document.activeElement as HTMLInputElement | null;
    if (a?.dataset.search === "main" && query) {
      setQuery("");
      return true;
    }
    if (a?.dataset.search === "keys" && keysQuery) {
      setKeysQuery("");
      return true;
    }
    return false;
  };

  const version = info && info !== "unavailable" ? info.version : null;

  return (
    <Modal
      title="Settings"
      onClose={close}
      onEscape={onEscape}
      width={880}
      bodyClassName="flex min-h-0 h-[min(560px,calc(100vh-150px))]"
      footer={
        <>
          <SaveStatus />
          <button type="button" className="q-btn q-btn-primary" onClick={close}>
            Done
          </button>
        </>
      }
    >
      <div className="w-[200px] shrink-0 bg-panel-2 border-r border-line flex flex-col min-h-0">
        <div className="p-2.5 pb-2 relative">
          <Icon name="search" size={12} className="absolute left-[18px] top-1/2 -translate-y-[40%] text-muted pointer-events-none" />
          <input
            ref={searchBox}
            type="search"
            data-search="main"
            aria-label="Search settings"
            aria-controls={panelId}
            placeholder="Search settings"
            className="q-input h-7 w-full pl-7"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>
        <div role="tablist" aria-orientation="vertical" aria-label="Settings pages" className="flex-1 min-h-0 overflow-y-auto px-1.5 pb-2" onKeyDown={railKeys}>
          {pages.map((p) => {
            const sel = !searching && p.id === page;
            const n = results?.count[p.id];
            return (
              <div key={p.id} role="none">
                {(p.id === "keyboard" || p.id === "about") && <div role="none" className="my-1.5 mx-2 border-t border-line" />}
                <button
                  id={tabId(p.id)}
                  type="button"
                  role="tab"
                  aria-selected={sel}
                  aria-controls={searching ? undefined : panelId}
                  tabIndex={p.id === page ? 0 : -1}
                  onClick={() => open(p.id)}
                  className={`w-full h-7 px-2 flex items-center gap-2 rounded text-[12px] font-medium text-left text-fg ${sel ? "bg-row-sel q-hl shadow-[inset_2px_0_0_var(--q-accent)]" : "hover:bg-hover"}`}
                >
                  <Icon name={ICONS[p.id]} size={14} className="text-muted" />
                  <span className="truncate">{p.title}</span>
                  {searching && n !== undefined && (
                    <span className="ml-auto text-[10.5px] text-muted tabular-nums" aria-label={n === 1 ? "1 result" : `${n} results`}>
                      {n || "·"}
                    </span>
                  )}
                </button>
              </div>
            );
          })}
        </div>
        <button type="button" className="mx-2 mb-2 px-2 h-6 text-left text-[11px] text-muted hover:text-fg rounded" onClick={() => open("about")}>
          {version ? `Querithm ${version}` : "About Querithm"}
        </button>
      </div>
      <div
        id={panelId}
        role={searching ? "region" : "tabpanel"}
        aria-labelledby={searching ? undefined : tabId(page)}
        aria-label={searching ? `Search results for ${query.trim()}` : undefined}
        tabIndex={-1}
        className="flex-1 min-w-0 overflow-y-auto px-6 py-5 bg-panel outline-none"
      >
        {results ? (
          <SearchResults results={results} query={query.trim()} open={open} announce={announce} />
        ) : page === "keyboard" ? (
          <KeyboardPage query={keysQuery} setQuery={setKeysQuery} announce={announce} />
        ) : page === "about" ? (
          <AboutPage announce={announce} />
        ) : (
          <SettingsPage page={page} announce={announce} />
        )}
      </div>
      <div role="status" className="sr-only">
        {status}
      </div>
    </Modal>
  );
}

function SearchResults({ results, query, open, announce }: { results: Results; query: string; open: (p: PageId) => void; announce: (t: string) => void }) {
  const hitPages = pages.filter((p) => results.count[p.id] !== undefined);
  if (!hitPages.length)
    return (
      <div className="py-10 text-center text-[12px] text-muted">
        No settings match “{query}”.
      </div>
    );
  return (
    <>
      {hitPages.map((p) => {
        const rows = results.rows.filter((r) => r.page === p.id);
        return (
          <section key={p.id} className="mb-6" aria-label={p.title}>
            <div className="flex items-center gap-2 mb-1">
              <h2 className="text-[13px] font-semibold text-fg">{p.title}</h2>
              <button type="button" className="ml-auto q-btn q-btn-ghost text-muted hover:text-fg" onClick={() => open(p.id)}>
                {p.id === "keyboard" && results.commands ? `Show ${results.commands} matching ${results.commands === 1 ? "shortcut" : "shortcuts"}` : `Open ${p.title}`}
              </button>
            </div>
            {rows.length > 0 && <GroupedRows defs={rows} announce={announce} />}
          </section>
        );
      })}
    </>
  );
}

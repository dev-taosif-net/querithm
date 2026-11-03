// Go Back / Go Forward history (VS Code's navigation history), kept pure so it is testable.
// An entry is a tab and, for query tabs, a cursor position. Switching tabs adds an entry; inside a tab,
// a cursor move of more than JUMP_LINES lines adds one, smaller moves update the current entry in place.

export interface NavEntry {
  tabId: string;
  line?: number;
  column?: number;
}

export interface NavStack {
  entries: NavEntry[];
  /** The entry the user is at; -1 when empty. */
  index: number;
}

export const JUMP_LINES = 10;
export const MAX_ENTRIES = 50;

export const emptyNav = (): NavStack => ({ entries: [], index: -1 });

export const canGoBack = (s: NavStack) => s.index > 0;
export const canGoForward = (s: NavStack) => s.index < s.entries.length - 1;

/** `s` with `e` recorded: a new entry (dropping the forward entries) or the current one updated. */
export function recordNav(s: NavStack, e: NavEntry): NavStack {
  const cur = s.entries[s.index];
  if (cur && cur.tabId === e.tabId) {
    if (e.line === undefined) return s;
    if (cur.line === undefined || Math.abs(e.line - cur.line) <= JUMP_LINES) {
      if (cur.line === e.line && cur.column === e.column) return s;
      const entries = s.entries.slice();
      entries[s.index] = e;
      return { entries, index: s.index };
    }
  }
  const entries = [...s.entries.slice(0, s.index + 1), e].slice(-MAX_ENTRIES);
  return { entries, index: entries.length - 1 };
}

/** `s` without the entries of a closed tab; entries left side by side at the same place merge. */
export function forgetNav(s: NavStack, tabId: string): NavStack {
  if (!s.entries.some((e) => e.tabId === tabId)) return s;
  const entries: NavEntry[] = [];
  let index = -1;
  s.entries.forEach((e, i) => {
    if (e.tabId !== tabId) {
      const prev = entries[entries.length - 1];
      if (!prev || prev.tabId !== e.tabId || prev.line !== e.line) entries.push(e);
      else if (e.line !== undefined) entries[entries.length - 1] = e;
    }
    if (i === s.index) index = entries.length - 1;
  });
  return { entries, index: entries.length ? Math.max(0, index) : -1 };
}

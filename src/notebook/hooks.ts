// What the app shell asks of a notebook tab (Phase C3), filled in by src/notebook/runtime.ts. A plain object, so the
// shell's modules (actions, tab actions, persistence) depend on no notebook code and no import cycle forms.
export const notebookHooks: {
  /** F5 / Ctrl+Enter / the top bar's Run: the focused cell, else every cell. */
  run?: (tabId: string) => Promise<void>;
  /** Cancel: the running cell and the queue behind it. */
  cancel?: (tabId: string) => void;
  /** The cells' text (open-tabs search, "is this tab empty?"). */
  text?: (tabId: string) => string;
  /** The notebook as a .sqlbook document (closed-tab snapshots). */
  serialize?: (tabId: string) => string;
  /** The tab is going away: its cells' models, executions and autosave copy go too. */
  dispose?: (tabId: string) => void;
  /** The database the cells ran on is gone (an in-memory one, lost in an engine restart): their results go, `note` says why. */
  outputsLost?: (tabId: string, note: string) => void;
  /** Reopens a closed notebook from its document; undefined when the document cannot be read. */
  reopen?: (o: { text: string; title: string; filePath?: string; profileId: string; database?: string; dirty?: boolean; activate: boolean; atEnd: boolean }) => string | undefined;
} = {};

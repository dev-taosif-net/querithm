// Snippet library (Phase A3): built-in and user snippets from the sidecar (store.db), cached for completion.
import { create } from "zustand";
import type { Dialect, Snippet } from "../ipc/contract";
import { RpcError, rpc } from "../ipc/client";

/** A refused save names the field the sidecar objected to, so the editor can show it inline. */
export type SaveResult = { ok: true; snippet: Snippet } | { ok: false; field?: string; message: string };

/** After a failed load, unforced loads (completion asks on every keystroke) wait this long before trying again. */
export const SNIPPET_RETRY_MS = 30_000;

interface SnippetState {
  snippets: Snippet[];
  loaded: boolean;
  error?: string;
  /** When the last load failed (Date.now()); cleared by a successful load. */
  failedAt?: number;
  /** Loads once; `force` reloads. Never throws: a failure is kept in `error`, and unforced retries back off. */
  load(force?: boolean): Promise<void>;
  save(s: Snippet): Promise<SaveResult>;
  remove(id: string): Promise<{ ok: boolean; message?: string }>;
}

let inflight: Promise<void> | null = null;

export const useSnippets = create<SnippetState>((set, get) => ({
  snippets: [],
  loaded: false,
  async load(force = false) {
    const { loaded, failedAt } = get();
    if (!force && (loaded || (failedAt !== undefined && Date.now() - failedAt < SNIPPET_RETRY_MS))) return;
    // Through a promise, so even a synchronous failure of the call lands in `error`.
    inflight ??= new Promise<Snippet[]>((resolve) => resolve(rpc("snippet.list")))
      .then((snippets) => set({ snippets: snippets ?? [], loaded: true, error: undefined, failedAt: undefined }))
      .catch((e) => set({ error: e instanceof Error ? e.message : String(e), failedAt: Date.now() }))
      .finally(() => (inflight = null));
    return inflight;
  },
  async save(s) {
    try {
      const snippet = await rpc("snippet.save", { snippet: s });
      set((st) => ({ snippets: [...st.snippets.filter((x) => x.id !== snippet.id), snippet] }));
      return { ok: true, snippet };
    } catch (e) {
      const field = e instanceof RpcError ? (e.data as { field?: string } | undefined)?.field : undefined;
      return { ok: false, field, message: e instanceof Error ? e.message : String(e) };
    }
  },
  async remove(id) {
    try {
      await rpc("snippet.delete", { id });
      set((st) => ({ snippets: st.snippets.filter((x) => x.id !== id) }));
      return { ok: true };
    } catch (e) {
      return { ok: false, message: e instanceof Error ? e.message : String(e) };
    }
  },
}));

/** What a tab of this dialect offers: snippets of that dialect and those for every dialect. */
export function snippetsFor(list: Snippet[], dialect: Dialect): Snippet[] {
  return list.filter((s) => s.dialect == null || s.dialect === dialect);
}

/** A new, unsaved snippet (the sidecar assigns the id). */
export function blankSnippet(over: Partial<Snippet> = {}): Snippet {
  return { id: "", title: "", prefix: "", body: "", dialect: null, tags: [], updatedAt: 0, builtIn: false, ...over };
}

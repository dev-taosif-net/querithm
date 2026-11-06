import { BusyButtonContent } from "../app/LoadingStatus";
// Snippet library UI (Phase A3): the list with search and the editor modal. The body is edited in the shared Monaco
// editor (borrowed with a temporary model), never a second instance (§12).
import { useEffect, useMemo, useRef, useState } from "react";
import type { Dialect, Snippet } from "../ipc/contract";
import { Field, Modal } from "../app/Modal";
import { ask, useUi, toast } from "../store/ui";
import { borrowEditor } from "../editor/editors";
import { DIALECT_LIST, DIALECTS, isDialect, knownDialect } from "../engines";
import { blankSnippet, snippetsFor, useSnippets } from "./snippetStore";
import { activeDialect, insertSnippet } from "./snippetActions";

const dialectLabel = (d?: Dialect | null) => (isDialect(d) ? DIALECTS[d].label : "Any");
const dialectOptions = DIALECT_LIST.map((d) => <option key={d} value={d}>{DIALECTS[d].label}</option>);

export function SnippetsPanel({ close }: { close: () => void }) {
  const { snippets, loaded, error } = useSnippets();
  const [q, setQ] = useState("");
  const [dialect, setDialect] = useState<Dialect | "all">(() => activeDialect());
  const [selected, setSelected] = useState<string | null>(null);
  // Opening the panel retries a failed load at once (completion's own retries back off).
  useEffect(() => {
    const s = useSnippets.getState();
    void s.load(s.error !== undefined);
  }, []);
  const list = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (dialect === "all" ? snippets : snippetsFor(snippets, dialect))
      .filter((s) => !needle || [s.prefix, s.title, ...s.tags].some((x) => x.toLowerCase().includes(needle)))
      .sort((a, b) => Number(a.builtIn) - Number(b.builtIn) || a.prefix.localeCompare(b.prefix));
  }, [snippets, q, dialect]);
  const current = list.find((s) => s.id === selected) ?? list[0];
  const edit = (s: Snippet) => useUi.getState().openDialog({ kind: "snippetEdit", snippet: s });
  const remove = async (s: Snippet) => {
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm", title: `Delete snippet “${s.prefix}”?`, body: `“${s.title}” is removed from your snippet library. This cannot be undone.`,
      danger: true, ok: "Delete", resolve,
    }));
    if (!ok) return;
    const r = await useSnippets.getState().remove(s.id);
    if (!r.ok) toast("error", r.message ?? "The snippet could not be deleted.");
  };
  return (
    <Modal
      title="Snippets"
      subtitle="Type a snippet's prefix in the editor, or insert it from here. Built-in snippets are read-only."
      onClose={close}
      width={860}
      bodyClassName="p-0"
      footer={
        <>
          <button className="q-btn" onClick={() => edit(blankSnippet({ dialect: dialect === "all" ? null : dialect }))}>New snippet…</button>
          <button className="q-btn q-btn-primary" disabled={!current} onClick={() => current && insertSnippet(current) && close()}>Insert</button>
        </>
      }
    >
      <div className="flex h-[420px]">
        <div className="w-[320px] border-r border-line flex flex-col">
          <div className="p-2 space-y-2 border-b border-line">
            <input className="q-input w-full" placeholder="Search prefix, title or tag…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search snippets" autoFocus />
            <select className="q-input w-full" value={dialect} onChange={(e) => setDialect(e.target.value as Dialect | "all")} aria-label="Dialect">
              <option value="all">All dialects</option>
              {dialectOptions}
            </select>
          </div>
          <div className="flex-1 overflow-y-auto" role="listbox" aria-label="Snippet list">
            {error && <div className="p-3 text-err">{error}</div>}
            {!loaded && !error && <div className="p-3 text-muted">Loading…</div>}
            {loaded && !list.length && <div className="p-3 text-muted">No snippets match.</div>}
            {list.map((s) => (
              <button
                key={s.id}
                role="option"
                aria-selected={current?.id === s.id}
                className={`w-full text-left px-3 py-1.5 flex items-center gap-2 ${current?.id === s.id ? "bg-accent/20 q-hl" : "hover:bg-raised"}`}
                onClick={() => setSelected(s.id)}
                onDoubleClick={() => insertSnippet(s) && close()}
              >
                <span className="q-mono text-[11.5px] text-fg">{s.prefix}</span>
                <span className="text-muted truncate">{s.title}</span>
                <span className="ml-auto text-[10px] text-muted">{s.builtIn ? "built-in" : dialectLabel(s.dialect)}</span>
              </button>
            ))}
          </div>
        </div>
        <div className="flex-1 flex flex-col min-w-0">
          {current ? (
            <>
              <div className="px-3 py-2 border-b border-line flex items-center gap-2">
                <span className="font-semibold text-fg truncate">{current.title}</span>
                <span className="text-[10px] text-muted">{dialectLabel(current.dialect)}{current.tags.length ? ` · ${current.tags.join(", ")}` : ""}</span>
                <span className="ml-auto flex gap-1.5">
                  {current.builtIn ? (
                    <button className="q-btn" onClick={() => edit({ ...current, id: "", builtIn: false, prefix: `${current.prefix}_copy`, title: `${current.title} (copy)` })}>Duplicate…</button>
                  ) : (
                    <>
                      <button className="q-btn" onClick={() => edit(current)}>Edit…</button>
                      <button className="q-btn text-err" onClick={() => void remove(current)}>Delete</button>
                    </>
                  )}
                </span>
              </div>
              <pre className="flex-1 overflow-auto p-3 q-mono text-[11.5px] text-fg/90 whitespace-pre-wrap" aria-label="Snippet body">{current.body}</pre>
            </>
          ) : (
            <div className="p-4 text-muted">Select a snippet.</div>
          )}
        </div>
      </div>
    </Modal>
  );
}

const FIELD_OF: Record<string, "title" | "prefix" | "body" | "tags" | "dialect"> = { title: "title", prefix: "prefix", body: "body", tags: "tags", dialect: "dialect" };

export function SnippetEditor({ snippet, close }: { snippet: Snippet; close: () => void }) {
  const [title, setTitle] = useState(snippet.title);
  const [prefix, setPrefix] = useState(snippet.prefix);
  const [dialect, setDialect] = useState<Dialect | "">(snippet.dialect ?? "");
  const [tags, setTags] = useState(snippet.tags.join(", "));
  const [error, setError] = useState<{ field?: string; message: string } | null>(null);
  const [saving, setSaving] = useState(false);
  const savePending = useRef(false);
  const host = useRef<HTMLDivElement>(null);
  const body = useRef<ReturnType<typeof borrowEditor> | null>(null);
  useEffect(() => {
    if (!host.current) return;
    const b = borrowEditor(host.current, snippet.body, DIALECTS[knownDialect(snippet.dialect)].language);
    body.current = b;
    return () => {
      body.current = null;
      b.release();
    };
  }, [snippet]);
  const save = async () => {
    if (savePending.current) return;
    savePending.current = true;
    setSaving(true);
    setError(null);
    const r = await useSnippets.getState().save({
      ...snippet, title, prefix: prefix.trim(), dialect: dialect || null, body: body.current?.getValue() ?? snippet.body,
      tags: tags.split(",").map((t) => t.trim()).filter(Boolean),
    });
    savePending.current = false;
    setSaving(false);
    if (r.ok) {
      toast("success", `Snippet "${r.snippet.prefix}" saved.`);
      close();
    } else setError({ field: r.field ? FIELD_OF[r.field] : undefined, message: r.message });
  };
  const fieldError = (f: string) => (error?.field === f ? <span className="text-err" role="alert">{error.message}</span> : undefined);
  return (
    <Modal
      title={snippet.id ? "Edit snippet" : "New snippet"}
      subtitle="Monaco snippet syntax: ${1:name} is a placeholder, $0 the final cursor, \\$ a literal dollar sign."
      onClose={close}
      width={760}
      footer={
        <>
          <button className="q-btn" onClick={close}>Cancel</button>
          <button className="q-btn q-btn-primary" disabled={saving} aria-busy={saving || undefined} onClick={() => void save()}><BusyButtonContent busy={saving} label="Save" busyLabel="Saving…" /></button>
        </>
      }
    >
      <div className="space-y-3">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Title" hint={fieldError("title")}>
            <input className="q-input w-full" value={title} onChange={(e) => setTitle(e.target.value)} aria-label="Title" aria-invalid={error?.field === "title"} autoFocus />
          </Field>
          <Field label="Prefix (what you type)" hint={fieldError("prefix")}>
            <input className="q-input w-full q-mono" value={prefix} onChange={(e) => setPrefix(e.target.value)} aria-label="Prefix" aria-invalid={error?.field === "prefix"} />
          </Field>
          <Field label="Dialect" hint={fieldError("dialect")}>
            <select className="q-input w-full" value={dialect} onChange={(e) => setDialect(e.target.value as Dialect | "")} aria-label="Snippet dialect">
              <option value="">Any dialect</option>
              {dialectOptions}
            </select>
          </Field>
          <Field label="Tags (comma separated)" hint={fieldError("tags")}>
            <input className="q-input w-full" value={tags} onChange={(e) => setTags(e.target.value)} aria-label="Tags" />
          </Field>
        </div>
        <Field label="Body" hint={fieldError("body")}>
          <div ref={host} className="relative h-[220px] border border-line rounded" data-testid="snippet-body" />
        </Field>
        {error && !error.field && <div className="text-err" role="alert">{error.message}</div>}
      </div>
    </Modal>
  );
}

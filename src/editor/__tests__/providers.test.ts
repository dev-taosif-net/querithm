import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { FakeEditor, FakeModel, fake } from "./fakeMonaco";
import { IDX, PG_IDX, PG_SNAPSHOT, SNAPSHOT } from "../completion/__tests__/fixture";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useConnections } from "../../store/connections";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useSnippets } from "../../snippets/snippetStore";

const hooks = vi.hoisted(() => ({
  content: [] as Array<(tabId: string, model: unknown) => void>,
  created: [] as Array<(e: unknown) => void>,
  disposed: [] as Array<(key: string) => void>,
}));
const rpc = vi.hoisted(() => vi.fn());
const toast = vi.hoisted(() => vi.fn());

vi.mock("../monaco", async () => ({ monaco: (await import("./fakeMonaco")).fake.monaco }));
vi.mock("../editors", () => ({
  tabIdOfModel: (m: { tabId?: string } | null) => m?.tabId,
  // Notebook cells (Phase C3): "nb:<cell>" belongs to the notebook tab "nb".
  modelOwner: (k?: string) => (k?.startsWith("nb:") ? "nb" : k),
  onModelContent: (l: (tabId: string, model: unknown) => void) => { hooks.content.push(l); return () => {}; },
  onEditorCreated: (l: (e: unknown) => void) => { hooks.created.push(l); return () => {}; },
  onModelDisposed: (l: (key: string) => void) => { hooks.disposed.push(l); return () => {}; },
}));
vi.mock("../../ipc/client", () => ({ rpc: (...a: unknown[]) => rpc(...a), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", () => ({ toast }));

const P = await import("../providers");

type Engine = "tsql" | "pg";
const CONF: Record<Engine, { lang: string; idx: typeof IDX; profile: string; db: string }> = {
  tsql: { lang: "tsql", idx: IDX, profile: "p1", db: "SalesDB" },
  pg: { lang: "pgsql", idx: PG_IDX, profile: "p2", db: "shop" },
};
let seq = 0;
function doc(engine: Engine, text: string, tabId = `pt${++seq}`) {
  const c = CONF[engine];
  const model = new FakeModel(text, c.lang, { toString: () => `u://${tabId}` }) as FakeModel & { tabId: string };
  model.tabId = tabId;
  useTabs.setState({ tabs: { ...useTabs.getState().tabs, [tabId]: { id: tabId, title: "q", profileId: c.profile, database: c.db } as never } });
  useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey(c.profile, c.db)]: { key: snapKey(c.profile, c.db), profileId: c.profile, database: c.db, status: "fresh", index: c.idx, version: 1 } } });
  return model;
}
const pos = (model: FakeModel, off: number) => model.getPositionAt(off);

beforeAll(() => P.installProviders());
beforeEach(() => {
  rpc.mockReset();
  toast.mockClear();
  useConnections.setState({ disconnected: {} });
  useSnippets.setState({ loaded: true, snippets: [], error: undefined });
  useSettings.setState({ tier2: true, keywordCase: "upper", keywordCaseOnType: true, autoAlias: true, indentSize: 4, leadingCommas: false, newlineBeforeJoin: true });
});
afterEach(() => vi.useRealTimers());

const completion = (lang: string) => fake.providers[lang].completion;

describe("docContext", () => {
  it("returns null for a model that belongs to no tab", () => {
    expect(P.docContext(new FakeModel("x", "tsql", { toString: () => "u" }) as never)).toBeNull();
  });
  it("picks the dialect from the language or the profile engine", () => {
    const m = doc("tsql", "select 1") as FakeModel & { tabId: string };
    expect(P.docContext(m as never)!.dialect).toBe("tsql");
    m.lang = "pgsql";
    expect(P.docContext(m as never)!.dialect).toBe("pg");
    m.lang = "tsql";
    useProfiles.setState({ profiles: [{ id: "p1", engine: "pg" } as never] });
    expect(P.docContext(m as never)!.dialect).toBe("pg");
    useProfiles.setState({ profiles: [] });
  });
});

describe("session-owned engines (SQLite, Phase D0d)", () => {
  it("tier 2 names the tab's session, so the sidecar completes from that session's own view", async () => {
    vi.useFakeTimers();
    useProfiles.setState({ profiles: [{ id: "lite", name: "App", engine: "sqlite", location: "file", file: { grantId: "g" }, environment: "dev" } as never] });
    const text = "select * from Ord";
    const model = new FakeModel(text, "tsql", { toString: () => "u://lite1" }) as FakeModel & { tabId: string };
    model.tabId = "lite1";
    useTabs.setState({ tabs: { ...useTabs.getState().tabs, lite1: { id: "lite1", title: "q", profileId: "lite", database: "main", sessionId: "s7" } as never } });
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey("lite", "main", "s7")]: { key: snapKey("lite", "main", "s7"), profileId: "lite", database: "main", sessionId: "s7", status: "fresh", index: IDX, version: 1 } } });
    rpc.mockResolvedValue([]);
    completion("tsql").provideCompletionItems(model, pos(model, text.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ dialect: "sqlite", profileId: "lite", database: "main", sessionId: "s7" }));
    useProfiles.setState({ profiles: [] });
  });
});

describe("document lifetime (M12)", () => {
  it("a disposed model (a closed tab, a deleted notebook cell) is forgotten by the sidecar under its docId", async () => {
    rpc.mockResolvedValue(undefined);
    expect(hooks.disposed).toHaveLength(1);
    hooks.disposed[0]("tab42");
    hooks.disposed[0]("nb:cell7");
    expect(rpc).toHaveBeenCalledWith("lang.forget", { docId: "tab42" });
    expect(rpc).toHaveBeenCalledWith("lang.forget", { docId: "nb:cell7" });
  });
  it("a sidecar that is down does not make closing a tab fail", async () => {
    rpc.mockRejectedValue(new Error("sidecar unavailable"));
    expect(() => hooks.disposed[0]("tab43")).not.toThrow();
    await Promise.resolve();
  });
});

describe("registration", () => {
  it("registers completion, hover, folding, formatting, highlights, code actions, and signature help", () => {
    for (const lang of ["tsql", "pgsql"]) {
      expect(Object.keys(fake.providers[lang]).sort()).toEqual([
        "codeAction", "completion", "documentHighlight", "folding", "format", "hover", "rangeFormat", "signature",
      ]);
    }
    expect(completion("tsql").triggerCharacters).toContain(".");
  });
  it("counts accepted completions through the noteUsage command", () => {
    expect(() => fake.commands["querithm.noteUsage"]({}, "Orders")).not.toThrow();
    P.noteUsage("ORDERS");
  });
});

describe("completion provider", () => {
  it("offers nothing for an unknown model, or inside a string", () => {
    expect(completion("tsql").provideCompletionItems(new FakeModel("x", "tsql", { toString: () => "z" }), { lineNumber: 1, column: 1 })).toEqual({ suggestions: [] });
    const m = doc("tsql", "select 'ab");
    expect(completion("tsql").provideCompletionItems(m, pos(m, 10))).toEqual({ suggestions: [] });
  });

  it("T-SQL: serves tier 1 immediately, then merges the sidecar's tier-2 refinement", async () => {
    vi.useFakeTimers();
    const text = "select * from dbo.Ord";
    const m = doc("tsql", text);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    editor.suggestVisible = true;
    fake.created.push(editor);
    rpc.mockResolvedValue([{ label: "Orders", kind: "table", detail: "dbo.Orders (5 cols)", isSnippet: false }, { label: "OrdersExtra", kind: "view", isSnippet: false, documentation: "doc" }]);
    const first = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    expect(first.incomplete).toBe(true);
    const labels = first.suggestions.map((s: any) => s.label.label);
    expect(labels).toContain("Orders");
    expect(rpc).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ docId: m.tabId, dialect: "tsql", profileId: "p1", database: "SalesDB", offset: text.length }));
    // changed items while the popup is visible re-trigger suggest
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
    const second = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    const orders = second.suggestions.find((s: any) => s.label.label === "Orders");
    expect(orders.detail).toBe("dbo.Orders (5 cols)");
    expect(second.suggestions.some((s: any) => s.label.label === "OrdersExtra" && s.sortText === "5OrdersExtra" && s.documentation.value === "doc")).toBe(true);
    expect(P.completionStats.tier2.length).toBeGreaterThan(0);
    expect(P.completionStats.tier1.length).toBeGreaterThan(0);
    expect(P.lastTier1Latency()).toBeGreaterThanOrEqual(0);
    fake.created.length = 0;
  });

  it("T-SQL: tier-2 failures are swallowed and tier 1 still works", async () => {
    vi.useFakeTimers();
    const text = "select * from dbo.Cust";
    const m = doc("tsql", text);
    rpc.mockRejectedValue(new Error("sidecar down"));
    const r = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    expect(r.suggestions.length).toBeGreaterThan(0);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
  });

  for (const engine of ["tsql", "pg"] as const) {
    const c = CONF[engine];
    const table = engine === "pg" ? "orders" : "Orders";
    const tablesIn = (r: { suggestions: any[] }) => r.suggestions.filter((s) => s.kind === fake.monaco.languages.CompletionItemKind.Struct).map((s) => s.label.label);

    it(`${engine}: tier 2 can only add to tier 1's tables — empty, conflicting or failing answers never remove one`, async () => {
      vi.useFakeTimers();
      const answers: unknown[] = [
        [],
        [{ label: table, kind: "keyword", isSnippet: false }], // same label, another kind
        [{ label: "zzz", kind: "column", isSnippet: false }],
        new Error("sidecar down"),
      ];
      for (const a of answers) {
        const text = "select * from ";
        const m = doc(engine, text);
        if (a instanceof Error) rpc.mockRejectedValueOnce(a); else rpc.mockResolvedValueOnce(a);
        expect(tablesIn(completion(c.lang).provideCompletionItems(m, pos(m, text.length)))).toContain(table);
        await vi.advanceTimersByTimeAsync(130);
        const after = completion(c.lang).provideCompletionItems(m, pos(m, text.length));
        expect(tablesIn(after), JSON.stringify(a)).toContain(table);
      }
    });
    it(`${engine}: a large document (thousands of lines) still offers tables at its end and in its middle`, () => {
      useSettings.setState({ tier2: false });
      const stmt = "SELECT o.a, CASE WHEN o.b = 'x; y' THEN 1 ELSE 0 END AS c -- FROM x\nFROM t1 o JOIN t2 p ON p.id = o.id\nWHERE EXISTS (SELECT 1 FROM t3 WHERE t3.id = o.id);\n";
      const body = stmt.repeat(1500);
      for (const text of [body + "SELECT * FROM ", body + "SELECT * FROM o", "SELECT * FROM o"]) {
        const m = doc(engine, text + (text.startsWith("SELECT * FROM o") ? "\n" + body : ""));
        expect(tablesIn(completion(c.lang).provideCompletionItems(m, pos(m, text.length))).length, text.slice(-20)).toBeGreaterThan(0);
      }
    });
    it(`${engine}: a disconnected server offers no tables and does not throw`, () => {
      const text = "select * from ";
      const m = doc(engine, text);
      useConnections.setState({ disconnected: { [c.profile]: true } });
      const r = completion(c.lang).provideCompletionItems(m, pos(m, text.length));
      expect(tablesIn(r)).toEqual([]);
      expect(rpc).not.toHaveBeenCalledWith("meta.snapshot", expect.anything());
    });
    it(`${engine}: a snippet whose prefix equals a table name sits beside the table, never replaces it`, () => {
      useSettings.setState({ tier2: false });
      useSnippets.setState({ loaded: true, error: undefined, snippets: [{ id: "s", prefix: table.toLowerCase(), title: "t", body: "x", dialect: null, tags: [], updatedAt: 0, builtIn: false }] });
      const text = `select * from ${table.slice(0, 3)}`;
      const m = doc(engine, text);
      const r = completion(c.lang).provideCompletionItems(m, pos(m, text.length));
      expect(tablesIn(r)).toContain(table);
    });

    it(`${engine}: a tab with no cached schema loads it on the completion request, and the next request has the tables`, async () => {
      const c = CONF[engine];
      useSettings.setState({ tier2: false });
      const text = "select * from ";
      const m = doc(engine, text);
      // The schema was never loaded (or its load failed and cached nothing).
      useSnapshots.setState({ entries: {} });
      rpc.mockImplementation(async (method: string) => (method === "meta.snapshot" ? (engine === "pg" ? PG_SNAPSHOT : SNAPSHOT) : []));
      const first = completion(c.lang).provideCompletionItems(m, pos(m, text.length));
      expect(first.incomplete).toBe(true);
      expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: c.profile, database: c.db });
      await vi.waitFor(() => expect(useSnapshots.getState().entries[snapKey(c.profile, c.db)]?.index).toBeDefined());
      const next = completion(c.lang).provideCompletionItems(m, pos(m, text.length));
      const kinds = next.suggestions.map((s: { kind: number }) => s.kind);
      expect(kinds).toContain(fake.monaco.languages.CompletionItemKind.Struct);
    });
  }

  it("does not ask the sidecar when tier 2 is turned off, for either dialect", async () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const m = doc("tsql", "select * from dbo.Ord");
    completion("tsql").provideCompletionItems(m, pos(m, 21));
    const pg = doc("pg", "select * from public.ord");
    const r = completion("pgsql").provideCompletionItems(pg, pos(pg, 24));
    expect(r.suggestions.map((s: any) => s.label.label)).toContain("orders");
    await vi.advanceTimersByTimeAsync(500);
    expect(rpc).not.toHaveBeenCalled();
  });

  it("PostgreSQL: tier 1 answers first, then the sidecar's PL/pgSQL tier 2 merges in like T-SQL's", async () => {
    vi.useFakeTimers();
    const text = "CREATE FUNCTION f(p_id int) RETURNS int LANGUAGE plpgsql AS $$ DECLARE v_total numeric; BEGIN RETURN v_";
    const m = doc("pg", text);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    editor.suggestVisible = true;
    fake.created.push(editor);
    rpc.mockResolvedValue([
      { label: "v_total", kind: "variable", detail: "numeric", isSnippet: false, sortText: "2v_total" },
      { label: "p_id", kind: "parameter", detail: "int", isSnippet: false, sortText: "1p_id" },
    ]);
    const first = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    expect(first.incomplete).toBe(true);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ docId: m.tabId, dialect: "pg", profileId: "p2", database: "shop", offset: text.length }));
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
    const second = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    const v = second.suggestions.find((s: any) => s.label.label === "v_total");
    expect(v.detail).toBe("numeric");
    expect(second.suggestions.some((s: any) => s.label.label === "p_id" && s.detail === "int")).toBe(true);
    fake.created.length = 0;
  });

  it("PostgreSQL: in a DO $$ body tier 1 reads as a string, tier 2 still answers and opens the popup itself", async () => {
    // Regression (smoke run): tier 1 had nothing, so no popup opened, and tier 2 only refreshed an open popup.
    vi.useFakeTimers();
    const text = "DO $$ DECLARE v_smoke_total numeric; BEGIN v_smoke";
    const m = doc("pg", text);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    editor.suggestVisible = false;
    editor.focus = true;
    fake.created.push(editor);
    rpc.mockResolvedValue([{ label: "v_smoke_total", kind: "variable", detail: "numeric", isSnippet: false }]);
    const first = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    expect(first).toEqual({ suggestions: [], incomplete: true });
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ dialect: "pg", offset: text.length }));
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
    const second = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    const v = second.suggestions.find((s: any) => s.label.label === "v_smoke_total");
    expect(v.detail).toBe("numeric");
    // The replaced range is the word being typed (v_smoke), not the whole line.
    expect(v.range).toMatchObject({ startColumn: text.length - "v_smoke".length + 1, endColumn: text.length + 1 });
    fake.created.length = 0;
  });

  it("PostgreSQL: in a routine body the replaced range covers a typed $ or an opening quote, so accepting does not duplicate it", async () => {
    // Regression: the range scanned [A-Za-z0-9_] only, so `$` + `$1` gave `$$1` and `r."Li` + `"LineNo"` gave `r.""LineNo"`.
    vi.useFakeTimers();
    for (const [text, label, typed] of [
      ["CREATE FUNCTION f(int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN $", "$1", "$"],
      ["CREATE FUNCTION f(int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN $1", "$1", "$1"],
      ['DO $$ DECLARE r record; BEGIN PERFORM r."Li', '"LineNo"', '"Li'],
      ["DO $$ DECLARE größe int; BEGIN gr\u00f6", "größe", "grö"],
      ["DO $fn$ BEGIN END $fn$; DO $body$", "x", ""],
    ] as const) {
      const m = doc("pg", text);
      rpc.mockResolvedValue([{ label, kind: "variable", isSnippet: false }]);
      completion("pgsql").provideCompletionItems(m, pos(m, text.length));
      await vi.advanceTimersByTimeAsync(130);
      const r = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
      const item = r.suggestions.find((x: any) => x.label.label === label || x.label === label);
      if (!item) { expect(label).toBe("x"); continue; } // right after an opening tag there is nothing to replace
      const replaced = text.slice(0, item.range.startColumn - 1) + item.insertText + text.slice(item.range.endColumn - 1);
      expect(replaced).toBe(text.slice(0, text.length - typed.length) + label);
    }
  });

  it("inDollarBody: only a dialect with dollar-quoted bodies has them (PostgreSQL), never T-SQL", () => {
    const text = "DO $$ SELECT 1 $$";
    expect(P.inDollarBody(text, 8, "pg")).toBe(true);
    expect(P.inDollarBody(text, 1, "pg")).toBe(false);
    expect(P.inDollarBody(text, 8, "tsql")).toBe(false);
  });

  it("dollarWordStart never reaches past the body's opening tag", () => {
    expect(P.dollarWordStart("DO $fn$", 7, 7)).toBe(7);
    expect(P.dollarWordStart("x $", 3)).toBe(2);
    expect(P.dollarWordStart('a."Q', 4)).toBe(2);
  });

  it("PostgreSQL: inside a quoted string or a comment the sidecar is never asked and no popup is forced open", async () => {
    vi.useFakeTimers();
    const editor = new FakeEditor(null, {});
    editor.focus = true;
    fake.created.push(editor);
    rpc.mockResolvedValue([]);
    for (const text of ["SELECT 'v_smo", "SELECT 1; -- v_smo", "SELECT 1 /* v_smo", "SELECT $$abc$$ || 'v_smo"]) {
      const m = doc("pg", text);
      editor.model = m;
      expect(completion("pgsql").provideCompletionItems(m, pos(m, text.length)).suggestions).toEqual([]);
      await vi.advanceTimersByTimeAsync(130);
    }
    expect(rpc).not.toHaveBeenCalledWith("lang.complete", expect.anything());
    expect(editor.triggered).toEqual([]);
    // T-SQL strings never ask the sidecar.
    rpc.mockClear();
    const ms = doc("tsql", "SELECT 'abc");
    expect(completion("tsql").provideCompletionItems(ms, pos(ms, 11))).toEqual({ suggestions: [] });
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).not.toHaveBeenCalled();
    fake.created.length = 0;
  });

  it("PostgreSQL: a tier-2 refinement of a tier-1 column keeps one item with the sidecar's detail", async () => {
    vi.useFakeTimers();
    const text = "select o. from public.orders o";
    const m = doc("pg", text);
    rpc.mockResolvedValue([{ label: "total", kind: "column", detail: "numeric (tier 2)", isSnippet: false }]);
    const first = completion("pgsql").provideCompletionItems(m, pos(m, 9));
    const count = (r: any) => r.suggestions.filter((s: any) => s.label.label === "total").length;
    expect(count(first)).toBe(1);
    await vi.advanceTimersByTimeAsync(130);
    const second = completion("pgsql").provideCompletionItems(m, pos(m, 9));
    expect(count(second)).toBe(1);
    expect(second.suggestions.find((s: any) => s.label.label === "total").detail).toBe("numeric (tier 2)");
  });

  it("PostgreSQL: a failed tier-2 call leaves tier 1 answering", async () => {
    vi.useFakeTimers();
    const text = "select * from public.ord";
    const m = doc("pg", text);
    rpc.mockRejectedValue(new Error("sidecar down"));
    const r = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
    expect(completion("pgsql").provideCompletionItems(m, pos(m, text.length)).suggestions.map((s: any) => s.label.label)).toContain("orders");
    expect(r.suggestions.length).toBeGreaterThan(0);
  });

  it("keeps a tier-1 auto-aliased insert text when tier 2 refines it", async () => {
    vi.useFakeTimers();
    const text = "select * from Ord";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([{ label: "Orders", kind: "table", detail: "from sidecar", insertText: "dbo.Orders", isSnippet: false }]);
    const first = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    const before = first.suggestions.find((s: any) => s.label.label === "Orders");
    await vi.advanceTimersByTimeAsync(130);
    const after = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((s: any) => s.label.label === "Orders");
    expect(after.insertText).toBe(before.insertText);
    expect(after.detail).toBe("from sidecar");
  });

  it("recases keyword completions per settings (upper, lower, and off while typing)", () => {
    vi.useFakeTimers(); // T-SQL schedules a tier-2 call; a real timer would fire inside a later test
    const m = doc("tsql", "sel");
    const find = () => completion("tsql").provideCompletionItems(m, pos(m, 3)).suggestions.find((s: any) => s.kind === 1 && String(s.label.label).toLowerCase() === "select");
    expect(find().insertText).toBe("SELECT");
    useSettings.setState({ keywordCase: "lower" });
    expect(find().insertText).toBe("select");
    useSettings.setState({ keywordCaseOnType: false });
    expect(find().insertText).toBe(find().label.label);
  });
});

describe("hover provider", () => {
  const hover = (lang: string) => fake.providers[lang].hover.provideHover;
  const md = (h: any) => h.contents.map((c: any) => c.value).join("\n");

  it("shows column type, nullability and key flags (T-SQL)", () => {
    const text = "select o.OrderId, o.Status from dbo.Orders o";
    const m = doc("tsql", text);
    const h = hover("tsql")(m, pos(m, text.indexOf("OrderId") + 2));
    expect(md(h)).toContain("**dbo.Orders.OrderId**");
    expect(md(h)).toContain("`bigint` NOT NULL · PK · IDENTITY");
    const h2 = hover("tsql")(m, pos(m, text.indexOf("Status") + 2));
    expect(md(h2)).toContain("`nvarchar(20)` NULL");
  });

  it("shows column details for PostgreSQL", () => {
    const text = "select o.created_at from public.orders o";
    const m = doc("pg", text);
    expect(md(hover("pgsql")(m, pos(m, text.indexOf("created_at") + 2)))).toContain("`timestamptz` NOT NULL");
  });

  it("lists a table's columns and a procedure's parameters", () => {
    let text = "select * from dbo.Customers";
    let m = doc("tsql", text);
    let h = md(hover("tsql")(m, pos(m, text.indexOf("Customers") + 2)));
    expect(h).toContain("**dbo.Customers** — table");
    expect(h).toContain("| 🔑 CustomerId | `int` | NOT NULL |");
    text = "exec dbo.usp_GetCustomerOrders";
    m = doc("tsql", text);
    h = md(hover("tsql")(m, pos(m, text.indexOf("usp_") + 2)));
    expect(h).toContain("@MinTotal decimal(18,2) OUTPUT");
    expect(h).toContain("@StartDate datetime2 = default");
    text = "select dbo.fn_OrderTotal(1)";
    m = doc("tsql", text);
    expect(md(hover("tsql")(m, pos(m, text.indexOf("fn_") + 2)))).toContain("RETURNS decimal(18,2)");
    const pgText = "select public.order_total(1)";
    const pm = doc("pg", pgText);
    expect(md(hover("pgsql")(pm, pos(pm, pgText.indexOf("order_total") + 2)))).toContain("RETURNS numeric");
  });

  it("shows only a return type for a parameterless function and truncates long column lists", () => {
    const wide = { id: 900, schema: "dbo", name: "Wide", kind: "table", columns: Array.from({ length: 30 }, (_, i) => ({ name: `c${i}`, type: "int", nullable: true, identity: false, computed: false, pk: false, ordinal: i })) };
    const fn = { id: 901, schema: "dbo", name: "fn_Now", kind: "func", returnType: "datetime2", params: [] };
    const idx = { ...IDX, objects: [...IDX.objects, wide as never, fn as never], byName: new Map(IDX.byName), byQualified: new Map(IDX.byQualified) };
    idx.byName.set("wide", [wide as never]); idx.byQualified.set("dbo.wide", wide as never);
    idx.byName.set("fn_now", [fn as never]); idx.byQualified.set("dbo.fn_now", fn as never);
    const m = doc("tsql", "select * from dbo.Wide");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "fresh", index: idx as never, version: 2 } } });
    expect(md(hover("tsql")(m, pos(m, 19)))).toContain("…and 5 more");
    const t = "select dbo.fn_Now()";
    const m2 = doc("tsql", t);
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "fresh", index: idx as never, version: 3 } } });
    expect(md(hover("tsql")(m2, pos(m2, t.indexOf("fn_") + 2)))).toContain("returns `datetime2`");
  });

  it("keeps server-returned names inert: no link, image, HTML, code break-out or extra table cell (L10)", () => {
    const evil = "x](https://evil.example)![i](https://evil.example/i.png)<img src=x onerror=alert(1)>|cell`code";
    const obj = {
      id: 902, schema: "dbo", name: "Evil", kind: "table",
      columns: [{ name: evil, type: "int` | injected |", nullable: true, identity: false, computed: false, pk: false, ordinal: 0 }],
    };
    const fn = { id: 903, schema: "dbo", name: "fn_Evil", kind: "func", returnType: "int", params: [{ name: "@p", type: "int\n```\n[x](https://evil.example)", output: false, hasDefault: false, ordinal: 1 }] };
    const idx = { ...IDX, objects: [...IDX.objects, obj as never, fn as never], byName: new Map(IDX.byName), byQualified: new Map(IDX.byQualified) };
    idx.byName.set("evil", [obj as never]); idx.byQualified.set("dbo.evil", obj as never);
    idx.byName.set("fn_evil", [fn as never]); idx.byQualified.set("dbo.fn_evil", fn as never);
    const m = doc("tsql", "select * from dbo.Evil");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "fresh", index: idx as never, version: 4 } } });
    const h = hover("tsql")(m, pos(m, 19));
    const text = md(h);
    expect(text).not.toMatch(/(^|[^\\])\]\(/); // no unescaped "](": no link or image
    expect(text).not.toMatch(/(^|[^\\])</); // no unescaped "<": no HTML
    const row = text.split("\n").find((l: string) => l.includes("cell"))!;
    expect(row.split(/(?<!\\)\|/).length).toBe(5); // | name | type | null | — three cells, nothing injected
    expect(h.contents.every((c: any) => c.isTrusted === false && c.supportHtml === false)).toBe(true);
    const t = "select dbo.fn_Evil(1)";
    const m2 = doc("tsql", t);
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "fresh", index: idx as never, version: 5 } } });
    const fence = md(hover("tsql")(m2, pos(m2, t.indexOf("fn_") + 2)));
    // The parameter's type stays on its line inside the code block: its backticks cannot close the fence.
    expect(fence).toMatch(/^````sql\n@p int ``` \[x\]\(https:\/\/evil\.example\)\nRETURNS int\n````$/m);
  });

  it("returns nothing for variables, unknown words, and unmapped models", () => {
    const m = doc("tsql", "declare @x int; select @x, 1");
    expect(hover("tsql")(m, pos(m, 24))).toBeNull();
    expect(hover("tsql")(m, pos(m, 5))).toBeNull();
    expect(hover("tsql")(new FakeModel("x", "tsql", { toString: () => "q" }), { lineNumber: 1, column: 1 })).toBeNull();
  });
});

describe("folding provider", () => {
  const folding = (lang: string) => fake.providers[lang].folding.provideFoldingRanges;
  it("maps the sidecar's ranges, with comment and region kinds", async () => {
    const m = doc("tsql", "begin\nend");
    rpc.mockResolvedValue([{ startLine: 1, endLine: 2, kind: "comment" }, { startLine: 3, endLine: 5, kind: "region" }, { startLine: 6, endLine: 9, kind: "block" }]);
    expect(await folding("tsql")(m)).toEqual([
      { start: 1, end: 2, kind: "comment" }, { start: 3, end: 5, kind: "region" }, { start: 6, end: 9, kind: undefined },
    ]);
    expect(rpc).toHaveBeenCalledWith("lang.folding", { docId: m.tabId, text: "begin\nend", dialect: "tsql" });
  });
  it("sends the pg dialect for PostgreSQL tabs; failure and unknown models give no ranges", async () => {
    const m = doc("pg", "select 1");
    rpc.mockResolvedValueOnce([]);
    await folding("pgsql")(m);
    expect(rpc).toHaveBeenCalledWith("lang.folding", expect.objectContaining({ dialect: "pg" }));
    rpc.mockRejectedValueOnce(new Error("x"));
    expect(await folding("pgsql")(m)).toEqual([]);
    expect(await folding("pgsql")(new FakeModel("x", "pgsql", { toString: () => "q" }))).toEqual([]);
  });
});

describe("formatting providers", () => {
  const fmt = (lang: string) => fake.providers[lang].format.provideDocumentFormattingEdits;
  const range = (lang: string) => fake.providers[lang].rangeFormat.provideDocumentRangeFormattingEdits;

  it("T-SQL formats via the sidecar and passes the formatter options", async () => {
    const m = doc("tsql", "select 1");
    rpc.mockResolvedValue({ text: "SELECT 1;" });
    const edits = await fmt("tsql")(m);
    expect(edits).toEqual([{ range: m.getFullModelRange(), text: "SELECT 1;" }]);
    expect(rpc).toHaveBeenCalledWith("lang.format", { text: "select 1", dialect: "tsql", options: { keywordCase: "upper", indentSize: 4, leadingCommas: false, newlineBeforeJoin: true } });
  });

  it("PostgreSQL formats locally with the keyword-case setting", async () => {
    const m = doc("pg", "select a from t where x = 1 and y = 2");
    const edits = await fmt("pgsql")(m);
    expect(edits[0].text).toContain("SELECT");
    expect(rpc).not.toHaveBeenCalled();
    useSettings.setState({ keywordCase: "preserve" });
    const kept = await fmt("pgsql")(m);
    expect(kept[0].text).toContain("select");
  });

  it("shows a warning and returns no edits when the formatter fails", async () => {
    const m = doc("pg", "select 'unterminated");
    expect(await fmt("pgsql")(m)).toEqual([]);
    expect(toast).toHaveBeenCalledWith("warn", expect.any(String));
  });

  it("formats just the selected range, and skips it on error", async () => {
    const m = doc("tsql", "select 1;\nselect 2");
    const r = new fake.monaco.Range(2, 1, 2, 9);
    rpc.mockResolvedValue({ text: "SELECT 2;" });
    expect(await range("tsql")(m, r)).toEqual([{ range: r, text: "SELECT 2;" }]);
    expect(rpc).toHaveBeenCalledWith("lang.format", expect.objectContaining({ text: "select 2" }));
    rpc.mockResolvedValue({ text: "select 2", error: "parse error" });
    expect(await range("tsql")(m, r)).toEqual([]);
  });

  it("returns no edits for unmapped models", async () => {
    const orphan = new FakeModel("x", "tsql", { toString: () => "q" });
    expect(await fmt("tsql")(orphan)).toEqual([]);
    expect(await range("tsql")(orphan, new fake.monaco.Range(1, 1, 1, 1))).toEqual([]);
  });
});

describe("signature help", () => {
  const sig = { provideSignatureHelp: (...a: any[]) => fake.providers.tsql.signature.provideSignatureHelp(...a) };
  it("maps the sidecar's signatures, active parameter included", async () => {
    const m = doc("tsql", "exec dbo.uncached_procedure ");
    rpc.mockResolvedValue({
      signatures: [{ label: "usp_GetOrderDetails(@OrderId bigint)", documentation: null, parameters: [{ label: "@OrderId bigint", documentation: "the id" }, { label: "@x", documentation: null }] }],
      activeSignature: 0, activeParameter: 1,
    });
    const r = await sig.provideSignatureHelp(m, pos(m, 29));
    expect(r.value.activeParameter).toBe(1);
    expect(r.value.signatures[0].parameters[0].documentation).toBe("the id");
    expect(r.value.signatures[0].documentation).toBeUndefined();
    expect(() => r.dispose()).not.toThrow();
  });
  it("is null when there are no signatures, when the call fails, or for unmapped models", async () => {
    const m = doc("tsql", "select 1");
    rpc.mockResolvedValueOnce({ signatures: [], activeSignature: 0, activeParameter: 0 });
    expect(await sig.provideSignatureHelp(m, pos(m, 1))).toBeNull();
    rpc.mockRejectedValueOnce(new Error("x"));
    expect(await sig.provideSignatureHelp(m, pos(m, 1))).toBeNull();
    expect(await sig.provideSignatureHelp(new FakeModel("x", "tsql", { toString: () => "q" }), pos(m, 1))).toBeNull();
  });
});

describe("diagnostics", () => {
  const notify = (tabId: string, m: unknown) => hooks.content.forEach((l) => l(tabId, m));

  it("T-SQL: debounces 300 ms, then sets markers with severities", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "selec 1");
    fake.monaco.editor.setModelMarkers.mockClear();
    rpc.mockResolvedValue([
      { line: 1, column: 1, endLine: 1, endColumn: 6, message: "bad", severity: "error" },
      { line: 1, column: 1, endLine: 1, endColumn: 2, message: "w", severity: "warning" },
      { line: 1, column: 1, endLine: 1, endColumn: 2, message: "i", severity: "info" },
    ]);
    notify(m.tabId, m);
    notify(m.tabId, m);
    await vi.advanceTimersByTimeAsync(299);
    expect(rpc).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(5);
    expect(rpc).toHaveBeenCalledTimes(1);
    const markers = fake.monaco.editor.setModelMarkers.mock.calls[0][2];
    expect(markers.map((x: any) => x.severity)).toEqual([8, 4, 2]);
    expect(markers[0]).toMatchObject({ message: "bad", source: "ScriptDom", startColumn: 1, endColumn: 6 });
  });

  it("PostgreSQL has no sidecar diagnostics: markers are cleared instead", async () => {
    vi.useFakeTimers();
    const m = doc("pg", "select 1");
    fake.monaco.editor.setModelMarkers.mockClear();
    notify(m.tabId, m);
    await vi.advanceTimersByTimeAsync(310);
    expect(rpc).not.toHaveBeenCalled();
    expect(fake.monaco.editor.setModelMarkers).toHaveBeenCalledWith(m, "querithm", []);
  });

  it("drops results for a stale version, a disposed model, and sidecar errors", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select 1");
    fake.monaco.editor.setModelMarkers.mockClear();
    rpc.mockImplementation(async () => { m.version++; return [{ line: 1, column: 1, endLine: 1, endColumn: 2, message: "x", severity: "error" }]; });
    notify(m.tabId, m);
    await vi.advanceTimersByTimeAsync(310);
    expect(fake.monaco.editor.setModelMarkers).not.toHaveBeenCalled();
    rpc.mockRejectedValue(new Error("down"));
    notify(m.tabId, m);
    await vi.advanceTimersByTimeAsync(310);
    expect(fake.monaco.editor.setModelMarkers).not.toHaveBeenCalled();
    m.disposed = true;
    rpc.mockClear();
    notify(m.tabId, m);
    await vi.advanceTimersByTimeAsync(310);
    expect(rpc).not.toHaveBeenCalled();
  });

  it("re-checks when an editor switches model", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select 1");
    const editor = new FakeEditor(null, {});
    hooks.created.forEach((l) => l(editor));
    rpc.mockResolvedValue([]);
    editor.setModel(m);
    await vi.advanceTimersByTimeAsync(310);
    expect(rpc).toHaveBeenCalledWith("lang.diagnostics", { docId: m.tabId, text: "select 1", dialect: "tsql" });
    editor.setModel(null);
    rpc.mockClear();
    await vi.advanceTimersByTimeAsync(310);
    expect(rpc).not.toHaveBeenCalled();
  });
});

describe("keyword case on type", () => {
  function typed(engine: Engine, text: string, change: { text: string; rangeOffset: number }, ev: Record<string, unknown> = {}) {
    const m = doc(engine, text);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    hooks.created.forEach((l) => l(editor));
    editor.contentCbs.forEach((cb) => cb({ isUndoing: false, isRedoing: false, isFlush: false, changes: [change], ...ev }));
    return { m, editor };
  }

  it("recases the word before a typed space as a separate undo step (T-SQL)", async () => {
    const { editor } = typed("tsql", "select ", { text: " ", rangeOffset: 6 });
    await Promise.resolve();
    expect(editor.edits).toHaveLength(1);
    expect(editor.edits[0].edits[0].text).toBe("SELECT");
    expect(editor.undoStops).toBe(2);
  });

  it("works for PostgreSQL too, and honours lower case", async () => {
    useSettings.setState({ keywordCase: "lower" });
    const { editor } = typed("pg", "SELECT ", { text: " ", rangeOffset: 6 });
    await Promise.resolve();
    expect(editor.edits[0].edits[0].text).toBe("select");
  });

  it("ignores undo, multi-change events, word characters, preserve mode, and the setting being off", async () => {
    for (const ev of [{ isUndoing: true }, { isRedoing: true }, { isFlush: true }]) {
      const r = typed("tsql", "select ", { text: " ", rangeOffset: 6 }, ev);
      await Promise.resolve();
      expect(r.editor.edits).toHaveLength(0);
    }
    const multi = typed("tsql", "select ", { text: " ", rangeOffset: 6 }, { changes: [{}, {}] });
    await Promise.resolve();
    expect(multi.editor.edits).toHaveLength(0);
    const word = typed("tsql", "selecta", { text: "a", rangeOffset: 6 });
    await Promise.resolve();
    expect(word.editor.edits).toHaveLength(0);
    useSettings.setState({ keywordCase: "preserve" });
    const pres = typed("tsql", "select ", { text: " ", rangeOffset: 6 });
    await Promise.resolve();
    expect(pres.editor.edits).toHaveLength(0);
    useSettings.setState({ keywordCase: "upper", keywordCaseOnType: false });
    const off = typed("tsql", "select ", { text: " ", rangeOffset: 6 });
    await Promise.resolve();
    expect(off.editor.edits).toHaveLength(0);
  });

  it("does nothing for a model without a tab, an already-cased word, or when the text moved on", async () => {
    const orphan = new FakeModel("select ", "tsql", { toString: () => "o" });
    const editor = new FakeEditor(null, {});
    editor.model = orphan;
    hooks.created.forEach((l) => l(editor));
    editor.contentCbs.forEach((cb) => cb({ changes: [{ text: " ", rangeOffset: 6 }] }));
    editor.model = null;
    editor.contentCbs.forEach((cb) => cb({ changes: [{ text: " ", rangeOffset: 6 }] }));
    const done = typed("tsql", "SELECT ", { text: " ", rangeOffset: 6 });
    const moved = typed("tsql", "select ", { text: " ", rangeOffset: 6 });
    moved.m.version++;
    await Promise.resolve();
    expect(editor.edits).toHaveLength(0);
    expect(done.editor.edits).toHaveLength(0);
    expect(moved.editor.edits).toHaveLength(0);
  });
});

describe("document highlights and code actions", () => {
  it("highlights occurrences of an identifier in the document", () => {
    const m = doc("tsql", "SELECT o.OrderId, o.CustomerId FROM dbo.Orders o WHERE o.OrderId > 10;");
    const p = fake.providers["tsql"].documentHighlight;
    const highlights = p.provideDocumentHighlights(m, pos(m, 12)); // cursor on "OrderId"
    expect(highlights.length).toBeGreaterThanOrEqual(2);
  });

  it("offers Expand '*' code action in a SELECT statement", () => {
    const m = doc("tsql", "SELECT * FROM dbo.Orders;");
    const p = fake.providers["tsql"].codeAction;
    const res = p.provideCodeActions(m, new fake.monaco.Range(1, 8, 1, 9));
    expect(res.actions.length).toBe(1);
    expect(res.actions[0].title).toBe("Expand '*' to column list");
    expect(res.actions[0].edit.edits[0].textEdit.text).toContain("OrderId, CustomerId");
  });

  it("serves tier-1 signature help synchronously when sidecar is not available", async () => {
    const m = doc("tsql", "SELECT dbo.fn_OrderTotal(");
    const p = fake.providers["tsql"].signature;
    rpc.mockRejectedValueOnce(new Error("sidecar offline"));
    const res = await p.provideSignatureHelp(m, pos(m, m.getValue().length));
    expect(res).not.toBeNull();
    expect(res.value.signatures[0].label).toContain("fn_OrderTotal");
  });

  it("installs cursor position listeners for pair and insert matching", () => {
    const m = doc("tsql", "INSERT INTO dbo.Orders (OrderId) VALUES (1);");
    const editor = new FakeEditor(null, {});
    editor.model = m;
    hooks.created.forEach((l) => l(editor));
    expect(editor.cursorPositionCbs.length).toBeGreaterThanOrEqual(1);
    expect(() => editor.cursorPositionCbs.forEach((cb) => cb({ position: { lineNumber: 1, column: 27 } }))).not.toThrow();
  });
});


it("returns cached signature help without waiting for or calling the sidecar", async () => {
  const m = doc("tsql", "SELECT dbo.fn_OrderTotal(");
  rpc.mockImplementation(() => new Promise(() => {}));
  const result = await fake.providers.tsql.signature.provideSignatureHelp(m, pos(m, m.getValue().length));
  expect(result.value.signatures[0].label).toContain("fn_OrderTotal");
  expect(rpc).not.toHaveBeenCalled();
});

it("every editor gets the comment items in its right-click menu", () => {
  const editor = new FakeEditor(null, {});
  hooks.created.forEach((create) => create(editor));
  expect(editor.actions.map((a) => a.id)).toEqual(["querithm.toggleComment", "querithm.comment", "querithm.uncomment", "querithm.blockComment"]);
});

it("debounces cursor decorations and cancels pending work when the editor closes", () => {
  vi.useFakeTimers();
  const m = doc("tsql", "BEGIN SELECT 1; END;");
  const editor = new FakeEditor(null, {}); editor.model = m;
  hooks.created.forEach((create) => create(editor));
  const decorations = editor.createDecorationsCollection.mock.results[0].value;
  const move = () => editor.cursorPositionCbs.forEach((cb) => cb({ position: { lineNumber: 1, column: 1 } }));
  move(); move();
  expect(decorations.set).not.toHaveBeenCalled();
  vi.advanceTimersByTime(75);
  expect(decorations.set).toHaveBeenCalledTimes(1);
  move(); editor.dispose(); vi.advanceTimersByTime(75);
  expect(decorations.set).toHaveBeenCalledTimes(1);
});

it("respects quoted PostgreSQL identifier case in occurrence highlighting", () => {
  const text = 'SELECT "Foo", foo, "foo", "Foo" FROM t';
  const m = doc("pg", text);
  const highlights = fake.providers.pgsql.documentHighlight.provideDocumentHighlights(m, pos(m, text.indexOf('"Foo"') + 1));
  expect(highlights).toHaveLength(2);
});

describe("snippet completion (Phase A3)", () => {
  const lib = [
    { id: "a", title: "T-SQL top", prefix: "selt", body: "SELECT TOP (${1:100}) *", dialect: "tsql", tags: [], updatedAt: 0, builtIn: true },
    { id: "b", title: "PG limit", prefix: "sell", body: "SELECT * LIMIT ${1:100}", dialect: "pg", tags: [], updatedAt: 0, builtIn: true },
    { id: "c", title: "Anywhere", prefix: "selany", body: "SELECT 1;", dialect: null, tags: [], updatedAt: 0, builtIn: false },
  ] as never[];
  // Library snippets only (tier 1 has keyword templates of the same kind).
  const snippetLabels = (r: any) => r.suggestions.filter((s: any) => s.kind === fake.monaco.languages.CompletionItemKind.Snippet && (s.detail ?? "").endsWith("snippet)")).map((s: any) => s.label.label).sort();

  it("offers the tab dialect's snippets (and dialect-free ones) as Monaco snippets", () => {
    vi.useFakeTimers();
    useSnippets.setState({ loaded: true, snippets: lib });
    const ms = doc("tsql", "sel");
    const r = completion("tsql").provideCompletionItems(ms, pos(ms, 3));
    expect(snippetLabels(r)).toEqual(["selany", "selt"]);
    const top = r.suggestions.find((s: any) => s.label.label === "selt");
    expect(top).toMatchObject({
      insertText: "SELECT TOP (${1:100}) *", insertTextRules: fake.monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
      sortText: "9selt", filterText: "selt", detail: "T-SQL top (built-in snippet)",
    });
    const pg = doc("pg", "sel");
    expect(snippetLabels(completion("pgsql").provideCompletionItems(pg, pos(pg, 3)))).toEqual(["selany", "sell"]);
  });

  it("offers no snippet after a qualifier or before a word is typed", () => {
    vi.useFakeTimers();
    useSnippets.setState({ loaded: true, snippets: lib });
    const q = doc("tsql", "select o.se from dbo.Orders o");
    expect(snippetLabels(completion("tsql").provideCompletionItems(q, pos(q, 11)))).toEqual([]);
    const blank = doc("tsql", "select ");
    expect(snippetLabels(completion("tsql").provideCompletionItems(blank, pos(blank, 7)))).toEqual([]);
  });

  it("loads the library on first use and offers it once loaded", async () => {
    vi.useFakeTimers();
    useSnippets.setState({ loaded: false, snippets: [] });
    rpc.mockImplementation(async (m: string) => (m === "snippet.list" ? lib : []));
    const m = doc("tsql", "sel");
    expect(snippetLabels(completion("tsql").provideCompletionItems(m, pos(m, 3)))).toEqual([]);
    expect(rpc).toHaveBeenCalledWith("snippet.list");
    await vi.advanceTimersByTimeAsync(0);
    expect(snippetLabels(completion("tsql").provideCompletionItems(m, pos(m, 3)))).toEqual(["selany", "selt"]);
  });
});

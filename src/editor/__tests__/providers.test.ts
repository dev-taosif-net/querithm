import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { FakeEditor, FakeModel, fake } from "./fakeMonaco";
import { IDX, PG_IDX, PG_SNAPSHOT, SNAPSHOT } from "../completion/__tests__/fixture";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useConnections } from "../../store/connections";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useSnippets } from "../../snippets/snippetStore";
import { buildSchemaIndex } from "../completion/schemaIndex";
import { DIALECTS } from "../../engines";

/** An index of a snapshot without objects (a first read still running, a failed one). */
const buildIndex = () => buildSchemaIndex({ profileId: "p1", database: "SalesDB", status: "fresh", objects: [], fks: [] }, "tsql", []);

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
// Usage history (store.db through lang.usage.*) has its own tests (usage.test.ts); here it is this session's counts.
const usageCounts = new Map<string, number>();
vi.mock("../usage", () => ({
  usageScores: () => usageCounts,
  recordUsage: (item: string) => usageCounts.set(item.toLowerCase(), (usageCounts.get(item.toLowerCase()) ?? 0) + 1),
  flushUsage: async () => {},
}));
// Row counts load in the background through meta.rowCounts (rowCounts.test.ts); none here.
vi.mock("../rowCounts", () => ({ rowCountsFor: () => undefined, onRowCounts: () => () => {} }));

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
/** A MySQL tab (D4): its own profile and a small snapshot. */
function mysqlDoc(text: string, tabId = `my${++seq}`) {
  const lang = DIALECTS.mySql.language;
  const model = new FakeModel(text, lang, { toString: () => `u://${tabId}` }) as FakeModel & { tabId: string };
  model.tabId = tabId;
  useProfiles.setState({ profiles: [...useProfiles.getState().profiles.filter((p: any) => p.id !== "my"), { id: "my", engine: "mySql", environment: "dev" } as never] });
  useTabs.setState({ tabs: { ...useTabs.getState().tabs, [tabId]: { id: tabId, title: "q", profileId: "my", database: "shop" } as never } });
  const idx = buildSchemaIndex({ profileId: "my", database: "shop", status: "fresh", fks: [], objects: [
    { id: 1, schema: "shop", name: "Customers", kind: "table", columns: [
      { name: "CustomerId", type: "int", nullable: false, identity: false, computed: false, pk: true, ordinal: 1 },
      { name: "Status", type: "int", nullable: true, identity: false, computed: false, pk: false, ordinal: 2 },
    ] }] }, "mySql", ["shop"]);
  useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey("my", "shop")]: { key: snapKey("my", "shop"), profileId: "my", database: "shop", status: "fresh", index: idx, version: 1 } } });
  return model;
}

beforeAll(() => P.installProviders());
beforeEach(() => {
  P.cancelTier2(); // a real 120 ms timer left by an earlier test would call rpc inside a later one
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

describe("Oracle tier 2 (D4 S09)", () => {
  it("names the login's schema, so the sidecar resolves unqualified names there; other engines send none", async () => {
    vi.useFakeTimers();
    useProfiles.setState({ profiles: [{ id: "ora", name: "Ora", engine: "oracle", username: "querithm_app", environment: "dev" } as never, { id: "p2", engine: "pg", username: "postgres" } as never] });
    expect(P.schemaParam("ora")).toEqual({ schema: "QUERITHM_APP" });
    expect(P.schemaParam("p2")).toEqual({});
    expect(P.schemaParam(null)).toEqual({});
    const text = "select * from emp";
    const model = new FakeModel(text, "oracle", { toString: () => "u://ora1" }) as FakeModel & { tabId: string };
    model.tabId = "ora1";
    useTabs.setState({ tabs: { ...useTabs.getState().tabs, ora1: { id: "ora1", title: "q", profileId: "ora", database: "FREEPDB1", sessionId: "s9" } as never } });
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey("ora", "FREEPDB1")]: { key: snapKey("ora", "FREEPDB1"), profileId: "ora", database: "FREEPDB1", status: "fresh", index: IDX, version: 1 } } });
    rpc.mockResolvedValue([]);
    completion("oracle").provideCompletionItems(model, pos(model, text.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ dialect: "oracle", profileId: "ora", schema: "QUERITHM_APP" }));
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
  it("registers completion, hover, folding, formatting, highlights, code actions, signature help and semantic tokens", () => {
    for (const lang of ["tsql", "pgsql"]) {
      expect(Object.keys(fake.providers[lang]).sort()).toEqual([
        "codeAction", "completion", "documentHighlight", "folding", "format", "hover", "rangeFormat", "semanticTokens", "signature",
      ]);
    }
    expect(completion("tsql").triggerCharacters).toContain(".");
  });
  it("registers the semantic tokens provider (App Themes §5.3) for every SQL language, gated off", () => {
    const langs = ["tsql", "pgsql", "sqlite", "mysql", "duckdb", "oracle"];
    for (const lang of langs) expect(fake.providers[lang]?.semanticTokens?.getLegend()).toEqual({ tokenTypes: ["identifier"], tokenModifiers: ["cte", "schema"] });
    const m = doc("tsql", "WITH r AS (SELECT 1 AS n) SELECT * FROM dbo.t, r");
    expect(fake.providers.tsql.semanticTokens.provideDocumentRangeSemanticTokens(m, m.getFullModelRange(), {})).toBeNull();
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
    // A table keeps tier 1's description (columns, rows; the schema is typed): the sidecar's wording says less (§9).
    expect(orders.detail).toBe("5 cols");
    expect(orders.label.description).toBe("5 cols");
    const extra = second.suggestions.find((s: any) => s.label.label === "OrdersExtra");
    expect(extra.documentation.value).toBe("doc");
    // Ranked like tier 1's items by how "Ord" matches it (audit M17): both are prefix matches, tier 1's table first.
    expect(extra.sortText > orders.sortText).toBe(true);
    expect(P.completionStats.tier2.length).toBeGreaterThan(0);
    expect(P.completionStats.tier1.length).toBeGreaterThan(0);
    expect(P.lastTier1Latency()).toBeGreaterThanOrEqual(0);
    fake.created.length = 0;
  });

  it("a tier-2 answer is not reused once the rest of the statement after the word changed (audit M15)", async () => {
    vi.useFakeTimers();
    const head = "select x.";
    const m1 = doc("tsql", head + " from dbo.Orders x", "m15");
    rpc.mockResolvedValue([{ label: "OnlyFromOrders", kind: "column", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m1, pos(m1, head.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
    // The same tab, the same text before the word, another table after it: asked again, never the old answer.
    const m2 = doc("tsql", head + " from dbo.Customers x", "m15");
    rpc.mockResolvedValue([{ label: "OnlyFromCustomers", kind: "column", isSnippet: false }]);
    const again = completion("tsql").provideCompletionItems(m2, pos(m2, head.length));
    expect(again.suggestions.some((s: any) => s.label.label === "OnlyFromOrders")).toBe(false);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(2);
  });

  it("inside a routine body tier 1 reads as a string, the rest of the body is part of the key too (M15, second review)", async () => {
    vi.useFakeTimers();
    const head = "DO LANGUAGE plpgsql $$ BEGIN PERFORM x.";
    const m1 = doc("pg", head + " FROM orders x; END $$", "m15b");
    rpc.mockResolvedValue([{ label: "only_from_orders", kind: "column", isSnippet: false }]);
    completion("pgsql").provideCompletionItems(m1, pos(m1, head.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
    const m2 = doc("pg", head + " FROM customers x; END $$", "m15b");
    rpc.mockResolvedValue([{ label: "only_from_customers", kind: "column", isSnippet: false }]);
    const again = completion("pgsql").provideCompletionItems(m2, pos(m2, head.length));
    expect(again.suggestions.some((s: any) => s.label === "only_from_orders" || s.label?.label === "only_from_orders")).toBe(false);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(2);
  });

  it("an item only tier 2 offers is ranked by how the word matches it, like tier 1's (audit M17)", async () => {
    vi.useFakeTimers();
    const text = "select * from Item";
    const m = doc("tsql", text);
    // Tier 1: OrderItems matches "Item" as a substring. Tier 2 alone: ItemArchive (a prefix match) and Unrelated (none).
    rpc.mockResolvedValue([
      { label: "ItemArchive", kind: "table", isSnippet: false },
      { label: "Unrelated", kind: "table", isSnippet: false },
    ]);
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    const r = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    const t2 = r.suggestions.find((s: any) => s.label.label === "ItemArchive");
    const t1 = r.suggestions.find((s: any) => s.label.label === "OrderItems");
    expect(t2 && t1).toBeTruthy();
    expect(t2.filterText.startsWith("Item")).toBe(true);
    expect(t2.sortText < t1.sortText, `${t2.sortText} vs ${t1.sortText}`).toBe(true);
    // An item that does not match the word keeps Monaco's own filtering (no typed-prefix filter text).
    const other = r.suggestions.find((s: any) => s.label.label === "Unrelated");
    expect(other?.filterText?.startsWith("Item") ?? false).toBe(false);
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
    // Only this test's documents count: on a slow runner (macos-15-intel) an earlier test's tier-2 debounce, on real
    // timers, fired during this one (seen: lang.complete for another tab's "select * from ").
    const mine = new Set([m.tabId, pg.tabId]);
    expect(rpc.mock.calls.filter((c) => c[0] === "lang.complete" && mine.has((c[1] as { docId?: string })?.docId ?? "")), JSON.stringify(rpc.mock.calls)).toEqual([]);
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
    editor.position = m.getPositionAt(text.length);
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
    expect(v.range.replace ?? v.range).toMatchObject({ startColumn: text.length - "v_smoke".length + 1, endColumn: text.length + 1 });
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
      const range = item.range.replace ?? item.range;
      const replaced = text.slice(0, range.startColumn - 1) + item.insertText + text.slice(range.endColumn - 1);
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

  it("dollarWordStart: a quote inside a string literal is no opened name (accepting replaced the string's text)", () => {
    // Completion audit (low, tier 1): an odd count of `"` on the line, one of them inside '…', put the word's start in the string.
    for (const [text, word] of [
      [`DO $$ BEGIN RAISE NOTICE 'say "hi'; PERFORM v_x`, "v_x"],
      [`DO $$ BEGIN RAISE NOTICE 'a "b'; PERFORM r."Li`, '"Li'],
      [`DO $$ BEGIN x := 'it''s "q'; RETURN $1`, "$1"],
      [`DO $$ BEGIN -- a "quote\nRETURN v_t`, "v_t"],
    ] as const) {
      expect(P.dollarWordStart(text, text.length, 6), text).toBe(text.length - word.length);
    }
  });

  it("dollarWordStart: a line that begins inside a string or comment opened on an earlier line (second audit review)", () => {
    // Lexing from the line start read `second'; PERFORM v_x` as a string: the range was empty and accepting `v_xyz` gave
    // `PERFORM v_xv_xyz`.
    for (const [text, word] of [
      [`DO $$ BEGIN RAISE NOTICE 'first\nsecond'; PERFORM v_x`, "v_x"],
      [`DO $$ BEGIN /* a\n it's */ PERFORM v_x`, "v_x"],
      [`DO $$ BEGIN RAISE NOTICE 'a\nb "c'; PERFORM r."Li`, '"Li'],
      [`DO $$ BEGIN RAISE NOTICE 'x'; PERFORM v_x`, "v_x"],
    ] as const) {
      expect(P.dollarWordStart(text, text.length, 6), text).toBe(text.length - word.length);
    }
    // The word stops at the cursor; a string the cursor sits in is no word.
    expect(P.dollarWordStart("DO $$ BEGIN PERFORM v_xyz; END $$", 22, 5)).toBe(20);
    expect(P.dollarWordStart("DO $$ BEGIN x := 'ab\ncd ef' ; END $$", 24, 5)).toBe(24);
  });

  it("PostgreSQL: accepting an item in a routine body after a string with a quote in it replaces the word only", async () => {
    vi.useFakeTimers();
    const text = `DO $$ BEGIN RAISE NOTICE 'say "hi'; PERFORM v_x`;
    const m = doc("pg", text);
    rpc.mockResolvedValue([{ label: "v_xyz", kind: "variable", isSnippet: false }]);
    completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    const item = completion("pgsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((x: any) => (x.label.label ?? x.label) === "v_xyz");
    const range = item.range.replace ?? item.range;
    expect(text.slice(0, range.startColumn - 1) + item.insertText + text.slice(range.endColumn - 1)).toBe(text.slice(0, -3) + "v_xyz");
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
    expect(after.detail).toBe(before.detail);
    expect(after.label).toEqual(before.label);
  });

  it("shows an object's name once, its schema leading the description, dbo included (Option A)", () => {
    vi.useFakeTimers(); // T-SQL schedules a tier-2 call
    rpc.mockResolvedValue([]);
    const text = "select * from Prod";
    const m = doc("tsql", text);
    const items = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions;
    const products = items.find((s: any) => s.kind === 2 && s.label.label === "Products");
    expect(products.label.description).toBe("sales · 3 cols");
    expect(products.insertText).toBe("sales.Products pro"); // what Enter inserts is unchanged
    const orders = completion("tsql").provideCompletionItems(doc("tsql", "select * from Ord"), { lineNumber: 1, column: 18 }).suggestions
      .find((s: any) => s.kind === 2 && s.label.label === "Orders");
    expect(orders.label.description).toBe("dbo · 5 cols");
    expect(items.some((s: any) => String(s.label.label).includes(".") && s.kind === 2)).toBe(false);
  });

  it("T-SQL: the sidecar's dbo.function and tier 1's are one item", async () => {
    vi.useFakeTimers();
    const text = "select fn_Ord";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([{ label: "dbo.fn_OrderTotal", kind: "func", detail: "returns money", insertText: "dbo.fn_OrderTotal(${1})", isSnippet: true }]);
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    const fns = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions.filter((s: any) => /fn_OrderTotal$/.test(s.label.label));
    expect(fns).toHaveLength(1);
    expect(fns[0].label.label).toBe("fn_OrderTotal");
    expect(fns[0].label.description).toMatch(/^dbo · function/);
    expect(fns[0].insertText).toBe("dbo.fn_OrderTotal(${1:@OrderId})");
  });

  it("an object only tier 2 lists looks like tier 1's rows; anything else stays as tier 2 sent it", async () => {
    vi.useFakeTimers();
    const text = "select * from Ord";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([
      { label: "sales.Products", kind: "table", detail: "sales · 3 cols", isSnippet: false },
      { label: "[odd.name]", kind: "table", detail: "dbo", isSnippet: false },
      { label: "Orders", kind: "proc", detail: "dbo · procedure", isSnippet: false }, // a name of another kind
    ]);
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    const items = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions;
    const p = items.find((s: any) => s.label.label === "Products");
    expect(p.label.description).toBe("sales · 3 cols");
    expect(p.insertText).toBe("sales.Products");
    expect(p.filterText).toBe("sales.Products");
    expect(items.find((s: any) => s.label.label === "[odd.name]").label.description).toBe("dbo");
    expect(items.find((s: any) => s.kind === 5 && s.label.label === "Orders").label.description).toBe("dbo · procedure");
    // After a typed schema tier 2's bare names belong to it: never described as the default schema's object.
    const q = "select * from sales.Ord";
    const mq = doc("tsql", q);
    rpc.mockResolvedValue([{ label: "Orders", kind: "table", detail: "sales · 9 cols", isSnippet: false }]);
    completion("tsql").provideCompletionItems(mq, pos(mq, q.length));
    await vi.advanceTimersByTimeAsync(130);
    const o = completion("tsql").provideCompletionItems(mq, pos(mq, q.length)).suggestions.find((s: any) => s.label.label === "Orders");
    expect(o.label.description).toBe("sales · 9 cols");
  });

  it("a CTE keeps the sidecar's richer description (only schema objects keep tier 1's)", async () => {
    vi.useFakeTimers();
    const text = "with r as (select 1 as n) select * from r";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([{ label: "r", kind: "cte", detail: "CTE (n)", isSnippet: false }]);
    const first = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((s: any) => s.label.label === "r");
    expect(first.label.description).toBe("CTE");
    await vi.advanceTimersByTimeAsync(130);
    const after = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((s: any) => s.label.label === "r");
    expect(after.label.description).toBe("CTE (n)");
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
    expect(h).toMatch(/\*\*dbo\.Customers\*\* · \*table · \d+ columns\*/);
    expect(h).toContain("| Column | Type | Nullability |");
    expect(h).toContain("| **🔑 CustomerId** | `int` | not null |");
    expect(h).toContain("| Email | `nvarchar(320)` | *null* |");
    const ot = "select * from dbo.Orders";
    const om = doc("tsql", ot);
    expect(md(hover("tsql")(om, pos(om, ot.indexOf("Orders") + 2)))).toContain("| **🔑 OrderId** | `bigint` | not null · identity |");
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
    expect(md(hover("tsql")(m, pos(m, 19)))).toContain("*…and 5 more columns*");
    expect(md(hover("tsql")(m, pos(m, 19)))).toContain("| c0 | `int` | *null* |");
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

  it("Oracle has none either (D4 S12, as PostgreSQL): the server's errors mark their line and column after a run instead", async () => {
    vi.useFakeTimers();
    useProfiles.setState({ profiles: [{ id: "ora", name: "Ora", engine: "oracle", username: "querithm_app", environment: "dev" } as never] });
    const m = new FakeModel("SELEC 1 FROM dual", "oracle", { toString: () => "u://oradiag" }) as FakeModel & { tabId: string };
    m.tabId = "oradiag";
    useTabs.setState({ tabs: { ...useTabs.getState().tabs, oradiag: { id: "oradiag", title: "q", profileId: "ora", database: "FREEPDB1" } as never } });
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

  it("offers no snippet where a new name is typed (an alias after AS) or inside an opened quote", () => {
    // Completion audit (low, tier 1): a snippet's body replaced an alias being typed, or landed inside `[sel…]`.
    vi.useFakeTimers();
    useSnippets.setState({ loaded: true, snippets: lib });
    for (const [text, lang, engine] of [
      ["select * from Orders AS sel", "tsql", "tsql"], ["select OrderId AS sel from Orders", "tsql", "tsql"],
      ["select * from [sel", "tsql", "tsql"], ['select * from "sel', "pgsql", "pg"],
    ] as const) {
      const m = doc(engine, text);
      const at = text.includes(" from Orders") && !text.endsWith("sel") ? text.indexOf(" from") : text.length;
      expect(snippetLabels(completion(lang).provideCompletionItems(m, pos(m, at))), text).toEqual([]);
    }
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

describe("completion provider: merge, quoting, trigger and range (review regressions)", () => {
  // dbo.Events has columns named like a function (Count) and a reserved word (User).
  const EVENTS = { id: 950, schema: "dbo", name: "Events", kind: "table", columns: [
    { name: "Count", type: "int", nullable: false, identity: false, computed: false, pk: false, ordinal: 1 },
    { name: "User", type: "nvarchar(50)", nullable: true, identity: false, computed: false, pk: false, ordinal: 2 },
  ] };
  let version = 100;
  function eventsDoc(text: string) {
    const m = doc("tsql", text);
    const idx = { ...IDX, objects: [...IDX.objects, EVENTS as never], byName: new Map(IDX.byName), byQualified: new Map(IDX.byQualified), byId: new Map(IDX.byId) };
    idx.byName.set("events", [EVENTS as never]); idx.byQualified.set("dbo.events", EVENTS as never); idx.byId.set(950, EVENTS as never);
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "fresh", index: idx as never, version: ++version } } });
    return m;
  }
  const labelOf = (s: any) => (typeof s.label === "string" ? s.label : s.label.label);

  it("keeps a column and a function of the same name (Count and COUNT) as two items", () => {
    vi.useFakeTimers();
    const text = "SELECT Cou FROM dbo.Events";
    const m = eventsDoc(text);
    const r = completion("tsql").provideCompletionItems(m, pos(m, "SELECT Cou".length));
    const count = r.suggestions.filter((s: any) => labelOf(s).toLowerCase() === "count");
    expect(count.map((s: any) => s.kind).sort()).toEqual([4, 6]); // Field and Function
  });

  it("a tier-2 answer never drops tier 1's quoting of a reserved-word column", async () => {
    vi.useFakeTimers();
    const text = "SELECT Us FROM dbo.Events";
    const m = eventsDoc(text);
    const at = pos(m, "SELECT Us".length);
    const first = completion("tsql").provideCompletionItems(m, at).suggestions.find((s: any) => labelOf(s) === "User");
    expect(first.insertText).toBe("[User]");
    rpc.mockResolvedValue([{ label: "User", kind: "column", detail: "nvarchar(50) NULL", isSnippet: false }]);
    await vi.advanceTimersByTimeAsync(130);
    const after = completion("tsql").provideCompletionItems(m, at).suggestions.find((s: any) => labelOf(s) === "User");
    expect(after.insertText).toBe("[User]");
    expect(after.detail).toBe("nvarchar(50) NULL");
  });

  it("a typed space opens the list only where an object name comes next, so Enter still starts a new line", () => {
    vi.useFakeTimers();
    const space = { triggerKind: 1, triggerCharacter: " " };
    const comma = { triggerKind: 1, triggerCharacter: "," };
    const cases: [string, any, boolean][] = [
      ["select * from ", space, true],
      ["select * from dbo.Orders o join ", space, true],
      ["select * from dbo.Orders o ", space, false],
      ["select * from dbo.Orders o where o.OrderId = 1 and ", space, false],
      ["select o.OrderId, ", comma, false],
      ["select * from dbo.Orders o ", { triggerKind: 0 }, true], // Ctrl+Space
    ];
    for (const [text, trigger, shown] of cases) {
      const m = doc("tsql", text);
      const r = completion("tsql").provideCompletionItems(m, pos(m, text.length), trigger);
      expect(r.suggestions.length > 0, `${text} ${JSON.stringify(trigger)}`).toBe(shown);
    }
  });

  it("a decimal point opens no list, so the next Enter cannot turn 1.5 into 1.5CustomerId (audit H8)", () => {
    vi.useFakeTimers();
    const dot = { triggerKind: 1, triggerCharacter: "." };
    for (const [engine, text] of [["tsql", "select * from dbo.Orders where TotalAmount > 1."], ["tsql", "select * from dbo.Orders where TotalAmount > 1.5"],
      ["pg", "select * from orders where total > 1."]] as const) {
      const m = doc(engine, text);
      expect(completion(CONF[engine].lang).provideCompletionItems(m, pos(m, text.length), dot).suggestions, text).toEqual([]);
    }
    // Still: a qualifier's dot lists its columns.
    const q = "select * from dbo.Orders o where o.";
    const m = doc("tsql", q);
    expect(completion("tsql").provideCompletionItems(m, pos(m, q.length), dot).suggestions.length).toBeGreaterThan(0);
  });

  it("documentation past the first rows is built when Monaco shows the item (resolveCompletionItem, audit H9)", async () => {
    vi.useFakeTimers();
    const { buildSchemaIndex } = await import("../completion/schemaIndex");
    const objects = Array.from({ length: 120 }, (_, k) => ({
      id: 1000 + k, schema: "dbo", name: `Big${k}`, kind: "table" as const,
      columns: [{ name: "id", type: "int", nullable: false, identity: false, computed: false, pk: true, ordinal: 1 }],
    }));
    const big = buildSchemaIndex({ profileId: "p1", database: "SalesDB", status: "fresh", objects, fks: [] }, "tsql", ["SalesDB"]);
    const text = "select * from Big";
    const m = doc("tsql", text);
    const key = snapKey("p1", "SalesDB");
    const before = useSnapshots.getState().entries[key];
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [key]: { ...before, index: big } } });
    try {
      const r = completion("tsql").provideCompletionItems(m, pos(m, text.length));
      const bigs = r.suggestions.filter((s: any) => String(s.label.label).startsWith("Big"));
      expect(bigs.length).toBe(120);
      expect(bigs.filter((s: any) => s.documentation).length).toBeGreaterThan(0); // the first rows: built
      const late = bigs.find((s: any) => !s.documentation);
      expect(late).toBeDefined();
      expect(completion("tsql").resolveCompletionItem(late).documentation.value).toContain("| Column |");
      // An item with nothing to build is returned as it is.
      expect(P.resolveCompletionItem({ label: "x", insertText: "x", kind: 0 } as any).documentation).toBeUndefined();
    } finally {
      useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [key]: before } });
    }
  });

  it("[ triggers completion only where [x] is a delimited name (SQL Server, SQLite) (audit H8)", () => {
    expect(completion("tsql").triggerCharacters).toContain("[");
    expect(completion("sqlite").triggerCharacters).toContain("[");
    for (const lang of ["pgsql", "mysql", "duckdb", "oracle"]) expect(completion(lang).triggerCharacters, lang).not.toContain("[");
  });

  it("accepting inside a word replaces the whole word", () => {
    vi.useFakeTimers();
    const text = "select * from dbo.Orders o where o.Cust";
    const m = doc("tsql", text + "omerId = 1");
    const r = completion("tsql").provideCompletionItems(m, pos(m, text.length));
    const item = r.suggestions.find((s: any) => labelOf(s) === "CustomerId");
    const full = text + "omerId = 1";
    const out = full.slice(0, item.range.replace.startColumn - 1) + item.insertText + full.slice(item.range.replace.endColumn - 1);
    expect(out).toBe("select * from dbo.Orders o where o.CustomerId = 1");
    expect(item.range.insert.endColumn).toBe(text.length + 1);
  });

  it("an opened bracket is replaced, closing bracket included, by the bracketed name", () => {
    vi.useFakeTimers();
    for (const text of ["select * from [Order D", "select * from [Order D]"]) {
      const m = doc("tsql", text);
      const caret = "select * from [Order D".length;
      const item = completion("tsql").provideCompletionItems(m, pos(m, caret)).suggestions.find((s: any) => labelOf(s) === "Order Details");
      const out = text.slice(0, item.range.replace.startColumn - 1) + item.insertText + text.slice(item.range.replace.endColumn - 1);
      expect(out.startsWith("select * from [Order Details]")).toBe(true);
      expect(out).not.toContain("]]");
      expect(item.filterText.startsWith("[")).toBe(true);
    }
  });
});

// Regression: Enter in the editor did nothing. Monaco's Enter accepted a list item equal to what was typed (the
// "Loading schema…" row, a keyword typed in full), and a late tier-2 answer re-opened the list after Escape or on the
// next line, so the next Enter went to that list too.
describe("Enter always reaches the editor: no list opened from code takes it", () => {
  const DO = { pg: "DO $$ DECLARE v_enter_total numeric; BEGIN v_enter", item: { label: "v_enter_total", kind: "variable", detail: "numeric", isSnippet: false } };
  function setup() {
    vi.useFakeTimers();
    const m = doc("pg", DO.pg);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    editor.focus = true;
    editor.position = m.getPositionAt(DO.pg.length);
    fake.created.push(editor);
    hooks.created.forEach((l) => l(editor));
    rpc.mockResolvedValue([DO.item]);
    return { m, editor };
  }
  afterEach(() => { fake.created.length = 0; });

  it("a list re-opened or refreshed from code is an auto list (smart Enter applies)", async () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
    expect(editor.triggerArgs).toEqual([{ auto: true }]);
  });

  it("a schema arriving refreshes an open list as an auto list", () => {
    const { editor } = setup();
    editor.suggestVisible = true;
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries } });
    expect(editor.triggered).toContain("schema:editor.action.triggerSuggest");
    expect(editor.triggerArgs.every((a) => JSON.stringify(a) === JSON.stringify({ auto: true }))).toBe(true);
  });

  it("tier 2 does not open a list after the user pressed Escape", async () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    editor.pressKey(fake.monaco.KeyCode.Escape);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.anything());
    expect(editor.triggered).toEqual([]);
  });

  it("tier 2 does not open a list on the next line when Enter was typed meanwhile", async () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    m.setValue(DO.pg + "\n");
    editor.position = m.getPositionAt(m.text.length);
    await vi.advanceTimersByTimeAsync(130);
    expect(editor.triggered).toEqual([]);
  });

  it("tier 2 still opens the list while the user keeps typing the same word", async () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    m.setValue(DO.pg + "_t");
    editor.position = m.getPositionAt(m.text.length);
    await vi.advanceTimersByTimeAsync(130);
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
  });

  // Completion audit (low, was Suspected): Monaco re-focuses the first row of a list re-opened from code, so an answer
  // arriving after the user moved the selection made Enter accept another item than the one they had picked.
  it("a late tier-2 answer leaves an open list alone once the user moved its selection", async () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    editor.suggestVisible = true;
    editor.pressKey(fake.monaco.KeyCode.DownArrow);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.anything());
    expect(editor.triggered).toEqual([]);
    // Kept for the next keystroke's list.
    const r = completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    expect(r.suggestions.some((s: any) => (s.label.label ?? s.label) === "v_enter_total")).toBe(true);
  });

  it("a schema or row counts arriving leave an open list alone once the user moved its selection", () => {
    const { m, editor } = setup();
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    editor.suggestVisible = true;
    editor.pressKey(fake.monaco.KeyCode.PageDown);
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries } });
    expect(editor.triggered).toEqual([]);
  });

  it("after typing again an answer refreshes the open list as before", async () => {
    const { m, editor } = setup();
    editor.suggestVisible = true;
    editor.pressKey(fake.monaco.KeyCode.UpArrow);
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length)); // a keystroke: Monaco asked for a new list
    await vi.advanceTimersByTimeAsync(130);
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
  });

  it("an Escape before the request does not block the next word's list", async () => {
    const { m, editor } = setup();
    editor.pressKey(fake.monaco.KeyCode.Escape);
    completion("pgsql").provideCompletionItems(m, pos(m, DO.pg.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(editor.triggered).toEqual(["tier2:editor.action.triggerSuggest"]);
  });

  it("cursorInWord: false off the word, before it, or with no cursor", () => {
    const m = doc("tsql", "select * from Ord\nx");
    const e = new FakeEditor(null, {});
    e.position = m.getPositionAt(17);
    expect(P.cursorInWord(e as never, m as never, 14, "ord")).toBe(true);
    expect(P.cursorInWord(e as never, m as never, 14, "orx")).toBe(false);
    e.position = m.getPositionAt(18);
    expect(P.cursorInWord(e as never, m as never, 14, "ord")).toBe(false);
    e.position = m.getPositionAt(10);
    expect(P.cursorInWord(e as never, m as never, 14, "ord")).toBe(false);
    e.position = null;
    expect(P.cursorInWord(e as never, m as never, 14, "ord")).toBe(false);
  });
});

describe("tier 2 cache: keyed by the word, the connection and the schema version", () => {
  const labelOf = (s: any) => (typeof s.label === "string" ? s.label : s.label.label);

  it("keeps tier 2's items while the word is extended, without asking again", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Or");
    rpc.mockResolvedValue([{ label: "OnlyTier2", kind: "table", detail: "t2", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, 16));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
    m.setValue("select * from Ord");
    const r = completion("tsql").provideCompletionItems(m, pos(m, 17));
    expect(r.suggestions.some((s: any) => labelOf(s) === "OnlyTier2")).toBe(true);
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
  });

  it("asks again at the same text after a schema refresh (a new snapshot version)", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Or");
    rpc.mockResolvedValue([{ label: "OnlyTier2", kind: "table", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, 16));
    await vi.advanceTimersByTimeAsync(130);
    const e = useSnapshots.getState().entries[snapKey("p1", "SalesDB")];
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey("p1", "SalesDB")]: { ...e, version: e.version + 1 } } });
    completion("tsql").provideCompletionItems(m, pos(m, 16));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(2);
  });

  it("an older answer that arrives after a newer one is not cached over it", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Or");
    let resolveOld!: (v: unknown) => void;
    rpc.mockImplementationOnce(() => new Promise((r) => { resolveOld = r; }));
    completion("tsql").provideCompletionItems(m, pos(m, 16));
    await vi.advanceTimersByTimeAsync(130); // old request in flight
    m.setValue("select * from Orders o where o.");
    rpc.mockResolvedValueOnce([{ label: "Newer", kind: "column", isSnippet: false }]);
    const at = pos(m, m.getValue().length);
    completion("tsql").provideCompletionItems(m, at);
    await vi.advanceTimersByTimeAsync(130);
    resolveOld([{ label: "Older", kind: "table", isSnippet: false }]);
    await vi.advanceTimersByTimeAsync(0);
    const r = completion("tsql").provideCompletionItems(m, at);
    expect(r.suggestions.some((s: any) => labelOf(s) === "Newer")).toBe(true);
  });
});

describe("completion provider: schema status row", () => {
  const labelOf = (s: any) => (typeof s.label === "string" ? s.label : s.label.label);
  const statusOf = (r: any) => r.suggestions.find((s: any) => s.sortText === "!");

  it("says the schema is loading, and accepting it changes nothing", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "loading", version: 1 } } });
    const row = statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)));
    expect(labelOf(row)).toBe("Loading schema…");
    expect(row.insertText).toBe("Ord");
  });

  it("says the load failed, or the server is disconnected", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "error" as never, version: 1, failedAt: Date.now(), error: "x" } } });
    expect(labelOf(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17))))).toBe("Schema not loaded");
    useConnections.setState({ disconnected: { p1: true } as never });
    expect(labelOf(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17))))).toBe("Not connected");
  });

  it("is not shown once the schema is there", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    expect(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)))).toBeUndefined();
  });

  // Completion audit M21: the row went away as soon as an index existed, even an empty one (a first refresh still
  // running, a load the sidecar answered with an error and nothing cached), so the list said nothing at all.
  const EMPTY = buildIndex();
  function entry(status: string, extra: Record<string, unknown> = {}) {
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: {
      key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: status as never, version: 1,
      snapshot: { profileId: "p1", database: "SalesDB", status: status as never, objects: [], fks: [] }, index: EMPTY, ...extra,
    } } });
  }
  it("says the schema is loading while a refresh or a first read runs and nothing is cached yet", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    for (const status of ["refreshing", "stale", "empty", "loading"]) {
      entry(status);
      expect(labelOf(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)))), status).toBe("Loading schema…");
    }
  });
  it("says the load failed when the sidecar answered with an error and nothing is cached", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    entry("error", { error: "login failed" });
    expect(labelOf(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17))))).toBe("Schema not loaded");
  });
  it("says nothing for a database that is really empty (a fresh snapshot without objects)", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    entry("fresh");
    expect(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)))).toBeUndefined();
  });
  it("says nothing while a refresh runs over a schema already cached", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "refreshing", index: IDX, snapshot: SNAPSHOT, version: 2 } } });
    expect(statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)))).toBeUndefined();
  });

  // Second audit review: an error answer with nothing cached still builds an (empty) index, so typing never reached
  // ensure() and the row said "retrying" while nothing retried.
  it("a failed load with nothing cached is retried from typing, not only on a tab or connection change", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    entry("error", { error: "timeout", failedAt: Date.now() - 60_000, retries: 0 });
    rpc.mockReturnValue(new Promise(() => {}));
    completion("tsql").provideCompletionItems(m, pos(m, 17));
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "p1", database: "SalesDB" });
  });
  it("once the retries are used up the row says how to read it again, and typing asks no more", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    entry("error", { error: "timeout", failedAt: Date.now() - 3_600_000, retries: 8 });
    rpc.mockReturnValue(new Promise(() => {}));
    const row = statusOf(completion("tsql").provideCompletionItems(m, pos(m, 17)));
    expect(rpc).not.toHaveBeenCalledWith("meta.snapshot", expect.anything());
    expect(labelOf(row)).toBe("Schema not loaded");
    expect(row.detail).not.toContain("retrying");
  });
  it("no loading row for a session's own empty view (a new in-memory database before it ran anything)", () => {
    vi.useFakeTimers();
    useProfiles.setState({ profiles: [{ id: "mem", name: "Mem", engine: "duckDb", location: "memory", environment: "dev" } as never] });
    const m = new FakeModel("select * from Ord", "duckdb", { toString: () => "u://mem1" }) as FakeModel & { tabId: string };
    m.tabId = "mem1";
    useTabs.setState({ tabs: { ...useTabs.getState().tabs, mem1: { id: "mem1", title: "q", profileId: "mem", database: "memory", sessionId: "s1" } as never } });
    const k = snapKey("mem", "memory", "s1");
    useSnapshots.setState({ entries: { [k]: {
      key: k, profileId: "mem", database: "memory", sessionId: "s1", status: "empty", version: 1,
      snapshot: { profileId: "mem", database: "memory", sessionId: "s1", status: "empty", objects: [], fks: [] }, index: EMPTY,
    } } });
    try {
      expect(statusOf(completion("duckdb").provideCompletionItems(m, pos(m, 17)))).toBeUndefined();
    } finally {
      useProfiles.setState({ profiles: [] });
    }
  });
});

describe("tier 2 cache: a capped answer", () => {
  it("asks again when the word grows after an answer that hit the sidecar's 500-item cap", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from O");
    rpc.mockResolvedValue(Array.from({ length: 500 }, (_, i) => ({ label: `O${i}`, kind: "table", isSnippet: false })));
    completion("tsql").provideCompletionItems(m, pos(m, 15));
    await vi.advanceTimersByTimeAsync(130);
    m.setValue("select * from Or");
    rpc.mockResolvedValue([{ label: "OrdersOnlyHere", kind: "table", isSnippet: false }]);
    const meanwhile = completion("tsql").provideCompletionItems(m, pos(m, 16));
    expect(meanwhile.suggestions.some((s: any) => s.label.label === "O1")).toBe(true); // the shorter word's answer meanwhile
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(2);
    const r = completion("tsql").provideCompletionItems(m, pos(m, 16));
    expect(r.suggestions.some((s: any) => s.label.label === "OrdersOnlyHere")).toBe(true);
  });

  it("asks again for another word at the same place: the sidecar answered only the first word's matches", async () => {
    // Regression: the cache was keyed by where the word starts, not by the word, so `x` then backspace and `o` reused
    // x's filtered answer and never showed tier-2-only names starting with `o`.
    vi.useFakeTimers();
    const m = doc("tsql", "select * from x");
    rpc.mockResolvedValue([{ label: "xOnly", kind: "tempTable", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, 15));
    await vi.advanceTimersByTimeAsync(130);
    m.setValue("select * from o");
    rpc.mockResolvedValue([{ label: "oTier2Only", kind: "tempTable", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, 15));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(2);
    const r = completion("tsql").provideCompletionItems(m, pos(m, 15));
    expect(r.suggestions.some((s: any) => s.label.label === "oTier2Only")).toBe(true);
  });

  it("keeps an uncapped answer while the word grows, without asking again", async () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from o");
    rpc.mockResolvedValue([{ label: "oTier2Only", kind: "tempTable", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, 15));
    await vi.advanceTimersByTimeAsync(130);
    m.setValue("select * from oT");
    const r = completion("tsql").provideCompletionItems(m, pos(m, 16));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledTimes(1);
    expect(r.suggestions.some((s: any) => s.label.label === "oTier2Only")).toBe(true);
  });

  it("sends the text as it was when the request was scheduled, so its offset is inside it", async () => {
    // Regression: the offset was taken at schedule time and the text 120 ms later; a shortened document put the offset
    // past its end and the sidecar refused the request.
    vi.useFakeTimers();
    const text = "select * from Orders";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([]);
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    m.setValue("select");
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.objectContaining({ text, offset: text.length }));
  });
});

describe("completion provider: icons", () => {
  it("a table function and a table get different icons", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from ufn");
    const items = completion("tsql").provideCompletionItems(m, pos(m, 17)).suggestions;
    const tvf = items.find((s: any) => (s.label.label ?? s.label).includes("ufn_CustomerOrders"));
    expect(tvf.kind).toBe(fake.monaco.languages.CompletionItemKind.Constructor);
    const t = completion("tsql").provideCompletionItems(doc("tsql", "select * from Ord"), { lineNumber: 1, column: 18 } as never).suggestions.find((s: any) => s.label.label === "Orders");
    expect(t.kind).toBe(fake.monaco.languages.CompletionItemKind.Struct);
  });
});

// Code audit 2026-11-26 (providers): every case failed before its fix.
describe("completion provider: code audit 2026-11-26", () => {
  const labelOf = (s: any) => (typeof s.label === "string" ? s.label : s.label.label);
  const apply = (text: string, item: any) => {
    const r = item.range.replace ?? item.range;
    const lines = text.split("\n");
    const at = (line: number, col: number) => lines.slice(0, line - 1).reduce((n, l) => n + l.length + 1, 0) + col - 1;
    return text.slice(0, at(r.startLineNumber, r.startColumn)) + item.insertText + text.slice(at(r.endLineNumber, r.endColumn));
  };

  it("H12: a `$` typed in a routine body is not put in front of every item's filter text", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const text = "CREATE FUNCTION f(int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN $";
    const m = doc("pg", text);
    const r = completion("pgsql").provideCompletionItems(m, pos(m, text.length));
    const dollared = r.suggestions.filter((s: any) => s.filterText?.startsWith("$") && !labelOf(s).startsWith("$"));
    expect(dollared.map(labelOf).slice(0, 10)).toEqual([]);
  });
  it("H12: still, a word typed in a routine body is put in front (tier 1's order holds)", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const text = "CREATE FUNCTION f(int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN PERFORM * FROM ord";
    const m = doc("pg", text);
    const item = completion("pgsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((s: any) => labelOf(s) === "orders");
    expect(item.filterText).toBe("ordorders");
  });

  it("H12: an item only tier 2 offers in an opened quote gets the quote once in its filter text", async () => {
    vi.useFakeTimers();
    const text = "select * from [Ord";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([{ label: "OrdersArchive", kind: "table", isSnippet: false }]);
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    const item = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions.find((s: any) => labelOf(s) === "OrdersArchive");
    expect(item.filterText).toBe("[OrdOrdersArchive");
  });

  it("M15: an opened quote in a routine body is replaced, closing quote included, by the quoted name", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const head = 'DO $$ BEGIN PERFORM * FROM "Order';
    const text = head + 'Arc" x; END $$';
    const m = doc("pg", text);
    const item = completion("pgsql").provideCompletionItems(m, pos(m, head.length)).suggestions.find((s: any) => labelOf(s) === "OrderArchive");
    expect(item, JSON.stringify(completion("pgsql").provideCompletionItems(m, pos(m, head.length)).suggestions.map(labelOf).slice(0, 20))).toBeDefined();
    expect(apply(text, item)).toBe('DO $$ BEGIN PERFORM * FROM "OrderArchive" x; END $$');
  });

  it("L8: an opened bracket is replaced up to its own closing bracket, a doubled one inside it included", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const head = "select * from [Order D";
    const text = head + "]]x] o";
    const m = doc("tsql", text);
    const item = completion("tsql").provideCompletionItems(m, pos(m, head.length)).suggestions.find((s: any) => labelOf(s) === "Order Details");
    expect(apply(text, item)).toBe("select * from [Order Details] o");
  });

  it("R3-4: an opened quote with no closer of its own does not take the next name's opening quote", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const cases = [
      ["tsql", "select [Cust", ", [Status] from dbo.Customers", "CustomerId", "select [CustomerId], [Status] from dbo.Customers"],
      // The FROM before the cursor: after it, the lexer's mispaired quotes hide it from tier 1 as well.
      ["pg", 'select * from public.customers order by "Cust', ', "email"', "customer_id", 'select * from public.customers order by "customer_id", "email"'],
      ["mySql", "select * from Customers order by `Cust", ", `Status`", "CustomerId", "select * from Customers order by `CustomerId`, `Status`"],
      ["tsql", "select * from dbo.Customers order by [Cust", ", [Name]", "CustomerId", "select * from dbo.Customers order by [CustomerId], [Name]"],
    ] as const;
    for (const [d, head, tail, label, want] of cases) {
      const text = head + tail;
      const m = d === "mySql" ? mysqlDoc(text) : doc(d, text);
      const r = completion(DIALECTS[d].language).provideCompletionItems(m, pos(m, head.length));
      const item = r.suggestions.find((s: any) => labelOf(s).toLowerCase() === label.toLowerCase());
      expect(item, `${d}: ${r.suggestions.map(labelOf).slice(0, 15).join(", ")}`).toBeDefined();
      const got = apply(text, item);
      expect(got, d).toBe(want);
    }
  });
  it("R3-4: still, an opened quote around a name with a space is replaced up to its own closing quote", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const head = "select * from [Order";
    const text = head + " Details] o";
    const m = doc("tsql", text);
    const item = completion("tsql").provideCompletionItems(m, pos(m, head.length)).suggestions.find((s: any) => labelOf(s) === "Order Details");
    expect(apply(text, item)).toBe("select * from [Order Details] o");
  });

  it("M18: a refresh running after a failed load says the schema is loading, not that the load failed", () => {
    vi.useFakeTimers();
    const m = doc("tsql", "select * from Ord");
    useSnapshots.setState({ entries: { [snapKey("p1", "SalesDB")]: { key: snapKey("p1", "SalesDB"), profileId: "p1", database: "SalesDB", status: "refreshing", version: 2, failedAt: Date.now() - 1000, retries: 1 } } });
    const row = completion("tsql").provideCompletionItems(m, pos(m, 17)).suggestions.find((s: any) => s.sortText === "!");
    expect(labelOf(row)).toBe("Loading schema…");
  });

  it("L8: cancelTier2() also drops a request already sent, so its late answer changes nothing", async () => {
    vi.useFakeTimers();
    const text = "select * from Ord";
    const m = doc("tsql", text);
    const editor = new FakeEditor(null, {});
    editor.model = m;
    editor.suggestVisible = true;
    fake.created.push(editor);
    let answer!: (v: unknown) => void;
    rpc.mockReturnValue(new Promise((r) => { answer = r; }));
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    await vi.advanceTimersByTimeAsync(130);
    expect(rpc).toHaveBeenCalledWith("lang.complete", expect.anything());
    P.cancelTier2();
    answer([{ label: "OrdersLate", kind: "table", isSnippet: false }]);
    await vi.advanceTimersByTimeAsync(10);
    expect(editor.triggered).toEqual([]);
    fake.created.length = 0;
  });

  it("M17: item documentation is untrusted markdown without HTML", () => {
    vi.useFakeTimers();
    useSettings.setState({ tier2: false });
    const text = "select * from Ord";
    const m = doc("tsql", text);
    const items = completion("tsql").provideCompletionItems(m, pos(m, text.length)).suggestions;
    const withDoc = items.map((s: any) => P.resolveCompletionItem(s)).filter((s: any) => s.documentation);
    expect(withDoc.length).toBeGreaterThan(0);
    for (const s of withDoc) expect(s.documentation).toMatchObject({ isTrusted: false, supportHtml: false });
  });

  it("M18: one keystroke reads the model's text once, tier 2 scheduled included", () => {
    vi.useFakeTimers();
    const text = "select * from Ord";
    const m = doc("tsql", text);
    rpc.mockResolvedValue([]);
    const getValue = vi.spyOn(m, "getValue");
    completion("tsql").provideCompletionItems(m, pos(m, text.length));
    expect(getValue).toHaveBeenCalledTimes(1);
    const pg = "DO $$ DECLARE v_total int; BEGIN v_to";
    const b = doc("pg", pg);
    const bodyValue = vi.spyOn(b, "getValue");
    completion("pgsql").provideCompletionItems(b, pos(b, pg.length));
    expect(bodyValue).toHaveBeenCalledTimes(1);
  });
});

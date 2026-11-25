// The semantic-token provider (App Themes §5.3): the gate, the relative encoding, the statement limit per call, and the
// refresh when the cached schema changes. The spans themselves are tested per engine in completion/__tests__/semantic.test.ts.
import { afterEach, describe, expect, it, vi } from "vitest";
import { FakeModel, FakeRange, fake } from "./fakeMonaco";
import { IDX } from "../completion/__tests__/fixture";
import { snapKey, useSnapshots } from "../../store/snapshots";

vi.mock("../monaco", async () => ({ monaco: (await import("./fakeMonaco")).fake.monaco }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

const ST = await import("../semanticTokens");
const { SEMANTIC_MAX_STATEMENTS } = await import("../completion");

const tsql = { dialect: "tsql" as const, schema: null };
const model = (text: string) => new FakeModel(text, "tsql", { toString: () => "u://sem" });
const whole = (m: FakeModel) => m.getFullModelRange();
const provide = (p: ReturnType<typeof ST.semanticTokensProvider>, m: FakeModel, r: FakeRange = whole(m)) =>
  p.provideDocumentRangeSemanticTokens(m as never, r as never, {} as never) as { data: Uint32Array } | null;

afterEach(() => ST.setSemanticEnabled(false));

describe("semantic tokens provider", () => {
  it("declares one type and the cte / schema modifiers (identifier.cte, identifier.schema)", () => {
    expect(ST.semanticTokensProvider(() => tsql).getLegend()).toEqual({ tokenTypes: ["identifier"], tokenModifiers: ["cte", "schema"] });
  });

  it("answers null while gated off (the default), and for a model of no tab", () => {
    const p = ST.semanticTokensProvider(() => tsql);
    const m = model("WITH r AS (SELECT 1 AS n) SELECT * FROM r");
    expect(ST.semanticEnabled()).toBe(false);
    expect(provide(p, m)).toBeNull();
    ST.setSemanticEnabled(true);
    expect(provide(p, m)?.data.length).toBe(10);
    expect(provide(ST.semanticTokensProvider(() => null), m)).toBeNull();
  });

  it("encodes [Δline, Δchar, length, type, modifiers] relative to the previous span", () => {
    ST.setSemanticEnabled(true);
    const m = model("WITH r AS (SELECT 1 AS n)\nSELECT r.n FROM r JOIN dbo.t x ON 1 = 1");
    expect([...provide(ST.semanticTokensProvider(() => tsql), m)!.data]).toEqual([
      0, 5, 1, 0, 1, // r (definition), cte
      1, 7, 1, 0, 1, // r. (unaliased qualifier), cte
      0, 9, 1, 0, 1, // FROM r, cte
      0, 7, 3, 0, 2, // dbo, schema
    ]);
  });

  it("drops a span that crosses a line or comes out of order", () => {
    const m = model("abc\ndef\nghi");
    const data = ST.encodeSemanticSpans(m as never, [
      { start: 0, end: 3, kind: "schema" },
      { start: 2, end: 6, kind: "cte" }, // "c\nde": two lines
      { start: 4, end: 7, kind: "cte" },
      { start: 1, end: 2, kind: "cte" }, // before the previous one
      { start: 9, end: 11, kind: "schema" },
    ]);
    expect([...data]).toEqual([0, 0, 3, 0, 2, 1, 0, 3, 0, 1, 1, 1, 2, 0, 2]);
  });

  it(`analyses only the statements overlapping the range, at most ${SEMANTIC_MAX_STATEMENTS} per call`, () => {
    ST.setSemanticEnabled(true);
    const p = ST.semanticTokensProvider(() => tsql);
    const m = model(Array.from({ length: 300 }, (_, i) => `SELECT * FROM dbo.t${i};`).join("\n"));
    expect(provide(p, m)!.data.length).toBe(SEMANTIC_MAX_STATEMENTS * 5);
    const view = provide(p, m, new FakeRange(250, 1, 260, 24))!.data;
    expect(view.length).toBe(11 * 5);
    expect([...view.slice(0, 5)]).toEqual([249, 14, 3, 0, 2]);
  });

  it("passes the cached schema through: a.b( calls are coloured only when it names a", () => {
    ST.setSemanticEnabled(true);
    const m = model("SELECT dbo.fn(1), x.value('a', 'int') FROM t");
    expect(provide(ST.semanticTokensProvider(() => tsql), m)!.data.length).toBe(0);
    expect([...provide(ST.semanticTokensProvider(() => ({ dialect: "tsql", schema: IDX })), m)!.data]).toEqual([0, 7, 3, 0, 2]);
  });

  it("is registered per SQL language; a schema change fires onDidChange and, while on, registers it again", () => {
    ST.installSemanticTokens(["tsql", "pgsql"], () => tsql);
    const registered = fake.providers.tsql.semanticTokens as ReturnType<typeof ST.semanticTokensProvider>;
    expect(registered).toBeDefined();
    expect(fake.providers.pgsql.semanticTokens).toBe(registered);
    const fired = vi.fn();
    const sub = registered.onDidChange(fired);
    const spy = vi.spyOn(fake.monaco.languages, "registerDocumentRangeSemanticTokensProvider");
    const put = (e: object) => useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [snapKey("p9", "db")]: e as never } });
    const entry = { key: snapKey("p9", "db"), profileId: "p9", database: "db", status: "fresh" as const, version: 1, index: IDX };
    put(entry);
    expect(fired).toHaveBeenCalledTimes(1);
    expect(spy).not.toHaveBeenCalled(); // gated off: Monaco is not asking anyway
    ST.setSemanticEnabled(true);
    expect(fired).toHaveBeenCalledTimes(2);
    spy.mockClear();
    put({ ...entry, index: { ...IDX }, version: 2 }); // a new index: the catalog changed
    expect(fired).toHaveBeenCalledTimes(3);
    expect(spy.mock.calls.map((c) => c[0])).toEqual(["tsql", "pgsql"]);
    useSnapshots.setState({ entries: useSnapshots.getState().entries }); // entries unchanged: nothing to refresh
    expect(fired).toHaveBeenCalledTimes(3);
    sub.dispose();
    put({ ...entry, index: { ...IDX }, version: 3 });
    expect(fired).toHaveBeenCalledTimes(3);
    spy.mockRestore();
  });

  it("status moves that keep the schema (loading, refreshing, error) register nothing again; a dropped schema does", () => {
    ST.installSemanticTokens(["tsql", "pgsql"], () => tsql); // once per module: a no-op after the test above
    ST.setSemanticEnabled(true);
    const spy = vi.spyOn(fake.monaco.languages, "registerDocumentRangeSemanticTokensProvider");
    const k = snapKey("p8", "db");
    const base = { key: k, profileId: "p8", database: "db", version: 1 };
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [k]: { ...base, status: "loading" } as never } }); // no index yet
    expect(spy).not.toHaveBeenCalled();
    const index = { ...IDX };
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [k]: { ...base, status: "fresh", index } as never } });
    expect(spy).toHaveBeenCalled();
    spy.mockClear();
    for (const status of ["refreshing", "error", "fresh"]) {
      useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, [k]: { ...base, status, index, version: 9 } as never } });
    }
    useSnapshots.setState({ entries: { ...useSnapshots.getState().entries, other: { key: "other", profileId: "p7", database: "x", status: "loading", version: 1 } as never } });
    expect(spy).not.toHaveBeenCalled();
    const { [k]: _gone, ...rest } = useSnapshots.getState().entries;
    useSnapshots.setState({ entries: rest });
    expect(spy).toHaveBeenCalled();
    spy.mockRestore();
  });

  it("indexesChanged: replaced, added or dropped indexes only", () => {
    const a = { index: {} }, b = { index: {} };
    expect(ST.indexesChanged({ x: a }, { x: a })).toBe(false);
    expect(ST.indexesChanged({ x: { ...a } }, { x: a })).toBe(false); // the entry replaced, its index kept
    expect(ST.indexesChanged({ x: b }, { x: a })).toBe(true);
    expect(ST.indexesChanged({ x: a, y: {} }, { x: a })).toBe(false); // a schema still loading
    expect(ST.indexesChanged({ x: a, y: b }, { x: a })).toBe(true);
    expect(ST.indexesChanged({}, { x: a })).toBe(true);
    expect(ST.indexesChanged({}, { y: {} })).toBe(false);
  });

  it("switching off drops the tokens already applied to every model", () => {
    const setSemanticTokens = vi.fn();
    fake.models.push({ tokenization: { setSemanticTokens } } as never);
    ST.setSemanticEnabled(true);
    expect(setSemanticTokens).not.toHaveBeenCalled();
    ST.setSemanticEnabled(false);
    expect(setSemanticTokens).toHaveBeenCalledWith(null, false);
    fake.models.pop();
  });
});

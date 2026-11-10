import { describe, it, expect } from "vitest";
import type { Dialect, FindUsagesResult, UsageHit } from "../../ipc/contract";
import { DIALECT_LIST } from "../../engines";
import { applyEdits, counts, groupHits, localRenameText, targetKindOf, targetLabel } from "../refactor";

const hit = (over: Partial<UsageHit>): UsageHit => ({
  id: over.id ?? `o:${over.offset ?? 0}`, tabId: null, object: null, line: 1, column: 1, offset: 0, length: 1, snippet: "", confidence: "exact", reason: null, definition: false, ...over,
});

describe("usages", () => {
  it("groups open tabs first, then objects by name, hits by line", () => {
    const r: FindUsagesResult = {
      target: { kind: "column", schema: "dbo", name: "Orders", column: "OrderDate" }, unchecked: [], serverUpdated: [], objectsScanned: 3, elapsedMs: 4,
      hits: [
        hit({ id: "a", object: { schema: "dbo", name: "usp_b", kind: "proc", objectId: 2 }, line: 9 }),
        hit({ id: "b", object: { schema: "dbo", name: "usp_a", kind: "proc", objectId: 1 }, line: 3 }),
        hit({ id: "c", object: { schema: "dbo", name: "usp_b", kind: "proc", objectId: 2 }, line: 2 }),
        hit({ id: "d", tabId: "t1", line: 1 }),
      ],
    };
    const g = groupHits(r, (id) => (id === "t1" ? "Query1.sql" : "?"));
    expect(g.map((x) => x.label)).toEqual(["Query1.sql", "dbo.usp_a", "dbo.usp_b"]);
    expect(g[2].hits.map((h) => h.line)).toEqual([2, 9]);
    expect(counts([hit({}), hit({ confidence: "possible" })])).toEqual({ exact: 1, possible: 1 });
  });

  it("labels targets and maps explorer kinds", () => {
    expect(targetLabel({ kind: "column", schema: "dbo", name: "Orders", column: "OrderDate" })).toBe("dbo.Orders.OrderDate");
    expect(targetLabel({ kind: "table", schema: "public", name: "orders", column: null })).toBe("public.orders");
    expect(targetKindOf("proc")).toBe("procedure");
    expect(targetKindOf("tvf")).toBe("function");
    expect(targetKindOf("table")).toBe("table");
  });
});

describe("local rename", () => {
  it("keeps a variable's @ and a name's quoting, quoting new names that need it", () => {
    expect(localRenameText("@n", "count", "tsql")).toBe("@count");
    expect(localRenameText("@n", "@count", "tsql")).toBe("@count");
    expect(localRenameText("[o]", "ord", "tsql")).toBe("[ord]");
    expect(localRenameText("o", "my alias", "tsql")).toBe("[my alias]");
    expect(localRenameText("o", "a]b", "tsql")).toBe("[a]]b]");
    expect(localRenameText("recent", "latest", "pg")).toBe("latest");
    expect(localRenameText("recent", "Latest", "pg")).toBe('"Latest"');
    expect(localRenameText('"x"', "y", "pg")).toBe('"y"');
  });

  // Phase D0e leftover: a plain-looking new name that is a reserved word must be delimited, or the edit breaks the query.
  const RESERVED_RENAME: Record<Dialect, [newName: string, text: string][]> = {
    tsql: [["order", "[order]"], ["Select", "[Select]"], ["user", "[user]"], ["orders", "orders"]],
    pg: [["order", '"order"'], ["user", '"user"'], ["limit", '"limit"'], ["orders", "orders"]],
    sqlite: [["order", '"order"'], ["pragma", '"pragma"'], ["Group", '"Group"'], ["orders", "orders"]],
    mySql: [["order", "`order`"], ["key", "`key`"], ["Unsigned", "`Unsigned`"], ["orders", "orders"]],
    duckDb: [["order", '"order"'], ["qualify", '"qualify"'], ["pivot", '"pivot"'], ["orders", "orders"]],
    oracle: [["ORDER", '"ORDER"'], ["LEVEL", '"LEVEL"'], ["order", '"order"'], ["ORDERS", "ORDERS"]],
  };

  it.each(DIALECT_LIST)("%s: a reserved word as the new name is delimited the dialect's way, any case", (d) => {
    for (const [name, text] of RESERVED_RENAME[d]) expect(localRenameText("o", name, d), `${d} ${name}`).toBe(text);
  });

  it("a reserved word keeps the delimiters the user wrote, and a T-SQL variable never needs any", () => {
    expect(localRenameText("[o]", "order", "tsql")).toBe("[order]");
    expect(localRenameText('"o"', "order", "tsql")).toBe('"order"');
    expect(localRenameText("`o`", "order", "mySql")).toBe("`order`");
    expect(localRenameText("@n", "select", "tsql")).toBe("@select");
  });

  it("applies edits from the end so offsets stay valid", () => {
    const text = "SELECT o.id FROM orders o WHERE o.qty > 1";
    const spans = [7, 24, 32].map((offset) => ({ offset, length: 1, text: "ord" }));
    expect(applyEdits(text, spans)).toBe("SELECT ord.id FROM orders ord WHERE ord.qty > 1");
  });

  it("handles unsorted spans and variable prefix preservation across dialects", () => {
    const text = "DECLARE @x int = 1; SELECT @x, @x + 1;";
    const spans = [
      { offset: 31, length: 2, text: "@total" },
      { offset: 8, length: 2, text: "@total" },
      { offset: 27, length: 2, text: "@total" },
    ];
    expect(applyEdits(text, spans)).toBe("DECLARE @total int = 1; SELECT @total, @total + 1;");
    expect(localRenameText('"my""col"', 'new"col', "pg")).toBe('"new""col"');
  });
});

// Phase D0e: identifier quoting done in TypeScript goes through one per-dialect table (engines.ts). The explorer, the
// ERD's join SQL and local renames must agree with it for every dialect, including the doubled closing delimiter.
import { describe, expect, it, vi } from "vitest";
import type { Dialect } from "../../../ipc/contract";

// objectActions reaches the editor and the app actions; only its pure naming helpers are under test here.
vi.mock("../../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../../app/actions", () => ({}));
vi.mock("../../../app/tabActions", () => ({}));
vi.mock("../../../editor/editors", () => ({}));
import { DIALECT_LIST, DIALECTS } from "../../../engines";
import { quoteIdentifier } from "../quoteIdentifier";
import { quoteIdent } from "../../../erd/erdLayout";
import { localRenameText } from "../../../refactor/refactor";
import { columnName, qualified } from "../../../explorer/objectActions";

const PINNED: Record<Dialect, [name: string, sql: string][]> = {
  tsql: [
    ["Orders", "Orders"],
    ["order_lines", "order_lines"],
    ["#tmp", "#tmp"],
    ["Order", "[Order]"],
    ["select", "[select]"],
    ["my table", "[my table]"],
    ["Odd]Name", "[Odd]]Name]"],
    ["]]", "[]]]]]"],
    ['quote"d', '[quote"d]'],
    ["1st", "[1st]"],
  ],
  pg: [
    ["orders", "orders"],
    ["order_lines$2", "order_lines$2"],
    ["Orders", '"Orders"'],
    ["order", '"order"'],
    ["my table", '"my table"'],
    ['we"ird', '"we""ird"'],
    ['""', '""""""'],
    ["odd]name", '"odd]name"'],
    ["#tmp", '"#tmp"'],
  ],
  // SQLite: case-insensitive, case-preserving names; "x" doubling ". Reserved words and anything irregular are quoted.
  sqlite: [
    ["Orders", "Orders"],
    ["order_lines", "order_lines"],
    ["order", '"order"'],
    ["pragma", '"pragma"'],
    ["my table", '"my table"'],
    ['we"ird', '"we""ird"'],
    ["a$b", '"a$b"'],
    ["1st", '"1st"'],
  ],
  // MySQL: `x` doubling `; `$` may start and continue a name.
  mySql: [
    ["Orders", "Orders"],
    ["$total", "$total"],
    ["order", "`order`"],
    ["unsigned", "`unsigned`"],
    ["my table", "`my table`"],
    ["we`ird", "`we``ird`"],
    ['say"hi', '`say"hi`'],
    ["1st", "`1st`"],
  ],
  // DuckDB: case-preserving, case-insensitive names; "x" doubling ". Reserved and type/function keywords are quoted.
  duckDb: [
    ["Orders", "Orders"],
    ["order_lines$2", "order_lines$2"],
    ["order", '"order"'],
    ["qualify", '"qualify"'],
    ["pivot", '"pivot"'],
    ["my table", '"my table"'],
    ['we"ird', '"we""ird"'],
    ["1st", '"1st"'],
  ],
};

describe("quoteIdentifier (the per-dialect table)", () => {
  it.each(DIALECT_LIST)("%s: plain names stay, the rest are delimited with the closing delimiter doubled", (d) => {
    for (const [name, sql] of PINNED[d]) expect(quoteIdentifier(name, d), `${d} ${name}`).toBe(sql);
  });

  it.each(DIALECT_LIST)("%s: a delimited name is exactly the table's quote()", (d) => {
    for (const [name, sql] of PINNED[d]) if (sql !== name) expect(sql).toBe(DIALECTS[d].quote(name));
  });
});

describe("TypeScript quoting outside the engine quoting service agrees with the table", () => {
  it.each(DIALECT_LIST)("%s: ERD join SQL quotes as quoteIdentifier does", (d) => {
    for (const [name] of PINNED[d]) expect(quoteIdent(name, d)).toBe(quoteIdentifier(name, d));
  });

  it.each(DIALECT_LIST)("%s: a local rename to a name that needs delimiters uses the dialect's delimiters", (d) => {
    expect(localRenameText("o", "my alias", d)).toBe(DIALECTS[d].quote("my alias"));
    expect(localRenameText("o", "a]b\"c", d)).toBe(DIALECTS[d].quote("a]b\"c"));
  });

  it("a local rename keeps the delimiter style the user wrote, escaping it", () => {
    for (const d of DIALECT_LIST) {
      expect(localRenameText("[o]", "x]y", d)).toBe("[x]]y]");
      expect(localRenameText('"o"', 'x"y', d)).toBe('"x""y"');
      expect(localRenameText("`o`", "x`y", d)).toBe("`x``y`");
    }
  });

  it("explorer names: SQL Server brackets every column; PostgreSQL quotes only where folding would change the name", () => {
    expect(columnName("Id", "mssql")).toBe("[Id]");
    expect(columnName("a]b", "mssql")).toBe("[a]]b]");
    expect(columnName("id", "pg")).toBe("id");
    expect(columnName("Id", "pg")).toBe('"Id"');
    expect(columnName('a"b', "pg")).toBe('"a""b"');
    expect(columnName("Id", undefined)).toBe("[Id]");
    expect(qualified({ schema: "dbo", name: "Odd]Name" }, "mssql")).toBe("dbo.[Odd]]Name]");
    expect(qualified({ schema: "public", name: 'We"ird' }, "pg")).toBe('public."We""ird"');
  });

  it("explorer names on SQLite and MySQL: delimited only where needed, each in its own quotes", () => {
    expect(columnName("Id", "sqlite")).toBe("Id");
    expect(columnName("order", "sqlite")).toBe('"order"');
    expect(columnName('a"b', "sqlite")).toBe('"a""b"');
    expect(columnName("Id", "mySql")).toBe("Id");
    expect(columnName("key", "mySql")).toBe("`key`");
    expect(columnName("a`b", "mySql")).toBe("`a``b`");
    expect(qualified({ schema: "main", name: "Order Lines" }, "sqlite")).toBe('main."Order Lines"');
    expect(qualified({ schema: "shop", name: "order" }, "mySql")).toBe("shop.`order`");
  });
});

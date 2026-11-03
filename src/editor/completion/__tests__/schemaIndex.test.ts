import { describe, it, expect } from "vitest";
import type { SchemaSnapshot, SnapObject } from "../../../ipc/contract";
import { ALL_KINDS, buildSchemaIndex, findObject, isDefaultSchema, resolveTable } from "../schemaIndex";
import type { TableInScope } from "../types";
import { CUSTOMERS, IDX, ORDERS, PG_IDX, PROC, PRODUCTS, TVF } from "./fixture";

const obj = (id: number, schema: string, name: string, kind: SnapObject["kind"] = "table"): SnapObject => ({ id, schema, name, kind });
const snap = (objects: SnapObject[], o: Partial<SchemaSnapshot> = {}): SchemaSnapshot => ({
  profileId: "p", database: "Db", status: "fresh", objects, fks: [], ...o,
});
const ref = (name: string, o: Partial<TableInScope> = {}): TableInScope => ({ name, isCte: false, isDerived: false, ...o });

describe("schemaIndex: build", () => {
  it("default schema follows the dialect", () => {
    expect(IDX.defaultSchema).toBe("dbo");
    expect(PG_IDX.defaultSchema).toBe("public");
  });
  it("tolerates a missing snapshot and missing arrays", () => {
    const idx = buildSchemaIndex(undefined as unknown as SchemaSnapshot, "tsql", undefined as unknown as string[]);
    expect(idx.objects).toEqual([]);
    expect(idx.fks).toEqual([]);
    expect(idx.databases).toEqual([]);
    expect(idx.profileId).toBe("");
    expect(idx.database).toBe("");
  });
  it("drops null objects and objects without a string name", () => {
    const bad = [null, { id: 9, schema: "dbo", kind: "table" }, obj(1, "dbo", "T")] as unknown as SnapObject[];
    const idx = buildSchemaIndex(snap(bad), "tsql", []);
    expect(idx.objects.map((o) => o.name)).toEqual(["T"]);
  });
  it("byName groups same-named objects across schemas, case-insensitively", () => {
    const idx = buildSchemaIndex(snap([obj(1, "dbo", "Orders"), obj(2, "sales", "ORDERS")]), "tsql", []);
    expect(idx.byName.get("orders")?.map((o) => o.id)).toEqual([1, 2]);
    expect(idx.byQualified.get("sales.orders")?.id).toBe(2);
    expect(idx.byId.get(1)?.schema).toBe("dbo");
  });
  it("a later duplicate schema.name replaces the earlier one in byQualified", () => {
    const idx = buildSchemaIndex(snap([obj(1, "dbo", "T"), obj(2, "DBO", "t")]), "tsql", []);
    expect(idx.byQualified.get("dbo.t")?.id).toBe(2);
    expect(idx.byName.get("t")).toHaveLength(2);
  });
  it("schemas are distinct (first casing wins), sorted, and exclude the empty schema", () => {
    const idx = buildSchemaIndex(snap([obj(1, "sales", "a"), obj(2, "", "b"), obj(3, "SALES", "c"), obj(4, "audit", "d")]), "tsql", []);
    expect(idx.schemas).toEqual(["audit", "sales"]);
    expect(idx.byQualified.get(".b")?.id).toBe(2);
  });
  it("databases are de-duplicated case-insensitively, keeping the first spelling and order", () => {
    const idx = buildSchemaIndex(snap([]), "tsql", ["Sales", "", "master", "SALES", 7 as unknown as string, "Archive"]);
    expect(idx.databases).toEqual(["Sales", "master", "Archive"]);
  });
});

describe("schemaIndex: findObject", () => {
  const twoSchemas = buildSchemaIndex(snap([obj(1, "sales", "Orders"), obj(2, "dbo", "Orders"), obj(3, "audit", "Log")]), "tsql", []);

  it("empty name finds nothing", () => expect(findObject(IDX, "")).toBeUndefined());
  it("qualified lookup is case-insensitive", () => expect(findObject(IDX, "orders", "DBO")).toBe(ORDERS));
  it("qualified lookup does not fall back to another schema", () => expect(findObject(IDX, "Orders", "sales")).toBeUndefined());
  it("unqualified lookup prefers the default schema even when listed later", () => expect(findObject(twoSchemas, "orders")?.id).toBe(2));
  it("unqualified lookup falls back to the first non-default match", () => expect(findObject(twoSchemas, "log")?.id).toBe(3));
  it("an empty schema string behaves like no schema", () => expect(findObject(twoSchemas, "Orders", "")?.id).toBe(2));
  it("non-relation kinds are filtered out by default", () => {
    expect(findObject(IDX, PROC.name)).toBeUndefined();
    expect(findObject(IDX, PROC.name, "dbo")).toBeUndefined();
    expect(findObject(IDX, PROC.name, "dbo", ALL_KINDS)).toBe(PROC);
  });
  it("TVFs count as relations", () => expect(findObject(IDX, TVF.name)).toBe(TVF));
  it("kind filter skips a default-schema object of the wrong kind", () => {
    const idx = buildSchemaIndex(snap([obj(1, "dbo", "X", "proc"), obj(2, "sales", "X", "table")]), "tsql", []);
    expect(findObject(idx, "x")?.id).toBe(2);
  });
});

describe("schemaIndex: resolveTable", () => {
  it("no index", () => expect(resolveTable(null, ref("Orders"))).toBeUndefined());
  it("CTEs and derived tables never resolve to snapshot objects", () => {
    expect(resolveTable(IDX, ref("Orders", { isCte: true }))).toBeUndefined();
    expect(resolveTable(IDX, ref("Orders", { isDerived: true }))).toBeUndefined();
  });
  it("a reference into another database does not resolve", () => expect(resolveTable(IDX, ref("Orders", { database: "Archive" }))).toBeUndefined());
  it("the current database (any case) resolves", () => expect(resolveTable(IDX, ref("Customers", { database: "salesdb", schema: "dbo" }))).toBe(CUSTOMERS));
  it("schema-qualified reference outside the default schema", () => expect(resolveTable(IDX, ref("products", { schema: "Sales" }))).toBe(PRODUCTS));
});

describe("schemaIndex: isDefaultSchema", () => {
  it("case-insensitive per dialect", () => {
    expect(isDefaultSchema(IDX, "DBO")).toBe(true);
    expect(isDefaultSchema(IDX, "public")).toBe(false);
    expect(isDefaultSchema(PG_IDX, "Public")).toBe(true);
  });
});

describe("schemaIndex: triggers", () => {
  it("leaves triggers out, so a trigger named like its table never shadows it or shows in completion", async () => {
    const { complete } = await import("../complete");
    const table = { ...obj(1, "public", "orders"), columns: [{ name: "id", type: "int", nullable: false, identity: false, computed: false, pk: true, ordinal: 1 }] };
    const idx = buildSchemaIndex(snap([table, { ...obj(2, "public", "orders", "trigger"), parentObjectId: 1 }, { ...obj(3, "public", "trg_x", "trigger"), parentObjectId: 1 }]), "pg", []);
    expect(idx.objects.map((o) => o.id)).toEqual([1]);
    expect(findObject(idx, "orders", "public")).toBe(table);
    expect(idx.byId.has(2)).toBe(false);
    const text = "SELECT * FROM ";
    const items = complete({ text, offset: text.length, dialect: "pg", schema: idx }).items;
    expect(items.some((i) => i.label === "trg_x")).toBe(false);
    expect(items.some((i) => i.label === "orders")).toBe(true);
  });
});

describe("default schema per dialect", () => {
  it("SQLite resolves unqualified names in main; MySQL in the snapshot's own database (the database is the schema)", () => {
    const s = buildSchemaIndex(snap([obj(1, "main", "t"), obj(2, "temp", "t")], { database: "main" }), "sqlite", ["main"]);
    expect(s.defaultSchema).toBe("main");
    expect(findObject(s, "t")?.id).toBe(1);
    const m = buildSchemaIndex(snap([obj(1, "other", "t"), obj(2, "shop", "t")], { database: "shop" }), "mySql", ["shop", "other"]);
    expect(m.defaultSchema).toBe("shop");
    expect(findObject(m, "t")?.id).toBe(2);
    expect(isDefaultSchema(m, "shop")).toBe(true);
  });
});

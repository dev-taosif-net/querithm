import type { SchemaSnapshot, SnapColumn, SnapFk, SnapObject } from "../../ipc/contract";

const col = (name: string, o: Partial<SnapColumn> = {}): SnapColumn => ({
  name, type: "int", nullable: false, identity: false, computed: false, pk: false, default: null, ordinal: 0, ...o,
});
const table = (id: number, name: string, columns: SnapColumn[], schema = "dbo"): SnapObject => ({
  id, schema, name, kind: "table", columns, params: null, returnType: null, modifyDate: null,
});
const fk = (name: string, parent: number, referenced: number, pairs: [string, string][]): SnapFk => ({
  name, parent, referenced, columns: pairs.map(([p, r]) => ({ parent: p, referenced: r })),
});

/** Customers <- Orders <- OrderLines -> Products, Employees (self FK), Isolated, sales.Region. */
export function erdSnapshot(): SchemaSnapshot {
  return {
    profileId: "p", database: "db", status: "fresh", refreshedAt: 0, serverVersion: null, error: null,
    objects: [
      table(1, "Customers", [col("CustomerId", { pk: true }), col("Name", { type: "nvarchar(50)" })]),
      table(2, "Orders", [col("OrderId", { pk: true }), col("CustomerId")]),
      table(3, "OrderLines", [col("OrderId", { pk: true }), col("ProductId", { nullable: true })]),
      table(4, "Products", [col("ProductId", { pk: true })]),
      table(5, "Employees", [col("EmployeeId", { pk: true }), col("ManagerId", { nullable: true })]),
      table(6, "Isolated", [col("Id", { pk: true })]),
      table(7, "Region", [col("RegionId", { pk: true })], "sales"),
    ],
    fks: [
      fk("FK_Orders_Customers", 2, 1, [["CustomerId", "CustomerId"]]),
      fk("FK_Lines_Orders", 3, 2, [["OrderId", "OrderId"]]),
      fk("FK_Lines_Products", 3, 4, [["ProductId", "ProductId"]]),
      fk("FK_Emp_Manager", 5, 5, [["ManagerId", "EmployeeId"]]),
    ],
  };
}

// Phase D engines, shaped as each provider's catalog sends them (the sidecar's per-engine catalog tests use the same
// objects): SQLite names its foreign keys fk_<table>_<id> and keeps everything in "main" (temporary objects in "temp");
// MySQL's schema is the database itself; DuckDB has schemas of its own and keeps exact DECIMAL types.
const view = (id: number, name: string, schema: string): SnapObject => ({ ...table(id, name, [], schema), kind: "view" });

/** SQLite: customer ← "order" (a reserved word) ← order_line → product; a session's temporary table; a view. */
export function sqliteSnapshot(database = "main"): SchemaSnapshot {
  return {
    profileId: "p", database, status: "fresh", refreshedAt: 0, serverVersion: "3.50.4", error: null,
    objects: [
      table(1, "customer", [col("id", { type: "INTEGER", pk: true }), col("name", { type: "TEXT" })], "main"),
      table(2, "order", [col("id", { type: "INTEGER", pk: true }), col("customer_id", { type: "INTEGER", nullable: true }), col("total", { type: "REAL", nullable: true })], "main"),
      table(3, "order_line", [col("order_id", { type: "INTEGER", pk: true }), col("line_no", { type: "INTEGER", pk: true }), col("product_id", { type: "INTEGER" })], "main"),
      table(4, "product", [col("id", { type: "INTEGER", pk: true }), col("label", { type: "", nullable: true })], "main"),
      table(5, "scratch", [col("x", { type: "", nullable: true })], "temp"),
      view(6, "big", "main"),
    ],
    fks: [fk("fk_order_0", 2, 1, [["customer_id", "id"]]), fk("fk_order_line_0", 3, 2, [["order_id", "id"]]), fk("fk_order_line_1", 3, 4, [["product_id", "id"]])],
  };
}

/** MySQL / MariaDB: the schema is the database ("shop"); m_parent ← m_child ← `order`, whose FK column is `key`. */
export function mySqlSnapshot(database = "shop"): SchemaSnapshot {
  return {
    profileId: "p", database, status: "fresh", refreshedAt: 0, serverVersion: "8.4.13", error: null,
    objects: [
      table(11, "m_parent", [col("id", { pk: true }), col("name", { type: "varchar(20)" })], database),
      table(12, "m_child", [col("id", { pk: true }), col("parent_id", { nullable: true })], database),
      table(13, "order", [col("id", { type: "bigint unsigned", pk: true }), col("key", { nullable: true }), col("flags", { type: "bit(8)", nullable: true })], database),
      view(14, "m_view", database),
    ],
    fks: [fk("fk_child", 12, 11, [["parent_id", "id"]]), fk("order_ibfk_1", 13, 12, [["key", "id"]])],
  };
}

/** DuckDB: main.customer ← main."order"; crm.region on its own; exact DECIMAL(10,2) and HUGEINT types. */
export function duckDbSnapshot(database = "sales"): SchemaSnapshot {
  return {
    profileId: "p", database, status: "fresh", refreshedAt: 0, serverVersion: "v1.5.6", error: null,
    objects: [
      table(21, "customer", [col("id", { type: "INTEGER", pk: true }), col("name", { type: "VARCHAR" }), col("credit", { type: "DECIMAL(10,2)", nullable: true })], "main"),
      table(22, "order", [col("id", { type: "BIGINT", pk: true }), col("customer_id", { type: "INTEGER", nullable: true }), col("total", { type: "HUGEINT", nullable: true })], "main"),
      table(23, "region", [col("code", { type: "VARCHAR", pk: true })], "crm"),
      view(24, "big", "main"),
    ],
    fks: [fk("order_customer_id_fkey", 22, 21, [["customer_id", "id"]])],
  };
}

/**
 * Oracle (D4 S12): the login schema APP (names folded to upper case) — CUSTOMER ← "ORDER" (a reserved word) ← ORDERS, and
 * "orders", created quoted in lower case beside ORDERS; HR.REGION in another schema; a view. The database is the service.
 */
export function oracleSnapshot(database = "FREEPDB1"): SchemaSnapshot {
  const num = (p: number) => ({ type: `NUMBER(${p})` });
  return {
    profileId: "p", database, status: "fresh", refreshedAt: 0, serverVersion: "Oracle AI Database 26ai Free 23.26.0.0.0", error: null,
    objects: [
      table(31, "CUSTOMER", [col("ID", { ...num(10), pk: true }), col("NAME", { type: "VARCHAR2(40)" })], "APP"),
      table(32, "ORDER", [col("ID", { ...num(10), pk: true }), col("CUSTOMER_ID", { ...num(10), nullable: true })], "APP"),
      table(33, "orders", [col("ID", { ...num(10), pk: true })], "APP"),
      table(34, "ORDERS", [col("ID", { ...num(10), pk: true }), col("ORDER_ID", { ...num(10), nullable: true }), col("AMOUNT", { type: "NUMBER(12,2)", nullable: true })], "APP"),
      table(35, "REGION", [col("CODE", { type: "VARCHAR2(3)", pk: true })], "HR"),
      view(36, "V_ORDERS", "APP"),
    ],
    fks: [fk("ORDER_CUSTOMER_FK", 32, 31, [["CUSTOMER_ID", "ID"]]), fk("ORDERS_ORDER_FK", 34, 32, [["ORDER_ID", "ID"]])],
  };
}

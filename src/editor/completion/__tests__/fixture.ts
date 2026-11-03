// SalesDB-like snapshot fixtures for the tier-1 completion tests.
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject, SnapParam } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import { scan } from "../scanner";
import type { CursorContext, SchemaIndex, Tier1Item } from "../types";

const col = (name: string, type: string, o: Partial<SnapColumn> = {}): SnapColumn => ({
  name, type, nullable: false, identity: false, computed: false, pk: false, ordinal: 0, ...o,
});
const prm = (name: string, type: string, ordinal: number, o: Partial<SnapParam> = {}): SnapParam => ({
  name, type, ordinal, output: false, hasDefault: false, ...o,
});

export const ORDERS: SnapObject = {
  id: 1, schema: "dbo", name: "Orders", kind: "table",
  columns: [
    col("OrderId", "bigint", { pk: true, identity: true }),
    col("CustomerId", "int"),
    col("OrderDate", "datetime2"),
    col("TotalAmount", "decimal(18,2)"),
    col("Status", "nvarchar(20)", { nullable: true }),
  ],
};
export const CUSTOMERS: SnapObject = {
  id: 2, schema: "dbo", name: "Customers", kind: "table",
  columns: [col("CustomerId", "int", { pk: true }), col("FullName", "nvarchar(200)"), col("Email", "nvarchar(320)", { nullable: true })],
};
export const ORDER_ITEMS: SnapObject = {
  id: 3, schema: "dbo", name: "OrderItems", kind: "table",
  columns: [
    col("OrderItemId", "int", { pk: true, identity: true }),
    col("OrderId", "bigint"),
    col("ProductId", "int"),
    col("Quantity", "int"),
    col("UnitPrice", "decimal(18,2)"),
    col("LineTotal", "decimal(29,2)", { computed: true, nullable: true }),
  ],
};
export const PRODUCTS: SnapObject = {
  id: 4, schema: "sales", name: "Products", kind: "table",
  columns: [col("ProductId", "int", { pk: true }), col("Name", "nvarchar(100)"), col("ListPrice", "money")],
};
export const VIEW: SnapObject = {
  id: 5, schema: "dbo", name: "vCustomerOrders", kind: "view",
  columns: [col("CustomerId", "int"), col("FullName", "nvarchar(200)"), col("OrderCount", "int")],
};
export const PROC: SnapObject = {
  id: 6, schema: "dbo", name: "usp_GetCustomerOrders", kind: "proc",
  params: [
    prm("@CustomerId", "int", 1),
    prm("@StartDate", "datetime2", 2, { hasDefault: true }),
    prm("@MinTotal", "decimal(18,2)", 3, { output: true }),
  ],
};
export const PROC2: SnapObject = { id: 7, schema: "dbo", name: "usp_GetOrderDetails", kind: "proc", params: [prm("@OrderId", "bigint", 1)] };
export const FUNC: SnapObject = {
  id: 8, schema: "dbo", name: "fn_OrderTotal", kind: "func", returnType: "decimal(18,2)", params: [prm("@OrderId", "bigint", 1)],
};
export const TVF: SnapObject = {
  id: 9, schema: "dbo", name: "ufn_CustomerOrders", kind: "tvf", params: [prm("@CustomerId", "int", 1)],
  columns: [col("OrderId", "bigint"), col("OrderDate", "datetime2"), col("TotalAmount", "decimal(18,2)")],
};
export const PROC3: SnapObject = { id: 10, schema: "sales", name: "usp_RefreshPrices", kind: "proc", params: [] };
export const ODETAILS: SnapObject = { id: 11, schema: "dbo", name: "Order Details", kind: "table", columns: [col("Id", "int", { pk: true })] };
export const UDT: SnapObject = { id: 12, schema: "dbo", name: "Phone", kind: "type" };
export const METRICS: SnapObject = {
  id: 13, schema: "dbo", name: "Metrics", kind: "table",
  columns: [col("AcidLevel", "int"), col("CustomerId", "int"), col("CidCode", "int")],
};
export const SHOPS: SnapObject = { id: 14, schema: "dbo", name: "Shops", kind: "table", columns: [col("ShopId", "int")] };

export const SNAPSHOT: SchemaSnapshot = {
  profileId: "p1",
  database: "SalesDB",
  status: "fresh",
  objects: [ORDERS, CUSTOMERS, ORDER_ITEMS, PRODUCTS, VIEW, PROC, PROC2, FUNC, TVF, PROC3, ODETAILS, UDT, METRICS, SHOPS],
  fks: [
    { name: "FK_Orders_Customers", parent: 1, referenced: 2, columns: [{ parent: "CustomerId", referenced: "CustomerId" }] },
    { name: "FK_OrderItems_Orders", parent: 3, referenced: 1, columns: [{ parent: "OrderId", referenced: "OrderId" }] },
    { name: "FK_OrderItems_Products", parent: 3, referenced: 4, columns: [{ parent: "ProductId", referenced: "ProductId" }] },
  ],
};

export const IDX: SchemaIndex = buildSchemaIndex(SNAPSHOT, "tsql", ["master", "Archive", "SalesDB"]);

export const PG_SNAPSHOT: SchemaSnapshot = {
  profileId: "p2",
  database: "shop",
  status: "fresh",
  objects: [
    {
      id: 101, schema: "public", name: "orders", kind: "table",
      columns: [col("order_id", "bigint", { pk: true, identity: true }), col("customer_id", "integer"), col("created_at", "timestamptz"), col("total", "numeric")],
    },
    { id: 102, schema: "public", name: "customers", kind: "table", columns: [col("customer_id", "integer", { pk: true }), col("full_name", "text"), col("email", "text", { nullable: true })] },
    { id: 103, schema: "reporting", name: "daily_sales", kind: "view", columns: [col("day", "date"), col("total", "numeric")] },
    { id: 104, schema: "public", name: "refresh_stats", kind: "proc", params: [prm("p_day", "date", 1), prm("p_force", "boolean", 2, { hasDefault: true })] },
    { id: 105, schema: "public", name: "order_total", kind: "func", returnType: "numeric", params: [prm("p_order_id", "bigint", 1)] },
    { id: 106, schema: "public", name: "OrderArchive", kind: "table", columns: [col("id", "bigint")] },
  ],
  fks: [{ name: "orders_customer_fk", parent: 101, referenced: 102, columns: [{ parent: "customer_id", referenced: "customer_id" }] }],
};
export const PG_IDX: SchemaIndex = buildSchemaIndex(PG_SNAPSHOT, "pg", ["shop", "postgres"]);

/** Split `sql` at the `|` cursor marker. */
export function at(sql: string): { text: string; offset: number } {
  const i = sql.indexOf("|");
  if (i < 0) throw new Error("no cursor marker");
  return { text: sql.slice(0, i) + sql.slice(i + 1), offset: i };
}

export function ctxOf(sql: string, dialect: Dialect = "tsql"): CursorContext {
  const { text, offset } = at(sql);
  return scan(text, offset, dialect);
}

export function itemsOf(sql: string, dialect: Dialect = "tsql", schema: SchemaIndex | null = dialect === "pg" ? PG_IDX : IDX, usage?: Map<string, number>): Tier1Item[] {
  const { text, offset } = at(sql);
  return complete({ text, offset, dialect, schema, usage }).items;
}

export const labels = (items: Tier1Item[]): string[] => items.map((i) => i.label);
export const byLabel = (items: Tier1Item[], label: string): Tier1Item | undefined => items.find((i) => i.label === label);

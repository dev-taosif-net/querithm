// Tier-1 completion corpus (completion audit 2026-11-25, Phase 0 item 7): per engine, SQL snippets with a `|` cursor and
// what the list must and must never offer there. Every tier-1 repro of the audit is a case (its id in `audit`).
//
// Item patterns: `label`, `kind:label` (kinds as Tier1Item.kind), a trailing `*` matches a label prefix. Labels compare
// case-insensitively (Oracle folds unquoted names to upper case, the SQL here is written in lower case).
// `insert` maps a label to its exact insert text. `empty`: no item at all. `unique`: no two items of one kind and label.
import type { Dialect } from "../../../../ipc/contract";

export const CORPUS_ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
export type CorpusEngine = (typeof CORPUS_ENGINES)[number];

export interface CorpusCase {
  sql: string;
  offer?: string[];
  notOffer?: string[];
  insert?: Record<string, string>;
  empty?: boolean;
  unique?: boolean;
  /** Engines a SHARED case does not apply to (their own case is in SPECIFIC). */
  except?: CorpusEngine[];
  /** The audit finding the case pins (H6, M9, …). */
  audit?: string;
}

/** Cases every engine answers the same way (names folded for Oracle by the schema, not by the SQL). */
export const SHARED: CorpusCase[] = [
  { sql: "SELECT * FROM |", offer: ["table:orders", "table:customers", "view:v_sales", "table:sales.products"], notOffer: ["column:*"] },
  { sql: "SELECT * FROM ord|", offer: ["table:orders", "table:order_items"], notOffer: ["table:customers"] },
  { sql: "SELECT * FROM sales.|", offer: ["table:products"], notOffer: ["table:orders", "table:customers"] },
  { sql: "SELECT * FROM orders |", offer: ["keyword:WHERE"], notOffer: ["table:customers", "column:*"] },
  { sql: "SELECT * FROM orders AS |", empty: true },
  { sql: "SELECT total AS |", empty: true },
  { sql: "SELECT * FROM orders -- FROM |", empty: true },
  { sql: "SELECT 'FROM |' FROM orders", empty: true },
  { sql: "SELECT * FROM orders /* JOIN | */", empty: true },
  { sql: "SELECT o.| FROM orders o", offer: ["column:order_id", "column:status"], notOffer: ["column:full_name", "table:*", "keyword:*"] },
  { sql: "SELECT | FROM orders o", offer: ["column:o.order_id", "column:o.total"], notOffer: ["column:full_name"] },
  { sql: "SELECT o.zzq| FROM orders o", notOffer: ["column:*"] },
  { sql: "SELECT * FROM orders o WHERE o.status = 'x' AND |", offer: ["column:o.total"], notOffer: ["table:customers"] },
  { sql: "SELECT * FROM orders o JOIN |", offer: ["table:customers", "table:order_items", "snippet:customers ON*"], notOffer: ["column:*"] },
  { sql: "SELECT * FROM orders o JOIN customers c ON |", offer: ["snippet:c.customer_id = o.customer_id", "column:c.email"] },
  { sql: "SELECT * FROM orders o JOIN customers c ON c.customer_id = o.customer_id WHERE c.|", offer: ["column:email"], notOffer: ["column:status"] },
  { sql: "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.customer_id = o.|)", offer: ["column:status"] },
  { sql: "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM |", offer: ["table:customers"], notOffer: ["column:*"] },
  { sql: "WITH x AS (SELECT order_id, total FROM orders) SELECT x.| FROM x", offer: ["column:order_id", "column:total"], notOffer: ["column:status"] },
  { sql: "SELECT d.| FROM (SELECT order_id, total FROM orders) d", offer: ["column:order_id", "column:total"], notOffer: ["column:status", "column:customer_id"] },
  { sql: "SELECT * FROM orders o ORDER BY |", offer: ["column:o.order_id"] },
  { sql: "SELECT o.| FROM orders o;\nSELECT * FROM customers c", offer: ["column:status"], notOffer: ["column:email"] },
  { sql: "INSERT INTO |", offer: ["table:orders"], notOffer: ["column:*"] },
  { sql: "INSERT INTO orders (|", offer: ["column:status", "column:total"], notOffer: ["column:full_name", "table:customers"] },
  { sql: "INSERT INTO orders (order_id, |", offer: ["column:status"], notOffer: ["column:order_id"] },
  { sql: "DELETE FROM orders WHERE |", offer: ["column:order_id"], notOffer: ["table:customers"] },
  { sql: "UPDATE orders SET |", offer: ["column:status", "column:total"], notOffer: ["column:o.status", "table:customers", "column:full_name"] },
  // H6: names spelled like statement verbs never split the statement.
  { sql: "SELECT * FROM posts p WHERE p.comment LIKE 'x' AND p.open = 1 AND |", offer: ["column:p.title"], audit: "H6" },
  { sql: "SELECT p.comment, | FROM posts p", offer: ["column:p.title"], audit: "H6" },
  { sql: "UPDATE posts SET comment = 'x', | WHERE id = 1", offer: ["column:title"], audit: "H6" },
  { sql: "SELECT * FROM posts p WHERE p.id = open AND |", offer: ["column:p.title"], audit: "H6" },
  // H8: no list inside or right after a number.
  { sql: "SELECT * FROM orders WHERE total > 1.|", empty: true, audit: "H8" },
  { sql: "SELECT * FROM orders WHERE total > 1.5|", empty: true, audit: "H8" },
  // Low (GROUP BY): a non-aggregated column is one item, not two.
  { sql: "SELECT status, COUNT(*) FROM orders GROUP BY |", offer: ["snippet:(all non-aggregated columns)", "column:status"], unique: true, audit: "low: duplicate GROUP BY item" },
  {
    sql: "SELECT o.status, c.full_name, COUNT(*) FROM orders o JOIN customers c ON c.customer_id = o.customer_id GROUP BY |",
    offer: ["column:o.status", "column:c.full_name"], unique: true, audit: "low: duplicate GROUP BY item",
  },
  // M26: a foreign key without its referenced column list (SQLite `REFERENCES products`) gives no broken JOIN snippet.
  { sql: "SELECT * FROM order_items i JOIN |", offer: ["table:orders", "snippet:orders ON*"], audit: "M26" },
  { sql: "SELECT * FROM order_items i JOIN sales.products p ON |", offer: ["column:p.product_id"], audit: "M26" },
  // M9 (the right side of SET keeps every source).
  { sql: "UPDATE orders SET status = | WHERE order_id = 1", offer: ["column:total"], audit: "M9" },
  // A derived table of a FROM list (not LATERAL) and a CTE body see only what they declare: no engine lets them name
  // the outer query's sources (completion audit M1, found on every engine's tier 1 too).
  { sql: "SELECT * FROM orders o JOIN (SELECT | FROM customers) d ON 1 = 1", offer: ["column:full_name", "column:email"], notOffer: ["column:o.*", "column:customers.*", "alias:o", "alias:d"], audit: "M1", except: ["duckDb"] },
  { sql: "SELECT * FROM orders o, (SELECT | FROM customers) d", offer: ["column:full_name"], notOffer: ["column:o.*", "alias:o", "alias:d"], audit: "M1", except: ["duckDb"] },
  { sql: "SELECT * FROM orders o JOIN (SELECT | FROM customers", offer: ["column:full_name"], notOffer: ["column:o.*", "alias:o"], audit: "M1", except: ["duckDb"] },
  { sql: "WITH x AS (SELECT | FROM customers) SELECT * FROM orders o", offer: ["column:full_name"], notOffer: ["column:o.*", "alias:o"], audit: "M1" },
  {
    sql: "SELECT * FROM orders o JOIN (SELECT c.customer_id FROM customers c WHERE EXISTS (SELECT 1 FROM order_items i WHERE i.order_id = |)) d ON 1 = 1",
    offer: ["column:c.full_name", "column:i.qty"], notOffer: ["column:o.*", "alias:o"], audit: "M1", except: ["duckDb"],
  },
  // Still: a subquery in an expression is correlated, it sees the outer query.
  { sql: "SELECT (SELECT | FROM customers) FROM orders o", offer: ["column:o.status", "column:customers.full_name"], audit: "M1" },
  { sql: "SELECT * FROM orders o WHERE o.total > (SELECT AVG(i.qty) FROM order_items i WHERE i.order_id = |)", offer: ["column:o.order_id"], audit: "M1" },
];

/** LATERAL (…) sees the FROM items before it (PostgreSQL, MySQL, DuckDB, Oracle). */
const LATERAL: CorpusCase[] = [
  { sql: "SELECT * FROM orders o JOIN LATERAL (SELECT | FROM customers) d ON true", offer: ["column:o.status", "column:customers.full_name"], audit: "M1" },
  { sql: "SELECT * FROM orders o, LATERAL (SELECT | FROM customers) d", offer: ["column:o.status"], audit: "M1" },
];
/** CROSS / OUTER APPLY (…) sees the FROM items before it (SQL Server, Oracle). */
const APPLY: CorpusCase[] = [
  { sql: "SELECT * FROM orders o CROSS APPLY (SELECT | FROM customers) d", offer: ["column:o.status", "column:customers.full_name"], audit: "M1" },
  { sql: "SELECT * FROM orders o OUTER APPLY (SELECT | FROM customers) d", offer: ["column:o.status"], audit: "M1" },
];

/** Cases for one engine (its own syntax, quoting and function catalog). */
export const SPECIFIC: Record<CorpusEngine, CorpusCase[]> = {
  tsql: [
    ...APPLY,
    { sql: "SELECT TOP 10 * FROM |", offer: ["table:orders"] },
    { sql: "SELECT * FROM orders WITH (NOLOCK) JOIN |", offer: ["table:customers"] },
    { sql: "SELECT * FROM orders o CROSS APPLY |", offer: ["table:customers"] },
    { sql: "DECLARE @x int = 1;\nSELECT * FROM orders WHERE total = |", offer: ["variable:@x", "column:total"] },
    { sql: "EXEC refresh_totals |", offer: ["parameter:@p_from", "parameter:@p_to"], notOffer: ["proc:refresh_totals"] },
    { sql: "SELECT * FROM [ord|", offer: ["table:orders"], insert: { orders: "[orders]" }, notOffer: ["keyword:*"] },
    { sql: "SELECT * FROM [orders]|", offer: ["keyword:WHERE"] },
    { sql: "SELECT CONVERT(|, total) FROM orders", offer: ["type:int"], notOffer: ["column:total"], audit: "M19" },
    { sql: "SELECT CONVERT(int, |) FROM orders", offer: ["column:total"], notOffer: ["type:int"], audit: "M19" },
    { sql: "CREATE PROCEDURE dbo.p AS\nSET NOCOUNT ON\nUPDATE orders SET status = 'x' WHERE |", offer: ["column:order_id"] },
    { sql: "WITH c AS (SELECT order_id, total AS amt FROM orders) INSERT INTO order_items (order_id, qty) SELECT c.| FROM c", offer: ["column:amt"] },
    { sql: "SELECT * FROM orders\nGO\nSELECT * FROM |", offer: ["table:customers"] },
    // M9: an UPDATE … FROM's SET targets are the updated table's columns, bare.
    { sql: "UPDATE o SET | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", offer: ["column:status", "column:total"], notOffer: ["column:full_name", "column:c.full_name", "column:c.email", "column:o.status", "alias:c"], audit: "M9" },
    { sql: "UPDATE o SET status = | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", offer: ["column:c.full_name", "column:o.total"], audit: "M9" },
    { sql: "MERGE INTO orders t USING customers s ON t.customer_id = s.customer_id WHEN MATCHED THEN UPDATE SET |", offer: ["column:status"], notOffer: ["column:s.email", "column:email", "column:t.status"], audit: "M9" },
    { sql: "MERGE INTO orders t USING customers s ON t.customer_id = s.customer_id WHEN MATCHED THEN UPDATE SET status = |", offer: ["column:s.email"], audit: "M9" },
  ],
  pg: [
    ...LATERAL,
    { sql: "SELECT * FROM orders o, LATERAL (SELECT * FROM |) x", offer: ["table:customers"] },
    { sql: "SELECT * FROM orders WHERE order_id = $1 AND |", offer: ["column:status"] },
    { sql: "CALL refresh_totals(|", offer: ["parameter:p_from"], notOffer: ["proc:refresh_totals"] },
    { sql: "SELECT | FROM (SELECT CustomerId FROM orders) d", insert: { "d.CustomerId": "d.CustomerId" }, audit: "H7" },
    { sql: "WITH x AS (SELECT 1 AS order) SELECT | FROM x", insert: { order: '"order"' }, audit: "H7" },
    { sql: "SELECT current_| FROM orders", insert: { current_date: "current_date" }, audit: "M18" },
    { sql: 'SELECT * FROM "ord|', offer: ["table:orders"], insert: { orders: '"orders"' }, notOffer: ["keyword:*"] },
    { sql: 'SELECT * FROM "orders"|', offer: ["keyword:WHERE"], audit: "low: openQuoteAt after a closing quote" },
    { sql: 'SELECT * FROM "orders" o WHERE o."status" = "|', notOffer: ["keyword:*"], audit: "low: openQuoteAt after a closing quote" },
    { sql: "CREATE FUNCTION f() RETURNS void AS $$ BEGIN PERFORM 1 FROM orders o WHERE o.| ; END $$ LANGUAGE plpgsql", offer: ["column:status"] },
    // M9: PostgreSQL rejects a qualified SET target, and only the updated table's columns can be set.
    { sql: "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id", offer: ["column:status", "column:total"], notOffer: ["column:o.status", "column:c.full_name", "column:full_name", "column:email", "alias:c", "alias:o"], audit: "M9" },
    { sql: "UPDATE orders o SET status = 'x', | FROM customers c", offer: ["column:total"], notOffer: ["column:o.total", "column:c.email"], audit: "M9" },
    { sql: "UPDATE orders o SET status = | FROM customers c", offer: ["column:c.full_name", "column:o.total"], audit: "M9" },
    { sql: "MERGE INTO orders t USING customers s ON t.customer_id = s.customer_id WHEN MATCHED THEN UPDATE SET |", offer: ["column:status"], notOffer: ["column:s.email", "column:email", "column:t.status"], audit: "M9" },
    { sql: "INSERT INTO orders (order_id, status) SELECT order_id, 'x' FROM order_items ON CONFLICT (order_id) DO UPDATE SET |", offer: ["column:status"], notOffer: ["column:qty", "column:product_id"], audit: "M9" },
    { sql: "INSERT INTO orders (order_id) VALUES (1) ON CONFLICT (order_id) DO UPDATE SET status = excluded.|", offer: ["column:status"], audit: "M9" },
    // H10: other dialects' clause words are names in PostgreSQL.
    { sql: "SELECT start.| FROM orders start", offer: ["column:status"], audit: "H10" },
    { sql: "SELECT | FROM orders qualify", offer: ["column:qualify.status"], audit: "H10" },
  ],
  mySql: [
    ...LATERAL,
    { sql: "SELECT * FROM `ord|", offer: ["table:orders"], insert: { orders: "`orders`" }, notOffer: ["keyword:*"] },
    { sql: "SELECT * FROM `orders`|", offer: ["keyword:WHERE"], audit: "low: openQuoteAt after a closing quote" },
    { sql: "SELECT * FROM orders o ORDER BY o.total DESC, |", offer: ["column:o.status"], audit: "H6" },
    { sql: "SELECT REPLACE(|, 'a', 'b') FROM orders", offer: ["column:total"], audit: "H6" },
    { sql: "SELECT IF(|, 1, 0) FROM orders", offer: ["column:total"], audit: "H6" },
    { sql: "SELECT * FROM orders o STRAIGHT_JOIN |", offer: ["table:customers"] },
    { sql: "INSERT INTO orders (order_id) VALUES (1) ON DUPLICATE KEY UPDATE |", offer: ["column:status"], notOffer: ["table:customers"] },
    { sql: "INSERT INTO orders (order_id) SELECT order_id FROM order_items ON DUPLICATE KEY UPDATE |", offer: ["column:status"], notOffer: ["column:qty"], audit: "M9" },
    // M19: INSERT … SET lists the target's columns, bare.
    { sql: "INSERT INTO orders SET |", offer: ["column:status", "column:total"], notOffer: ["table:customers", "column:o.status"], audit: "M19" },
    { sql: "INSERT INTO orders SET status = 'a', |", offer: ["column:total"], audit: "M19" },
    { sql: "INSERT orders SET |", offer: ["column:status"], audit: "M19" },
    { sql: "REPLACE INTO orders SET |", offer: ["column:status"], audit: "M19" },
    { sql: "INSERT INTO orders SET status = 'a';\nSELECT * FROM |", offer: ["table:customers"], audit: "M19" },
    // M19: CALL p(…) takes values, not procedure names.
    { sql: "CALL refresh_totals(|", notOffer: ["proc:refresh_totals", "schema:shop"], offer: ["keyword:NULL"], audit: "M19" },
    { sql: "CALL refresh_totals(1, |", notOffer: ["proc:refresh_totals"], audit: "M19" },
    { sql: "CALL |", offer: ["proc:refresh_totals"], audit: "M19" },
    // M19: CONVERT(expr, type) and CONVERT(expr USING charset).
    { sql: "SELECT CONVERT(|, CHAR) FROM orders", offer: ["column:total"], notOffer: ["type:*"], audit: "M19" },
    { sql: "SELECT CONVERT(total, |) FROM orders", offer: ["type:char"], notOffer: ["column:total"], audit: "M19" },
    { sql: "SELECT CONVERT(total USING |) FROM orders", notOffer: ["table:*", "column:*"], audit: "M19" },
    { sql: "SELECT CONVERT(total USING utf8mb4), | FROM orders", offer: ["column:status"], audit: "M19" },
    // M9 stays as MySQL accepts it: a multi-table UPDATE may set any table's column, qualified.
    { sql: "UPDATE orders o JOIN customers c ON c.customer_id = o.customer_id SET |", offer: ["column:o.status", "column:c.full_name"], audit: "M9" },
    // H10: a reserved word after a FROM item starts a clause, never the alias.
    { sql: "SELECT | FROM orders USE INDEX (ix_status) WHERE status = 'x'", offer: ["column:status"], notOffer: ["column:USE.status", "alias:USE"], audit: "H10" },
    { sql: "SELECT | FROM orders FORCE INDEX (ix_status)", offer: ["column:status"], notOffer: ["column:FORCE.status"], audit: "H10" },
    { sql: "SELECT | FROM orders PARTITION (p0)", offer: ["column:status", "column:order_id"], notOffer: ["column:p0", "column:PARTITION.p0"], audit: "H10" },
    { sql: "SELECT | FROM orders LOCK IN SHARE MODE", offer: ["column:status"], notOffer: ["column:LOCK.status"], audit: "H10" },
  ],
  sqlite: [
    { sql: "INSERT OR REPLACE INTO |", offer: ["table:orders"] },
    { sql: "SELECT * FROM [ord|", offer: ["table:orders"], insert: { orders: "[orders]" } },
    { sql: "SELECT * FROM `ord|", offer: ["table:orders"], insert: { orders: "`orders`" } },
    { sql: 'SELECT * FROM "orders"|', offer: ["keyword:WHERE"], audit: "low: openQuoteAt after a closing quote" },
    { sql: "SELECT * FROM json_each('[]') j WHERE j.|", offer: ["column:key", "column:value"] },
    { sql: "SELECT * FROM pragma_table_info('orders') p WHERE p.|", offer: ["column:name"] },
    { sql: "SELECT * FROM orders o NATURAL JOIN |", offer: ["table:customers"] },
    { sql: "UPDATE orders SET status = 'x' FROM customers c WHERE |", offer: ["column:c.email"] },
    { sql: "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id", offer: ["column:status"], notOffer: ["column:o.status", "column:c.full_name", "column:full_name"], audit: "M9" },
    { sql: "UPDATE orders o SET status = | FROM customers c", offer: ["column:c.full_name"], audit: "M9" },
    { sql: "INSERT INTO orders (order_id, status) SELECT order_id, 'x' FROM order_items WHERE true ON CONFLICT (order_id) DO UPDATE SET |", offer: ["column:status"], notOffer: ["column:qty"], audit: "M9" },
    // H10: INDEXED BY starts a clause, never the alias.
    { sql: "SELECT | FROM orders INDEXED BY ix_status WHERE status = 'x'", offer: ["column:status"], notOffer: ["column:INDEXED.status"], audit: "H10" },
  ],
  duckDb: [
    ...LATERAL,
    // DuckDB detects lateral references itself: a derived table in FROM sees the items before it without LATERAL.
    { sql: "SELECT * FROM orders o JOIN (SELECT | FROM customers) d ON 1 = 1", offer: ["column:o.status", "column:customers.full_name"], notOffer: ["alias:d"], audit: "M1" },
    { sql: "SELECT * FROM orders o, (SELECT o.| ) d", offer: ["column:status"], audit: "M1" },
    { sql: "FROM |", offer: ["table:orders"] },
    { sql: "FROM orders o SELECT o.|", offer: ["column:status"] },
    { sql: "SELECT * FROM orders o ASOF JOIN |", offer: ["table:customers"] },
    { sql: "SELECT * FROM orders QUALIFY row_number() OVER () = 1;\nSELECT * FROM |", offer: ["table:customers"] },
    { sql: "SELECT current_| FROM orders", insert: { current_date: "current_date" }, audit: "M18" },
    { sql: "SELECT * FROM range(10) r WHERE r.|", offer: ["column:range"] },
    { sql: 'SELECT * FROM "orders"|', offer: ["keyword:WHERE"], audit: "low: openQuoteAt after a closing quote" },
    { sql: "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id", offer: ["column:status"], notOffer: ["column:o.status", "column:c.full_name", "column:full_name"], audit: "M9" },
    { sql: "UPDATE orders o SET status = | FROM customers c", offer: ["column:c.full_name"], audit: "M9" },
    { sql: "INSERT INTO orders (order_id, status) SELECT order_id, 'x' FROM order_items ON CONFLICT (order_id) DO UPDATE SET |", offer: ["column:status"], notOffer: ["column:qty"], audit: "M9" },
    // H10: reserved words and USING SAMPLE end the FROM item instead of naming it.
    { sql: "SELECT | FROM orders QUALIFY row_number() OVER () = 1", offer: ["column:status"], notOffer: ["column:QUALIFY.status"], audit: "H10" },
    { sql: "SELECT | FROM orders ASOF JOIN customers c ON c.customer_id = orders.customer_id", offer: ["column:orders.status", "column:c.full_name"], notOffer: ["column:ASOF.status"], audit: "H10" },
    { sql: "SELECT | FROM orders SEMI JOIN customers c ON c.customer_id = orders.customer_id", offer: ["column:orders.status"], notOffer: ["column:SEMI.status"], audit: "H10" },
    { sql: "SELECT | FROM orders USING SAMPLE 10%", offer: ["column:status"], notOffer: ["column:orders.status"], audit: "H10" },
  ],
  oracle: [
    ...LATERAL,
    ...APPLY,
    { sql: "SELECT * FROM |", offer: ["table:DUAL"] },
    { sql: "WITH x AS (SELECT id, title FROM posts) SELECT | FROM x", insert: { id: "id", title: "title" }, audit: "H7" },
    { sql: 'WITH x AS (SELECT 1 AS "lower" FROM dual) SELECT | FROM x', insert: { lower: '"lower"' }, audit: "H7" },
    { sql: "SELECT SYS| FROM orders", insert: { SYSDATE: "SYSDATE" }, audit: "M18" },
    { sql: 'SELECT * FROM "ORD|', offer: ["table:ORDERS"], insert: { ORDERS: '"ORDERS"' } },
    { sql: 'SELECT * FROM "ORDERS"|', offer: ["keyword:WHERE"], audit: "low: openQuoteAt after a closing quote" },
    { sql: "CALL refresh_totals(|", offer: ["parameter:P_FROM"], notOffer: ["proc:REFRESH_TOTALS"], audit: "M19" },
    { sql: "BEGIN\n  UPDATE orders SET status = 'x' WHERE |;\nEND;", offer: ["column:ORDER_ID"] },
    { sql: "MERGE INTO orders t USING customers s ON (t.customer_id = s.customer_id) WHEN MATCHED THEN UPDATE SET |", offer: ["column:STATUS"], notOffer: ["column:s.EMAIL", "column:EMAIL"], audit: "M9" },
    { sql: "MERGE INTO orders t USING customers s ON (t.customer_id = s.customer_id) WHEN MATCHED THEN UPDATE SET status = |", offer: ["column:s.EMAIL"], audit: "M9" },
    { sql: "UPDATE orders o SET | WHERE o.order_id = 1", offer: ["column:STATUS"], notOffer: ["column:full_name"] },
    // H10: clause words after a FROM item start a clause, never the alias.
    { sql: "SELECT | FROM orders START WITH order_id = 1 CONNECT BY PRIOR order_id = customer_id", offer: ["column:STATUS"], notOffer: ["column:START.STATUS", "alias:START"], audit: "H10" },
    { sql: "SELECT | FROM orders CONNECT BY PRIOR order_id = customer_id", offer: ["column:STATUS"], notOffer: ["column:CONNECT.STATUS"], audit: "H10" },
    { sql: "SELECT | FROM orders MINUS SELECT customer_id FROM customers", offer: ["column:STATUS"], notOffer: ["column:FULL_NAME", "column:CUSTOMERS.FULL_NAME", "column:MINUS.STATUS"], audit: "H10" },
    { sql: "SELECT order_id FROM orders MINUS SELECT | FROM customers", offer: ["column:FULL_NAME"], notOffer: ["column:STATUS", "column:ORDERS.STATUS"], audit: "H10" },
    { sql: "SELECT | FROM orders AS OF TIMESTAMP SYSDATE - 1", offer: ["column:STATUS"], notOffer: ["column:OF.STATUS"], audit: "H10" },
    { sql: "SELECT | FROM orders PARTITION (p1)", offer: ["column:STATUS", "column:ORDER_ID"], notOffer: ["column:P1", "column:PARTITION.P1"], audit: "H10" },
    { sql: "SELECT | FROM orders SAMPLE (10)", offer: ["column:STATUS"], notOffer: ["column:SAMPLE.STATUS"], audit: "H10" },
  ],
};

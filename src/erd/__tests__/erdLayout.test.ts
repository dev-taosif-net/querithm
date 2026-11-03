import { describe, expect, it } from "vitest";
import type { SchemaSnapshot, SnapColumn, SnapFk, SnapObject } from "../../ipc/contract";
import { aliasFor, buildGraph, joinPlan, joinSql, layoutGraph, matching, neighbourhood, quoteIdent, schemaScope, statementTables } from "../erdLayout";
import { buildSchemaIndex } from "../../editor/completion/schemaIndex";
import { buildSvg, edgePath } from "../erdExport";

const col = (name: string, o: Partial<SnapColumn> = {}): SnapColumn => ({
  name, type: "int", nullable: false, identity: false, computed: false, pk: false, default: null, ordinal: 0, ...o,
});
const table = (id: number, name: string, columns: SnapColumn[], schema = "dbo"): SnapObject => ({
  id, schema, name, kind: "table", columns, params: null, returnType: null, modifyDate: null,
});
const fk = (name: string, parent: number, referenced: number, pairs: [string, string][]): SnapFk => ({
  name, parent, referenced, columns: pairs.map(([p, r]) => ({ parent: p, referenced: r })),
});

// Customers ← Orders ← OrderLines → Products; Shipments → OrderLines (composite); Employees self-ref;
// Customers → sales.Region (nullable, cross-schema); Isolated has no FKs.
function snapshot(): SchemaSnapshot {
  return {
    profileId: "p", database: "db", status: "fresh", refreshedAt: 0, serverVersion: null, error: null,
    objects: [
      table(1, "Customers", [col("CustomerId", { pk: true }), col("Name", { type: "nvarchar(50)" }), col("RegionId", { nullable: true })]),
      table(2, "Orders", [col("OrderId", { pk: true }), col("CustomerId"), col("Note", { type: "nvarchar(max)", nullable: true })]),
      table(3, "OrderLines", [col("OrderId", { pk: true }), col("LineNo", { pk: true }), col("ProductId", { nullable: true })]),
      table(4, "Products", [col("ProductId", { pk: true })]),
      table(5, "Employees", [col("EmployeeId", { pk: true }), col("ManagerId", { nullable: true })]),
      table(6, "Isolated", [col("Id", { pk: true })]),
      table(7, "Region", [col("RegionId", { pk: true }), col("Label", { type: "<b>&\"x\"" })], "sales"),
      table(8, "Shipments", [col("ShipmentId", { pk: true }), col("OrderId"), col("LineNo")]),
      { ...table(9, "v_Orders", [col("OrderId")]), kind: "view" },
    ],
    fks: [
      fk("FK_Orders_Customers", 2, 1, [["CustomerId", "CustomerId"]]),
      fk("FK_Lines_Orders", 3, 2, [["OrderId", "OrderId"]]),
      fk("FK_Lines_Products", 3, 4, [["ProductId", "ProductId"]]),
      fk("FK_Emp_Manager", 5, 5, [["ManagerId", "EmployeeId"]]),
      fk("FK_Customers_Region", 1, 7, [["RegionId", "RegionId"]]),
      fk("FK_Ship_Lines", 8, 3, [["OrderId", "OrderId"], ["LineNo", "LineNo"]]),
    ],
  };
}

const sorted = (s: Set<number>) => [...s].sort((a, b) => a - b);

describe("ERD subgraphs", () => {
  it("1-hop and 2-hop neighbourhoods follow FKs both ways", () => {
    const s = snapshot();
    expect(sorted(neighbourhood(s, [2], 1))).toEqual([1, 2, 3]);
    expect(sorted(neighbourhood(s, [2], 2))).toEqual([1, 2, 3, 4, 7, 8]);
    expect(sorted(neighbourhood(s, [2], "all"))).toEqual([1, 2, 3, 4, 7, 8]);
    expect(sorted(neighbourhood(s, [6], "all"))).toEqual([6]);
    expect(sorted(neighbourhood(s, [9], 1))).toEqual([]); // views are not diagram tables
  });

  it("schema scope and search highlight", () => {
    const s = snapshot();
    expect(sorted(schemaScope(s, "sales"))).toEqual([7]);
    expect(schemaScope(s, null).size).toBe(8);
    expect(sorted(matching(s, schemaScope(s, null), "regionid"))).toEqual([1, 7]);
    expect(sorted(matching(s, schemaScope(s, null), "orderl"))).toEqual([3]);
    expect(matching(s, [1], "  ").size).toBe(0);
  });

  it("graph: edges only between shown tables, composite FKs as one edge, self references, hidden neighbours", () => {
    const g = buildGraph(snapshot(), new Set([2, 3, 5, 8]), false);
    expect(g.nodes.map((n) => n.name)).toEqual(["Employees", "OrderLines", "Orders", "Shipments"]);
    expect(g.edges.map((e) => e.name).sort()).toEqual(["FK_Emp_Manager", "FK_Lines_Orders", "FK_Ship_Lines"]);
    const ship = g.edges.find((e) => e.name === "FK_Ship_Lines")!;
    expect(ship.pairs).toHaveLength(2);
    expect(g.edges.find((e) => e.name === "FK_Emp_Manager")!.self).toBe(true);
    expect(g.edges.find((e) => e.name === "FK_Emp_Manager")!.optional).toBe(true);
    expect(g.edges.find((e) => e.name === "FK_Lines_Orders")!.optional).toBe(false);
    expect(g.nodes.find((n) => n.name === "Orders")!.hidden).toBe(1); // Customers is not shown
    expect(g.nodes.find((n) => n.name === "OrderLines")!.hidden).toBe(1); // Products
  });

  it("compact cards show key columns only, but report the full column count", () => {
    const g = buildGraph(snapshot(), new Set([2]), true);
    const orders = g.nodes[0];
    expect(orders.columns.map((c) => c.name)).toEqual(["OrderId", "CustomerId"]);
    expect(orders.columnCount).toBe(3);
    expect(orders.columns[1]).toMatchObject({ fk: true, pk: false, notNull: true });
  });

  it("layout puts referenced tables to the right of the tables pointing at them", () => {
    const g = buildGraph(snapshot(), new Set([1, 2, 3]), false);
    const pos = new Map(layoutGraph(g).map((p) => [p.id, p]));
    expect(pos.size).toBe(3);
    expect(pos.get(1)!.x).toBeGreaterThan(pos.get(2)!.x);
    expect(pos.get(2)!.x).toBeGreaterThan(pos.get(3)!.x);
  });

  it("600 tables: a 2-hop subgraph builds well under the 30 ms budget", () => {
    const objects: SnapObject[] = [];
    const fks: SnapFk[] = [];
    for (let i = 1; i <= 600; i++) {
      objects.push(table(i, `T${i}`, [col("Id", { pk: true }), col("ParentId", { nullable: true }), ...Array.from({ length: 13 }, (_, k) => col(`C${k}`))]));
      if (i > 1 && i % 2 === 0) fks.push(fk(`FK${i}`, i, Math.floor(i / 2), [["ParentId", "Id"]]));
    }
    const s: SchemaSnapshot = { ...snapshot(), objects, fks };
    // Benchmark metric #18 (30 ms). One warm-up, then the best of three: a GC pause or a preempted thread on a busy
    // machine hits one run, a slow algorithm hits all of them.
    let g = buildGraph(s, neighbourhood(s, [10], 2), false);
    let best = Infinity;
    for (let run = 0; run < 3; run++) {
      const t0 = performance.now();
      g = buildGraph(s, neighbourhood(s, [10], 2), false);
      best = Math.min(best, performance.now() - t0);
    }
    expect(best).toBeLessThan(30);
    expect(g.nodes.length).toBeGreaterThan(3);
  });
});

describe("current-query mode", () => {
  it("takes the tables of the statement at the cursor, not of the whole script", () => {
    // PostgreSQL folds unquoted names to lower case, so the mixed-case fixture names are quoted there.
    for (const [dialect, text] of [
      ["tsql", "SELECT * FROM Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId;\nSELECT * FROM Products;"],
      ["pg", 'SELECT * FROM dbo."Orders" o JOIN dbo."Customers" c ON c."CustomerId" = o."CustomerId";\nSELECT * FROM dbo."Products";'],
    ] as const) {
      const idx = buildSchemaIndex(snapshot(), dialect, []);
      expect(statementTables(idx, text, 10, dialect).sort()).toEqual([1, 2]);
      expect(statementTables(idx, text, text.length - 3, dialect)).toEqual([4]);
    }
    // Views and CTEs are not diagram tables.
    const idx = buildSchemaIndex(snapshot(), "tsql", []);
    expect(statementTables(idx, "WITH x AS (SELECT 1 AS a) SELECT * FROM x JOIN v_Orders v ON 1 = 1", 30, "tsql")).toEqual([]);
  });
});

describe("JOIN generator", () => {
  it("joins through the tables on the shortest FK path", () => {
    expect(joinSql(snapshot(), [1, 3], "tsql")).toEqual({
      sql: "SELECT c.*, ol.*\nFROM Customers c\nJOIN Orders o ON o.CustomerId = c.CustomerId\nJOIN OrderLines ol ON ol.OrderId = o.OrderId;",
      tables: [1, 2, 3],
    });
  });

  it("composite keys join on every column pair (LINENO is reserved in T-SQL, so it is bracketed)", () => {
    const r = joinSql(snapshot(), [8, 3], "tsql");
    expect("sql" in r && r.sql).toBe("SELECT s.*, ol.*\nFROM Shipments s\nJOIN OrderLines ol ON s.OrderId = ol.OrderId AND s.[LineNo] = ol.[LineNo];");
    const p = joinSql(snapshot(), [8, 3], "pg");
    expect("sql" in p && p.sql).toContain('ON s."OrderId" = ol."OrderId" AND s."LineNo" = ol."LineNo"');
  });

  it("qualifies non-default schemas and quotes per dialect", () => {
    const r = joinSql(snapshot(), [1, 7], "pg");
    expect("sql" in r && r.sql).toBe('SELECT c.*, r.*\nFROM dbo."Customers" c\nJOIN sales."Region" r ON c."RegionId" = r."RegionId";');
    const t = joinSql(snapshot(), [1, 7], "tsql");
    expect("sql" in t && t.sql).toContain("JOIN sales.Region r ON c.RegionId = r.RegionId");
  });

  it("refuses tables that no FK path connects (never a cross join)", () => {
    const r = joinSql(snapshot(), [2, 6], "tsql");
    expect(r).toEqual({ error: expect.stringContaining("dbo.Orders and dbo.Isolated are not connected by foreign keys") });
    expect(joinSql(snapshot(), [2], "tsql")).toEqual({ error: "Select at least two tables." });
  });

  it("prefers the path with fewer nullable FK columns when hop counts tie", () => {
    // A → B (nullable) → D and A → C (NOT NULL) → D.
    const s: SchemaSnapshot = {
      ...snapshot(),
      objects: [
        table(1, "A", [col("Id", { pk: true }), col("BId", { nullable: true }), col("CId")]),
        table(2, "B", [col("Id", { pk: true }), col("DId")]),
        table(3, "C", [col("Id", { pk: true }), col("DId")]),
        table(4, "D", [col("Id", { pk: true })]),
      ],
      fks: [fk("ab", 1, 2, [["BId", "Id"]]), fk("ac", 1, 3, [["CId", "Id"]]), fk("bd", 2, 4, [["DId", "Id"]]), fk("cd", 3, 4, [["DId", "Id"]])],
    };
    const plan = joinPlan(s, [1, 4]);
    expect("steps" in plan && plan.steps.map((x) => x.table)).toEqual([1, 3, 4]);
  });

  it("aliases are unique and never keywords; identifiers are quoted only when needed", () => {
    const taken = new Set<string>();
    expect(aliasFor("Orders", taken, "tsql")).toBe("o");
    expect(aliasFor("Options", taken, "tsql")).toBe("o2");
    expect(aliasFor("order_lines", taken, "pg")).toBe("ol");
    expect(aliasFor("AsOfStock", new Set(), "tsql")).toBe("aos");
    expect(aliasFor("AppSettings", new Set(), "tsql")).toBe("as2");
    expect(quoteIdent("Order", "tsql")).toBe("[Order]");
    expect(quoteIdent("order_lines", "pg")).toBe("order_lines");
    expect(quoteIdent("Odd]Name", "tsql")).toBe("[Odd]]Name]");
    expect(quoteIdent('we"ird', "pg")).toBe('"we""ird"');
  });
});

describe("ERD export", () => {
  it("draws cards and edges as plain SVG with escaped text, no foreignObject", () => {
    const g = buildGraph(snapshot(), new Set([1, 2, 7]), false);
    const pos = layoutGraph(g);
    const colors = { bg: "#000", card: "#111", header: "#222", line: "#333", fg: "#fff", muted: "#999", pk: "#fa0", fk: "#0af", edge: "#888" };
    const svg = buildSvg(g, pos, colors);
    expect(svg.startsWith("<svg")).toBe(true);
    expect(svg).not.toContain("foreignObject");
    expect(svg.match(/<g transform/g)).toHaveLength(3);
    expect(svg.match(/<path d="M /g)).toHaveLength(2);
    expect(svg).toContain("&lt;b&gt;&amp;&quot;x&quot;");
    expect(svg).toContain('stroke-dasharray="5 3"'); // the nullable Customers → Region edge
  });

  it("edges start at the FK column row and end at the referenced key row", () => {
    const g = buildGraph(snapshot(), new Set([1, 2]), false);
    const pos = new Map(layoutGraph(g).map((p) => [p.id, p]));
    const e = g.edges[0];
    const d = edgePath(e, g, pos);
    const orders = pos.get(2)!;
    // Orders.CustomerId is the second column: header + 1.5 rows down.
    expect(d).toContain(` ${orders.y + 30 + 20 + 10} `);
  });

  it("joins three or more tables across a branching path without duplicating intermediate tables", () => {
    const r = joinSql(snapshot(), [1, 4, 7], "tsql");
    expect("sql" in r && r.sql).toBe(
      [
        "SELECT c.*, p.*, r.*",
        "FROM Customers c",
        "JOIN Orders o ON o.CustomerId = c.CustomerId",
        "JOIN OrderLines ol ON ol.OrderId = o.OrderId",
        "JOIN Products p ON ol.ProductId = p.ProductId",
        "JOIN sales.Region r ON c.RegionId = r.RegionId;",
      ].join("\n"),
    );
    expect("tables" in r && r.tables).toEqual([1, 2, 3, 4, 7]);
  });
});

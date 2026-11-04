import { describe, expect, it } from "vitest";
import { tooltipText } from "../TabIdentity";

const prof = (extra: any): any => ({ id: "p", name: "Billing", environment: "prod", auth: "sql", ...extra });

describe("tab tooltip: server address", () => {
  it.each([
    ["mssql", 1444, "Server 10.0.4.12:1444"],
    ["pg", 5432, "Server 10.0.4.12"],
    ["mySql", 3307, "Server 10.0.4.12:3307"],
  ] as const)("%s: the address follows the connection line", (engine, port, line) => {
    const t = tooltipText({ kind: "query", title: "a.sql", profile: prof({ engine, host: "10.0.4.12", port }), database: "Billing" });
    expect(t.split("\n")).toEqual(["a.sql", "PROD Billing › Billing", line]);
  });

  it.each(["sqlite", "duckDb"] as const)("%s: a local database has no server line", (engine) => {
    const t = tooltipText({ kind: "query", title: "a.sql", profile: prof({ engine, location: "file" }), database: "main" });
    expect(t).not.toContain("Server");
  });

  it("says when the connection goes through an SSH bastion, and keeps the transaction line after it", () => {
    const t = tooltipText({ kind: "query", title: "a.sql", profile: prof({ engine: "pg", host: "10.0.4.12", ssh: { host: "jump-01" } }), txOpen: true, spid: 53 });
    expect(t.split("\n")).toEqual(["a.sql", "PROD Billing", "Server 10.0.4.12 via SSH jump-01", "Transaction open · SPID 53"]);
  });

  it("a compare tab shows source → target addresses", () => {
    const t = tooltipText({
      kind: "dataCompare", title: "Data compare", profile: prof({ engine: "pg", host: "10.0.0.1" }), database: "a",
      target: { profile: prof({ engine: "pg", host: "10.0.0.2", port: 5433 }), database: "b" },
    });
    expect(t).toContain("Server 10.0.0.1 → 10.0.0.2:5433");
    const fileTarget = tooltipText({ kind: "schemaCompare", title: "Schema compare", profile: prof({ engine: "mssql", host: "sql1" }), target: { file: "model.qschema.json" } });
    expect(fileTarget).toContain("Server sql1 → (none)");
  });
});

import { beforeEach, describe, expect, it, vi } from "vitest";
import { buildSchemaIndex } from "../completion/schemaIndex";
import { complete, formatRows } from "../completion/complete";

const rpc = vi.fn();
const sessionFor = vi.fn(async () => "explorer-session");
vi.mock("../../ipc/client", () => ({ rpc: (...a: unknown[]) => rpc(...a) }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor }) } }));
const R = await import("../rowCounts");

const idx = buildSchemaIndex({
  profileId: "p", database: "app", status: "fresh", fks: [],
  objects: [
    { id: 1, schema: "dbo", name: "Orders", kind: "table", columns: [] },
    { id: 2, schema: "dbo", name: "Logs", kind: "table", columns: [] },
    { id: 3, schema: "dbo", name: "v", kind: "view", columns: [] },
  ],
}, "tsql", ["app"]);
const settle = () => new Promise((r) => setTimeout(r, 0));

describe("row counts for completion", () => {
  beforeEach(() => { R.resetRowCounts(); rpc.mockReset(); sessionFor.mockClear(); });

  it("loads once per schema version, for tables only, and tells the editor when they arrive", async () => {
    rpc.mockResolvedValue([{ objectId: 1, rows: 1234, estimated: true }, { objectId: 2, rows: null, estimated: true }]);
    const arrived = vi.fn();
    const off = R.onRowCounts(arrived);
    expect(R.rowCountsFor(idx, 1, "p", "app")).toBeUndefined();
    await settle();
    const m = R.rowCountsFor(idx, 1, "p", "app")!;
    expect(rpc).toHaveBeenCalledTimes(1);
    expect(rpc).toHaveBeenCalledWith("meta.rowCounts", { sessionId: "explorer-session", database: "app", objectIds: [1, 2] });
    expect(m.get(1)).toEqual({ rows: 1234, estimated: true });
    expect(arrived).toHaveBeenCalledTimes(1);
    R.rowCountsFor(idx, 2, "p", "app"); // a refresh: counted again
    await settle();
    expect(rpc).toHaveBeenCalledTimes(2);
    off();
  });

  it("uses a session-owned tab's own session, and a failure leaves items without counts", async () => {
    rpc.mockRejectedValue(new Error("down"));
    R.rowCountsFor(idx, 1, "p", "app", "tab-session");
    await settle();
    expect(rpc.mock.calls[0][1]).toMatchObject({ sessionId: "tab-session" });
    expect(sessionFor).not.toHaveBeenCalled();
    expect(R.rowCountsFor(idx, 1, "p", "app", "tab-session")).toBeUndefined();
  });

  it("table items show the count; an engine without one says rows n/a", () => {
    const rowCounts = new Map([[1, { rows: 1234, estimated: true }], [2, { rows: null, estimated: true }]]);
    const items = complete({ text: "SELECT * FROM ", offset: 14, dialect: "tsql", schema: idx, rowCounts, autoAlias: false }).items;
    expect(items.find((i) => i.label === "Orders")?.detail).toBe("table · dbo · ~1.2k rows");
    expect(items.find((i) => i.label === "Logs")?.detail).toBe("table · dbo · rows n/a");
    expect(items.find((i) => i.label === "v")?.detail).toBe("view · dbo");
    expect([formatRows(999), formatRows(1000), formatRows(15_400), formatRows(5_600_000), formatRows(2e9)]).toEqual(["999", "1k", "15k", "5.6M", "2B"]);
  });
});

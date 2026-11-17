import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const rpc = vi.fn();
vi.mock("../../ipc/client", () => ({ rpc: (...a: unknown[]) => rpc(...a) }));
const U = await import("../usage");

const DAY = 24 * 3600 * 1000;

describe("completion usage", () => {
  beforeEach(() => { U.resetUsage(); rpc.mockReset(); });
  afterEach(() => vi.useRealTimers());

  it("loads a database's history once and ranks recent, frequent items higher", async () => {
    const now = 100 * DAY;
    rpc.mockResolvedValue([
      { item: "orders", uses: 4, lastUsedAt: now - DAY },
      { item: "customers", uses: 4, lastUsedAt: now - 60 * DAY },
    ]);
    U.usageScores("p", "db", now);
    await Promise.resolve(); await Promise.resolve(); await new Promise((r) => setTimeout(r, 0));
    const s = U.usageScores("p", "db", now);
    expect(rpc).toHaveBeenCalledTimes(1);
    expect(rpc).toHaveBeenCalledWith("lang.usage.list", { profileId: "p", database: "db" });
    expect(s.get("orders")!).toBeGreaterThan(s.get("customers")!);
    expect(U.usageScores("p", "other", now)).not.toBe(s); // per database
  });

  it("counts an accepted item at once and writes uses in one batch", async () => {
    vi.useFakeTimers();
    rpc.mockResolvedValue([]);
    U.recordUsage("Orders", "p", "db");
    U.recordUsage("orders", "p", "db");
    U.recordUsage("Customers", "p", "db");
    expect(U.usageScores("p", "db").get("orders")).toBeGreaterThan(U.usageScores("p", "db").get("customers")!);
    await vi.advanceTimersByTimeAsync(1600);
    const writes = rpc.mock.calls.filter((c) => c[0] === "lang.usage.record");
    expect(writes).toEqual([["lang.usage.record", { profileId: "p", database: "db", items: ["orders", "customers"] }]]);
  });

  it("without a connection counts for this session only and writes nothing", async () => {
    vi.useFakeTimers();
    U.recordUsage("orders");
    expect(U.usageScores(undefined, undefined).get("orders")).toBeGreaterThan(0);
    await vi.advanceTimersByTimeAsync(2000);
    expect(rpc).not.toHaveBeenCalled();
  });

  it("a sidecar that is down leaves ranking working", async () => {
    rpc.mockRejectedValue(new Error("down"));
    U.recordUsage("orders", "p", "db");
    await new Promise((r) => setTimeout(r, 0));
    expect(U.usageScores("p", "db").get("orders")).toBeGreaterThan(0);
    await expect(U.flushUsage()).resolves.toBeUndefined();
  });
});

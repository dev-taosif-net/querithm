import { describe, expect, it } from "vitest";
import { pruneExpanded, takeNewlyConnected } from "../expansion";

describe("explorer expansion", () => {
  it("drops every key of a server that left the tree and keeps the others", () => {
    const keys = new Set(["s:p1", "d:p1", "db:p1:Sales", "sc:p1:Sales:dbo", "c:p1:Sales:tables", "o:p1:Sales:7", "s:p2", "db:p2:Hr", "env:prod", "h:tables"]);
    expect([...pruneExpanded(keys, ["p2"])]).toEqual(["s:p2", "db:p2:Hr", "env:prod", "h:tables"]);
  });
  it("returns the same set when nothing changes (no re-render)", () => {
    const keys = new Set(["s:p1", "db:p1:Sales"]);
    expect(pruneExpanded(keys, ["p1"])).toBe(keys);
  });
  it("reports a server as new only on its first connect while it stays in the tree", () => {
    const seen = new Set<string>();
    const up = new Set(["p1"]);
    const isUp = (id: string) => up.has(id);
    expect(takeNewlyConnected(seen, ["p1", "p2"], isUp)).toEqual(["p1"]);
    up.add("p2");
    expect(takeNewlyConnected(seen, ["p1", "p2"], isUp)).toEqual(["p2"]);
    // a drop and reconnect (or sidecar restart) keeps the user's expansion
    up.clear();
    expect(takeNewlyConnected(seen, ["p1", "p2"], isUp)).toEqual([]);
    up.add("p1").add("p2");
    expect(takeNewlyConnected(seen, ["p1", "p2"], isUp)).toEqual([]);
    // leaving the tree (disconnect) forgets it, so the next connect expands again
    expect(takeNewlyConnected(seen, ["p2"], isUp)).toEqual([]);
    expect(takeNewlyConnected(seen, ["p1", "p2"], isUp)).toEqual(["p1"]);
  });
});

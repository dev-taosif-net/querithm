// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";

const g = { nodes: [{ id: 1, width: 100, height: 60 }, { id: 2, width: 100, height: 60 }], edges: [{ from: 1, to: 2 }] };

afterEach(() => { vi.restoreAllMocks(); vi.resetModules(); });

describe("erdLayout.worker", () => {
  it("answers a layout request with positions", async () => {
    const post = vi.spyOn(self, "postMessage").mockImplementation(() => {});
    await import("../erdLayout.worker");
    (self as any).onmessage({ data: g });
    expect(post).toHaveBeenCalledTimes(1);
    const msg = post.mock.calls[0][0] as { positions: Array<{ id: number }> };
    expect(msg.positions.map((p) => p.id).sort()).toEqual([1, 2]);
  });

  it("reports a layout failure instead of throwing", async () => {
    const post = vi.spyOn(self, "postMessage").mockImplementation(() => {});
    await import("../erdLayout.worker");
    (self as any).onmessage({ data: null });
    expect(post.mock.calls[0][0]).toEqual({ error: expect.any(String) });
  });
});

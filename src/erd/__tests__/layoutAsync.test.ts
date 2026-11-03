// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const workers: Array<{ posted: unknown[]; terminated: boolean; onmessage?: (e: { data: unknown }) => void; onerror?: (e: { message: string }) => void }> = [];
vi.mock("../erdLayout.worker?worker", () => ({
  default: class {
    posted: unknown[] = []; terminated = false; onmessage?: (e: { data: unknown }) => void; onerror?: (e: { message: string }) => void;
    constructor() { workers.push(this); }
    postMessage(x: unknown) { this.posted.push(x); }
    terminate() { this.terminated = true; }
  },
}));

const { layoutAsync, WORKER_THRESHOLD } = await import("../layoutAsync");

const req = (n: number) => ({
  nodes: Array.from({ length: n }, (_, i) => ({ id: i + 1, width: 100, height: 60 })),
  edges: n > 1 ? [{ from: 1, to: 2 }] : [],
});

beforeEach(() => { workers.length = 0; });

describe("layoutAsync", () => {
  it("lays small diagrams out inline without a worker", async () => {
    const out = await layoutAsync(req(5));
    expect(out).toHaveLength(5);
    expect(workers).toHaveLength(0);
  });

  it("uses a worker above the threshold, resolves with its positions, and terminates it", async () => {
    const p = layoutAsync(req(WORKER_THRESHOLD + 1));
    expect(workers).toHaveLength(1);
    expect(workers[0].posted).toHaveLength(1);
    workers[0].onmessage!({ data: { positions: [{ id: 1, x: 1, y: 2 }] } });
    expect(await p).toEqual([{ id: 1, x: 1, y: 2 }]);
    expect(workers[0].terminated).toBe(true);
  });

  it("rejects with the worker's error message or an error event", async () => {
    const a = layoutAsync(req(WORKER_THRESHOLD + 1));
    workers[0].onmessage!({ data: { error: "dagre blew up" } });
    await expect(a).rejects.toThrow("dagre blew up");
    const b = layoutAsync(req(WORKER_THRESHOLD + 1));
    workers[1].onerror!({ message: "script error" });
    await expect(b).rejects.toThrow("script error");
    expect(workers[1].terminated).toBe(true);
  });
});

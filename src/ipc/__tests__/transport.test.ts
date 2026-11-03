import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const tauri = vi.hoisted(() => ({ invoke: vi.fn(), listen: vi.fn() }));
vi.mock("@tauri-apps/api/core", () => ({ invoke: tauri.invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: tauri.listen }));

class FakeWS {
  static last: FakeWS;
  binaryType = "";
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onmessage: ((m: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  constructor(public url: string) {
    FakeWS.last = this;
  }
  send(s: string) {
    this.sent.push(s);
  }
}

beforeEach(() => {
  vi.resetModules();
  tauri.invoke.mockReset();
  tauri.listen.mockReset();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe("tauri transport", () => {
  async function load() {
    vi.stubGlobal("window", { __TAURI_INTERNALS__: {} });
    return import("../transport");
  }
  it("invokes commands and wraps binary frames", async () => {
    const m = await load();
    expect(m.isTauri).toBe(true);
    const t = m.transport();
    expect(t.kind).toBe("tauri");
    expect(m.transport()).toBe(t);
    tauri.invoke.mockResolvedValueOnce("r");
    await expect(t.invoke("cmd", { a: 1 })).resolves.toBe("r");
    expect(tauri.invoke).toHaveBeenCalledWith("cmd", { a: 1 });
    tauri.invoke.mockResolvedValueOnce(new Uint8Array([4, 0, 0]).buffer);
    const f = await t.resultsNext("e");
    expect(Array.from(f)).toEqual([4, 0, 0]);
    expect(tauri.invoke).toHaveBeenLastCalledWith("results_next", { executionId: "e" });
  });
  it("listen unwraps the event payload", async () => {
    const m = await load();
    const un = () => {};
    tauri.listen.mockImplementation(async (_e: string, cb: (e: { payload: unknown }) => void) => {
      cb({ payload: { x: 1 } });
      return un;
    });
    const got = vi.fn();
    expect(await m.transport().listen("ev", got)).toBe(un);
    expect(got).toHaveBeenCalledWith({ x: 1 });
  });
});

describe("bridge transport", () => {
  async function load() {
    vi.stubGlobal("window", {});
    vi.stubGlobal("WebSocket", FakeWS);
    const m = await import("../transport");
    const t = m.transport();
    return { m, t, ws: FakeWS.last };
  }

  it("uses the default bridge url", async () => {
    const { m, t, ws } = await load();
    expect(m.isTauri).toBe(false);
    expect(t.kind).toBe("bridge");
    expect(ws.url).toBe("ws://127.0.0.1:1421");
    expect(ws.binaryType).toBe("arraybuffer");
  });

  it("correlates JSON replies and rejects errors", async () => {
    const { t, ws } = await load();
    ws.onopen!();
    const ok = t.invoke("a", { x: 1 });
    const bad = t.invoke("b");
    await Promise.resolve();
    await Promise.resolve();
    expect(ws.sent.map((s) => JSON.parse(s))).toEqual([
      { id: 1, cmd: "a", args: { x: 1 } },
      { id: 2, cmd: "b", args: {} },
    ]);
    ws.onmessage!({ data: JSON.stringify({ id: 1, ok: true, result: 42 }) });
    ws.onmessage!({ data: JSON.stringify({ id: 2, ok: false, error: { message: "e" } }) });
    ws.onmessage!({ data: JSON.stringify({ id: 99, ok: true }) }); // unknown id ignored
    await expect(ok).resolves.toBe(42);
    await expect(bad).rejects.toEqual({ message: "e" });
  });

  it("resolves binary replies keyed by a u32 id and serves resultsNext", async () => {
    const { t, ws } = await load();
    ws.onopen!();
    const p = t.resultsNext("exec");
    await Promise.resolve();
    await Promise.resolve();
    expect(JSON.parse(ws.sent[0])).toMatchObject({ cmd: "results_next", args: { executionId: "exec" } });
    const buf = new ArrayBuffer(7);
    new DataView(buf).setUint32(0, 1, true);
    new Uint8Array(buf).set([2, 5, 0], 4);
    ws.onmessage!({ data: buf });
    expect(Array.from(await p)).toEqual([2, 5, 0]);
  });

  it("dispatches events to listeners and supports unlisten", async () => {
    const { t, ws } = await load();
    const cb = vi.fn();
    const un = await t.listen("sidecar://notify", cb);
    ws.onmessage!({ data: JSON.stringify({ event: "sidecar://notify", payload: { method: "m" } }) });
    expect(cb).toHaveBeenCalledWith({ method: "m" });
    un();
    ws.onmessage!({ data: JSON.stringify({ event: "sidecar://notify", payload: 2 }) });
    expect(cb).toHaveBeenCalledTimes(1);
  });

  it("close rejects pending calls with sidecarDown and emits a dead status", async () => {
    const { t, ws } = await load();
    ws.onopen!();
    const status = vi.fn();
    await t.listen("sidecar://status", status);
    const p = t.invoke("x");
    await Promise.resolve();
    await Promise.resolve();
    ws.onclose!();
    await expect(p).rejects.toMatchObject({ code: -32003, data: { code: "sidecarDown" } });
    expect(status).toHaveBeenCalledWith({ state: "dead", detail: "bridge closed" });
  });

  it("an unreachable bridge rejects with a hint", async () => {
    const { t, ws } = await load();
    const p = t.invoke("x");
    ws.onerror!();
    await expect(p).rejects.toThrow(/pnpm dev:bridge/);
  });
});

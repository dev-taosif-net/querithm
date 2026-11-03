import { describe, it, expect, vi, beforeEach } from "vitest";

const { invoke, listen, tx } = vi.hoisted(() => {
  const invoke = vi.fn();
  const listen = vi.fn();
  return { invoke, listen, tx: { invoke, listen, resultsNext: vi.fn() } };
});
vi.mock("../transport", () => ({ transport: () => tx }));

import { rpc, RpcError, toRpcError, onNotify, onSidecarStatus, shell, ipcTimings } from "../client";

describe("rpc", () => {
  beforeEach(() => invoke.mockReset());

  it("sends method and params through the transport", async () => {
    invoke.mockResolvedValue([1]);
    await expect(rpc("profile.list")).resolves.toEqual([1]);
    expect(invoke).toHaveBeenCalledWith("rpc", { method: "profile.list", params: null });
    await rpc("group.delete", { name: "g" });
    expect(invoke).toHaveBeenLastCalledWith("rpc", { method: "group.delete", params: { name: "g" } });
  });

  it("maps failures to RpcError with app code and data", async () => {
    invoke.mockImplementation(async () => {
      throw { message: "nope", code: -32001, data: { code: "notFound", data: { id: 1 } } };
    });
    let err: RpcError = undefined as never;
    try {
      await rpc("profile.list");
    } catch (e) {
      err = e as RpcError;
    }
    expect(err instanceof RpcError).toBe(true);
    invoke.mockReset();
    expect([err.message, err.code, err.appCode, err.data]).toEqual(["nope", -32001, "notFound", { id: 1 }]);
  });

  it("records timings per method (bounded) and logs nothing for lang.*", async () => {
    invoke.mockResolvedValue(null);
    for (let i = 0; i < 510; i++) await rpc("profile.list");
    const t = ipcTimings();
    expect(t["profile.list"].length).toBeLessThanOrEqual(502);
    expect(t["profile.list"].length).toBeGreaterThan(400);
  });

  it("logs slow calls in dev", async () => {
    const dbg = vi.spyOn(console, "debug").mockImplementation(() => {});
    const now = vi.spyOn(performance, "now");
    let t = 0;
    now.mockImplementation(() => (t += 500));
    invoke.mockResolvedValue(null);
    await rpc("profile.list");
    await rpc("lang.tokens" as never);
    expect(dbg).toHaveBeenCalledTimes(1);
    expect(dbg.mock.calls[0][0]).toContain("profile.list");
    now.mockRestore();
    dbg.mockRestore();
  });
});

describe("toRpcError", () => {
  it("passes RpcError through, wraps objects with message, stringifies the rest", () => {
    const e = new RpcError("x", 1);
    expect(toRpcError(e)).toBe(e);
    const w = toRpcError({ message: "m" });
    expect([w.message, w.code, w.appCode]).toEqual(["m", -32000, undefined]);
    expect(toRpcError("boom").code).toBe(-32000);
    expect(toRpcError("boom").message).toBe("boom");
    expect(toRpcError(null).message).toBe("null");
  });
});

describe("notifications", () => {
  it("dispatches by method, isolates handler failures, unsubscribes", () => {
    let cb!: (p: unknown) => void;
    listen.mockImplementation(async (_e: string, f: (p: unknown) => void) => {
      cb = f;
      return () => {};
    });
    const a = vi.fn();
    const bad = vi.fn(() => {
      throw new Error("x");
    });
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    const offA = onNotify("trace.event", a);
    onNotify("trace.event", bad);
    onNotify("debug.event", vi.fn());
    expect(listen).toHaveBeenCalledTimes(1); // subscribed once
    expect(listen.mock.calls[0][0]).toBe("sidecar://notify");
    cb({ method: "trace.event", params: { n: 1 } });
    expect(a).toHaveBeenCalledWith({ n: 1 });
    expect(bad).toHaveBeenCalled();
    expect(err).toHaveBeenCalled();
    cb({ method: "other", params: 1 });
    offA();
    cb({ method: "trace.event", params: { n: 2 } });
    expect(a).toHaveBeenCalledTimes(1);
    err.mockRestore();
  });
});

describe("onSidecarStatus", () => {
  it("forwards status and unsubscribes once the listener is ready", async () => {
    const un = vi.fn();
    let cb!: (p: unknown) => void;
    listen.mockImplementation(async (_e: string, f: (p: unknown) => void) => {
      cb = f;
      return un;
    });
    const h = vi.fn();
    const off = onSidecarStatus(h);
    await Promise.resolve();
    await Promise.resolve();
    cb({ state: "dead" });
    expect(h).toHaveBeenCalledWith({ state: "dead" });
    off();
    expect(un).toHaveBeenCalled();
  });
  it("unsubscribing before the listener resolves cancels it immediately", async () => {
    const un = vi.fn();
    listen.mockImplementation(async () => un);
    const off = onSidecarStatus(vi.fn());
    off();
    await Promise.resolve();
    await Promise.resolve();
    expect(un).toHaveBeenCalledTimes(1);
  });
});

describe("shell", () => {
  beforeEach(() => invoke.mockReset().mockResolvedValue(undefined));
  it("maps each helper to its command", async () => {
    await shell.secretSet("p", "s");
    await shell.secretDelete("p");
    await shell.secretExists("p");
    await shell.status();
    await shell.restart();
    await shell.appInfo();
    await shell.resultsDiscard("e1");
    expect(invoke.mock.calls).toEqual([
      ["secret_set", { profileId: "p", secret: "s" }],
      ["secret_delete", { profileId: "p" }],
      ["secret_exists", { profileId: "p" }],
      ["sidecar_status"],
      ["sidecar_restart"],
      ["app_info"],
      ["results_discard", { executionId: "e1" }],
    ]);
  });
});

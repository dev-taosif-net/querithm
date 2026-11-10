import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";

const appVersion: string = JSON.parse(readFileSync(new URL("../../src-tauri/tauri.conf.json", import.meta.url), "utf8")).version;

// dev-bridge.mjs is a top-level program. It is run here against a fake sidecar process, data socket and WebSocket
// server (no .NET, no network, no filesystem outside a temp dir), then driven through the messages a browser would send.
const h = vi.hoisted(() => {
  const state = {
    child: undefined as any,
    sockets: [] as any[],
    wss: undefined as undefined | { opts: any; connection?: (ws: any) => void },
    existsSync: true,
    spawnSyncStatus: 0,
    spawnCalls: [] as any[],
    spawnSyncCalls: [] as any[],
    mkdirs: [] as string[],
    written: new Map<string, Buffer>(),
    connectCalls: [] as string[],
  };
  return state;
});

vi.mock("node:child_process", () => {
  const spawn = (bin: string, args: string[], opts: any) => {
    const { EventEmitter: EE } = require("node:events");
    const { PassThrough: PT } = require("node:stream");
    const child: any = new EE();
    child.stdin = { written: [] as string[], write(s: string) { this.written.push(s); } };
    child.stdout = new PT();
    child.pid = 4242;
    child.exitCode = null;
    child.kill = () => {};
    h.child = child;
    h.spawnCalls.push({ bin, args, opts });
    return child;
  };
  const spawnSync = (...a: any[]) => { h.spawnSyncCalls.push(a); return { status: h.spawnSyncStatus }; };
  return { spawn, spawnSync, default: { spawn, spawnSync } };
});
vi.mock("node:fs", async (orig) => {
  const real = await orig<typeof import("node:fs")>();
  const api = {
    ...real,
    existsSync: (p: string) => (String(p).includes("querithm-sidecar") ? h.existsSync : real.existsSync(p)),
    mkdirSync: (p: string) => void h.mkdirs.push(p),
    mkdtempSync: (p: string) => `${p}${h.mkdirs.length}x`,
    writeFileSync: (p: string, b: Buffer) => void h.written.set(p, b),
  };
  return { ...api, default: api };
});
vi.mock("node:net", () => {
  const createConnection = (endpoint: string) => {
    const { EventEmitter: EE } = require("node:events");
    const s: any = new EE();
    s.written = [] as string[];
    s.write = (x: string) => void s.written.push(x);
    s.paused = false;
    s.pause = () => { s.paused = true; };
    s.resume = () => { s.paused = false; };
    s.destroyed = false;
    s.destroy = () => { s.destroyed = true; s.emit("close"); };
    h.sockets.push(s);
    h.connectCalls.push(endpoint);
    return s;
  };
  return { createConnection, default: { createConnection } };
});
vi.mock("ws", () => ({
  WebSocketServer: class {
    constructor(opts: any) { h.wss = { opts }; }
    on(ev: string, cb: (ws: any) => void) { if (ev === "connection") h.wss!.connection = cb; }
  },
}));

const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
function ulid(bytes: number[]) {
  let v = 0n;
  for (const b of bytes) v = (v << 8n) | BigInt(b);
  let s = "";
  for (let i = 0; i < 26; i++) { s = ALPHABET[Number(v & 31n)] + s; v >>= 5n; }
  return s;
}
const idBytes = (n: number) => Array.from({ length: 16 }, (_, i) => (i === 15 ? n : 0));
/** A data-plane frame: u32 length, kind, 16 id bytes, u16 result index (0), payload. */
function frame(kind: number, execNo: number, payload = "") {
  const body = Buffer.concat([Buffer.from([kind]), Buffer.from(idBytes(execNo)), Buffer.from([0, 0]), Buffer.from(payload)]);
  const len = Buffer.alloc(4);
  len.writeUInt32LE(body.length, 0);
  return Buffer.concat([len, body]);
}

class FakeWs extends EventEmitter {
  readyState = 1;
  sent: any[] = [];
  send(x: any) { this.sent.push(x); }
  json() { return this.sent.filter((s) => typeof s === "string").map((s) => JSON.parse(s)); }
}

const exitHandlers: Record<string, () => void> = {};
let processOn: ReturnType<typeof vi.spyOn>;
let savedEnv: NodeJS.ProcessEnv;

async function start(env: Record<string, string> = {}) {
  vi.resetModules();
  Object.assign(process.env, { QUERITHM_SIDECAR: "/fake/querithm-sidecar", QUERITHM_BRIDGE_PORT: "9021", QUERITHM_UI_PORT: "1999", QUERITHM_DATA_DIR: "/tmp/qdata", ...env });
  await import("../dev-bridge.mjs");
}
function connect() {
  const ws = new FakeWs();
  h.wss!.connection!(ws);
  return ws;
}
const flush = () => new Promise<void>((r) => setImmediate(r));
async function send(ws: FakeWs, id: number, cmd: string, args: Record<string, unknown> = {}) {
  ws.emit("message", JSON.stringify({ id, cmd, args }));
  await flush();
}
/** Answers the oldest unanswered sidecar request. */
async function reply(result: unknown, error?: unknown) {
  const last = JSON.parse(h.child.stdin.written.at(-1));
  h.child.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: last.id, ...(error ? { error } : { result }) }) + "\n");
  await flush();
  return last;
}

beforeEach(() => {
  savedEnv = { ...process.env };
  Object.assign(h, { child: undefined, sockets: [], wss: undefined, existsSync: true, spawnSyncStatus: 0, spawnCalls: [], spawnSyncCalls: [], mkdirs: [], connectCalls: [] });
  h.written.clear();
  for (const k of Object.keys(exitHandlers)) delete exitHandlers[k];
  processOn = vi.spyOn(process, "on").mockImplementation(((ev: string, cb: () => void) => { exitHandlers[ev] = cb; return process; }) as never);
  vi.spyOn(console, "log").mockImplementation(() => {});
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  process.env = savedEnv;
});

describe("startup", () => {
  it("spawns the sidecar with a token, socket endpoint, parent pid and data dir, and serves only on loopback", async () => {
    await start();
    const { bin, opts } = h.spawnCalls[0];
    expect(bin).toBe("/fake/querithm-sidecar");
    expect(opts.env.QUERITHM_TOKEN).toMatch(/^[0-9a-f]{64}$/);
    expect(opts.env.QUERITHM_PARENT_PID).toBe(String(process.pid));
    expect(opts.env.QUERITHM_DATA_DIR).toBe("/tmp/qdata");
    expect(opts.env.QUERITHM_SOCKET).toContain(process.platform === "win32" ? `querithm-bridge-${process.pid}` : `qb-${process.pid}.sock`);
    expect(h.mkdirs).toContain("/tmp/qdata");
    expect(h.wss!.opts).toMatchObject({ host: "127.0.0.1", port: 9021 });
    expect(h.spawnSyncCalls).toHaveLength(0);
  });

  it("builds the sidecar first when the binary is missing, and exits when the build fails", async () => {
    h.existsSync = false;
    await start();
    expect(h.spawnSyncCalls[0][0]).toBe("dotnet");
    expect(h.spawnSyncCalls[0][1]).toEqual(expect.arrayContaining(["build", "Debug"]));
    expect(h.spawnCalls).toHaveLength(1);
    vi.restoreAllMocks();
    const exit = vi.spyOn(process, "exit").mockImplementation((() => { throw new Error("exit"); }) as never);
    vi.spyOn(console, "log").mockImplementation(() => {});
    h.existsSync = false;
    h.spawnSyncStatus = 3;
    await expect(start()).rejects.toThrow("exit");
    expect(exit).toHaveBeenCalledWith(3);
  });

  it("uses the Windows named pipe naming on win32", async () => {
    const real = Object.getOwnPropertyDescriptor(process, "platform")!;
    Object.defineProperty(process, "platform", { value: "win32" });
    try {
      await start({ QUERITHM_SIDECAR: "/fake/querithm-sidecar.exe" });
    } finally {
      Object.defineProperty(process, "platform", real);
    }
    const env = h.spawnCalls[0].opts.env;
    expect(env.QUERITHM_SOCKET).toBe(`querithm-bridge-${process.pid}`);
    vi.advanceTimersByTime(150);
    expect(h.connectCalls[0]).toBe(`\\\\.\\pipe\\querithm-bridge-${process.pid}`);
  });

  it("registers exit and SIGINT cleanup", async () => {
    await start();
    const kill = vi.spyOn(h.child, "kill");
    exitHandlers.exit();
    expect(kill).toHaveBeenCalled();
    const exit = vi.spyOn(process, "exit").mockImplementation((() => undefined) as never);
    exitHandlers.SIGINT();
    expect(exit).toHaveBeenCalledWith(0);
    expect(processOn).toHaveBeenCalled();
  });

  it("only lets the dev UI's origin connect (others are logged and refused)", async () => {
    await start();
    const verify = h.wss!.opts.verifyClient;
    expect(verify({ origin: "http://127.0.0.1:1999" })).toBe(true);
    expect(verify({ origin: "http://localhost:1999" })).toBe(true);
    expect(verify({ origin: "https://evil.example" })).toBe(false);
    expect(verify({ origin: undefined })).toBe(false);
    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining("rejected connection from origin https://evil.example"));
    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining("(none)"));
  });
});

describe("control plane", () => {
  it("relays rpc calls to the sidecar and returns results or errors to the right client", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "rpc", { method: "profile.list", params: null });
    const req = await reply([{ id: "p" }]);
    expect(req).toMatchObject({ jsonrpc: "2.0", method: "profile.list" });
    expect("params" in req).toBe(false);
    expect(ws.json()).toEqual([{ id: 1, ok: true, result: [{ id: "p" }] }]);
    await send(ws, 2, "rpc", { method: "query.execute", params: { sql: "x" } });
    await reply(null, { code: -32000, message: "boom" });
    expect(ws.json()[1]).toEqual({ id: 2, ok: false, error: { code: -32000, message: "boom" } });
    await send(ws, 3, "rpc", { method: "meta.ping" });
    h.child.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: JSON.parse(h.child.stdin.written.at(-1)).id }) + "\n");
    await flush();
    expect(ws.json()[2]).toEqual({ id: 3, ok: true, result: null });
  });

  it("forwards sidecar notifications to every open client and ignores garbage lines and closed sockets", async () => {
    await start();
    const a = connect(), b = connect(), c = connect();
    c.readyState = 3;
    h.child.stdout.write("not json\n");
    h.child.stdout.write(JSON.stringify({ method: "query.done", params: { executionId: "e" } }) + "\n");
    h.child.stdout.write(JSON.stringify({ method: "bare" }) + "\n");
    await flush();
    expect(a.json()[0]).toEqual({ event: "sidecar://notify", payload: { method: "query.done", params: { executionId: "e" } } });
    expect(b.json()).toHaveLength(2);
    expect(b.json()[1].payload).toEqual({ method: "bare", params: null });
    expect(c.sent).toHaveLength(0);
    a.emit("close");
    h.child.stdout.write(JSON.stringify({ method: "later", params: {} }) + "\n");
    await flush();
    expect(a.json()).toHaveLength(2);
    expect(b.json()).toHaveLength(3);
  });

  it("tells clients when the sidecar dies", async () => {
    await start();
    const ws = connect();
    h.child.exitCode = 1;
    h.child.emit("exit", 1);
    expect(ws.json()[0]).toEqual({ event: "sidecar://status", payload: { state: "dead", detail: "exited" } });
    await send(ws, 1, "sidecar_status");
    expect(ws.json().at(-1).result).toEqual({ state: "dead", pid: 4242 });
  });

  it("ignores malformed client messages", async () => {
    await start();
    const ws = connect();
    ws.emit("message", "{{{");
    ws.emit("message", JSON.stringify({ id: 1 }));
    await flush();
    expect(ws.sent).toHaveLength(0);
  });

  it("answers the shell-only commands itself", async () => {
    await start({ QUERITHM_BENCH: "1" });
    const ws = connect();
    await send(ws, 1, "sidecar_status");
    await send(ws, 2, "sidecar_restart");
    await send(ws, 3, "app_info");
    await send(ws, 4, "results_discard", { executionId: "nothing" });
    await send(ws, 5, "bogus_command");
    const r = ws.json();
    expect(r[0].result).toEqual({ state: "running", pid: 4242 });
    expect(r[1]).toEqual({ id: 2, ok: true, result: null });
    expect(r[2].result).toMatchObject({ version: appVersion, bench: true, dataDir: "/tmp/qdata", platform: expect.stringMatching(/linux|macos|windows/) });
    expect(r[3].ok).toBe(true);
    expect(r[4]).toEqual({ id: 5, ok: false, error: { code: -32601, message: "unknown command bogus_command" } });
  });
});

describe("updater stand-in", () => {
  it("without QUERITHM_FAKE_UPDATE the updater reports itself disabled, like a dev build of the shell", async () => {
    await start({ QUERITHM_FAKE_UPDATE: "" });
    const ws = connect();
    await send(ws, 1, "update_check");
    await send(ws, 2, "update_download");
    await send(ws, 3, "update_install");
    const r = ws.json();
    expect(r[0]).toEqual({ id: 1, ok: false, error: { kind: "disabled", message: "Updates are available in installed release builds only." } });
    expect(r[1].error.kind).toBe("noUpdate");
    expect(r[2].error.kind).toBe("cancelled");
  });

  it("with a fake version it offers the release, streams progress to every client, and refuses the install", async () => {
    await start({ QUERITHM_FAKE_UPDATE: "9.9.9" });
    const ws = connect();
    const other = connect();
    await send(ws, 1, "update_check");
    expect(ws.json()[0]).toMatchObject({ id: 1, ok: true, result: { version: "9.9.9", currentVersion: appVersion, notes: expect.stringContaining("<b>") } });
    const download = send(ws, 2, "update_download");
    for (let i = 0; i < 3; i++) {
      await flush();
      vi.advanceTimersByTime(150);
    }
    await download;
    await flush();
    const progress = other.json().filter((m) => m.event === "update://progress").map((m) => m.payload.downloaded);
    expect(progress).toEqual([0, 512 * 1024, 1024 * 1024]);
    expect(ws.json().find((m) => m.id === 2)).toEqual({ id: 2, ok: true, result: null });
    await send(ws, 3, "update_install");
    expect(ws.json().find((m) => m.id === 3)).toMatchObject({ ok: false, error: { kind: "cancelled" } });
  });
});

describe("secrets (in memory only)", () => {
  it("stores, reports and deletes a secret; session.open / profile.test get it injected when none is given", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "secret_exists", { profileId: "p" });
    await send(ws, 2, "secret_set", { profileId: "p", secret: "pw" });
    await send(ws, 3, "secret_set", { profileId: "p:ssh", secret: "sshpw" });
    await send(ws, 4, "secret_exists", { profileId: "p" });
    expect(ws.json().map((x) => x.result)).toEqual([false, null, null, true]);
    await send(ws, 5, "rpc", { method: "session.open", params: { profileId: "p", database: "d", secret: null } });
    let sent = JSON.parse(h.child.stdin.written.at(-1));
    expect(sent.params).toMatchObject({ secret: "pw", sshSecret: "sshpw" });
    await reply({ sessionId: "s1" });
    await send(ws, 6, "rpc", { method: "profile.test", params: { profileId: "p", secret: "explicit", sshSecret: "explicit2" } });
    sent = JSON.parse(h.child.stdin.written.at(-1));
    expect(sent.params).toMatchObject({ secret: "explicit", sshSecret: "explicit2" });
    await reply({ ok: true });
    await send(ws, 7, "rpc", { method: "meta.snapshot", params: { profileId: "p" } });
    expect(JSON.parse(h.child.stdin.written.at(-1)).params.secret).toBeUndefined();
    await send(ws, 8, "secret_delete", { profileId: "p" });
    await send(ws, 9, "secret_exists", { profileId: "p" });
    expect(ws.json().at(-1).result).toBe(false);
    await send(ws, 10, "rpc", { method: "session.open", params: { profileId: "p", secret: null } });
    expect(JSON.parse(h.child.stdin.written.at(-1)).params.secret).toBeNull();
  });

  it("drops a deleted profile's secrets once the sidecar deleted it, as the shell does (L2)", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "secret_set", { profileId: "p", secret: "pw" });
    await send(ws, 2, "secret_set", { profileId: "p:ssh", secret: "sshpw" });
    await send(ws, 3, "rpc", { method: "profile.delete", params: { profileId: "p" } });
    await reply(null, { code: -32001, message: "busy", data: { code: "profileBusy" } });
    await send(ws, 4, "secret_exists", { profileId: "p" });
    expect(ws.json().find((m) => m.id === 4)?.result).toBe(true); // a refused delete keeps them
    await send(ws, 5, "rpc", { method: "profile.delete", params: { profileId: "p" } });
    await reply(null);
    await send(ws, 6, "secret_exists", { profileId: "p" });
    await send(ws, 7, "secret_exists", { profileId: "p:ssh" });
    expect([6, 7].map((id) => ws.json().find((m) => m.id === id)?.result)).toEqual([false, false]);
  });
});

describe("picked files", () => {
  it("stages an upload under a sanitised name, hands back a token, and injects the path for import calls", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "import_stage_file", { name: "../../etc/pa ss<>wd.csv", data: Buffer.from("a,b\n1,2").toString("base64") });
    const staged = ws.json()[0].result;
    expect(staged).toMatchObject({ name: "pa ss__wd.csv", size: 7 });
    const [written] = [...h.written.entries()];
    expect(written[0]).toMatch(/pa ss__wd\.csv$/);
    expect(written[1].toString()).toBe("a,b\n1,2");
    await send(ws, 2, "rpc", { method: "import.inspect", params: { fileToken: staged.token, filePath: null } });
    expect(JSON.parse(h.child.stdin.written.at(-1)).params.filePath).toBe(written[0]);
    // M1: a path the webview names, in any spelling, is refused before the sidecar sees the call.
    const before = h.child.stdin.written.length;
    await send(ws, 3, "rpc", { method: "import.inspect", params: { fileToken: staged.token, filePath: "/etc/shadow" } });
    await send(ws, 4, "rpc", { method: "import.inspect", params: { fileToken: staged.token, FilePath: "/etc/shadow" } });
    for (const id of [3, 4]) expect(ws.json().find((m) => m.id === id)).toMatchObject({ ok: false, error: { data: { code: "shellOwned" } } });
    expect(h.child.stdin.written.length).toBe(before);
  });

  it("defaults the upload name and refuses unknown tokens before reaching the sidecar", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "import_stage_file", { data: "" });
    expect(ws.json()[0].result).toMatchObject({ name: "upload", size: 0 });
    const before = h.child.stdin.written.length;
    await send(ws, 2, "rpc", { method: "import.inspect", params: { fileToken: "forged" } });
    expect(ws.json()[1]).toMatchObject({ id: 2, ok: false, error: { data: { code: "fileToken" } } });
    expect(h.child.stdin.written.length).toBe(before);
  });

  it("spends the token only when import.run actually started a job", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "import_stage_file", { name: "a.csv", data: "YQ==" });
    const token = ws.json()[0].result.token;
    await send(ws, 2, "rpc", { method: "import.run", params: { fileToken: token } });
    await reply({ blocked: { reason: "prodFirstRun" } });
    await send(ws, 3, "rpc", { method: "import.run", params: { fileToken: token } });
    await reply({ jobId: "job1" });
    await send(ws, 4, "rpc", { method: "import.run", params: { fileToken: token } });
    expect(ws.json().map((x) => x.ok)).toEqual([true, true, true, false]);
    expect(ws.json()[3].error.data.code).toBe("fileToken");
  });

  it("a failed import.run leaves the token usable", async () => {
    await start();
    const ws = connect();
    await send(ws, 1, "import_stage_file", { name: "a.csv", data: "YQ==" });
    const token = ws.json()[0].result.token;
    await send(ws, 2, "rpc", { method: "import.run", params: { fileToken: token } });
    await reply(null, { code: 1, message: "fail" });
    expect(ws.json()[1].ok).toBe(false);
    await send(ws, 3, "rpc", { method: "import.run", params: { fileToken: token } });
    expect(JSON.parse(h.child.stdin.written.at(-1)).method).toBe("import.run");
    expect(h.child.stdin.written).toHaveLength(2);
  });
});

describe("data plane", () => {
  async function connected() {
    await start();
    vi.advanceTimersByTime(150);
    const sock = h.sockets[0];
    const token = h.spawnCalls[0].opts.env.QUERITHM_TOKEN;
    sock.emit("connect");
    return { sock, token, ws: connect() };
  }
  const nextFrame = async (ws: FakeWs, id: number, execNo: number, timeout = false) => {
    ws.emit("message", JSON.stringify({ id, cmd: "results_next", args: { executionId: ulid(idBytes(execNo)) } }));
    await flush();
    if (timeout) { vi.advanceTimersByTime(20_001); await flush(); }
    return ws.sent.filter((s) => Buffer.isBuffer(s)).at(-1) as Buffer | undefined;
  };

  it("authenticates with the token and serves frames by execution id, tagged with the request id", async () => {
    const { sock, token, ws } = await connected();
    expect(h.connectCalls[0]).toContain(process.platform === "win32" ? `querithm-bridge-${process.pid}` : `qb-${process.pid}.sock`);
    expect(sock.written).toEqual([token + "\n"]);
    sock.emit("data", frame(1, 7, "SCHEMA"));
    const out = (await nextFrame(ws, 77, 7))!;
    expect(out.readUInt32LE(0)).toBe(77);
    expect(out[4]).toBe(1);
    expect(out.readUInt16LE(5)).toBe(0); // the result index travels to the webview
    expect(out.subarray(7).toString()).toBe("SCHEMA");
  });

  it("reassembles frames split across reads and delivers several in order", async () => {
    const { sock, ws } = await connected();
    const a = frame(2, 3, "AAA"), b = frame(2, 3, "BB");
    const all = Buffer.concat([a, b]);
    sock.emit("data", all.subarray(0, 6));
    sock.emit("data", all.subarray(6, a.length + 2));
    sock.emit("data", all.subarray(a.length + 2));
    expect((await nextFrame(ws, 1, 3))!.subarray(7).toString()).toBe("AAA");
    expect((await nextFrame(ws, 2, 3))!.subarray(7).toString()).toBe("BB");
  });

  it("waits for a frame that has not arrived yet", async () => {
    const { sock, ws } = await connected();
    ws.emit("message", JSON.stringify({ id: 5, cmd: "results_next", args: { executionId: ulid(idBytes(9)) } }));
    await flush();
    expect(ws.sent).toHaveLength(0);
    sock.emit("data", frame(3, 9, "late"));
    await flush();
    expect((ws.sent.at(-1) as Buffer).subarray(7).toString()).toBe("late");
  });

  it("returns an empty frame when nothing arrives within 20 s", async () => {
    const { ws } = await connected();
    const out = (await nextFrame(ws, 4, 11, true))!;
    expect(out.readUInt32LE(0)).toBe(4);
    expect(out.length).toBe(4);
  });

  it("applies backpressure: holds 8 batches, pauses the socket on the ninth and resumes once one is taken", async () => {
    const { sock, ws } = await connected();
    for (let i = 0; i < 8; i++) sock.emit("data", frame(2, 5, `b${i}`));
    expect(sock.paused).toBe(false);
    sock.emit("data", frame(2, 5, "b8"));
    expect(sock.paused).toBe(true);
    expect((await nextFrame(ws, 1, 5))!.subarray(7).toString()).toBe("b0");
    expect(sock.paused).toBe(false);
    for (let i = 1; i <= 8; i++) expect((await nextFrame(ws, i + 1, 5))!.subarray(7).toString()).toBe(`b${i}`);
  });

  it("answers an ended or discarded run with the shell's 0xFE frame, as ipc.rs does", async () => {
    const { sock, ws } = await connected();
    sock.emit("data", frame(4, 6, "end"));
    expect((await nextFrame(ws, 1, 6))![4]).toBe(4);
    // After its end the run is gone: a late poll ends at once instead of waiting 20 s.
    expect([...(await nextFrame(ws, 2, 6))!.subarray(4)]).toEqual([0xfe, 0, 0]);
    // discard: queued frames vanish, later batches are ignored, the poll loop is told to stop, the end frame forgets it
    sock.emit("data", frame(2, 8, "x"));
    await send(ws, 3, "results_discard", { executionId: ulid(idBytes(8)) });
    sock.emit("data", frame(2, 8, "ignored"));
    expect([...(await nextFrame(ws, 4, 8))!.subarray(4)]).toEqual([0xfe, 0, 0]);
    sock.emit("data", frame(4, 8, ""));
    expect(sock.paused).toBe(false);
    expect([...(await nextFrame(ws, 9, 8))!.subarray(4)]).toEqual([0xfe, 0, 0]);
  });

  it("closes and reconnects on a frame length outside the shell's bounds, before reading or keeping it", async () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    const { sock, ws } = await connected();
    sock.emit("data", frame(1, 7, "kept"));
    const bad = Buffer.alloc(4);
    bad.writeUInt32LE(256 * 1024 * 1024 + 1, 0);
    sock.emit("data", Buffer.concat([bad, Buffer.from("no body follows")]));
    expect(sock.destroyed).toBe(true);
    expect(err).toHaveBeenCalledWith(expect.stringContaining("lost its framing"));
    vi.advanceTimersByTime(60);
    expect(h.sockets).toHaveLength(2);
    // Whole frames that came before stay readable.
    expect((await nextFrame(ws, 1, 7))!.subarray(7).toString()).toBe("kept");
    // A length shorter than the header is no frame either.
    h.sockets[1].emit("connect");
    const short = Buffer.alloc(4);
    short.writeUInt32LE(17, 0);
    h.sockets[1].emit("data", short);
    expect(h.sockets[1].destroyed).toBe(true);
    err.mockRestore();
  });

  it("drops a full queue whose consumer stopped polling for 30 s; other runs keep flowing", async () => {
    vi.useRealTimers();
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { sock, ws } = await connected();
    for (let i = 0; i < 8; i++) sock.emit("data", frame(2, 5, `b${i}`));
    // The ninth batch waits in the bridge (socket paused), and a later run's frame behind it with it.
    sock.emit("data", Buffer.concat([frame(2, 5, "b8"), frame(1, 6, "other")]));
    expect(sock.paused).toBe(true);
    vi.advanceTimersByTime(30_000);
    expect(sock.paused).toBe(false);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("nothing asked for them for 30 s"));
    expect([...(await nextFrame(ws, 1, 5))!.subarray(4)]).toEqual([0xfe, 0, 0]);
    expect((await nextFrame(ws, 2, 6))!.subarray(7).toString()).toBe("other");
    warn.mockRestore();
  });

  it("keeps retrying for a minute while the sidecar's socket is not up yet, then gives up and says so", async () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    await start();
    vi.advanceTimersByTime(150);
    h.sockets[0].emit("error", new Error("ENOENT"));
    vi.advanceTimersByTime(60);
    expect(h.sockets).toHaveLength(2);
    h.sockets[1].emit("error", new Error("ENOENT"));
    // A slow Debug sidecar listens after more than the old 10 s window (200 × 50 ms): still retrying then.
    for (let i = 0; i < 300; i++) {
      vi.advanceTimersByTime(60);
      h.sockets.at(-1).emit("error", new Error("ENOENT"));
    }
    expect(h.sockets.length).toBeGreaterThan(300);
    expect(err).not.toHaveBeenCalled();
    for (let i = 0; i < 1000; i++) {
      vi.advanceTimersByTime(60);
      h.sockets.at(-1).emit("error", new Error("ENOENT"));
    }
    const total = h.sockets.length;
    vi.advanceTimersByTime(1000);
    expect(h.sockets.length).toBe(total);
    expect(total).toBeLessThanOrEqual(1202);
    expect(err).toHaveBeenCalledWith(expect.stringContaining("data plane connection failed"));
    err.mockRestore();
  });

  it("reconnects when an established data plane closes", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { sock, token } = await connected();
    sock.emit("close");
    vi.advanceTimersByTime(60);
    expect(h.sockets).toHaveLength(2);
    h.sockets[1].emit("connect");
    expect(h.sockets[1].written).toEqual([token + "\n"]);
    // An error on the live socket is followed by its close: it does not start a second retry chain.
    h.sockets[1].emit("error", new Error("EPIPE"));
    vi.advanceTimersByTime(60);
    expect(h.sockets).toHaveLength(2);
    warn.mockRestore();
  });
});


describe("hostile RPC credential isolation", () => {
  it("raw profile.save cannot move saved credentials to another endpoint", async () => {
    await start();
    const ws = connect();
    const old = { id: "victim", name: "victim", engine: "pg", auth: "password", host: "old.invalid", ssh: { host: "old-jump.invalid", username: "u", auth: "password" } };
    const edited = { ...old, host: "attacker.invalid" };
    await send(ws, 1, "secret_set", { profileId: "victim", secret: "private-db-zz1" });
    await send(ws, 2, "secret_set", { profileId: "victim:ssh", secret: "private-ssh-zz1" });
    await send(ws, 3, "rpc", { method: "profile.save", params: edited });
    // A hardened relay reads the trusted old profile before forwarding the edit.
    if (JSON.parse(h.child.stdin.written.at(-1)).method === "profile.list") await reply([old]);
    await reply(edited);
    await send(ws, 4, "rpc", { method: "session.open", params: { profileId: "victim", secret: null, sshSecret: null } });
    const sent = JSON.parse(h.child.stdin.written.at(-1));
    expect(sent.method).toBe("session.open");
    expect(sent.params.secret).toBeNull();
    expect(sent.params.sshSecret).toBeNull();
    await reply({ sessionId: "s1" });
  });
});


it("rejects public writes to timed keyring envelope accounts", async () => {
  await start();
  const ws = connect();
  await send(ws, 1, "secret_set", { profileId: "victim:timed", secret: "not-json" });
  await send(ws, 2, "secret_delete", { profileId: "victim:ssh:timed" });
  expect(ws.json().filter((m) => m.id === 1 || m.id === 2)).toEqual([
    expect.objectContaining({ id: 1, ok: false }), expect.objectContaining({ id: 2, ok: false }),
  ]);
});

#!/usr/bin/env node
// Development bridge: runs the real .NET sidecar and exposes the same commands the Rust shell provides
// (rpc, results_next, results_discard, secret_*, sidecar_status, app_info) over a local WebSocket, so the UI can
// be developed and benchmarked in a plain browser. Never shipped. Secrets are kept in memory only.
//
//   pnpm dev:bridge            # then: pnpm dev:web  → http://127.0.0.1:1420
//
import { spawn, spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";
import { isAllowedOrigin, parseMessage } from "./dev-bridge-guard.mjs";
import { createFileTokens, refuseShellKeys } from "./dev-bridge-files.mjs";
import { wantedSecrets } from "./dev-bridge-secrets.mjs";
import { createGrants } from "./dev-bridge-grants.mjs";
import { createRelay, framingError, kindError, POLL_WAIT_MS, ulid } from "./dev-bridge-relay.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const port = Number(process.env.QUERITHM_BRIDGE_PORT ?? 1421);
const uiPort = Number(process.env.QUERITHM_UI_PORT ?? 1420);
const exe = process.platform === "win32" ? "querithm-sidecar.exe" : "querithm-sidecar";
let bin = process.env.QUERITHM_SIDECAR ?? path.join(root, "sidecar", "Querithm.Host", "bin", "Debug", "net10.0", exe);
if (!existsSync(bin)) {
  console.log("[bridge] building the sidecar…");
  const r = spawnSync("dotnet", ["build", path.join(root, "sidecar", "Querithm.Host"), "-c", "Debug", "-v", "q"], { stdio: "inherit" });
  if (r.status !== 0) process.exit(r.status ?? 1);
}
const dataDir = process.env.QUERITHM_DATA_DIR ?? path.join(os.homedir(), ".local", "share", "querithm-dev");
const fakeUpdate = process.env.QUERITHM_FAKE_UPDATE ?? "";
// The installed app reports the Tauri package version (src-tauri/sidecar.rs app_info); the bridge reports the same one.
const appVersion = JSON.parse(readFileSync(path.join(root, "src-tauri", "tauri.conf.json"), "utf8")).version;
mkdirSync(dataDir, { recursive: true });

const token = randomBytes(32).toString("hex");
const endpoint = process.platform === "win32" ? `\\\\.\\pipe\\querithm-bridge-${process.pid}` : path.join(os.tmpdir(), `qb-${process.pid}.sock`);
const child = spawn(bin, [], {
  env: {
    ...process.env, QUERITHM_TOKEN: token, QUERITHM_SOCKET: process.platform === "win32" ? `querithm-bridge-${process.pid}` : endpoint,
    QUERITHM_PARENT_PID: String(process.pid), QUERITHM_DATA_DIR: dataDir,
  },
  stdio: ["pipe", "pipe", "inherit"],
});
child.on("exit", (code) => {
  console.log(`[bridge] sidecar exited (${code})`);
  broadcast({ event: "sidecar://status", payload: { state: "dead", detail: "exited" } });
});
process.on("exit", () => child.kill());
process.on("SIGINT", () => process.exit(0));

// ---- control plane ----
let nextId = 1;
const pending = new Map();
readline.createInterface({ input: child.stdout, crlfDelay: Infinity }).on("line", (line) => {
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    return;
  }
  if (msg.id != null && pending.has(msg.id)) {
    const p = pending.get(msg.id);
    pending.delete(msg.id);
    if (msg.error) p.reject(msg.error);
    else p.resolve(msg.result ?? null);
  } else if (msg.method) {
    broadcast({ event: "sidecar://notify", payload: { method: msg.method, params: msg.params ?? null } });
  }
});

function call(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, ...(params != null ? { params } : {}) }) + "\n");
  });
}

// ---- data plane (src-tauri/src/ipc.rs's relay, as dev-bridge-relay.mjs: same limits, guard and "gone" frame) ----
const relay = createRelay();

// A Debug sidecar can take well over 10 s to start listening (first JIT, a build still finishing): keep trying for a
// minute, and say so when giving up — without a data plane no run shows rows (the sidecar fails them with the reason).
const DATA_CONNECT_ATTEMPTS = 1200;
function connectData(attempt = 0) {
  const sock = net.createConnection(endpoint);
  const generation = relay.generation();
  let buf = Buffer.alloc(0);
  let connected = false;
  sock.on("connect", () => {
    connected = true;
    sock.write(token + "\n");
    console.log("[bridge] data plane connected");
  });
  sock.on("error", (e) => {
    if (connected) return; // "close" follows and reconnects
    if (attempt < DATA_CONNECT_ATTEMPTS) setTimeout(() => connectData(attempt + 1), 50);
    else console.error(`[bridge] data plane connection failed: ${e.message}`);
  });
  sock.on("close", () => {
    if (!connected || dataSock !== sock) return;
    console.warn("[bridge] data plane closed; reconnecting");
    setTimeout(() => connectData(), 50);
  });
  // Frames in order; a full queue keeps its frame here (the socket paused) until the relay has space or drops the run.
  // A frame taken while this loop runs (a waiting results_next) calls back in: the running loop goes on instead.
  let draining = false;
  const drain = () => {
    if (draining) return;
    draining = true;
    try {
      while (buf.length >= 4) {
        const len = buf.readUInt32LE(0);
        const lost = framingError(len) ?? (buf.length >= 5 ? kindError(buf[4]) : null);
        if (lost) {
          // Never allocated or waited for: the connection closes and reconnects, as the shell's does.
          console.error(`[bridge] ${lost}`);
          buf = Buffer.alloc(0);
          sock.destroy();
          return;
        }
        if (buf.length < 4 + len) break;
        const body = buf.subarray(4, 4 + len);
        const kind = body[0];
        // The webview gets [kind][resultIndex u16][payload]; the id is implied by the call.
        if (!relay.push(ulid(body.subarray(1, 17)), kind, Buffer.concat([Buffer.from([kind]), body.subarray(17)]), generation)) {
          sock.pause();
          return;
        }
        buf = buf.subarray(4 + len);
      }
      sock.resume();
    } finally {
      draining = false;
    }
  };
  let offSpace = () => {};
  sock.on("connect", () => {
    offSpace = relay.onSpace(drain);
  });
  sock.on("close", () => offSpace());
  sock.on("data", (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    drain();
  });
  dataSock = sock;
}
let dataSock = null;
setTimeout(() => connectData(), 100);

// ---- shell commands ----
const secrets = new Map();
const policies = new Map();
function getSecret(id) {
  const entry = secrets.get(id);
  if (!entry) return undefined;
  const policy = policies.get(id);
  if (policy?.passwordRetention === "duration" && Date.now() - entry.entered >= policy.passwordRetentionMinutes * 60000) {
    secrets.delete(id);
    return undefined;
  }
  return entry.value;
}
const files = createFileTokens();
const grants = createGrants();
const uploads = mkdtempSync(path.join(os.tmpdir(), "querithm-bridge-files-"));
async function handle(cmd, args) {
  switch (cmd) {
    case "rpc": {
      const params = args.params ?? null;
      // Mirrors the shell (M1): no shell-owned name, and no other spelling of one, before anything is injected.
      const owned = refuseShellKeys(params);
      if (owned) throw owned;
      if ((args.method === "session.open" || args.method === "profile.test") && params && params.profileId) {
        // Mirrors the shell (Phase D0a): only the secrets the sign-in needs are looked up; session.open from the stored profile.
        const edited = args.method === "profile.test" && params.profile && typeof params.profile === "object" ? params.profile : null;
        const [db, ssh] = wantedSecrets(edited ?? policies.get(params.profileId));
        if (params.secret == null && db) params.secret = getSecret(params.profileId) ?? null;
        if (params.sshSecret == null && ssh) params.sshSecret = getSecret(`${params.profileId}:ssh`) ?? null;
      }
      const refused = files.inject(args.method, params) ?? grants.inject(args.method, params);
      if (refused) throw refused;
      if (args.method !== "import.run") {
        const result = await call(args.method, params);
        grants.settle(args.method, params, result);
        // Mirrors the shell (L2): a deleted profile's secrets go with it (the database's and the SSH tunnel's).
        if (args.method === "profile.delete" && params?.profileId) {
          for (const account of [params.profileId, `${params.profileId}:ssh`]) secrets.delete(account);
          policies.delete(params.profileId);
        }
        const profiles = args.method === "profile.list" ? result : args.method === "profile.save" ? [result] : [];
        for (const profile of profiles) {
          grants.remember(profile);
          getSecret(profile.id); // discard expired values before changing a policy
          const previous = policies.get(profile.id);
          if (previous && (previous.passwordRetention !== profile.passwordRetention || previous.passwordRetentionMinutes !== profile.passwordRetentionMinutes)) {
            const entry = secrets.get(profile.id);
            if (entry) entry.entered = Date.now();
          }
          policies.set(profile.id, profile);
        }
        return result;
      }
      // The picked file is spent only by an import that started (not by a Production guard block).
      let started = false;
      try {
        const r = await call(args.method, params);
        started = r?.jobId != null;
        return r;
      } finally {
        files.settleRun(params?.fileToken, started);
      }
    }
    case "results_discard":
      relay.discard(String(args.executionId));
      return null;
    case "import_stage_file": {
      // Browser stand-in for the native file dialog: the page uploads the file it picked.
      const name = path.basename(String(args.name ?? "upload")).replace(/[^\w.\- ]/g, "_") || "upload";
      const dir = mkdtempSync(path.join(uploads, "f-"));
      const target = path.join(dir, name);
      const bytes = Buffer.from(String(args.data ?? ""), "base64");
      writeFileSync(target, bytes);
      return { token: files.register(target), name, size: bytes.length };
    }
    case "db_stage_file": {
      // Browser stand-in for the shell's database picker (Phase D0c): the page uploads the database it picked; the grant
      // names this private copy, so dev-mode writes never reach the original.
      const name = path.basename(String(args.name ?? "database")).replace(/[^\w.\- ]/g, "_") || "database";
      const dir = mkdtempSync(path.join(uploads, "db-"));
      const target = path.join(dir, name);
      const bytes = Buffer.from(String(args.data ?? ""), "base64");
      writeFileSync(target, bytes);
      return { grantId: grants.register(target), name, size: bytes.length };
    }
    case "db_grant_write":
      // No native dialog here; the grant names the bridge's own copy of an upload, never the user's file.
      grants.allowWrites(String(args.grantId));
      return true;
    case "db_grant_read_only":
      grants.readOnly(String(args.grantId));
      return null;
    case "export_read": {
      // Browser stand-in for the fs plugin's scoped read: only files the sidecar wrote under <data dir>/exports.
      const root = path.resolve(dataDir, "exports");
      const target = path.resolve(String(args.path ?? ""));
      if (!target.startsWith(root + path.sep)) throw new Error("Only exported files can be read.");
      return readFileSync(target, "utf8");
    }
    case "secret_set":
      secrets.set(args.profileId, { value: args.secret, entered: Date.now() });
      return null;
    case "secret_end_connection":
      if (policies.get(args.profileId)?.passwordRetention === "once") secrets.delete(args.profileId);
      return null;
    case "secret_delete":
      secrets.delete(args.profileId);
      return null;
    case "secret_exists":
      return getSecret(args.profileId) !== undefined;
    case "sidecar_status":
      return { state: child.exitCode == null ? "running" : "dead", pid: child.pid };
    case "sidecar_restart":
      return null;
    case "app_info":
      return {
        version: appVersion, platform: process.platform === "win32" ? "windows" : process.platform === "darwin" ? "macos" : "linux", bench: process.env.QUERITHM_BENCH === "1", dataDir,
        logsDir: path.join(dataDir, "logs"), workspaceOwner: true,
      };
    // The shell's updater (src-tauri/src/updater.rs). QUERITHM_FAKE_UPDATE=<version> offers a fake release so the
    // smoke test can drive the button and dialog; its install always fails, like a cancelled admin prompt.
    case "update_check":
      if (!fakeUpdate) throw { kind: "disabled", message: "Updates are available in installed release builds only." };
      return { version: fakeUpdate, currentVersion: appVersion, notes: "Smoke release notes\n<b>not html</b>", date: null };
    case "update_download":
      if (!fakeUpdate) throw { kind: "noUpdate", message: "Check for updates first." };
      for (const downloaded of [0, 512 * 1024, 1024 * 1024]) {
        broadcast({ event: "update://progress", payload: { downloaded, total: 1024 * 1024 } });
        await new Promise((r) => setTimeout(r, 150));
      }
      return null;
    case "update_install":
      throw { kind: "cancelled", message: "The update was not installed because the administrator prompt was cancelled." };
    default:
      throw { code: -32601, message: `unknown command ${cmd}` };
  }
}

const clients = new Set();
function broadcast(msg) {
  const text = JSON.stringify(msg);
  for (const c of clients) if (c.readyState === 1) c.send(text);
}

// Loopback alone is not enough: any page in the developer's browser can open ws://127.0.0.1, so only the dev UI's
// origin may connect.
const wss = new WebSocketServer({
  host: "127.0.0.1",
  port,
  verifyClient: ({ origin }) => {
    const ok = isAllowedOrigin(origin, uiPort);
    if (!ok) console.warn(`[bridge] rejected connection from origin ${origin ?? "(none)"}`);
    return ok;
  },
});
wss.on("connection", (ws) => {
  clients.add(ws);
  ws.on("close", () => clients.delete(ws));
  ws.on("message", async (raw) => {
    const msg = parseMessage(raw);
    if (!msg) return;
    const { id, cmd, args } = msg;
    if (cmd === "results_next") {
      const frame = await relay.next(String(args.executionId), POLL_WAIT_MS);
      const out = Buffer.alloc(4 + frame.length);
      out.writeUInt32LE(id, 0);
      frame.copy(out, 4);
      ws.send(out);
      return;
    }
    try {
      ws.send(JSON.stringify({ id, ok: true, result: await handle(cmd, args) }));
    } catch (error) {
      ws.send(JSON.stringify({ id, ok: false, error }));
    }
  });
});
console.log(`[bridge] ws://127.0.0.1:${port}  sidecar pid ${child.pid}  data ${dataDir}`);

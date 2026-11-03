// Waits until the docker fixture's engines accept queries. The compose services have no healthcheck, so
// `docker compose up -d --wait` returns once each container process starts: a fresh SQL Server then needs 20 s or more on
// a CI runner before it takes a login (CI failed with "prelogin failure" 20 s after start), and the postgres image runs a
// socket-only temporary server while it initializes, which `docker compose exec psql` would otherwise connect to.
// SQL Server 2022 also sometimes exits seconds after start on GitHub's runners ("CoInitializeSecurity failure. (HRESULT
// 0x800706b5)", exit 255) while the same image and runner start it on other runs, so an exited container is started again.
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const COMPOSE = ["compose", "-p", "querithm-fixture", "-f", path.join(here, "..", "bench", "docker", "compose.yml")];

export const TIMEOUT_MS = 180_000;
export const INTERVAL_MS = 2_000;
/** Bounds one `docker compose exec`, so a hung exec cannot outlive the deadline by more than this. */
export const PROBE_TIMEOUT_MS = 30_000;
/** How many times one wait starts a container that exited before it was ready; each start gets a full `TIMEOUT_MS`. */
export const MAX_RESTARTS = 2;

/**
 * Readiness commands, run inside the service container. SQL Server takes the SA password from the container's own
 * environment and gives up a login after 5 s; PostgreSQL is probed over TCP, which the init-time server doesn't listen on.
 */
export const PROBES = {
  mssql: ["sh", "-c", `/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -l 5 -Q "SELECT 1"`],
  postgres: ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-q"],
};

/** Synchronous sleep for these synchronous loader scripts. */
function sleepSync(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * Runs `probe` (returning `{ ok, output, exitCode? }`) until it is ok or `timeoutMs` has passed; the clock and the sleep are
 * injectable for tests. An `exitCode` means the container has exited: `restart(n, exitCode)` starts it again with a fresh
 * deadline, at most `maxRestarts` times, and without `restart` or once they are used up the wait throws at once.
 * Returns the number of attempts; throws with the last probe output once the next attempt would miss the deadline.
 */
export function waitUntilReady(name, probe, { timeoutMs = TIMEOUT_MS, intervalMs = INTERVAL_MS, maxRestarts = MAX_RESTARTS, restart, now = Date.now, sleep = sleepSync } = {}) {
  let deadline = now() + timeoutMs;
  for (let attempt = 1, restarts = 0; ; attempt++) {
    const { ok, output, exitCode } = probe();
    if (ok) return attempt;
    const last = output.trim() || "(no output)";
    if (exitCode !== undefined) {
      if (!restart || restarts >= maxRestarts)
        throw new Error(`${name} exited with code ${exitCode}${restarts ? ` after ${restarts} restart${restarts === 1 ? "" : "s"}` : ""}. Last probe: ${last}`);
      restart(++restarts, exitCode);
      deadline = now() + timeoutMs;
    } else if (now() + intervalMs > deadline)
      throw new Error(`${name} did not accept connections within ${Math.round(timeoutMs / 1000)} s (${attempt} attempts). Last probe: ${last}`);
    sleep(intervalMs);
  }
}

/** The state of a compose service's container, e.g. `{ state: "exited", exitCode: 255 }`; null when docker reports none. */
export function containerState(service, spawn = spawnSync) {
  const r = spawn("docker", [...COMPOSE, "ps", "-a", "--format", "{{.State}} {{.ExitCode}}", service], {
    encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: PROBE_TIMEOUT_MS,
  });
  const [state, code] = (r.stdout ?? "").trim().split(/\s+/);
  return r.status === 0 && state ? { state, exitCode: Number(code) } : null;
}

/** One readiness probe of a compose service. A failed probe of an exited container carries the container's exit code. */
export function probeService(service, spawn = spawnSync) {
  const r = spawn("docker", [...COMPOSE, "exec", "-T", service, ...PROBES[service]], {
    encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: PROBE_TIMEOUT_MS,
  });
  const output = `${r.stdout ?? ""}${r.stderr ?? ""}${r.error ? r.error.message : ""}`;
  if (r.status === 0) return { ok: true, output };
  const c = containerState(service, spawn);
  return c?.state === "exited" ? { ok: false, output, exitCode: c.exitCode } : { ok: false, output };
}

/** Starts a compose service's existing container again, keeping its data. */
export function startService(service, spawn = spawnSync) {
  return spawn("docker", [...COMPOSE, "start", service], { stdio: ["ignore", "inherit", "inherit"], timeout: PROBE_TIMEOUT_MS });
}

function printLog(service) {
  spawnSync("docker", [...COMPOSE, "logs", "--tail", "50", service], { stdio: ["ignore", "inherit", "inherit"], timeout: PROBE_TIMEOUT_MS });
}

/**
 * Waits for each service in turn, printing the log of a container that exited before it was ready and starting it again.
 * On timeout, or when it keeps exiting, prints the reason and the service's recent log, then exits 1.
 */
export function waitForFixture(services, opts) {
  for (const service of services) {
    const started = Date.now();
    let restarts = 0;
    const restart = (n, exitCode) => {
      restarts = n;
      console.error(`${service} exited with code ${exitCode} before it accepted connections; its log, then start ${n + 1}:`);
      printLog(service);
      startService(service);
    };
    try {
      const attempts = waitUntilReady(service, () => probeService(service), { restart, ...opts });
      const again = restarts ? `, ${restarts} restart${restarts === 1 ? "" : "s"}` : "";
      console.log(`${service} ready (${attempts} probe${attempts === 1 ? "" : "s"}${again}, ${((Date.now() - started) / 1000).toFixed(1)} s)`);
    } catch (e) {
      console.error(e.message);
      printLog(service);
      process.exit(1);
    }
  }
}

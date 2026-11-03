import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import { containerState, PROBE_TIMEOUT_MS, PROBES, probeService, startService, waitUntilReady } from "../wait-ready.mjs";

type ProbeResult = { ok: boolean; output: string };

/** A fake clock that only moves when the wait sleeps. */
function fakeClock() {
  const c = { t: 0, slept: [] as number[] };
  return { c, now: () => c.t, sleep: (ms: number) => { c.slept.push(ms); c.t += ms; } };
}

describe("fixture readiness wait", () => {
  it("returns on the first successful probe without sleeping", () => {
    const { c, now, sleep } = fakeClock();
    expect(waitUntilReady("mssql", () => ({ ok: true, output: "" }), { now, sleep })).toBe(1);
    expect(c.slept).toEqual([]);
  });

  it("retries a server that is still starting until it accepts a query", () => {
    const { c, now, sleep } = fakeClock();
    const results: ProbeResult[] = [
      { ok: false, output: "Client unable to establish connection due to prelogin failure." },
      { ok: false, output: "TCP Provider: Error code 0x68." },
      { ok: true, output: "1" },
    ];
    expect(waitUntilReady("mssql", () => results.shift()!, { intervalMs: 2_000, now, sleep })).toBe(3);
    expect(c.slept).toEqual([2_000, 2_000]);
  });

  it("gives up at the deadline with the last probe output, never sleeping past it", () => {
    const { c, now, sleep } = fakeClock();
    let n = 0;
    const probe = () => ({ ok: false, output: `prelogin failure #${++n}\n` });
    expect(() => waitUntilReady("mssql", probe, { timeoutMs: 10_000, intervalMs: 2_000, now, sleep }))
      .toThrow("mssql did not accept connections within 10 s (6 attempts). Last probe: prelogin failure #6");
    expect(c.t).toBeLessThanOrEqual(10_000);
  });

  it("starts a container that exited at startup again, with a fresh deadline", () => {
    const { c, now, sleep } = fakeClock();
    const crash = "service \"mssql\" is not running";
    const results: (ProbeResult & { exitCode?: number })[] = [
      { ok: false, output: "TCP Provider: Error code 0x2749." },
      { ok: false, output: crash, exitCode: 255 },
      ...Array.from({ length: 4 }, () => ({ ok: false, output: "prelogin failure" })),
      { ok: true, output: "1" },
    ];
    const restarts: [number, number, number][] = [];
    const restart = (n: number, code: number) => restarts.push([n, code, c.t]);
    // Restarted at t = 2 s; ready at t = 12 s, past the first start's 10 s deadline and inside the second's.
    expect(waitUntilReady("mssql", () => results.shift()!, { timeoutMs: 10_000, intervalMs: 2_000, restart, now, sleep })).toBe(7);
    expect(c.t).toBe(12_000);
    expect(restarts).toEqual([[1, 255, 2_000]]);
  });

  it("gives up once the container keeps exiting after its restarts", () => {
    const { now, sleep } = fakeClock();
    let restarts = 0;
    const probe = () => ({ ok: false, output: "service \"mssql\" is not running\n", exitCode: 255 });
    expect(() => waitUntilReady("mssql", probe, { maxRestarts: 2, restart: () => restarts++, now, sleep }))
      .toThrow("mssql exited with code 255 after 2 restarts. Last probe: service \"mssql\" is not running");
    expect(restarts).toBe(2);
  });

  it("fails at once on an exited container when it may not restart it, instead of probing it until the deadline", () => {
    const { c, now, sleep } = fakeClock();
    expect(() => waitUntilReady("postgres", () => ({ ok: false, output: "", exitCode: 1 }), { now, sleep }))
      .toThrow("postgres exited with code 1. Last probe: (no output)");
    expect(c.t).toBe(0);
  });

  it("names an empty probe output instead of printing nothing", () => {
    const { now, sleep } = fakeClock();
    expect(() => waitUntilReady("postgres", () => ({ ok: false, output: "" }), { timeoutMs: 0, now, sleep }))
      .toThrow("Last probe: (no output)");
  });

  it("probes inside the fixture's container, bounded, ready only on exit 0", () => {
    const calls: { cmd: string; args: string[]; opts: { timeout: number } }[] = [];
    const spawn = (r: object) => (cmd: string, args: string[], opts: { timeout: number }) => { calls.push({ cmd, args, opts }); return r; };

    expect(probeService("postgres", spawn({ status: 0, stdout: "", stderr: "" })).ok).toBe(true);
    const { cmd, args, opts } = calls[0];
    expect(cmd).toBe("docker");
    expect(args.slice(0, 3)).toEqual(["compose", "-p", "querithm-fixture"]);
    expect(args.slice(args.indexOf("exec"), args.indexOf("exec") + 3)).toEqual(["exec", "-T", "postgres"]);
    expect(args.slice(-PROBES.postgres.length)).toEqual(PROBES.postgres);
    expect(opts.timeout).toBe(PROBE_TIMEOUT_MS);

    const refused = probeService("mssql", spawn({ status: 1, stdout: "", stderr: "prelogin failure" }));
    expect(refused).toEqual({ ok: false, output: "prelogin failure" });
    // A hung exec killed by the timeout: no exit status, the spawn error is the reason.
    const hung = probeService("mssql", spawn({ status: null, stdout: "", stderr: "", error: new Error("spawnSync docker ETIMEDOUT") }));
    expect(hung).toEqual({ ok: false, output: "spawnSync docker ETIMEDOUT" });
  });

  it("reads the container's state after a failed probe and reports an exited container's exit code", () => {
    const calls: string[][] = [];
    const spawn = (ps: object) => (_cmd: string, args: string[]) => {
      calls.push(args);
      return args.includes("exec") ? { status: 1, stdout: "", stderr: "service \"mssql\" is not running" } : ps;
    };

    expect(probeService("mssql", spawn({ status: 0, stdout: "exited 255\n", stderr: "" })))
      .toEqual({ ok: false, output: "service \"mssql\" is not running", exitCode: 255 });
    const ps = calls[1];
    expect(ps.slice(0, 3)).toEqual(["compose", "-p", "querithm-fixture"]);
    expect(ps.slice(ps.indexOf("ps"))).toEqual(["ps", "-a", "--format", "{{.State}} {{.ExitCode}}", "mssql"]);

    // Still starting, or docker could not say: keep probing.
    expect(probeService("mssql", spawn({ status: 0, stdout: "running 0\n", stderr: "" }))).not.toHaveProperty("exitCode");
    expect(probeService("mssql", spawn({ status: 1, stdout: "", stderr: "no such service: mssql" }))).not.toHaveProperty("exitCode");
    expect(containerState("mssql", spawn({ status: 0, stdout: "", stderr: "" }))).toBeNull();
  });

  it("does not read the container's state after a successful probe", () => {
    const calls: string[][] = [];
    expect(probeService("postgres", (_cmd: string, args: string[]) => { calls.push(args); return { status: 0, stdout: "", stderr: "" }; }).ok).toBe(true);
    expect(calls).toHaveLength(1);
  });

  it("starts the service's existing container, which keeps its data, bounded", () => {
    const calls: { args: string[]; opts: { timeout: number } }[] = [];
    startService("mssql", (_cmd: string, args: string[], opts: { timeout: number }) => { calls.push({ args, opts }); return { status: 0 }; });
    expect(calls[0].args.slice(0, 3)).toEqual(["compose", "-p", "querithm-fixture"]);
    expect(calls[0].args.slice(-2)).toEqual(["start", "mssql"]);
    expect(calls[0].opts.timeout).toBe(PROBE_TIMEOUT_MS);
  });

  it("probes PostgreSQL over TCP, which the image's init-time temporary server does not listen on", () => {
    expect(PROBES.postgres).toEqual(expect.arrayContaining(["pg_isready", "-h", "127.0.0.1"]));
  });

  it("logs in to SQL Server with a login timeout and the container's own password variable", () => {
    const script = PROBES.mssql.at(-1);
    expect(script).toContain('-P "$MSSQL_SA_PASSWORD"');
    expect(script).toContain("-l 5");
    expect(script).toContain("-b");
  });

  it("has no shebang line, so vitest can import it from a Windows (CRLF) checkout", () => {
    expect(readFileSync(new URL("../wait-ready.mjs", import.meta.url), "utf8").startsWith("#!")).toBe(false);
  });
});

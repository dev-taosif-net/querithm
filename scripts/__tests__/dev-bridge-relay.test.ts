import { readFileSync } from "node:fs";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import * as relayModule from "../dev-bridge-relay.mjs";

const { createRelay, framingError, ulid, GONE, KIND_GONE, MAX_FRAME_LEN, FRAME_HEADER_LEN, STALE_CONSUMER_MS, MAX_BUFFERED_BATCHES, REMEMBERED_ENDS, POLL_WAIT_MS } = relayModule;

const A = "01AAAAAAAAAAAAAAAAAAAAAAAA";
const B = "01BBBBBBBBBBBBBBBBBBBBBBBB";
const batch = (n: number) => Buffer.from([0x02, 0, 0, n]);
const end = () => Buffer.from([0x04, 0, 0]);

/** A relay on a fake clock that moves with the fake timers. */
function rig(staleAfterMs = 1_000) {
  const logs: string[] = [];
  const relay = createRelay({ staleAfterMs, now: () => Date.now(), log: (m: string) => logs.push(m) });
  let spaces = 0;
  relay.onSpace(() => spaces++);
  return { relay, logs, spaces: () => spaces };
}

beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] }));
afterEach(() => vi.useRealTimers());

describe("dev bridge relay = src-tauri/src/ipc.rs relay", () => {
  it("uses the shell's constants", () => {
    const rust = readFileSync(new URL("../../src-tauri/src/ipc.rs", import.meta.url), "utf8");
    const value = (name: string) => rust.match(new RegExp(`const ${name}: [^=]+= ([^;]+);`))![1];
    expect(value("MAX_BUFFERED_BATCHES")).toBe(String(MAX_BUFFERED_BATCHES));
    expect(value("KIND_GONE")).toBe(`0x${KIND_GONE.toString(16).toUpperCase()}`);
    expect(value("FRAME_HEADER_LEN")).toBe("1 + 16 + 2");
    expect(FRAME_HEADER_LEN).toBe(19);
    expect(value("MAX_FRAME_LEN")).toBe("256 * 1024 * 1024");
    expect(MAX_FRAME_LEN).toBe(256 * 1024 * 1024);
    expect(value("STALE_CONSUMER")).toBe(`Duration::from_secs(${STALE_CONSUMER_MS / 1000})`);
    expect(value("REMEMBERED_ENDS")).toBe(String(REMEMBERED_ENDS));
    expect(value("POLL_WAIT")).toBe(`Duration::from_secs(${POLL_WAIT_MS / 1000})`);
  });

  it("eight batches are buffered, then the reader must wait until one is taken", async () => {
    const { relay, spaces } = rig();
    for (let i = 0; i < 8; i++) expect(relay.push(A, 0x02, batch(i))).toBe(true);
    expect(relay.push(A, 0x02, batch(8))).toBe(false);
    // Schema and end frames are never held back.
    expect(relay.push(A, 0x01, Buffer.from([0x01, 0, 0]))).toBe(true);
    expect(await relay.next(A, 10)).toEqual(batch(0));
    expect(spaces()).toBeGreaterThan(0);
    expect(relay.push(A, 0x02, batch(8))).toBe(true);
  });

  it("a full queue nobody polls is dropped after the bound and other executions keep flowing", async () => {
    const { relay, logs, spaces } = rig(1_000);
    for (let i = 0; i < 8; i++) relay.push(A, 0x02, batch(i));
    expect(relay.push(A, 0x02, batch(8))).toBe(false);
    vi.advanceTimersByTime(999);
    expect(spaces()).toBe(0);
    vi.advanceTimersByTime(1);
    expect(spaces()).toBe(1); // the reader offers the frame again
    expect(relay.push(A, 0x02, batch(8))).toBe(true); // dropped with the queue
    expect(logs[0]).toContain(`results of ${A} dropped`);
    expect(relay.push(B, 0x02, batch(1))).toBe(true);
    expect(await relay.next(B, 10)).toEqual(batch(1));
    expect(await relay.next(A, 10)).toEqual(GONE);
    relay.push(A, 0x04, end());
    expect(await relay.next(A, 10)).toEqual(GONE);
    expect(relay.entries()).toBe(1); // B only
  });

  it("a consumer that keeps reading a full queue is never cut off", async () => {
    const { relay } = rig(1_000);
    for (let round = 0; round < 20; round++) {
      while (relay.push(A, 0x02, batch(round))) { /* fill */ }
      vi.advanceTimersByTime(500);
      const f = await relay.next(A, 10);
      expect([...f]).not.toEqual([...GONE]);
    }
  });

  it("results_discard frees a paused reader and drops the run until its end", async () => {
    const { relay, spaces } = rig();
    for (let i = 0; i < 8; i++) relay.push(A, 0x02, batch(i));
    expect(relay.push(A, 0x02, batch(8))).toBe(false);
    relay.discard(A);
    expect(spaces()).toBe(1); // the paused reader goes on
    expect(await relay.next(A, 10)).toEqual(GONE); // buffered frames went with it
    expect(relay.push(A, 0x02, batch(9))).toBe(true);
    relay.push(A, 0x04, end());
    expect(relay.entries()).toBe(0);
  });

  it("results_discard after the end leaves no entry", async () => {
    const { relay } = rig();
    relay.push(A, 0x02, batch(1));
    relay.push(A, 0x04, end());
    relay.discard(A); // the end is buffered: gone at once
    expect(relay.entries()).toBe(0);
    expect(await relay.next(A, 10)).toEqual(GONE);
    relay.push(B, 0x04, end());
    expect(await relay.next(B, 10)).toEqual(end());
    relay.discard(B); // consumed end: nothing made
    expect(relay.entries()).toBe(0);
    // A late frame of an ended run never makes a new entry.
    expect(relay.push(B, 0x02, batch(2))).toBe(true);
    expect(relay.entries()).toBe(0);
  });

  it("an unknown run waits, then answers nothing yet; a frame that arrives meanwhile is handed over", async () => {
    const { relay } = rig();
    const empty = relay.next(A, 20_000);
    vi.advanceTimersByTime(20_000);
    expect((await empty).length).toBe(0);
    const late = relay.next(A, 20_000);
    relay.push(A, 0x01, Buffer.from([0x01, 0, 0, 9]));
    expect([...(await late)]).toEqual([0x01, 0, 0, 9]);
  });

  it("remembers the newest ended runs only", async () => {
    const { relay } = rig();
    for (let i = 0; i <= REMEMBERED_ENDS; i++) {
      const id = `run-${i}`;
      relay.push(id, 0x04, end());
      await relay.next(id, 10);
    }
    expect(await relay.next("run-1", 10)).toEqual(GONE);
    const forgotten = relay.next("run-0", 10);
    vi.advanceTimersByTime(10);
    expect((await forgotten).length).toBe(0);
  });

  it("refuses a frame length outside the header and the 256 MiB cap", () => {
    for (const len of [0, 18, MAX_FRAME_LEN + 1, 0xffffffff]) expect(framingError(len)).toMatch(/lost its framing/);
    for (const len of [19, MAX_FRAME_LEN]) expect(framingError(len)).toBeNull();
  });

  it("names executions as the sidecar's ULID text", () => {
    expect(ulid(new Uint8Array(16))).toBe("00000000000000000000000000");
    expect(ulid(Uint8Array.from({ length: 16 }, (_, i) => (i === 15 ? 1 : 0)))).toBe("00000000000000000000000001");
  });
});

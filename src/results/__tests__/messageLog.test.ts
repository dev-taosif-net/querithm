import { describe, expect, it } from "vitest";
import type { ServerMessage } from "../../ipc/contract";
import { ArrowResultSet } from "../model";
import { errorsIn, header, heightOf, MessageLogIndex, setMarks, timeOfDay, type Entry, type SetMark } from "../messageLog";

const msg = (severity: ServerMessage["severity"], message: string, over: Partial<ServerMessage> = {}): ServerMessage =>
  ({ executionId: "e1", severity, message, batchIndex: 0, ts: 1_700_000_000_000, ...over }) as ServerMessage;

/** The full listing the Messages log did on every render before it was built incrementally (the oracle). */
function listAll(messages: readonly ServerMessage[], marks: readonly SetMark[], errorsOnly: boolean): { entries: Entry[]; offsets: number[] } {
  const out: Entry[] = [];
  const starts = new Map<number, number[]>();
  for (const [at, index] of marks) starts.set(at, [...(starts.get(at) ?? []), index]);
  const place = (at: number) => {
    if (errorsOnly) return;
    for (const index of starts.get(at) ?? []) out.push({ kind: "set", index, key: `s${index}` });
  };
  let n = 0;
  messages.forEach((m, i) => {
    place(i);
    const key = m.severity === "error" ? `e${n++}` : `m${i}`;
    if (!errorsOnly || m.severity === "error") out.push({ kind: "msg", m, i, key });
  });
  place(messages.length);
  const offsets = [0];
  for (const e of out) offsets.push(offsets[offsets.length - 1] + heightOf(e));
  return { entries: out, offsets };
}

const same = (index: MessageLogIndex, messages: readonly ServerMessage[], marks: readonly SetMark[], errorsOnly: boolean) => {
  const want = listAll(messages, marks, errorsOnly);
  expect(index.entries).toEqual(want.entries);
  expect(index.offsets).toEqual(want.offsets);
  // The message lines carry the list's own message objects.
  index.entries.forEach((e, k) => e.kind === "msg" && expect(e.m).toBe((want.entries[k] as { m: ServerMessage }).m));
};

/** A small deterministic generator (the scenarios must replay the same on every run). */
function rng(seed: number) {
  let s = seed >>> 0;
  return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 2 ** 32);
}

function randomMessage(r: () => number, i: number): ServerMessage {
  const x = r();
  if (x < 0.15) return msg("error", `failed ${i}`, r() < 0.5 ? { number: 208, class: 16, state: 1, line: 3, editorLine: 7 } : {});
  if (x < 0.25) return msg("warning", `careful ${i}\nsecond line`);
  return msg("info", r() < 0.1 ? `line ${i}\nmore\nand more` : `line ${i}`);
}

describe("MessageLogIndex", () => {
  it("lists the same lines and offsets as the full listing while batches arrive, sets begin, the filter flips and the log is cleared", () => {
    const r = rng(7);
    const index = new MessageLogIndex();
    let messages: ServerMessage[] = [];
    const setAfter: Record<number, number> = {};
    let sets: ArrowResultSet[] = [];
    let nextSet = 0;
    let errorsOnly = false;
    for (let step = 0; step < 400; step++) {
      const x = r();
      if (x < 0.55) {
        // A batch, appended in place as actions.ts does.
        const n = Math.floor(r() * 40);
        for (let k = 0; k < n; k++) messages.push(randomMessage(r, messages.length));
      } else if (x < 0.7) {
        // A result set begins after the messages that arrived so far (sometimes two at once).
        for (let k = r() < 0.2 ? 2 : 1; k > 0; k--) {
          const rs = new ArrowResultSet(nextSet++);
          sets = [...sets, rs];
          setAfter[rs.index] = messages.length;
        }
      } else if (x < 0.78) errorsOnly = !errorsOnly;
      else if (x < 0.8) messages = []; // Clear: a new list (setAfter stays, so later markers appear once the log is long enough)
      else if (x < 0.82 && sets.length) sets = sets.filter((_, k) => k !== Math.floor(r() * sets.length)); // a set dropped at the end of the stream
      const marks = setMarks({ setAfter }, { resultSets: sets });
      index.sync(messages, marks, errorsOnly);
      same(index, messages, marks, errorsOnly);
    }
  });

  it("keeps the lines it built: a batch adds only its own lines (and the trailing set markers move after them)", () => {
    const messages: ServerMessage[] = Array.from({ length: 1000 }, (_, i) => randomMessage(rng(i), i));
    const index = new MessageLogIndex();
    const marks: SetMark[] = [[1000, 0]];
    const before = [...index.sync(messages, marks, false).entries];
    expect(before.at(-1)).toEqual({ kind: "set", index: 0, key: "s0" });
    messages.push(msg("info", "next"), msg("error", "bad"));
    const after = index.sync(messages, marks, false).entries;
    // Every line already built is the same object (React skips it), the marker stays where the set began.
    for (let k = 0; k < before.length; k++) expect(after[k]).toBe(before[k]);
    expect(after.slice(before.length).map((e) => e.key)).toEqual(["m1000", `e${before.filter((e) => e.key.startsWith("e")).length}`]);
    same(index, messages, marks, false);
  });

  it("reads only the new messages of a grown list", () => {
    const list: ServerMessage[] = Array.from({ length: 20_000 }, (_, i) => msg(i % 1000 === 0 ? "error" : "info", `line ${i}`));
    let reads = 0;
    const counted = new Proxy(list, {
      get(target, p, recv) {
        if (typeof p === "string" && /^\d+$/.test(p)) reads++;
        return Reflect.get(target, p, recv);
      },
    });
    const index = new MessageLogIndex();
    index.sync(counted, [], false);
    for (let k = 0; k < 500; k++) list.push(msg("info", `line ${20_000 + k}`));
    reads = 0;
    index.sync(counted, [], false);
    expect(reads).toBeLessThanOrEqual(500 + 2);
    same(index, list, [], false);
  });

  it("starts over for another list, a rewritten list, a moved marker and the other filter", () => {
    const a = [msg("info", "a0"), msg("error", "a1"), msg("info", "a2")];
    const b = [msg("error", "b0")];
    const index = new MessageLogIndex();
    index.sync(a, [[1, 0]], false);
    same(index, a, [[1, 0]], false);
    // Another run's list.
    index.sync(b, [], false);
    same(index, b, [], false);
    index.sync(a, [[1, 0]], false);
    // The marker of set 0 now belongs elsewhere among the lines already built.
    index.sync(a, [[2, 0]], false);
    same(index, a, [[2, 0]], false);
    // Errors only, and back.
    index.sync(a, [[2, 0]], true);
    same(index, a, [[2, 0]], true);
    expect(index.entries.map((e) => e.key)).toEqual(["e0"]);
    index.sync(a, [[2, 0]], false);
    same(index, a, [[2, 0]], false);
    // The same list rewritten in place (shorter, or a different message where the last read one was).
    a.length = 1;
    index.sync(a, [], false);
    same(index, a, [], false);
    a[0] = msg("error", "replaced");
    index.sync(a, [], false);
    same(index, a, [], false);
    expect(index.entries.map((e) => e.key)).toEqual(["e0"]);
  });

  it("places sets that began together in result-set order, and none past the end of the log", () => {
    const messages = [msg("info", "x")];
    const index = new MessageLogIndex();
    const marks = setMarks({ setAfter: { 0: 1, 1: 1, 2: 0, 3: 9 } }, { resultSets: [0, 1, 2, 3].map((i) => new ArrowResultSet(i)) });
    expect(index.sync(messages, marks, false).entries.map((e) => e.key)).toEqual(["s2", "m0", "s0", "s1"]);
    same(index, messages, marks, false);
  });
});

describe("heightOf", () => {
  it("counts a message's lines and its error header", () => {
    expect(heightOf({ kind: "set", index: 0, key: "s0" })).toBe(22);
    expect(heightOf({ kind: "msg", m: msg("info", "a\nb"), i: 0, key: "m0" })).toBe(2 * 19 + 4);
    const e = msg("error", "boom", { number: 50000 });
    expect(header(e)).toBe("Msg 50000");
    expect(heightOf({ kind: "msg", m: e, i: 0, key: "e0" })).toBe(2 * 19 + 4);
  });
});

describe("errorsIn", () => {
  it("gives the errors and their positions, extended as the list grows and scanned again when it is rewritten", () => {
    const list = [msg("info", "a"), msg("error", "b"), msg("warning", "c")];
    expect(errorsIn(list).list.map((m) => m.message)).toEqual(["b"]);
    expect(errorsIn(list).at).toEqual([1]);
    list.push(msg("error", "d"), msg("info", "e"));
    expect(errorsIn(list).list).toEqual(list.filter((m) => m.severity === "error"));
    expect(errorsIn(list).at).toEqual([1, 3]);
    list.length = 2;
    expect(errorsIn(list).at).toEqual([1]);
    list[1] = msg("info", "fixed");
    expect(errorsIn(list).list).toEqual([]);
    expect(errorsIn([]).list).toEqual([]);
  });
});

describe("timeOfDay", () => {
  it("is the text of toLocaleTimeString(undefined, { hour12: false })", () => {
    const base = Date.UTC(2026, 0, 1);
    for (let k = 0; k < 2000; k++) {
      const ts = base + k * 15_811_337.25;
      expect(timeOfDay(ts)).toBe(new Date(ts).toLocaleTimeString(undefined, { hour12: false }));
    }
    expect(timeOfDay(NaN)).toBe("Invalid Date");
    expect(timeOfDay(9e15)).toBe("Invalid Date");
  });
});

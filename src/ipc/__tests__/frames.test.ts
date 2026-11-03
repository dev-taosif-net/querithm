import { describe, it, expect, vi, beforeEach } from "vitest";
import { tableFromArrays, tableToIPC, type RecordBatch, type Schema } from "apache-arrow";

import {
  decodeStream, isStreaming, ResultsDroppedError, stopAllStreams, stopStream, streamExecution, FRAME_BATCH, FRAME_END_EXECUTION, FRAME_END_RESULT, FRAME_GONE, FRAME_SCHEMA,
} from "../frames";

const { queue, resultsNext, invoke } = vi.hoisted(() => {
  const queue: Uint8Array[] = [];
  return { queue, resultsNext: vi.fn(async (_id: string) => queue.shift() ?? new Uint8Array(0)), invoke: vi.fn(async (..._a: unknown[]) => undefined) };
});
vi.mock("../transport", () => ({ transport: () => ({ resultsNext, invoke }) }));

const ipc = (cols: Record<string, Int32Array>) => tableToIPC(tableFromArrays(cols), "stream");
const frame = (kind: number, resultIndex: number, payload: Uint8Array = new Uint8Array(0)) => {
  const f = new Uint8Array(3 + payload.length);
  f[0] = kind;
  f[1] = resultIndex & 0xff;
  f[2] = resultIndex >> 8;
  f.set(payload, 3);
  return f;
};

function recorder() {
  const log: string[] = [];
  return {
    log,
    sink: {
      onSchema: (i: number, s: Schema) => log.push(`schema ${i} ${s.fields.map((f) => f.name).join(",")}`),
      onBatch: (i: number, b: RecordBatch) => log.push(`batch ${i} ${b.numRows}`),
      onEndResultSet: (i: number, truncated: boolean) => log.push(`end ${i} ${truncated}`),
      onEnd: () => log.push("done"),
    },
  };
}

describe("frames: decodeStream", () => {
  it("decodes a payload that sits at an unaligned offset inside a larger buffer", () => {
    const f = frame(FRAME_BATCH, 0, ipc({ id: Int32Array.from([7, 8, 9]) }));
    const payload = f.subarray(3);
    expect(payload.byteOffset % 8).not.toBe(0);
    const { schema, batches } = decodeStream(payload);
    expect(schema.fields.map((x) => x.name)).toEqual(["id"]);
    expect(batches[0].getChildAt(0)!.toArray()).toEqual(Int32Array.from([7, 8, 9]));
  });
  it("drops empty batches (a schema-only stream has none)", () => {
    expect(decodeStream(ipc({ id: new Int32Array(0) })).batches).toEqual([]);
  });
});

describe("frames: streamExecution", () => {
  beforeEach(() => {
    queue.length = 0;
    resultsNext.mockReset();
    resultsNext.mockImplementation(async () => queue.shift() ?? new Uint8Array(0));
    invoke.mockClear();
  });

  it("dispatches each frame kind to the sink in order and stops at end-of-execution", async () => {
    const r = recorder();
    queue.push(
      frame(FRAME_SCHEMA, 0, ipc({ a: new Int32Array(0) })),
      new Uint8Array(0), // empty poll: try again
      frame(FRAME_BATCH, 0, ipc({ a: Int32Array.from([1, 2]) })),
      frame(FRAME_END_RESULT, 0, Uint8Array.of(1)),
      frame(FRAME_END_RESULT, 1, Uint8Array.of(0)),
      frame(FRAME_END_EXECUTION, 0),
      frame(FRAME_END_RESULT, 2, Uint8Array.of(0)), // never read
    );
    await streamExecution("e1", r.sink).done;
    expect(r.log).toEqual(["schema 0 a", "batch 0 2", "end 0 true", "end 1 false", "done"]);
    expect(resultsNext).toHaveBeenCalledTimes(6);
    expect(resultsNext).toHaveBeenCalledWith("e1");
  });
  it("reads the result index as a little-endian u16", async () => {
    const r = recorder();
    queue.push(frame(FRAME_END_RESULT, 258, Uint8Array.of(0)), frame(FRAME_END_EXECUTION, 0));
    await streamExecution("e1", r.sink).done;
    expect(r.log[0]).toBe("end 258 false");
  });
  it("ignores unknown frame kinds", async () => {
    const r = recorder();
    queue.push(frame(0x7f, 0, Uint8Array.of(1, 2, 3)), frame(FRAME_END_EXECUTION, 0));
    await streamExecution("e1", r.sink).done;
    expect(r.log).toEqual(["done"]);
  });
  it("an end-result frame without a payload byte is not truncated", async () => {
    const r = recorder();
    queue.push(frame(FRAME_END_RESULT, 0), frame(FRAME_END_EXECUTION, 0));
    await streamExecution("e1", r.sink).done;
    expect(r.log[0]).toBe("end 0 false");
  });
  it("cancel stops polling after the in-flight call without calling onEnd, and drops the run's frames in the shell", async () => {
    const r = recorder();
    const h = streamExecution("e1", r.sink); // queue empty: every poll returns an empty frame
    expect(isStreaming("e1")).toBe(true);
    h.cancel();
    await h.done;
    expect(r.log).toEqual([]);
    expect(resultsNext).toHaveBeenCalledTimes(1);
    expect(invoke).toHaveBeenCalledWith("results_discard", { executionId: "e1" });
    expect(isStreaming("e1")).toBe(false);
  });
  it("a frame that arrives after cancel is not dispatched", async () => {
    const r = recorder();
    let release!: (f: Uint8Array) => void;
    resultsNext.mockImplementationOnce(() => new Promise((res) => (release = res)));
    const h = streamExecution("e1", r.sink);
    h.cancel();
    release(frame(FRAME_END_RESULT, 0, Uint8Array.of(0)));
    await h.done;
    expect(r.log).toEqual([]);
  });
  it("a frame that does not decode ends the stream and drops the run's frames in the shell (H3)", async () => {
    const r = recorder();
    const cut = ipc({ a: Int32Array.from([1, 2, 3]) }).slice(0, 20); // a stream cut inside its schema message
    queue.push(frame(FRAME_BATCH, 0, cut), frame(FRAME_END_EXECUTION, 0));
    await expect(streamExecution("bad", r.sink).done).rejects.toThrow();
    expect(invoke).toHaveBeenCalledWith("results_discard", { executionId: "bad" });
    expect(resultsNext).toHaveBeenCalledTimes(1); // never polls again
    expect(isStreaming("bad")).toBe(false);
  });
  it("a sink that throws, or a poll that fails, ends the stream the same way", async () => {
    queue.push(frame(FRAME_END_RESULT, 0, Uint8Array.of(0)));
    const sink = { ...recorder().sink, onEndResultSet: () => { throw new Error("sink"); } };
    await expect(streamExecution("s1", sink).done).rejects.toThrow("sink");
    resultsNext.mockRejectedValueOnce(new Error("bridge closed"));
    await expect(streamExecution("s2", recorder().sink).done).rejects.toThrow("bridge closed");
    expect(invoke.mock.calls).toEqual([["results_discard", { executionId: "s1" }], ["results_discard", { executionId: "s2" }]]);
  });
  it("frames the shell dropped end the stream with the reason, and nothing is discarded again", async () => {
    queue.push(frame(FRAME_GONE, 0));
    const done = streamExecution("gone", recorder().sink).done;
    await expect(done).rejects.toBeInstanceOf(ResultsDroppedError);
    await expect(done).rejects.toThrow(/dropped: nothing read them for 30 s/);
    expect(invoke).not.toHaveBeenCalled();
  });
  it("a stream that reached its end discards nothing when cancelled later", async () => {
    queue.push(frame(FRAME_END_EXECUTION, 0));
    const h = streamExecution("e1", recorder().sink);
    await h.done;
    h.cancel();
    expect(stopStream("e1")).toBe(false);
    expect(invoke).not.toHaveBeenCalled();
  });
  it("stopStream stops a live stream and discards; stopAllStreams (engine restart) stops every stream without discarding", async () => {
    const a = streamExecution("a", recorder().sink);
    const b = streamExecution("b", recorder().sink);
    const c = streamExecution("c", recorder().sink);
    expect(stopStream("a")).toBe(true);
    await a.done;
    expect(invoke).toHaveBeenCalledTimes(1);
    expect(stopAllStreams().sort()).toEqual(["b", "c"]);
    await Promise.all([b.done, c.done]);
    expect(invoke).toHaveBeenCalledTimes(1); // the shell's buffers went with the engine
    expect([isStreaming("a"), isStreaming("b"), isStreaming("c")]).toEqual([false, false, false]);
  });
});

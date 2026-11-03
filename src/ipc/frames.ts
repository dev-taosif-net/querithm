// Data-plane reader (§5): pulls frames for one execution and decodes Arrow IPC payloads. Rows are never JSON.
import { tableFromIPC, type RecordBatch, type Schema } from "apache-arrow";
import { transport } from "./transport";

export const FRAME_SCHEMA = 0x01;
export const FRAME_BATCH = 0x02;
export const FRAME_END_RESULT = 0x03;
export const FRAME_END_EXECUTION = 0x04;
/** Made by the shell, never by the sidecar: the run's frames were dropped (nothing read them for 30 s), so none follow. */
export const FRAME_GONE = 0xfe;

export interface FrameSink {
  onSchema(resultIndex: number, schema: Schema): void;
  onBatch(resultIndex: number, batch: RecordBatch): void;
  onEndResultSet(resultIndex: number, truncated: boolean): void;
  onEnd(): void;
}

/** The shell dropped this run's frames (its reader had stopped asking for them): the rest of its results never arrive. */
export class ResultsDroppedError extends Error {
  constructor() {
    super("The rest of this run's results were dropped: nothing read them for 30 s. Run the query again to see them.");
  }
}

/** Each 0x01/0x02 payload is a self-contained Arrow IPC stream (schema + at most one batch). */
export function decodeStream(payload: Uint8Array): { schema: Schema; batches: RecordBatch[] } {
  // Arrow reads flatbuffers and value buffers in place; a view at an unaligned offset (e.g. inside a transport
  // envelope) decodes wrongly in the browser build, so copy those into a fresh, 8-byte-aligned buffer.
  const bytes = payload.byteOffset % 8 === 0 ? payload : payload.slice();
  const table = tableFromIPC(bytes);
  return { schema: table.schema, batches: table.batches.filter((b) => b.numRows > 0) };
}

export interface StreamHandle {
  done: Promise<void>;
  /**
   * Stops polling. Unless `discard` is false the shell also drops the run's buffered and later frames (when its end has
   * not arrived yet): frames nobody reads would hold the one reader every run shares (§5). After an engine restart the
   * shell's buffers are already gone, so nothing is discarded then.
   */
  cancel(o?: { discard?: boolean }): void;
}

/** Streams still reading, by execution id: disposing a run stops its own, an engine restart stops them all. */
const live = new Map<string, StreamHandle>();

function discard(executionId: string) {
  void transport().invoke<void>("results_discard", { executionId }).catch(() => {});
}

/** Reads until end-of-execution. Polls with results_next (each call blocks up to ~20 s in the shell). */
export function streamExecution(executionId: string, sink: FrameSink): StreamHandle {
  let stopped = false;
  // The run's end went through the shell (its end frame, or the shell dropped it): nothing is left there to discard.
  let ended = false;
  const handle: StreamHandle = {
    done: Promise.resolve(),
    cancel(o = {}) {
      if (stopped) return;
      stopped = true;
      if (live.get(executionId) === handle) live.delete(executionId);
      if (!ended && o.discard !== false) discard(executionId);
    },
  };
  live.set(executionId, handle);
  handle.done = (async () => {
    try {
      while (!stopped) {
        const frame = await transport().resultsNext(executionId);
        if (stopped) return;
        if (frame.byteLength === 0) continue;
        const kind = frame[0];
        const resultIndex = frame[1] | (frame[2] << 8);
        const payload = frame.subarray(3);
        switch (kind) {
          case FRAME_SCHEMA:
            sink.onSchema(resultIndex, decodeStream(payload).schema);
            break;
          case FRAME_BATCH: {
            const { batches } = decodeStream(payload);
            for (const b of batches) sink.onBatch(resultIndex, b);
            break;
          }
          case FRAME_END_RESULT:
            sink.onEndResultSet(resultIndex, payload[0] === 1);
            break;
          case FRAME_END_EXECUTION:
            ended = true;
            sink.onEnd();
            return;
          case FRAME_GONE:
            ended = true;
            throw new ResultsDroppedError();
        }
      }
    } catch (e) {
      if (stopped) return; // cancelled meanwhile: nobody waits for this stream any more
      // A frame that does not decode, a sink that throws or a failed poll ends the stream: the shell must not keep
      // buffering a run nobody reads any more (it would stall every other tab's results).
      if (!ended) discard(executionId);
      ended = true;
      throw e;
    } finally {
      stopped = true;
      if (live.get(executionId) === handle) live.delete(executionId);
    }
  })();
  return handle;
}

/** Stops the run's stream, if it is still reading, and drops its frames in the shell. False when none was reading. */
export function stopStream(executionId: string): boolean {
  const h = live.get(executionId);
  if (!h) return false;
  h.cancel();
  return true;
}

/** True while a stream is reading this run's frames (its end has not arrived). */
export function isStreaming(executionId: string): boolean {
  return live.has(executionId);
}

/** After an engine restart: every stream stops (the shell's buffers went with the engine). Returns their run ids. */
export function stopAllStreams(): string[] {
  const ids = [...live.keys()];
  for (const h of [...live.values()]) h.cancel({ discard: false });
  return ids;
}

// Runs buildJsonView in a web worker (json.worker.ts), or inline where there is no Worker (tests).
import { buildJsonView, type JsonViewResult } from "./jsonView";

let seq = 0;

/** Formats `text` off the main thread. `signal` aborts (and terminates the worker) when the viewer moves on. */
export async function parseJson(text: string, signal?: AbortSignal): Promise<JsonViewResult> {
  if (typeof Worker === "undefined") return buildJsonView(text);
  const { default: JsonWorker } = await import("./json.worker?worker");
  const worker = new JsonWorker();
  const id = ++seq;
  try {
    return await new Promise<JsonViewResult>((resolve, reject) => {
      const abort = () => reject(new DOMException("aborted", "AbortError"));
      if (signal?.aborted) return abort();
      signal?.addEventListener("abort", abort, { once: true });
      worker.onmessage = (e: MessageEvent<{ id: number; result: JsonViewResult }>) => {
        if (e.data.id === id) resolve(e.data.result);
      };
      worker.onerror = (e) => resolve({ ok: false, error: `The JSON formatter stopped: ${e.message || "unknown error"}` });
      worker.postMessage({ id, text });
    });
  } finally {
    worker.terminate();
  }
}

// Formats a JSON cell value off the main thread (Phase C2): up to 10 MB of text never blocks the UI.
import { buildJsonView } from "./jsonView";

export interface JsonWorkerRequest {
  id: number;
  text: string;
}

self.onmessage = (e: MessageEvent<JsonWorkerRequest>) => {
  const { id, text } = e.data;
  const post = (self as unknown as Worker).postMessage.bind(self);
  try {
    const r = buildJsonView(text);
    if (r.ok) post({ id, result: r }, [r.view.starts.buffer, r.view.depth.buffer, r.view.foldEnd.buffer]);
    else post({ id, result: r });
  } catch (err) {
    post({ id, result: { ok: false, error: err instanceof Error ? err.message : String(err) } });
  }
};

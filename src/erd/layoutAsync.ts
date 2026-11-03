// Small diagrams lay out inline; above 100 tables the layout runs in a web worker.
import { layoutGraph, type Positioned } from "./erdLayout";
import LayoutWorker from "./erdLayout.worker?worker";
import type { LayoutRequest } from "./erdLayout.worker";

export const WORKER_THRESHOLD = 100;

export function layoutAsync(req: LayoutRequest): Promise<Positioned[]> {
  if (req.nodes.length <= WORKER_THRESHOLD) return Promise.resolve(layoutGraph(req));
  const worker = new LayoutWorker();
  return new Promise<Positioned[]>((resolve, reject) => {
    worker.onmessage = (e: MessageEvent<{ positions?: Positioned[]; error?: string }>) => (e.data.positions ? resolve(e.data.positions) : reject(new Error(e.data.error)));
    worker.onerror = (e) => reject(new Error(e.message));
    worker.postMessage(req);
  }).finally(() => worker.terminate());
}

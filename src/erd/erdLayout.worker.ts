// Dagre layout off the main thread for large diagrams (> 100 tables), so panning never stalls.
import { layoutGraph } from "./erdLayout";

export interface LayoutRequest {
  nodes: { id: number; width: number; height: number }[];
  edges: { from: number; to: number; self?: boolean }[];
}

self.onmessage = (e: MessageEvent<LayoutRequest>) => {
  try {
    (self as unknown as Worker).postMessage({ positions: layoutGraph(e.data) });
  } catch (err) {
    (self as unknown as Worker).postMessage({ error: err instanceof Error ? err.message : String(err) });
  }
};

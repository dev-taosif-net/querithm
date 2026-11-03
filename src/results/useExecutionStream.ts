// A result stream the sidecar produces outside a query tab (import preview, data compare diffs): frames from the data
// plane into an ExecutionData the grid renders; closed on the sidecar when the component goes away.
import { useEffect, useState, useSyncExternalStore } from "react";
import { rpc } from "../ipc/client";
import { streamExecution } from "../ipc/frames";
import { ExecutionData, executionData } from "./model";

export function useExecutionStream(executionId: string | undefined): ExecutionData | undefined {
  const [data, setData] = useState<ExecutionData | undefined>();
  useEffect(() => {
    if (!executionId) return;
    const d = new ExecutionData(executionId);
    executionData.set(executionId, d);
    setData(d);
    const h = streamExecution(executionId, {
      onSchema: (ri, schema) => d.get(ri).setSchema(schema),
      onBatch: (ri, b) => { d.get(ri).append(b); d.notify(); },
      onEndResultSet: (ri, truncated) => { d.get(ri).finish(truncated); d.notify(); },
      onEnd: () => d.notify(),
    });
    // A stream that broke off (a frame that does not decode, frames the shell dropped) shows what arrived as interrupted.
    h.done.catch((e) => {
      console.warn("result stream ended", e);
      d.finishStream();
    });
    return () => {
      // Stops polling and drops the shell's buffered frames (H3: an undrained queue stalled every tab's results), then
      // closes the execution, which also stops its producer (a data compare still streaming).
      h.cancel();
      executionData.delete(executionId);
      void rpc("query.close", { executionId }).catch(() => {});
    };
  }, [executionId]);
  useSyncExternalStore(data?.subscribe ?? (() => () => {}), data?.getVersion ?? (() => 0));
  return data;
}

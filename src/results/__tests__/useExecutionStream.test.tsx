// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import type { FrameSink } from "../../ipc/frames";

const { rpc, stream, handles } = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  handles: [] as { sink: FrameSink; cancel: ReturnType<typeof vi.fn>; done: Promise<void> }[],
  stream: vi.fn(),
}));
vi.mock("../../ipc/client", () => ({ rpc }));
vi.mock("../../ipc/frames", () => ({
  streamExecution: (id: string, sink: FrameSink) => {
    const h = { sink, cancel: vi.fn(), done: new Promise<void>(() => {}) };
    handles.push(h);
    stream(id);
    return h;
  },
}));

import { executionData } from "../model";
import { useExecutionStream } from "../useExecutionStream";
import { ordersBatch } from "./fixtures";

beforeEach(() => {
  rpc.mockClear();
  handles.length = 0;
  executionData.clear();
});

describe("useExecutionStream", () => {
  it("does nothing without an execution id", () => {
    const { result } = renderHook(() => useExecutionStream(undefined));
    expect(result.current).toBeUndefined();
    expect(stream).not.toHaveBeenCalled();
  });

  it("feeds frames into an ExecutionData registered for the grid", () => {
    const { result } = renderHook(() => useExecutionStream("e1"));
    expect(executionData.get("e1")).toBe(result.current);
    const batch = ordersBatch();
    act(() => {
      handles[0].sink.onSchema(0, batch.schema);
      handles[0].sink.onBatch(0, batch);
    });
    expect(result.current!.resultSets[0].rowCount).toBe(3);
    expect(result.current!.resultSets[0].complete).toBe(false);
    act(() => {
      handles[0].sink.onEndResultSet(0, true);
      handles[0].sink.onEnd();
    });
    expect(result.current!.resultSets[0]).toMatchObject({ complete: true, truncated: true });
  });

  it("on unmount cancels the stream, unregisters and closes the execution on the sidecar (errors ignored)", () => {
    rpc.mockRejectedValueOnce(new Error("gone"));
    const { unmount } = renderHook(() => useExecutionStream("e2"));
    unmount();
    expect(handles[0].cancel).toHaveBeenCalled();
    expect(executionData.has("e2")).toBe(false);
    expect(rpc).toHaveBeenCalledWith("query.close", { executionId: "e2" });
  });
});

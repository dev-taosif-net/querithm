// @vitest-environment jsdom
// H3 with the real stream reader: a consumer that goes away (a closed Data Compare tab, the import wizard) must drop its
// run's frames in the shell, or the shell's one socket reader waits on its full queue and every tab's results stall.
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { tableFromArrays, tableToIPC } from "apache-arrow";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  invoke: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  next: vi.fn(async (_id: string): Promise<Uint8Array> => new Promise<Uint8Array>(() => {})),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc }));
vi.mock("../../ipc/transport", () => ({ transport: () => ({ resultsNext: m.next, invoke: m.invoke }) }));

import { isStreaming } from "../../ipc/frames";
import { executionData } from "../model";
import { useExecutionStream } from "../useExecutionStream";

beforeEach(() => {
  vi.clearAllMocks();
  m.next.mockImplementation(() => new Promise<Uint8Array>(() => {}));
  executionData.clear();
});

describe("useExecutionStream (real frame reader)", () => {
  it("unmounting while frames are still coming drops them in the shell and closes the execution", async () => {
    const { unmount } = renderHook(() => useExecutionStream("cmp1"));
    await waitFor(() => expect(m.next).toHaveBeenCalledWith("cmp1"));
    expect(isStreaming("cmp1")).toBe(true);
    unmount();
    expect(m.invoke).toHaveBeenCalledWith("results_discard", { executionId: "cmp1" });
    expect(m.rpc).toHaveBeenCalledWith("query.close", { executionId: "cmp1" });
    expect(isStreaming("cmp1")).toBe(false);
  });

  it("a stream that already ended is only closed: the shell has nothing left to drop", async () => {
    m.next.mockResolvedValueOnce(Uint8Array.of(0x04, 0, 0));
    const { unmount } = renderHook(() => useExecutionStream("prev1"));
    await waitFor(() => expect(isStreaming("prev1")).toBe(false));
    unmount();
    expect(m.invoke).not.toHaveBeenCalled();
    expect(m.rpc).toHaveBeenCalledWith("query.close", { executionId: "prev1" });
  });

  it("a frame that does not decode ends the stream: the shell drops the rest and what arrived is marked finished", async () => {
    const cut = tableToIPC(tableFromArrays({ a: Int32Array.from([1]) }), "stream").slice(0, 20); // cut inside its schema message
    m.next.mockResolvedValueOnce(Uint8Array.of(0x02, 0, 0, ...cut));
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { result } = renderHook(() => useExecutionStream("bad1"));
    await waitFor(() => expect(result.current?.streamEnded).toBe(true));
    expect(m.invoke).toHaveBeenCalledWith("results_discard", { executionId: "bad1" });
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });
});

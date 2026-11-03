import { beforeEach, describe, expect, it, vi } from "vitest";

const { saveBytes, toast, flags, workers } = vi.hoisted(() => ({
  saveBytes: vi.fn(async () => "/tmp/out.csv" as string | null),
  toast: vi.fn(),
  flags: { isTauri: true },
  workers: [] as { posted: unknown[]; terminated: boolean; reply: (w: unknown) => void }[],
}));
vi.mock("../../app/saveBytes", () => ({ saveBytes }));
vi.mock("../../store/ui", () => ({ toast }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return flags.isTauri; } }));
vi.mock("../export.worker?worker", () => ({
  default: class {
    posted: unknown[] = [];
    terminated = false;
    onmessage: ((e: { data: unknown }) => void) | null = null;
    onerror: ((e: { message: string }) => void) | null = null;
    constructor() {
      workers.push(this as never);
    }
    postMessage(m: unknown) {
      this.posted.push(m);
      queueMicrotask(() => {
        const mode = (m as { sheetName: string }).sheetName;
        if (mode === "boom") this.onmessage?.({ data: { error: "xlsx exploded" } });
        else if (mode === "crash") this.onerror?.({ message: "worker crashed" });
        else this.onmessage?.({ data: new Uint8Array([1, 2, 3]) });
      });
    }
    terminate() {
      this.terminated = true;
    }
    reply() {}
  },
}));

import { exportFormats, exportResultSet } from "../export";
import { ArrowResultSet } from "../model";
import { ordersSet } from "./fixtures";

beforeEach(() => {
  saveBytes.mockClear();
  toast.mockClear();
  workers.length = 0;
  flags.isTauri = true;
});

describe("exportFormats", () => {
  it("offers xlsx, csv, tsv and json with mime types", () => {
    expect(exportFormats.map((f) => f.format)).toEqual(["xlsx", "csv", "tsv", "json"]);
    expect(exportFormats.find((f) => f.format === "csv")!.mime).toBe("text/csv");
  });
});

describe("exportResultSet", () => {
  it("does nothing for a result set without batches", async () => {
    await exportResultSet(new ArrowResultSet(0), "csv", "x");
    expect(workers).toHaveLength(0);
    expect(saveBytes).not.toHaveBeenCalled();
  });

  it("sends one IPC buffer per batch to the worker, saves the bytes and toasts in the app", async () => {
    const rs = ordersSet();
    await exportResultSet(rs, "csv", "orders-result1");
    expect(workers[0].posted).toHaveLength(1);
    const msg = workers[0].posted[0] as { format: string; batches: ArrayBuffer[]; sheetName: string };
    expect(msg.format).toBe("csv");
    expect(msg.batches).toHaveLength(1);
    expect(msg.batches[0].byteLength).toBeGreaterThan(0);
    expect(workers[0].terminated).toBe(true);
    expect(saveBytes).toHaveBeenCalledWith("orders-result1.csv", new Uint8Array([1, 2, 3]), { name: "CSV", extension: "csv", mime: "text/csv" });
    expect(toast).toHaveBeenCalledWith("success", "Exported 3 rows to /tmp/out.csv");
  });

  it("stays quiet in the browser or when the save dialog was cancelled", async () => {
    flags.isTauri = false;
    await exportResultSet(ordersSet(), "json", "n");
    expect(toast).not.toHaveBeenCalled();
    flags.isTauri = true;
    saveBytes.mockResolvedValueOnce(null);
    await exportResultSet(ordersSet(), "tsv", "n");
    expect(toast).not.toHaveBeenCalled();
  });

  it("worker error replies and crashes reject, and the worker is still terminated", async () => {
    await expect(exportResultSet(ordersSet(), "xlsx", "boom")).rejects.toThrow("xlsx exploded");
    expect(workers[0].terminated).toBe(true);
    await expect(exportResultSet(ordersSet(), "xlsx", "crash")).rejects.toThrow("worker crashed");
    expect(workers[1].terminated).toBe(true);
    expect(saveBytes).not.toHaveBeenCalled();
  });
});

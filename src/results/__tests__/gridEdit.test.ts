import { describe, expect, it, vi } from "vitest";

const { rpc, ask, toast, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string) {
      super(message);
    }
  }
  return { rpc: vi.fn(), ask: vi.fn(), toast: vi.fn(), RpcError };
});
vi.mock("../../ipc/client", () => ({ rpc, RpcError }));
vi.mock("../../store/ui", () => ({ ask: (make: (r: (v: unknown) => void) => unknown) => new Promise((res) => ask(make(res), res)), toast }));

import { useExecutions } from "../../store/executions";
import { useProfiles } from "../../store/profiles";
import { useTabs } from "../../store/tabs";
import { applyChanges, editInfo as getEditInfo, ensureTokens, resolvePendingEdits, reviewChanges, scheduleTokens, stagingFor } from "../gridEdit";
import { executionData } from "../model";
import { stagingKey, stagings } from "../staging";
import { editInfo, ordersSet, registerExecution, view } from "./fixtures";

const EX = "ex1";
const ex = (row: number) => ({ kind: "existing" as const, row });

function setup(engine: "mssql" | "pg" = "mssql", env = "dev", tokenKind: string | null = "rowversion") {
  rpc.mockReset();
  ask.mockReset();
  toast.mockReset();
  stagings.clear();
  executionData.clear();
  useExecutions.setState({ views: { [EX]: view(EX, { edits: { 0: editInfo({ tokenKind }) } }) } });
  useProfiles.setState({ profiles: [{ id: "p1", name: "P", engine, environment: env } as never] });
  useTabs.setState({ tabs: { tab1: { id: "tab1", profileId: "p1", executions: [EX] } as never } });
  const rs = ordersSet();
  registerExecution(EX, rs);
  const staging = stagingFor(EX, rs, engine)!;
  return { rs, staging };
}

/** Answers ask(): the next dialog kinds resolve with the given values. */
function answers(...vals: unknown[]) {
  ask.mockImplementation((_d, resolve) => resolve(vals.shift()));
}

const okResult = { ok: true, appliedUpdates: 1, appliedInserts: 0, appliedDeletes: 0 };

describe("stagingFor / editInfo", () => {
  it("creates one staging per result set, and none for read-only results", () => {
    const { rs, staging } = setup();
    expect(stagingFor(EX, rs, "mssql")).toBe(staging);
    expect(staging.edit.table).toBe("orders");
    expect(getEditInfo(EX, 0)?.table).toBe("orders");
    useExecutions.setState({ views: { [EX]: view(EX, { edits: { 0: editInfo({ editable: false }) } }) } });
    stagings.clear();
    expect(stagingFor(EX, rs, "pg")).toBeUndefined();
    expect(getEditInfo("nope", 0)).toBeUndefined();
  });
});

describe("ensureTokens", () => {
  for (const engine of ["mssql", "pg"] as const) {
    it(`reads tokens for edited rows and stores them (${engine})`, async () => {
      const { staging } = setup(engine);
      staging.setCell(ex(0), 1, { value: "uno" });
      rpc.mockResolvedValue({ tokens: ["tok"] });
      await ensureTokens(EX, staging);
      expect(rpc).toHaveBeenCalledWith("data.rowTokens", expect.objectContaining({ sessionId: "sess1", executionId: EX, resultIndex: 0 }));
      expect(rpc.mock.calls[0][1].keys).toEqual([[{ isNull: false, useDefault: false, text: "1" }]]);
      expect(staging.tokens.get(0)).toBe("tok");
      expect(toast).not.toHaveBeenCalled();
    });
  }

  it("warns when rows changed on the server (null tokens)", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    staging.setCell(ex(1), 1, { value: "dos" });
    rpc.mockResolvedValue({ tokens: [null, null] });
    await ensureTokens(EX, staging);
    expect(staging.conflicted.size).toBe(2);
    expect(toast).toHaveBeenCalledWith("warn", expect.stringContaining("2 edited rows have changed"));
    staging.setCell(ex(2), 1, { value: "tres" });
    rpc.mockResolvedValue({ tokens: [null] });
    await ensureTokens(EX, staging);
    expect(toast).toHaveBeenLastCalledWith("warn", expect.stringContaining("1 edited row has changed"));
  });

  it("does nothing without edits or without a session", async () => {
    const { staging } = setup();
    await ensureTokens(EX, staging);
    staging.setCell(ex(0), 1, { value: "uno" });
    useExecutions.setState({ views: {} });
    await ensureTokens(EX, staging);
    expect(rpc).not.toHaveBeenCalled();
  });

  it("serializes concurrent reads for the same staging", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockResolvedValue({ tokens: ["t"] });
    await Promise.all([ensureTokens(EX, staging), ensureTokens(EX, staging)]);
    expect(rpc).toHaveBeenCalledTimes(1);
  });

  it("scheduleTokens debounces and reports failures as a toast", async () => {
    vi.useFakeTimers();
    try {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      rpc.mockRejectedValue(new Error("down"));
      scheduleTokens(EX, staging);
      scheduleTokens(EX, staging);
      await vi.advanceTimersByTimeAsync(300);
      expect(rpc).toHaveBeenCalledTimes(1);
      expect(toast).toHaveBeenCalledWith("error", "down");
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("reviewChanges", () => {
  for (const engine of ["mssql", "pg"] as const) {
    it(`shows the generated SQL for the tab's engine (${engine})`, async () => {
      const { staging } = setup(engine);
      staging.setCell(ex(0), 1, { value: "uno" });
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : { sql: "UPDATE ...", notes: ["n"] }));
      answers(true);
      expect(await reviewChanges("tab1", EX, staging)).toBe(true);
      expect(ask.mock.calls[0][0]).toMatchObject({ kind: "gridPreview", sql: "UPDATE ...", engine, notes: ["n"] });
      expect(ask.mock.calls[0][0].title).toContain("dbo.orders: 1 updated");
    });
  }

  it("returns false and toasts when the preview fails", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => {
      if (m === "data.rowTokens") return { tokens: ["t"] };
      throw new Error("bad sql");
    });
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenCalledWith("error", "bad sql");
  });

  it("refuses when values are invalid, the session is closed or token reads fail", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "waytoolong" }); // nvarchar(5)
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", expect.stringContaining("Fix this value first"));
    staging.revert([ex(0)]);
    staging.setCell(ex(0), 1, { value: "uno" });
    useExecutions.setState({ views: { [EX]: { ...view(EX), sessionId: "" } } });
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", expect.stringContaining("session is closed"));
    useExecutions.setState({ views: { [EX]: view(EX, { edits: { 0: editInfo() } }) } });
    rpc.mockRejectedValue(new Error("tokens down"));
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", "tokens down");
  });

  it("lists many problems with an ellipsis", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "waytoolong" });
    for (let i = 0; i < 4; i++) staging.addRow(); // each new row misses the NOT NULL name
    await reviewChanges("tab1", EX, staging);
    expect(toast.mock.calls.at(-1)![1]).toMatch(/Fix 5 values first: .*…$/);
  });

  it("drops edits of rows that changed on the server when the user agrees, and stops when nothing is left", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockResolvedValue({ tokens: [null] });
    answers(true);
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(ask.mock.calls[0][0]).toMatchObject({ kind: "confirm", title: "Rows changed on the server" });
    expect(staging.hasChanges).toBe(false);
  });

  it("keeps everything when the user declines to drop", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockResolvedValue({ tokens: [null] });
    answers(false);
    expect(await reviewChanges("tab1", EX, staging)).toBe(false);
    expect(staging.hasChanges).toBe(true);
  });
});

describe("applyChanges", () => {
  it("is a no-op without changes", async () => {
    const { staging } = setup();
    expect(await applyChanges("tab1", EX, staging)).toBe(true);
    expect(rpc).not.toHaveBeenCalled();
  });

  for (const engine of ["mssql", "pg"] as const) {
    it(`writes, commits the staging and notifies the grid (${engine})`, async () => {
      const { staging } = setup(engine);
      staging.setCell(ex(0), 1, { value: "uno" });
      const notify = vi.spyOn(executionData.get(EX)!, "notify");
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : okResult));
      expect(await applyChanges("tab1", EX, staging)).toBe(true);
      expect(rpc).toHaveBeenCalledWith("data.applyPatch", expect.objectContaining({ forceOverwriteConflicts: false }));
      expect(staging.hasChanges).toBe(false);
      expect(staging.cell(ex(0), 1).value).toBe("uno");
      expect(toast).toHaveBeenCalledWith("success", "1 row written to dbo.orders.");
      expect(notify).toHaveBeenCalled();
    });
  }

  for (const outcome of ["written", "failed"] as const) {
    it(`edits made while an Apply is in flight are kept when it is ${outcome} (W2)`, async () => {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      let reply!: (v: unknown) => void;
      let fail!: (e: unknown) => void;
      rpc.mockImplementation((m: string) => (m === "data.applyPatch" ? new Promise((res, rej) => ((reply = res), (fail = rej))) : Promise.resolve({ tokens: ["t", "t"] })));
      const applied = applyChanges("tab1", EX, staging);
      await vi.waitFor(() => expect(reply).toBeDefined());
      // While the patch is on its way: a newer value of the cell being written, another row, a new row.
      staging.setCell(ex(0), 1, { value: "eins" });
      staging.setCell(ex(1), 2, { value: "9" });
      staging.addRow(new Map([[1, { value: "neu" }]]));
      if (outcome === "written") reply({ ...okResult });
      else fail(new RpcError("deadlock victim", -32000, "dbError"));
      expect(await applied).toBe(outcome === "written");
      if (outcome === "written") {
        // The server holds what was sent; the newer value is still staged over it, never shown as written.
        expect(staging.original(0, 1)).toBe("uno");
        expect(staging.cell(ex(0), 1)).toMatchObject({ value: "eins", staged: true });
      } else {
        expect(staging.cell(ex(0), 1)).toMatchObject({ value: "eins", staged: true });
        expect(staging.original(0, 1)).not.toBe("uno");
      }
      expect(staging.cell(ex(1), 2)).toMatchObject({ value: "9", staged: true });
      expect(staging.counts).toEqual({ updates: 2, inserts: 1, deletes: 0 });
      // The next Apply sends exactly what is still staged.
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t", "t"] } : { ...okResult, appliedUpdates: 2, appliedInserts: 1 }));
      expect(await applyChanges("tab1", EX, staging)).toBe(true);
      const patch = rpc.mock.calls.filter((c) => c[0] === "data.applyPatch").at(-1)![1];
      expect(patch.updates.map((u: { changes: Record<string, unknown> }) => u.changes)).toEqual([{ name: { isNull: false, useDefault: false, text: "eins" } }, { qty: { isNull: false, useDefault: false, text: "9" } }]);
      expect(patch.inserts).toHaveLength(1);
      expect(staging.hasChanges).toBe(false);
    });
  }

  it("uses plural wording for several rows", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : { ...okResult, appliedUpdates: 2 }));
    await applyChanges("tab1", EX, staging);
    expect(toast).toHaveBeenCalledWith("success", "2 rows written to dbo.orders.");
  });

  it("Prod always shows the preview first; cancelling it writes nothing", async () => {
    const { staging } = setup("mssql", "prod");
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : m === "data.previewPatch" ? { sql: "x", notes: [] } : okResult));
    answers(false);
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(rpc.mock.calls.some((c) => c[0] === "data.applyPatch")).toBe(false);
    answers(true);
    expect(await applyChanges("tab1", EX, staging)).toBe(true);
    // an already reviewed apply skips the second preview
    staging.setCell(ex(1), 1, { value: "dos" });
    ask.mockReset();
    expect(await applyChanges("tab1", EX, staging, false, true)).toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });

  it("asks for guard confirmations: destructive and prod set the matching flag, declining stops", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    const seen: { d: boolean; p: boolean }[] = [];
    rpc.mockImplementation(async (m: string, p: { confirmedDestructive: boolean; confirmedProd: boolean }) => {
      if (m === "data.rowTokens") return { tokens: ["t"] };
      seen.push({ d: p.confirmedDestructive, p: p.confirmedProd });
      if (seen.length === 1) return { ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, blocked: { reason: "destructive" } };
      if (seen.length === 2) return { ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, blocked: { reason: "prod" } };
      return okResult;
    });
    answers(true, true);
    expect(await applyChanges("tab1", EX, staging)).toBe(true);
    expect(seen).toEqual([{ d: false, p: false }, { d: true, p: false }, { d: true, p: true }]);
    staging.setCell(ex(0), 1, { value: "x" });
    seen.length = 0;
    answers(false);
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
  });

  it("gives up when the guard keeps blocking", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : { ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, blocked: { reason: "prod" } }));
    answers(true, true, true);
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
  });

  it("reports sidecar failures; known codes without a prefix", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => {
      if (m === "data.rowTokens") return { tokens: ["t"] };
      throw new RpcError("Result closed", 1, "resultClosed");
    });
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", "Result closed");
    rpc.mockImplementation(async (m: string) => {
      if (m === "data.rowTokens") return { tokens: ["t"] };
      throw new Error("deadlock");
    });
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", "Could not apply: deadlock");
  });

  it("two Ctrl+S before the reply send one patch: the second joins the first (H6)", async () => {
    const { staging } = setup();
    staging.addRow(new Map([[1, { value: "n" }]]));
    let reply!: (v: unknown) => void;
    rpc.mockImplementation((m: string) => (m === "data.applyPatch" ? new Promise((res) => (reply = res)) : Promise.resolve({ tokens: [] })));
    const first = applyChanges("tab1", EX, staging);
    const second = applyChanges("tab1", EX, staging);
    expect(second).toBe(first);
    await vi.waitFor(() => expect(reply).toBeDefined());
    reply({ ok: true, appliedUpdates: 0, appliedInserts: 1, appliedDeletes: 0, insertedKeys: { 0: [{ text: "9" }] } });
    expect(await Promise.all([first, second])).toEqual([true, true]);
    expect(rpc.mock.calls.filter((c) => c[0] === "data.applyPatch")).toHaveLength(1);
    expect(staging.hasChanges).toBe(false);
    // Once it is done, a new Apply runs again.
    staging.addRow(new Map([[1, { value: "m" }]]));
    rpc.mockResolvedValue({ ok: true, appliedUpdates: 0, appliedInserts: 1, appliedDeletes: 0 });
    expect(await applyChanges("tab1", EX, staging)).toBe(true);
    expect(rpc.mock.calls.filter((c) => c[0] === "data.applyPatch")).toHaveLength(2);
  });

  it("a cancelled Apply keeps the staged rows for a retry and says nothing was written", async () => {
    const { staging } = setup();
    staging.addRow(new Map([[1, { value: "n" }]]));
    rpc.mockRejectedValueOnce(new RpcError("The grid changes were cancelled and rolled back: nothing was written.", 1, "cancelled"));
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("warn", "The grid changes were cancelled and rolled back: nothing was written.");
    expect(staging.hasChanges).toBe(true);
    rpc.mockResolvedValue({ ok: true, appliedUpdates: 0, appliedInserts: 1, appliedDeletes: 0 });
    expect(await applyChanges("tab1", EX, staging)).toBe(true);
  });

  it("a commit cut short blocks every later Apply of the staging, so its inserts are never sent twice (H5)", async () => {
    const { staging } = setup();
    staging.addRow(new Map([[1, { value: "n" }]]));
    const unknown = "The connection failed while the grid changes were being committed, so whether they were written is unknown.";
    rpc.mockRejectedValueOnce(new RpcError(unknown, 1, "outcomeUnknown"));
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", unknown);
    rpc.mockReset();
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(rpc).not.toHaveBeenCalled();
    expect(toast).toHaveBeenLastCalledWith("error", unknown);
    // Discarding is still possible.
    staging.discardAll();
    expect(staging.hasChanges).toBe(false);
  });

  it("a row-level error is shown and nothing is written", async () => {
    const { staging } = setup();
    staging.addRow(new Map([[1, { value: "n" }]]));
    rpc.mockResolvedValue({ ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, conflicts: [{ rowIndex: 0, insert: true, reason: "error", message: "duplicate key" }] });
    expect(await applyChanges("tab1", EX, staging)).toBe(false);
    expect(toast).toHaveBeenLastCalledWith("error", "New row 1: duplicate key Nothing was written.");
    rpc.mockResolvedValue({ ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, error: "boom", conflicts: [{ rowIndex: 4, insert: false, reason: "error" }] });
    await applyChanges("tab1", EX, staging);
    expect(toast).toHaveBeenLastCalledWith("error", "Row 5: boom Nothing was written.");
  });

  describe("conflicts", () => {
    const conflict = (rows: { rowIndex: number; reason: string }[]) => ({ ok: false, appliedUpdates: 0, appliedInserts: 0, appliedDeletes: 0, conflicts: rows.map((r) => ({ insert: false, ...r })) });

    it("cancel keeps the edits", async () => {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : conflict([{ rowIndex: 0, reason: "changedOnServer" }])));
      answers("cancel");
      expect(await applyChanges("tab1", EX, staging)).toBe(false);
      expect(ask.mock.calls[0][0].kind).toBe("gridConflicts");
      expect(staging.hasChanges).toBe(true);
    });

    it("discard drops the conflicting rows, marks them and applies the rest", async () => {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      staging.setCell(ex(1), 1, { value: "dos" });
      let patches = 0;
      rpc.mockImplementation(async (m: string) => {
        if (m === "data.rowTokens") return { tokens: ["t", "t"] };
        return patches++ === 0 ? conflict([{ rowIndex: 0, reason: "changedOnServer" }]) : okResult;
      });
      answers("discard");
      expect(await applyChanges("tab1", EX, staging)).toBe(true);
      expect(staging.conflicted.has(0)).toBe(true);
      expect(patches).toBe(2);
    });

    it("discard of the only edited row ends without a second apply", async () => {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : conflict([{ rowIndex: 0, reason: "changedOnServer" }])));
      answers("discard");
      expect(await applyChanges("tab1", EX, staging)).toBe(false);
    });

    it("overwrite re-applies with force and skips rows deleted on the server", async () => {
      const { staging } = setup();
      staging.setCell(ex(0), 1, { value: "uno" });
      staging.setCell(ex(1), 1, { value: "dos" });
      const forced: boolean[] = [];
      rpc.mockImplementation(async (m: string, p: { forceOverwriteConflicts: boolean }) => {
        if (m === "data.rowTokens") return { tokens: ["t", "t"] };
        forced.push(p.forceOverwriteConflicts);
        return forced.length === 1 ? conflict([{ rowIndex: 0, reason: "changedOnServer" }, { rowIndex: 1, reason: "deletedOnServer" }]) : okResult;
      });
      answers("overwrite");
      expect(await applyChanges("tab1", EX, staging)).toBe(true);
      expect(forced).toEqual([false, true]);
      expect(staging.edits.has(1)).toBe(false);
    });

    it("overwrite with only deleted rows leaves nothing to apply", async () => {
      const { staging } = setup();
      staging.setCell(ex(1), 1, { value: "dos" });
      rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : conflict([{ rowIndex: 1, reason: "deletedOnServer" }])));
      answers("overwrite");
      expect(await applyChanges("tab1", EX, staging)).toBe(false);
    });
  });
});

describe("resolvePendingEdits", () => {
  it("is true for unknown tabs and tabs without staged changes", async () => {
    setup();
    expect(await resolvePendingEdits("missing")).toBe(true);
    expect(await resolvePendingEdits("tab1")).toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });

  it("cancel keeps, discard clears, apply writes", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    staging.addRow();
    answers("cancel");
    expect(await resolvePendingEdits("tab1")).toBe(false);
    expect(ask.mock.calls[0][0].summary).toBe("1 updated, 1 new and 0 deleted row(s) are staged in the grid and not written yet.");
    expect(staging.hasChanges).toBe(true);
    answers("discard");
    expect(await resolvePendingEdits("tab1")).toBe(true);
    expect(staging.hasChanges).toBe(false);

    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => (m === "data.rowTokens" ? { tokens: ["t"] } : okResult));
    answers("apply");
    expect(await resolvePendingEdits("tab1")).toBe(true);
    expect(rpc.mock.calls.some((c) => c[0] === "data.applyPatch")).toBe(true);
  });

  it("apply fails the close when a write fails", async () => {
    const { staging } = setup();
    staging.setCell(ex(0), 1, { value: "uno" });
    rpc.mockImplementation(async (m: string) => {
      if (m === "data.rowTokens") return { tokens: ["t"] };
      throw new Error("no");
    });
    answers("apply");
    expect(await resolvePendingEdits("tab1")).toBe(false);
    expect(stagings.has(stagingKey(EX, 0))).toBe(true);
  });
});

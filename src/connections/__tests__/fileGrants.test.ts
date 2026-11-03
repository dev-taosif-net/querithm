import { describe, expect, it, vi } from "vitest";

const m = vi.hoisted(() => ({ invoke: vi.fn(), isTauri: true }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.isTauri; }, transport: () => ({ invoke: m.invoke }) }));

import { makeReadOnly, needsRepick, pickDatabaseFile, requestWriteAccess } from "../fileGrants";

describe("database file grants in the webview (Phase D0c)", () => {
  it("asks the shell for the picker and the consent dialog; never sends a path", async () => {
    m.invoke.mockResolvedValueOnce({ grantId: "g", name: "sales.db", size: 3 });
    expect(await pickDatabaseFile()).toEqual({ grantId: "g", name: "sales.db", size: 3 });
    expect(m.invoke).toHaveBeenLastCalledWith("db_pick_file");
    m.invoke.mockResolvedValueOnce(false);
    expect(await requestWriteAccess("g")).toBe(false);
    expect(m.invoke).toHaveBeenLastCalledWith("db_grant_write", { grantId: "g" });
    m.invoke.mockResolvedValueOnce(undefined);
    await makeReadOnly("g");
    expect(m.invoke).toHaveBeenLastCalledWith("db_grant_read_only", { grantId: "g" });
    for (const call of m.invoke.mock.calls) expect(JSON.stringify(call)).not.toMatch(/path/i);
  });

  it("knows which refusals ask for a re-pick", () => {
    for (const c of ["fileGrant", "fileMissing", "fileMoved", "fileReplaced", "fileGrantBound", "fileGrantUnknown"]) expect(needsRepick(c)).toBe(true);
    expect(needsRepick("needsSecret")).toBe(false);
    expect(needsRepick(undefined)).toBe(false);
  });
});

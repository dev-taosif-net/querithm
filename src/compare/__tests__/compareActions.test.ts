// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useConnections } from "../../store/connections";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), invoke: vi.fn(), openTool: vi.fn(), activeTabState: vi.fn(), saveBytes: vi.fn(), toast: vi.fn(), tauri: { v: false },
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.tauri.v; }, transport: () => ({ invoke: m.invoke }) }));
vi.mock("../../app/actions", () => ({ activeTabState: m.activeTabState }));
vi.mock("../../app/toolActions", () => ({ openTool: m.openTool }));
vi.mock("../../app/saveBytes", () => ({ saveBytes: m.saveBytes }));
vi.mock("../../store/ui", () => ({ toast: m.toast }));

const A = await import("../compareActions");

beforeEach(() => {
  vi.clearAllMocks();
  m.tauri.v = false;
  useConnections.setState({ sessionFor: vi.fn().mockResolvedValue("sess") } as never);
});
afterEach(() => vi.restoreAllMocks());

describe("openSchemaCompare", () => {
  it("uses the given database as the source", () => {
    A.openSchemaCompare({ profileId: "p", database: "db", schema: "sales" });
    expect(m.openTool).toHaveBeenCalledWith("schemaCompare", "Schema compare", {
      profileId: "p", database: "db", params: { source: { kind: "live", profileId: "p", database: "db", schema: "sales" }, target: { kind: "live" } },
    });
  });
  it("falls back to the active tab's database, or to an empty source", () => {
    m.activeTabState.mockReturnValue({ profileId: "p2", database: "d2" });
    A.openSchemaCompare();
    expect(m.openTool.mock.calls[0][2].params.source).toEqual({ kind: "live", profileId: "p2", database: "d2", schema: null });
    m.activeTabState.mockReturnValue({ profileId: "p2" });
    A.openSchemaCompare();
    expect(m.openTool.mock.calls[1][2]).toMatchObject({ profileId: undefined, params: { source: { kind: "live" } } });
  });
});

describe("openDataCompare", () => {
  it("starts from the table given (explorer) with the same database as the target default", () => {
    A.openDataCompare({ profileId: "p", database: "db", table: "dbo.Orders" });
    expect(m.openTool).toHaveBeenCalledWith("dataCompare", "Data compare", {
      profileId: "p", database: "db",
      params: { source: { mode: "table", profileId: "p", database: "db", table: "dbo.Orders" }, target: { mode: "table", profileId: "p", database: "db" } },
    });
  });
  it("uses the active tab's database without a table, or nothing at all", () => {
    m.activeTabState.mockReturnValue({ profileId: "p", database: "db" });
    A.openDataCompare();
    expect(m.openTool.mock.calls[0][2].params.source).toEqual({ mode: "table", profileId: "p", database: "db", table: undefined });
    m.activeTabState.mockReturnValue(undefined);
    A.openDataCompare();
    expect(m.openTool.mock.calls[1][2].profileId).toBeUndefined();
  });
});

describe("pickSchemaFile", () => {
  it("asks the shell for a native dialog in the app", async () => {
    m.tauri.v = true;
    m.invoke.mockResolvedValue({ token: "t", name: "a.json", size: 3 });
    expect(await A.pickSchemaFile()).toEqual({ token: "t", name: "a.json", size: 3 });
    expect(m.invoke).toHaveBeenCalledWith("compare_pick_file");
  });

  function stubInput(file: File | null, event: "change" | "cancel") {
    const input: any = { type: "", accept: "", files: file ? [file] : [], click: vi.fn(() => queueMicrotask(() => (event === "change" ? input.onchange() : input.oncancel()))) };
    const orig = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string) => (tag === "input" ? input : orig(tag)));
    return input;
  }

  it("uploads the chosen file to the dev bridge in the browser (base64)", async () => {
    const file = new File([new Uint8Array([104, 105])], "model.json");
    (file as any).arrayBuffer = async () => new Uint8Array([104, 105]).buffer;
    const input = stubInput(file, "change");
    m.invoke.mockResolvedValue({ token: "tok", name: "model.json", size: 2 });
    const r = await A.pickSchemaFile();
    expect(input.accept).toBe(".json");
    expect(m.invoke).toHaveBeenCalledWith("import_stage_file", { name: "model.json", data: btoa("hi") });
    expect(r).toEqual({ token: "tok", name: "model.json", size: 2 });
  });

  it("returns null when the picker is cancelled", async () => {
    stubInput(null, "cancel");
    expect(await A.pickSchemaFile()).toBeNull();
    stubInput(null, "change");
    expect(await A.pickSchemaFile()).toBeNull();
    expect(m.invoke).not.toHaveBeenCalled();
  });
});

describe("sideParams", () => {
  it("passes a saved file by token", async () => {
    expect(await A.sideParams({ kind: "file", token: "tk", name: "x" })).toEqual({ fileToken: "tk" });
  });
  it("opens a session for a live side, with the schema or null", async () => {
    expect(await A.sideParams({ kind: "live", profileId: "p", database: "db", schema: "sales" })).toEqual({ sessionId: "sess", database: "db", schema: "sales" });
    expect(await A.sideParams({ kind: "live", profileId: "p", database: "db" })).toEqual({ sessionId: "sess", database: "db", schema: null });
  });
  it("refuses a live side with no connection or database", async () => {
    await expect(A.sideParams({ kind: "live" })).rejects.toThrow("Choose a connection and a database.");
    await expect(A.sideParams({ kind: "live", profileId: "p" })).rejects.toThrow("Choose a connection");
  });
});

describe("exportSchemaModel", () => {
  it("saves the model JSON as a file and confirms", async () => {
    m.rpc.mockResolvedValue({ fileName: "db.qschema.json", json: "{}" });
    m.saveBytes.mockResolvedValue("/tmp/db.qschema.json");
    await A.exportSchemaModel({ kind: "live", profileId: "p", database: "db", schema: "dbo" });
    expect(m.rpc).toHaveBeenCalledWith("schemaCompare.exportModel", { sessionId: "sess", database: "db", schema: "dbo" });
    const [name, bytes, filter] = m.saveBytes.mock.calls[0];
    expect(name).toBe("db.qschema.json");
    expect(new TextDecoder().decode(bytes)).toBe("{}");
    expect(filter.extension).toBe("json");
    expect(m.toast).toHaveBeenCalledWith("success", "Saved /tmp/db.qschema.json");
  });
  it("stays quiet when the save dialog is cancelled, and ignores sides that are not ready", async () => {
    m.rpc.mockResolvedValue({ fileName: "f", json: "{}" });
    m.saveBytes.mockResolvedValue(null);
    await A.exportSchemaModel({ kind: "live", profileId: "p", database: "db" });
    expect(m.toast).not.toHaveBeenCalled();
    await A.exportSchemaModel({ kind: "file", token: "t", name: "n" });
    await A.exportSchemaModel({ kind: "live" });
    expect(m.rpc).toHaveBeenCalledTimes(1);
  });
  it("reports failures", async () => {
    m.rpc.mockRejectedValue(new Error("no access"));
    await A.exportSchemaModel({ kind: "live", profileId: "p", database: "db" });
    expect(m.toast).toHaveBeenCalledWith("error", "no access");
  });
});

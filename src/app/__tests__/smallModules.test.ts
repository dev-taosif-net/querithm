// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  tauri: false,
  status: undefined as undefined | ((s: any) => void),
  notify: new Map<string, (p: any) => void>(),
  restart: vi.fn(async () => {}),
  save: vi.fn(),
  write: vi.fn(async () => {}),
  restarted: vi.fn(),
  failed: vi.fn((): string[] => []),
  rpc: vi.fn(async (..._a: any[]) => undefined as any),
  shellStatus: vi.fn(async (): Promise<any> => ({ state: "running" })),
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../ipc/client", () => ({
  onSidecarStatus: (cb: any) => { h.status = cb; return () => { h.status = undefined; }; },
  onNotify: (m: string, cb: any) => { h.notify.set(m, cb); return () => h.notify.delete(m); },
  shell: { restart: () => h.restart(), status: () => h.shellStatus() },
  rpc: (...a: any[]) => h.rpc(...a),
  toRpcError: (e: any) => (e && typeof e === "object" ? { message: e.message, appCode: e.data?.code } : { message: String(e) }),
}));
vi.mock("../actions", () => ({ onSidecarRestarted: () => h.restarted(), onSidecarFailed: () => h.failed(), workspaceHooks: { onToolAdded: undefined as any, onTabRemoved: undefined as any } }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ save: (...a: any[]) => h.save(...a) }));
vi.mock("@tauri-apps/plugin-fs", () => ({ writeFile: (...a: any[]) => (h.write as any)(...a) }));

import { DATA_PLANE_LOST, EngineDown, installEngineStatus, showStartupNotices, useEngineStatus, whyEngineDown } from "../engineStatus";
import { saveBytes } from "../saveBytes";
import { closeTool, openTool } from "../toolActions";
import { workspaceHooks } from "../actions";
import { useUi } from "../../store/ui";
import { useToolTabs } from "../../store/toolTabs";
import { useSettings, useSettingsStatus } from "../../store/settings";

const filter = { name: "PNG", extension: "png", mime: "image/png" };

describe("engine status", () => {
  beforeEach(() => {
    useUi.setState({ banner: null });
    useEngineStatus.setState({ state: "running" } as any);
    h.restarted.mockClear();
  });
  it("shows a warning banner when the engine dies, then restores sessions after a restart", () => {
    const off = installEngineStatus();
    h.status!({ state: "dead" });
    expect(useEngineStatus.getState().state).toBe("dead");
    expect(useUi.getState().banner).toMatchObject({ kind: "warn" });
    h.status!({ state: "restarted" });
    expect(h.restarted).toHaveBeenCalled();
    off();
    expect(h.status).toBeUndefined();
  });
  it("an engine that failed takes the tabs' sessions along: the banner gives the reason, what was lost and Restart (W1)", () => {
    const off = installEngineStatus();
    h.failed.mockReturnValueOnce(["Open transactions in a.sql were lost (rolled back by the server)."]);
    h.status!({ state: "failed", detail: "The engine stopped twice in a minute; restart it from the banner." });
    expect(h.failed).toHaveBeenCalledTimes(1);
    const b = useUi.getState().banner!;
    expect(b).toMatchObject({ kind: "error", text: "The engine stopped twice in a minute; restart it from the banner. Open transactions in a.sql were lost (rolled back by the server)." });
    b.action!.run();
    expect(h.restart).toHaveBeenCalled();
    off();
  });
  it("a settings save that failed while the engine was down is retried once it has restarted", async () => {
    vi.useFakeTimers();
    try {
      const off = installEngineStatus();
      h.rpc.mockRejectedValueOnce(new Error("engine down"));
      useSettings.getState().set({ wordWrap: true });
      await vi.advanceTimersByTimeAsync(400);
      expect(useSettingsStatus.getState().save).toBe("failed");
      h.rpc.mockClear();
      h.status!({ state: "restarted" });
      await vi.advanceTimersByTimeAsync(0);
      expect(h.rpc).toHaveBeenCalledWith("settings.set", expect.objectContaining({ key: "ui.settings" }));
      expect(useSettingsStatus.getState().save).toBe("saved");
      off();
    } finally {
      vi.useRealTimers();
    }
  });
  it("a lost results channel shows an error banner that restarts the engine and clears itself on reconnect", () => {
    const off = installEngineStatus();
    h.notify.get("dataPlane.state")!({ connected: false });
    const b = useUi.getState().banner!;
    expect(b).toMatchObject({ kind: "error", text: DATA_PLANE_LOST });
    b.action!.run();
    expect(h.restart).toHaveBeenCalled();
    h.notify.get("dataPlane.state")!({ connected: true });
    expect(useUi.getState().banner).toBeNull();
    // A reconnect never hides a different banner.
    useUi.getState().setBanner({ kind: "warn", text: "other" });
    h.notify.get("dataPlane.state")!({ connected: true });
    expect(useUi.getState().banner!.text).toBe("other");
    off();
    expect(h.notify.has("dataPlane.state")).toBe(false);
  });
  it("a failed engine shows an error banner whose action restarts it", () => {
    installEngineStatus();
    h.status!({ state: "failed", detail: "exit 3" });
    const b = useUi.getState().banner!;
    expect(b).toMatchObject({ kind: "error", text: "exit 3" });
    b.action!.run();
    expect(h.restart).toHaveBeenCalled();
    h.status!({ state: "failed" });
    expect(useUi.getState().banner!.text).toBe("The Querithm engine stopped.");
    h.status!({ state: "running" });
  });
});

describe("engine that did not start (M18) and startup notices (M22)", () => {
  beforeEach(() => {
    useUi.setState({ banner: null });
    h.shellStatus.mockReset();
  });
  const down = { code: -32003, message: "The Querithm engine is not running.", data: { code: "sidecarDown" } };

  it("a ping that finds no engine and a shell that failed to spawn it gives the shell's reason", async () => {
    h.shellStatus.mockResolvedValue({ state: "failed", detail: "The Querithm engine did not start: could not start /opt/q/querithm-sidecar: No such file or directory (os error 2). The engine file is missing; an antivirus program may have quarantined it. Restore it or reinstall Querithm." });
    const e = await whyEngineDown(down);
    expect(e).toBeInstanceOf(EngineDown);
    expect((e as Error).message).toMatch(/^The Querithm engine did not start: could not start \/opt\/q\/querithm-sidecar: .*antivirus/);
  });

  it("any other failure, or an engine the shell still runs, passes through as it was", async () => {
    h.shellStatus.mockResolvedValue({ state: "running" });
    expect(await whyEngineDown(down)).toBe(down);
    const mismatch = new Error("Engine contract 2 does not match");
    expect(await whyEngineDown(mismatch)).toBe(mismatch);
    expect(h.shellStatus).toHaveBeenCalledTimes(1);
    h.shellStatus.mockRejectedValue(new Error("no shell"));
    expect(await whyEngineDown(down)).toBe(down);
  });

  it("a failed engine's banner keeps the shell's reason and offers Restart engine", () => {
    installEngineStatus();
    h.status!({ state: "failed", detail: "The Querithm engine did not start: could not start x: denied" });
    expect(useUi.getState().banner).toMatchObject({ kind: "error", text: "The Querithm engine did not start: could not start x: denied", action: { label: "Restart engine" } });
    h.status!({ state: "running" });
  });

  it("a notice the engine met after start is taken when it says so, and never replaces a banner already showing", async () => {
    const pruned = { code: "historyPruned", message: "Querithm deleted 12 query history entries past the history retention setting, which keeps 90 days and at most 50,000 entries. Change it under Retention in the History dialog to keep more." };
    let pending = [pruned];
    h.rpc.mockImplementation(async (m: string) => {
      if (m !== "system.notices") return undefined;
      const all = pending;
      pending = [];
      return all;
    });
    const off = installEngineStatus();
    h.notify.get("system.noticeAdded")!(pruned);
    await vi.waitFor(() => expect(useUi.getState().banner).toEqual({ kind: "warn", text: pruned.message }));

    useUi.getState().setBanner({ kind: "error", text: "other" });
    pending = [pruned];
    h.notify.get("system.noticeAdded")!(pruned);
    await vi.waitFor(() => expect(useUi.getState().toasts.map((t) => t.text)).toContain(pruned.message));
    expect(useUi.getState().banner!.text).toBe("other");
    off();
    expect(h.notify.has("system.noticeAdded")).toBe(false);
    useUi.setState({ banner: null, toasts: [] });
    h.rpc.mockReset();
    h.rpc.mockImplementation(async () => undefined);
  });

  it("a damaged store the engine set aside is told once, with the kept file's name", async () => {
    h.rpc.mockImplementation(async (m: string) => m === "system.notices"
      ? [{ code: "storeReset", file: "store.db.corrupt-20261103T101500Z", message: "Querithm's local data file was damaged and was set aside; connection profiles and history were reset. Passwords in the system keyring are untouched." }]
      : undefined);
    await showStartupNotices();
    expect(useUi.getState().banner).toEqual({
      kind: "warn",
      text: "Querithm's local data file was damaged and was set aside; connection profiles and history were reset. Passwords in the system keyring are untouched. The damaged file was kept as store.db.corrupt-20261103T101500Z.",
    });
    useUi.setState({ banner: null });
    h.rpc.mockRejectedValue(new Error("engine down"));
    await showStartupNotices();
    expect(useUi.getState().banner).toBeNull();
    h.rpc.mockReset();
    h.rpc.mockImplementation(async () => undefined);
  });
});

describe("saveBytes", () => {
  beforeEach(() => { h.save.mockReset(); h.write.mockClear(); });
  it("browser: downloads through an anchor and revokes the URL later", async () => {
    vi.useFakeTimers();
    h.tauri = false;
    URL.createObjectURL = vi.fn(() => "blob:1");
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    expect(await saveBytes("d.png", new Uint8Array([1]), filter)).toBe("d.png");
    expect(click).toHaveBeenCalled();
    vi.advanceTimersByTime(5001);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:1");
    click.mockRestore();
    vi.useRealTimers();
  });
  it("Tauri: writes to the chosen path, or returns null when cancelled", async () => {
    h.tauri = true;
    h.save.mockResolvedValueOnce(null);
    expect(await saveBytes("d.png", new Uint8Array([1]), filter)).toBeNull();
    expect(h.write).not.toHaveBeenCalled();
    h.save.mockResolvedValueOnce("/tmp/d.png");
    expect(await saveBytes("d.png", new Uint8Array([1, 2]), filter)).toBe("/tmp/d.png");
    expect(h.write).toHaveBeenCalledWith("/tmp/d.png", new Uint8Array([1, 2]));
    expect(h.save).toHaveBeenLastCalledWith({ defaultPath: "d.png", filters: [{ name: "PNG", extensions: ["png"] }] });
  });
});

describe("tool tabs", () => {
  it("openTool registers the tool and notifies the workspace; closeTool removes it", () => {
    const added = vi.fn();
    const removed = vi.fn();
    workspaceHooks.onToolAdded = added;
    workspaceHooks.onTabRemoved = removed;
    const id = openTool("erd", "ERD", { profileId: "pg1", database: "d", params: { root: "t" } });
    expect(useToolTabs.getState().tools[id]).toMatchObject({ kind: "erd", profileId: "pg1", params: { root: "t" } });
    expect(added).toHaveBeenCalledWith(id, "ERD");
    const id2 = openTool("schemaCompare", "Cmp");
    expect(useToolTabs.getState().tools[id2].params).toEqual({});
    closeTool(id);
    expect(removed).toHaveBeenCalledWith(id);
    expect(useToolTabs.getState().tools[id]).toBeUndefined();
    workspaceHooks.onToolAdded = undefined;
    workspaceHooks.onTabRemoved = undefined;
    closeTool(id2);
    openTool("dataCompare", "x");
  });
});

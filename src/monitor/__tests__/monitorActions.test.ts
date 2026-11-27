// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const m = vi.hoisted(() => ({ openTool: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, onSidecarStatus: () => () => {} }));
vi.mock("../../app/actions", () => ({ newQueryTab: vi.fn(), txAction: vi.fn(), workspaceHooks: { activate: vi.fn() }, activeTabState: () => undefined }));
vi.mock("../../app/toolActions", () => ({ openTool: m.openTool }));

import { modelsFor, openActivityMonitor, registerModel } from "../monitorActions";
import { MonitorModel } from "../model";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useUi } from "../../store/ui";

beforeEach(() => {
  vi.clearAllMocks();
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, lastBinding: undefined } as never);
  useProfiles.setState({ profiles: [] } as never);
  useConnections.setState({ servers: {}, disconnected: {} } as never);
  useUi.setState({ toasts: [] } as never);
});

describe("openActivityMonitor", () => {
  it("with every tab closed, uses the last tab's server unless disconnected", () => {
    useProfiles.setState({ profiles: [{ id: "p1", name: "Prod", engine: "mssql" } as never] });
    useTabs.setState({ lastBinding: { profileId: "p1", database: "Sales" } } as never);
    openActivityMonitor();
    expect(m.openTool).toHaveBeenCalledWith("activity", "Activity — Prod", { profileId: "p1" });
    m.openTool.mockClear();
    useConnections.setState({ disconnected: { p1: true } } as never);
    openActivityMonitor();
    expect(m.openTool).not.toHaveBeenCalled();
    expect(useUi.getState().toasts.at(-1)?.text).toContain("Choose a server first");
  });
});

describe("registerModel", () => {
  it("a closing tab unregisters only its own model, not a newer one with the same monitor id", () => {
    const texts = async () => ({ texts: {}, missing: [] });
    const old = new MonitorModel("mon1", texts);
    const unregisterOld = registerModel(old, "p1");
    // The sidecar restarted and handed out the same id to a new monitor.
    const fresh = new MonitorModel("mon1", texts);
    const unregisterFresh = registerModel(fresh, "p1");
    unregisterOld();
    expect(modelsFor("p1")).toEqual([fresh]);
    unregisterFresh();
    expect(modelsFor("p1")).toEqual([]);
  });
});

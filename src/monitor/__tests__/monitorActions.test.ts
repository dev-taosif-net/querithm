// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, onSidecarStatus: () => () => {} }));
vi.mock("../../app/actions", () => ({ newQueryTab: vi.fn(), txAction: vi.fn(), workspaceHooks: { activate: vi.fn() }, activeTabState: () => undefined }));
vi.mock("../../app/toolActions", () => ({ openTool: vi.fn() }));

import { modelsFor, registerModel } from "../monitorActions";
import { MonitorModel } from "../model";

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

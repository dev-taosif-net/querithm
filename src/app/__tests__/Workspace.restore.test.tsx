// @vitest-environment jsdom
// Startup restore against the real Dockview grid: the fake in Workspace.test.tsx cannot show which group a panel
// lands in. Regression: restoring a non-active tab first split it into its own group, which showed the watermark.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";
import "./domPolyfills";

const h = vi.hoisted(() => ({ props: {} as any, hooks: {} as any }));

vi.mock("dockview", async (orig) => ({
  ...(await orig<typeof import("dockview")>()),
  DockviewReact: (p: any) => { h.props = p; return null; },
}));
vi.mock("dockview/dist/styles/dockview.css", () => ({}));
vi.mock("../QueryTab", () => ({ QueryTabHeader: () => null, QueryTabPanel: () => null }));
vi.mock("../ToolTab", () => ({ ToolTabHeader: () => null, ToolTabPanel: () => null }));
vi.mock("../actions", () => ({ workspaceHooks: h.hooks, newQueryTab: vi.fn() }));
vi.mock("../../editor/editors", () => ({ disposeGroupEditor: vi.fn() }));
vi.mock("../tabActions", () => ({ applyPinnedOrder: vi.fn() }));
vi.mock("../commands", () => ({ commands: [], keysFor: () => [], displayKeys: (k: string) => k }));

import { createDockview, type DockviewApi } from "dockview";
import { Workspace } from "../Workspace";
import { useTabs } from "../../store/tabs";

const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.3, actualPlan: false };
const addTab = (id: string) => useTabs.getState().add({ ...base, id, title: id } as any);

let api: DockviewApi;
beforeEach(() => {
  for (const k of Object.keys(h.hooks)) delete h.hooks[k];
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  const host = document.createElement("div");
  document.body.appendChild(host);
  api = createDockview(host, {
    createComponent: () => {
      const element = document.createElement("div");
      return { element, init: () => {} };
    },
  });
  render(<Workspace />);
  act(() => h.props.onReady({ api }));
});
afterEach(() => {
  api.dispose();
  cleanup();
});

const restore = (ids: string[], activeId: string) => {
  for (const id of ids) {
    addTab(id);
    h.hooks.onTabAdded(id, id === activeId, true);
  }
};

describe("Workspace: startup restore in the real Dockview grid", () => {
  it("restored tabs share one group in saved order, the active one shown, when the active tab is not first", () => {
    restore(["onboard", "spr", "q2", "q1"], "spr");
    expect(api.groups).toHaveLength(1);
    expect(api.groups[0].panels.map((p) => p.id)).toEqual(["onboard", "spr", "q2", "q1"]);
    expect(api.groups[0].activePanel?.id).toBe("spr");
    expect(api.activePanel?.id).toBe("spr");
  });

  it("the active tab first: still one group", () => {
    restore(["a", "b", "c"], "a");
    expect(api.groups).toHaveLength(1);
    expect(api.groups[0].activePanel?.id).toBe("a");
  });

  it("an inactive tab opened later joins the active group instead of splitting", () => {
    restore(["a", "b"], "b");
    addTab("bg");
    h.hooks.onTabAdded("bg", false, true);
    expect(api.groups).toHaveLength(1);
    expect(api.groups[0].activePanel?.id).toBe("b");
  });

  it("without an active group, an inactive tab still opens in the existing group", () => {
    restore(["a"], "a");
    // E.g. focus left the grid: Dockview has groups but none active.
    (api as any).component.doSetGroupActive(undefined);
    expect(api.activeGroup).toBeUndefined();
    addTab("b");
    h.hooks.onTabAdded("b", false, true);
    expect(api.groups).toHaveLength(1);
    expect(api.groups[0].panels.map((p) => p.id)).toEqual(["a", "b"]);
  });
});

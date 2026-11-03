// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import "./domPolyfills";

const h = vi.hoisted(() => ({ showPlan: vi.fn() }));
vi.mock("../actions", () => ({ showPlan: h.showPlan }));
vi.mock("../../trace/TracePanel", () => ({ TracePanel: ({ lead }: { lead: ReactNode }) => <div>{lead}trace-panel</div> }));
vi.mock("../../debug/DebugPanel", () => ({ DebugPanel: ({ lead }: { lead: ReactNode }) => <div>{lead}debug-panel</div> }));

import { RuntimePanel, runtimeToolOf } from "../RuntimePanel";
import { useTabs, type TabState } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { ENGINE_LIST, engineTraits } from "../../engines";

const base = { id: "t1", title: "q.sql", txState: "none", busy: false, dirty: false, executions: [], bottomTab: "runtime", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
function show(over: Partial<TabState> = {}) {
  const t = { ...base, ...over } as TabState;
  useTabs.setState({ tabs: { t1: t }, order: ["t1"], activeId: "t1" });
  return render(<RuntimePanel tab={t} />);
}
const seg = () => within(screen.getByRole("group", { name: "Runtime tool" }));

beforeEach(() => {
  vi.clearAllMocks();
  useProfiles.setState({
    profiles: ENGINE_LIST.map((e) => ({ id: e, engine: e, environment: "dev" })) as never,
  });
});
afterEach(cleanup);

describe("RuntimePanel", () => {
  it("SQL Server opens on Trace, with the switch at the start of Trace's own row; Debug stays in its place, unavailable", () => {
    show({ profileId: "mssql" });
    expect(screen.getByText("trace-panel")).toBeInTheDocument();
    expect(seg().getByRole("button", { name: /Trace/ })).toHaveAttribute("aria-pressed", "true");
    expect(seg().getByRole("button", { name: /Debug/ })).toHaveAttribute("title", "Not available on SQL Server");
  });

  it("SQL Server, Debug chosen: says why and offers Trace instead", async () => {
    show({ profileId: "mssql", runtimeTool: "debug" });
    expect(screen.getByRole("group", { name: "Step debugging isn't available on SQL Server" })).toBeInTheDocument();
    expect(screen.getByText(/Trace shows each statement's line, duration and rows/)).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "Switch to Trace" }));
    expect(useTabs.getState().tabs.t1.runtimeTool).toBe("trace");
  });

  it("PostgreSQL opens on Debug: without a session it says how to start one; with one it shows the debugger", () => {
    show({ profileId: "pg" });
    expect(screen.getByRole("group", { name: "No debug session" })).toBeInTheDocument();
    expect(screen.getByText(/pldebugger extension/)).toBeInTheDocument();
    cleanup();
    show({ profileId: "pg", debugId: "d1" });
    expect(screen.getByText("debug-panel")).toBeInTheDocument();
  });

  it("PostgreSQL, Trace chosen: says why and offers Debug", async () => {
    show({ profileId: "pg", runtimeTool: "trace" });
    expect(screen.getByRole("group", { name: "Trace isn't available on PostgreSQL" })).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "Switch to Debug" }));
    expect(useTabs.getState().tabs.t1.runtimeTool).toBe("debug");
  });

  // Every engine with neither tool (SQLite, DuckDB, MySQL today; a new engine joins automatically).
  it.each(ENGINE_LIST.filter((e) => !engineTraits(e).trace && !engineTraits(e).debugger))("%s has neither tool: both sides say why, and Trace points to the actual plan when the engine has one", async (engine) => {
    const traits = engineTraits(engine as never);
    show({ profileId: engine });
    expect(seg().getByRole("button", { name: /Trace/ })).toHaveAttribute("title", `Not available on ${traits.shortLabel}`);
    expect(seg().getByRole("button", { name: /Debug/ })).toHaveAttribute("title", `Not available on ${traits.shortLabel}`);
    expect(screen.getByRole("group", { name: `Trace isn't available on ${traits.shortLabel}` })).toBeInTheDocument();
    const plan = screen.queryByRole("button", { name: "Show the actual plan" });
    expect(!!plan).toBe(traits.actualPlan !== "none");
    if (plan) {
      await userEvent.setup().click(plan);
      expect(h.showPlan).toHaveBeenCalledWith("t1", "actual");
    }
  });

  it("without a connection, says to connect", () => {
    show();
    expect(screen.getByRole("group", { name: "Connect the tab to use Trace or Debug" })).toBeInTheDocument();
    expect(seg().getByRole("button", { name: /Trace/ })).toHaveAttribute("title", "Connect the tab first");
  });

  it("runtimeToolOf: the tab's choice, else a running debug session, else the engine's own tool", () => {
    expect(runtimeToolOf({ runtimeTool: "debug" }, engineTraits("mssql"))).toBe("debug");
    expect(runtimeToolOf({ debugId: "d" }, engineTraits("mssql"))).toBe("debug");
    expect(runtimeToolOf({}, engineTraits("mssql"))).toBe("trace");
    expect(runtimeToolOf({}, engineTraits("pg"))).toBe("debug");
    expect(runtimeToolOf({}, engineTraits("sqlite"))).toBe("trace");
  });
});

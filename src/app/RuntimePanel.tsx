// The Runtime view of the bottom panel (§12 bottom panel): Trace (SQL Server Extended Events, not a debugger) and Debug
// (PostgreSQL pldebugger) share one fixed slot behind a Trace | Debug switch, so the tab row is the same on every
// engine. The side an engine cannot use keeps its place and says why, with what this engine offers instead.
import type { ReactNode } from "react";
import { useTabs, type RuntimeTool, type TabState } from "../store/tabs";
import { useProfiles } from "../store/profiles";
import { engineTraits, type EngineTraits } from "../engines";
import { TracePanel } from "../trace/TracePanel";
import { DebugPanel } from "../debug/DebugPanel";
import { Seg } from "./panelKit";
import { Icon } from "./icons";
import { showPlan } from "./actions";

/** The tool a tab's Runtime view shows: its choice, else the one its engine has (Trace first). */
export function runtimeToolOf(tab: Pick<TabState, "runtimeTool" | "debugId">, traits: EngineTraits): RuntimeTool {
  if (tab.runtimeTool) return tab.runtimeTool;
  if (tab.debugId) return "debug";
  return !traits.trace && traits.debugger ? "debug" : "trace";
}

export function RuntimePanel({ tab }: { tab: TabState }) {
  const engine = useProfiles((s) => s.profiles.find((p) => p.id === tab.profileId)?.engine);
  const traits = engineTraits(engine);
  const tool = runtimeToolOf(tab, traits);
  const connected = !!tab.profileId;
  const traceOk = connected && traits.trace;
  const debugOk = connected && (traits.debugger || !!tab.debugId);
  const why = {
    trace: !connected ? "Connect the tab first" : traits.trace ? undefined : `Not available on ${traits.shortLabel}`,
    debug: !connected ? "Connect the tab first" : traits.debugger || tab.debugId ? undefined : `Not available on ${traits.shortLabel}`,
  };
  const lead = (
    <Seg<RuntimeTool>
      label="Runtime tool"
      value={tool}
      options={[
        { value: "trace", label: "Trace", icon: "clock", unavailable: why.trace },
        { value: "debug", label: "Debug", icon: "step", unavailable: why.debug },
      ]}
      onChange={(v) => useTabs.getState().update(tab.id, { runtimeTool: v })}
    />
  );
  const switchTo = (v: RuntimeTool) => useTabs.getState().update(tab.id, { runtimeTool: v });

  if (tool === "trace" && traceOk) return <div className="flex-1 flex flex-col min-h-0" data-q-runtime="trace"><TracePanel tab={tab} lead={lead} /></div>;
  if (tool === "debug" && debugOk && tab.debugId) return <div className="flex-1 flex flex-col min-h-0" data-q-runtime="debug"><DebugPanel tab={tab} lead={lead} /></div>;

  let card: ReactNode;
  if (!connected) {
    card = <Card icon="plug" title="Connect the tab to use Trace or Debug" text="Trace records statement timing on SQL Server; Debug steps through PostgreSQL functions." />;
  } else if (tool === "debug" && debugOk) {
    card = (
      <Card
        icon="step"
        title="No debug session"
        text="Right-click a function or procedure in the Object Explorer and choose Debug. Needs the pldebugger extension (pldbgapi) on the server; click the editor gutter to set breakpoints."
      />
    );
  } else if (tool === "trace") {
    card = (
      <Card
        icon="unavailable"
        title={`Trace isn't available on ${traits.shortLabel}`}
        text={`Trace uses SQL Server Extended Events, which ${traits.shortLabel} does not have.${traits.debugger ? " Debug steps through PL/pgSQL functions here." : traits.actualPlan !== "none" ? " Explain › Actual shows the time each operator took." : ""}`}
        action={traits.debugger ? { label: "Switch to Debug", run: () => switchTo("debug") } : traits.actualPlan !== "none" ? { label: "Show the actual plan", run: () => void showPlan(tab.id, "actual") } : undefined}
      />
    );
  } else {
    card = (
      <Card
        icon="unavailable"
        title={`Step debugging isn't available on ${traits.shortLabel}`}
        text={`Querithm's step debugger uses PostgreSQL's pldebugger extension; ${traits.shortLabel} has no equivalent here.${traits.trace ? " Trace shows each statement's line, duration and rows for this session instead." : ""}`}
        action={traits.trace ? { label: "Switch to Trace", run: () => switchTo("trace") } : undefined}
      />
    );
  }
  return (
    <div className="flex-1 flex flex-col min-h-0" data-q-runtime={tool}>
      <div className="h-[28px] px-2 border-b border-line flex items-center gap-2 text-[11px] shrink-0">{lead}</div>
      <div className="flex-1 overflow-auto bg-bg">{card}</div>
    </div>
  );
}

function Card({ icon, title, text, action }: { icon: "plug" | "step" | "unavailable"; title: string; text: string; action?: { label: string; run: () => void } }) {
  return (
    <div className="flex gap-3.5 p-5 max-w-3xl" role="group" aria-label={title} data-q-capability-card>
      <span className="text-muted pt-0.5"><Icon name={icon} size={26} strokeWidth={1.75} /></span>
      <div className="space-y-2">
        <div className="text-[13px] font-semibold text-fg">{title}</div>
        <p className="text-[12.5px] text-muted leading-relaxed m-0">{text}</p>
        {action && (
          <button type="button" className="q-btn q-btn-primary h-[24px]" onClick={action.run}>
            {action.label}
          </button>
        )}
      </div>
    </div>
  );
}

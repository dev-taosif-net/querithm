// Dockview panels for tool tabs (ER diagram, schema compare, data compare). Each kind loads on first use.
import { lazy, Suspense, useRef, useState } from "react";
import type { IDockviewPanelHeaderProps, IDockviewPanelProps } from "dockview";
import { useToolTabs } from "../store/toolTabs";
import { envColor, useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { bandColor, useThemeKey } from "./tabStyle";
import { Icon } from "./icons";
import { TabLine1, TabLine2, tooltipText, useTabSemantics, type TabFacts } from "./TabIdentity";
import { toolFacts } from "./tabFacts";
import { TabContextMenu } from "./TabMenu";
import { closeTool } from "./toolActions";

const ErdTab = lazy(() => import("../erd/ErdTab").then((m) => ({ default: m.ErdTab })));
const SchemaCompareTab = lazy(() => import("../compare/SchemaCompareTab").then((m) => ({ default: m.SchemaCompareTab })));
const DataCompareTab = lazy(() => import("../compare/DataCompareTab").then((m) => ({ default: m.DataCompareTab })));
const ActivityTab = lazy(() => import("../monitor/ActivityTab").then((m) => ({ default: m.ActivityTab })));

type Params = { toolId: string };

export function ToolTabPanel(props: IDockviewPanelProps<Params>) {
  const tool = useToolTabs((s) => s.tools[props.params.toolId]);
  if (!tool) return null;
  return (
    <div className="q-query-page h-full bg-bg overflow-hidden flex flex-col">
      <Suspense fallback={<div className="flex-1 flex items-center justify-center text-muted text-xs">Loading…</div>}>
        {tool.kind === "erd" && <ErdTab tool={tool} />}
        {tool.kind === "schemaCompare" && <SchemaCompareTab tool={tool} />}
        {tool.kind === "dataCompare" && <DataCompareTab tool={tool} />}
        {tool.kind === "activity" && <ActivityTab tool={tool} />}
      </Suspense>
    </div>
  );
}

/** Tool tab header: the same anatomy as a query tab (D17), with the tool's kind icon in place of a verb. */
export function ToolTabHeader(props: IDockviewPanelHeaderProps<Params>) {
  const tool = useToolTabs((s) => s.tools[props.params.toolId]);
  const profiles = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const disconnected = useConnections((s) => s.disconnected);
  useThemeKey();
  const root = useRef<HTMLDivElement>(null);
  const tabEl = useRef<HTMLElement | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const byId = (id: string | undefined) => (id ? profiles.find((p) => p.id === id) : undefined);
  const facts: TabFacts | undefined = tool ? toolFacts(tool, byId, (id) => !!disconnected[id]) : undefined;
  useTabSemantics(root, tabEl, facts, setMenu);
  if (!tool || !facts) return null;
  // The band follows where the tool writes or watches: a compare into Prod is a Prod tab.
  const bandProfile = facts.target?.profile ?? facts.profile;
  const band = bandProfile?.environment === "prod" ? bandColor(envColor(bandProfile, groups)) : undefined;
  return (
    <div
      ref={root}
      className="q-tab"
      data-disc={facts.disconnected || undefined}
      title={tooltipText(facts)}
      onContextMenu={(e) => {
        e.preventDefault();
        e.stopPropagation();
        setMenu({ x: e.clientX, y: e.clientY });
      }}
      onMouseDown={(e) => {
        if (e.button === 1) {
          e.preventDefault();
          closeTool(tool.id);
        }
      }}
    >
      {band && <span className="q-tab-band" style={{ background: band }} aria-hidden />}
      <span className="q-tab-sep" aria-hidden />
      <span className="q-tab-main">
        <span className="q-tab-top">
          <TabLine1 f={facts} />
          <span className="q-tab-slot">
            <button
              type="button"
              tabIndex={-1}
              className="q-tab-x"
              onClick={(e) => {
                e.stopPropagation();
                closeTool(tool.id);
              }}
              title="Close (Ctrl+W)"
              aria-label={`Close ${tool.title}`}
            >
              <Icon name="close" size={12} strokeWidth={2.2} />
            </button>
          </span>
        </span>
        <TabLine2 f={facts} />
      </span>
      {menu && <TabContextMenu tabId={tool.id} at={menu} anchor={tabEl} onClose={() => setMenu(null)} />}
    </div>
  );
}

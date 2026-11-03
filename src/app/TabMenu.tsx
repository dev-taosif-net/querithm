// The tab's context menu (D19), on the app's keyboard menu: right-click, Shift+F10 or the Menu key on a focused tab.
// A header line names what the menu acts on (server and database); Close comes first; bulk closes show how many tabs
// they close and are unavailable when that is none; the connection is named; Move and Split right are here too.
import type { RefObject } from "react";
import { useTabs } from "../store/tabs";
import { useToolTabs } from "../store/toolTabs";
import { useClosedTabs } from "../store/closedTabs";
import { envLabel, useProfiles } from "../store/profiles";
import { DropMenu, type MenuEntry } from "./ToolbarMenu";
import { ServerChip } from "./EnvTag";
import { closeTab, duplicateTab, workspaceHooks } from "./actions";
import { closeTool } from "./toolActions";
import { closeTabs, moveTabBy, planClose, reopenClosed, stripOrder, togglePin } from "./tabActions";
import { closeReport, type ClosePlan } from "./tabNav";

/**
 * "Close others (28)": the count of a bulk close, or why it is unavailable: none in its scope, or all of them kept
 * because they are running or hold a transaction (D20).
 */
function bulk(label: string, plan: ClosePlan, run: () => void, none: string): MenuEntry {
  const n = plan.close.length;
  if (n) return { label: `${label} (${n})`, run };
  const kept = plan.running.length + plan.inTx.length;
  return { label, run, unavailable: kept ? `Kept: ${closeReport(0, plan).split(": ")[1]}` : none };
}

export function tabMenuItems(tabId: string, onRename?: () => void): MenuEntry[] {
  const t = useTabs.getState().tabs[tabId];
  const tool = useToolTabs.getState().tools[tabId];
  const profileId = t?.profileId ?? tool?.profileId;
  const profile = useProfiles.getState().byId(profileId);
  const order = stripOrder(tabId);
  const i = order.indexOf(tabId);
  const plan = (scope: Parameters<typeof planClose>[0]) => planClose(scope, tabId);
  const pinned = !!t?.pinned;
  const run = (scope: Parameters<typeof closeTabs>[0]) => () => void closeTabs(scope, tabId);
  const onConn = plan("connection").close.length;
  const items: MenuEntry[] = [
    { label: "Close", hint: "Ctrl+W", run: () => (tool ? closeTool(tabId) : void closeTab(tabId)) },
    bulk("Close others", plan("others"), run("others"), "No other tabs"),
    bulk("Close to the left", plan("left"), run("left"), "None to the left"),
    bulk("Close to the right", plan("right"), run("right"), "None to the right"),
    bulk("Close saved and empty", plan("saved"), run("saved"), "None saved or empty"),
    profile
      ? onConn
        ? { label: ["Close", onConn, "on", envLabel[profile.environment]?.toUpperCase(), profile.name].filter(Boolean).join(" "), run: run("connection") }
        : { label: `Close tabs on ${profile.name}`, run: run("connection"), unavailable: "None to close" }
      : { label: "Close tabs on this connection", run: run("connection"), unavailable: "No connection" },
    { label: "Close all", detail: "keeps pinned", run: run("all") },
    "sep",
  ];
  if (t) {
    if (onRename) items.push({ label: "Rename", hint: "F2", run: onRename });
    items.push({ label: "Duplicate", run: () => duplicateTab(tabId) }, { label: pinned ? "Unpin" : "Pin", run: () => togglePin(tabId) });
  }
  const neighbour = (j: number) => order[j] !== undefined && !!useTabs.getState().tabs[order[j]]?.pinned === pinned;
  items.push(
    {
      label: "Move",
      run: () => {},
      sub: [
        { label: "Move left", hint: "Ctrl+Shift+PgUp", run: () => moveTabBy(tabId, -1), unavailable: neighbour(i - 1) ? undefined : "First already" },
        { label: "Move right", hint: "Ctrl+Shift+PgDn", run: () => moveTabBy(tabId, 1), unavailable: neighbour(i + 1) ? undefined : "Last already" },
        {
          label: "Move to start",
          run: () => {
            const first = order.findIndex((id) => !!useTabs.getState().tabs[id]?.pinned === pinned);
            if (first >= 0 && first !== i) workspaceHooks.moveTab?.(tabId, first);
          },
          unavailable: neighbour(i - 1) ? undefined : "First already",
        },
      ],
    },
    { label: "Split right", run: () => workspaceHooks.splitRight?.(tabId) },
    "sep",
    { label: "Reopen closed tab", hint: "Ctrl+Shift+T", run: () => void reopenClosed(), unavailable: useClosedTabs.getState().list.length ? undefined : "None closed yet" },
  );
  return items;
}

export function TabContextMenu({ tabId, at, anchor, onClose, onRename }: {
  tabId: string;
  at: { x: number; y: number };
  /** The tab: focus returns to it when the menu is dismissed from the keyboard. */
  anchor: RefObject<HTMLElement | null>;
  onClose: () => void;
  onRename?: () => void;
}) {
  const t = useTabs.getState().tabs[tabId];
  const tool = useToolTabs.getState().tools[tabId];
  return (
    <DropMenu
      anchor={anchor}
      at={at}
      label="Tab"
      header={t?.profileId || tool?.profileId ? <ServerChip profileId={t?.profileId ?? tool?.profileId} database={t?.database ?? tool?.database} surface="var(--q-raised)" /> : undefined}
      items={tabMenuItems(tabId, onRename)}
      onClose={onClose}
    />
  );
}

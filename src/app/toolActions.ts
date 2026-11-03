// Opening and closing tool tabs (ER diagram, compare). Query tabs live in actions.ts.
import { newToolId, useToolTabs, type ToolKind, type ToolTab } from "../store/toolTabs";
import { workspaceHooks } from "./actions";
import { useTabs } from "../store/tabs";

export function openTool(kind: ToolKind, title: string, o: { profileId?: string; database?: string; sessionId?: string; params?: Record<string, unknown> } = {}): string {
  const tool: ToolTab = { id: newToolId(), kind, title, profileId: o.profileId, database: o.database, ...(o.sessionId ? { sessionId: o.sessionId } : {}), params: o.params ?? {} };
  useToolTabs.getState().add(tool);
  workspaceHooks.onToolAdded?.(tool.id, title);
  return tool.id;
}

export function closeTool(id: string) {
  workspaceHooks.onTabRemoved?.(id);
  useToolTabs.getState().remove(id);
  useTabs.getState().forget(id);
}

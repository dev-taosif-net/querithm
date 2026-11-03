// Activity monitor wiring: one notification router for every monitor, opening the tool tab, Go to tab, and stopping
// monitors when their server is disconnected or the sidecar restarts.
import { create } from "zustand";
import type { SessionBlockingEvent } from "../ipc/contract";
import { onNotify, onSidecarStatus, rpc } from "../ipc/client";
import { openTool } from "../app/toolActions";
import { workspaceHooks, activeTabState } from "../app/actions";
import { useTabs } from "../store/tabs";
import { useToolTabs } from "../store/toolTabs";
import { useProfiles } from "../store/profiles";
import { toast } from "../store/ui";
import { MonitorModel } from "./model";

const models = new Map<string, { model: MonitorModel; profileId: string }>();
let routed = false;

function route() {
  if (routed) return;
  routed = true;
  onNotify("monitor.snapshot", (s) => models.get(s.monitorId)?.model.apply(s));
  onNotify("monitor.stopped", (e) => models.get(e.monitorId)?.model.markStopped(e.reason));
  onNotify("session.blocking", (e) => useBlocking.getState().set(e));
  onSidecarStatus((s) => {
    if (s.state === "restarted" || s.state === "dead")
      for (const { model } of models.values()) model.markStopped("The sidecar restarted: the monitor stopped and its history is lost.");
  });
}

export function registerModel(model: MonitorModel, profileId: string) {
  route();
  models.set(model.monitorId, { model, profileId });
  return () => models.delete(model.monitorId);
}

export function modelsFor(profileId: string) {
  return [...models.values()].filter((m) => m.profileId === profileId).map((m) => m.model);
}

/** Explorer server node → "Activity monitor", palette command. One tab per call (two monitors on a server are allowed). */
export function openActivityMonitor(profileId?: string) {
  const id = profileId ?? activeTabState()?.profileId;
  if (!id) {
    toast("warn", "Choose a server first (open a query tab on it, or use the explorer).");
    return;
  }
  const name = useProfiles.getState().byId(id)?.name ?? "server";
  openTool("activity", `Activity — ${name}`, { profileId: id });
}

/** Profile disconnected: its monitors stop now (the sidecar may still remember the password) — L3. */
export async function stopMonitorsFor(profileId: string) {
  for (const m of modelsFor(profileId)) {
    m.markStopped("Disconnected from the server.");
    await rpc("monitor.stop", { monitorId: m.monitorId }).catch(() => {});
  }
}

/** The query tab whose session leases a Querithm connection (OwnSessionId → tab). */
export function tabForSession(sessionId: string) {
  return Object.values(useTabs.getState().tabs).find((t) => t.sessionId === sessionId);
}

export function goToSession(sessionId: string): boolean {
  const t = tabForSession(sessionId);
  if (!t) {
    toast("warn", "That tab is no longer open.");
    return false;
  }
  useToolTabs.getState().setActive(undefined);
  workspaceHooks.activate?.(t.id);
  return true;
}

export function tabTitleForSession(sessionId: string): string | undefined {
  return tabForSession(sessionId)?.title;
}

// ---- own-tab blocking banner state (B1b) ----

export interface BlockingState {
  blockedCount: number;
  maxWaitMs: number;
  source: string;
  unavailable?: string | null;
  at: number;
}

interface BlockingStore {
  bySession: Record<string, BlockingState>;
  /** Dismissed until (epoch ms), per session. */
  muted: Record<string, number>;
  set(e: SessionBlockingEvent): void;
  mute(sessionId: string, ms?: number): void;
}

export const MUTE_MS = 5 * 60_000;

export const useBlocking = create<BlockingStore>((set) => ({
  bySession: {},
  muted: {},
  set(e) {
    set((s) => ({
      bySession: { ...s.bySession, [e.sessionId]: { blockedCount: e.blockedCount, maxWaitMs: e.maxWaitMs, source: e.source, unavailable: e.unavailable, at: Date.now() } },
    }));
  },
  mute(sessionId, ms = MUTE_MS) {
    set((s) => ({ muted: { ...s.muted, [sessionId]: Date.now() + ms } }));
  },
}));

export function ensureBlockingRoute() {
  route();
}

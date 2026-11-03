import { create } from "zustand";
import type { DebugEvent, DebugFrame, DebugVariable, TraceEvent } from "../ipc/contract";
import { onNotify } from "../ipc/client";

export interface TraceState {
  traceId: string;
  sessionId: string;
  events: TraceEvent[];
  error?: string | null;
  stopped: boolean;
}

export interface DebugState {
  debugId: string;
  tabId: string;
  functionOid: number;
  state: "attaching" | "paused" | "running" | "finished" | "error";
  line?: number | null;
  currentOid?: number | null;
  stack: DebugFrame[];
  variables: DebugVariable[];
  log: string[];
  breakpoints: number[];
}

interface DiagStore {
  traces: Record<string, TraceState>;
  debugs: Record<string, DebugState>;
  clearTrace(traceId: string): void;
  putDebug(d: DebugState): void;
  patchDebug(id: string, p: Partial<DebugState>): void;
}

export const useDiag = create<DiagStore>((set) => ({
  traces: {},
  debugs: {},
  clearTrace(traceId) {
    set((s) => (s.traces[traceId] ? { traces: { ...s.traces, [traceId]: { ...s.traces[traceId], events: [] } } } : s));
  },
  putDebug(d) {
    set((s) => ({ debugs: { ...s.debugs, [d.debugId]: d } }));
    // Events sent while debug.attach was still running (e.g. the first pause) are replayed now.
    const stashed = orphans.get(d.debugId);
    orphans.delete(d.debugId);
    stashed?.forEach(onDebug);
  },
  patchDebug(id, p) {
    set((s) => (s.debugs[id] ? { debugs: { ...s.debugs, [id]: { ...s.debugs[id], ...p } } } : s));
  },
}));

const orphans = new Map<string, DebugEvent[]>();

function onDebug(e: DebugEvent) {
  const d = useDiag.getState().debugs[e.debugId];
  if (!d) {
    const list = orphans.get(e.debugId) ?? [];
    list.push(e);
    orphans.set(e.debugId, list.slice(-50));
    return;
  }
  const p: Partial<DebugState> = {};
  if (e.kind === "paused") Object.assign(p, { state: "paused", line: e.line, currentOid: e.functionOid, stack: e.stack ?? [], variables: e.variables ?? [] });
  else if (e.kind === "running") p.state = "running";
  else if (e.kind === "finished") Object.assign(p, { state: "finished", line: null, log: e.message ? [...d.log, e.message] : d.log });
  else if (e.kind === "error") Object.assign(p, { state: "error", line: null, log: [...d.log, `Error: ${e.message}`] });
  else if (e.kind === "attached") p.state = "paused";
  useDiag.getState().patchDebug(e.debugId, p);
}

export function installDiagListeners() {
  const a = onNotify("trace.event", (e) =>
    useDiag.setState((s) => {
      const prev = s.traces[e.traceId] ?? { traceId: e.traceId, sessionId: e.sessionId, events: [], stopped: false };
      return {
        traces: {
          ...s.traces,
          [e.traceId]: { ...prev, events: e.events.length ? [...prev.events, ...e.events].slice(-5000) : prev.events, error: e.error, stopped: e.stopped || prev.stopped },
        },
      };
    }),
  );
  const b = onNotify("debug.event", onDebug);
  return () => {
    a();
    b();
  };
}

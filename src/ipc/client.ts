// Typed control-plane client (§5, §12). Every IPC call goes through here; calls over 100 ms are logged in dev.
import type { NotificationMap, NotificationMethod, RpcMap, RpcMethod } from "./contract";
import { transport } from "./transport";

export class RpcError extends Error {
  constructor(
    message: string,
    readonly code: number,
    /** Stable application code from the sidecar (notFound, txOpen, busy, needsSecret, sidecarDown, ...). */
    readonly appCode?: string,
    readonly data?: unknown,
  ) {
    super(message);
  }
}

type ParamsArg<M extends RpcMethod> = RpcMap[M]["params"] extends void ? [] : [RpcMap[M]["params"]];

const SLOW_MS = 100;

export async function rpc<M extends RpcMethod>(method: M, ...params: ParamsArg<M>): Promise<RpcMap[M]["result"]> {
  const t0 = performance.now();
  try {
    return await transport().invoke<RpcMap[M]["result"]>("rpc", { method, params: params[0] ?? null });
  } catch (e) {
    throw toRpcError(e);
  } finally {
    const ms = performance.now() - t0;
    if (import.meta.env.DEV && ms > SLOW_MS && !method.startsWith("lang.")) console.debug(`[ipc] ${method} took ${ms.toFixed(0)} ms`);
    recordTiming(method, ms);
  }
}

export function toRpcError(e: unknown): RpcError {
  if (e instanceof RpcError) return e;
  if (e && typeof e === "object" && "message" in e) {
    const o = e as { message: string; code?: number; data?: { code?: string; data?: unknown } };
    return new RpcError(o.message, o.code ?? -32000, o.data?.code, o.data?.data);
  }
  return new RpcError(String(e), -32000);
}

const handlers = new Map<string, Set<(p: unknown) => void>>();
let subscribed = false;

function ensureSubscribed() {
  if (subscribed) return;
  subscribed = true;
  void transport().listen("sidecar://notify", (payload) => {
    const { method, params } = payload as { method: string; params: unknown };
    handlers.get(method)?.forEach((h) => {
      try {
        h(params);
      } catch (err) {
        console.error(`[ipc] handler for ${method} failed`, err);
      }
    });
  });
}

export function onNotify<M extends NotificationMethod>(method: M, handler: (p: NotificationMap[M]) => void): () => void {
  ensureSubscribed();
  let set = handlers.get(method);
  if (!set) handlers.set(method, (set = new Set()));
  set.add(handler as (p: unknown) => void);
  return () => set!.delete(handler as (p: unknown) => void);
}

export interface SidecarStatus {
  state: "running" | "dead" | "restarted" | "failed";
  detail?: string | null;
  restarts?: number;
}

export function onSidecarStatus(handler: (s: SidecarStatus) => void): () => void {
  let un: (() => void) | null = null;
  let cancelled = false;
  void transport()
    .listen("sidecar://status", (p) => handler(p as SidecarStatus))
    .then((u) => (cancelled ? u() : (un = u)));
  return () => {
    cancelled = true;
    un?.();
  };
}

/** Shell commands that are not JSON-RPC (keyring, status). */
export const shell = {
  secretSet: (profileId: string, secret: string) => transport().invoke<void>("secret_set", { profileId, secret }),
  secretDelete: (profileId: string) => transport().invoke<void>("secret_delete", { profileId }),
  secretEndConnection: (profileId: string) => transport().invoke<void>("secret_end_connection", { profileId }),
  secretExists: (profileId: string) => transport().invoke<boolean>("secret_exists", { profileId }),
  status: () => transport().invoke<SidecarStatus & { pid?: number }>("sidecar_status"),
  restart: () => transport().invoke<void>("sidecar_restart"),
  /** `logsDir`: the sidecar's and the shell's logs (M21). `workspaceOwner`: false in a second app window, which neither restores nor writes the saved tabs (M23). */
  appInfo: () => transport().invoke<{ version: string; platform: string; bench: boolean; dataDir?: string; logsDir?: string | null; workspaceOwner?: boolean }>("app_info"),
  resultsDiscard: (executionId: string) => transport().invoke<void>("results_discard", { executionId }),
  updateCheck: () => transport().invoke<UpdateInfo | null>("update_check"),
  updateDownload: () => transport().invoke<void>("update_download"),
  updateInstall: () => transport().invoke<void>("update_install"),
  /** H4: the webview got the window's close request (the shell stops waiting to close a hung webview). */
  closeAck: () => transport().invoke<void>("app_close_ack"),
  /** H4: the answer to the close request: true closes the window, false keeps it. */
  closeAnswer: (close: boolean) => transport().invoke<void>("app_close_answer", { close }),
};

/** A newer signed release found by the shell's updater (§13 rule 10). */
export interface UpdateInfo {
  version: string;
  currentVersion: string;
  /** Untrusted release notes; shown as plain text only. */
  notes?: string | null;
  date?: string | null;
}

/** Shell updater failure: `kind` is network | signature | unsupported | cancelled | install | permission | busy | disabled | noUpdate | noDownload. */
export interface UpdateFailure {
  kind: string;
  message: string;
}

export function onUpdateProgress(handler: (p: { downloaded: number; total?: number | null }) => void): () => void {
  let un: (() => void) | null = null;
  let cancelled = false;
  void transport()
    .listen("update://progress", (p) => handler(p as { downloaded: number; total?: number | null }))
    .then((u) => (cancelled ? u() : (un = u)));
  return () => {
    cancelled = true;
    un?.();
  };
}

// ---- timing samples for the bench harness (§14) ----
const timings = new Map<string, number[]>();

function recordTiming(method: string, ms: number) {
  let arr = timings.get(method);
  if (!arr) timings.set(method, (arr = []));
  if (arr.length > 500) arr.shift();
  arr.push(ms);
}

export function ipcTimings(): Record<string, number[]> {
  return Object.fromEntries(timings);
}

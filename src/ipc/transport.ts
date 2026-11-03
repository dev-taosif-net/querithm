// How the webview reaches the shell. In the app this is Tauri IPC; in a plain browser during development
// it is the dev bridge (scripts/dev-bridge.mjs), which runs the real sidecar behind a local WebSocket.
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

export interface Transport {
  readonly kind: "tauri" | "bridge";
  invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T>;
  /** Next data-plane frame of an execution: [kind u8][resultIndex u16 LE][payload]; empty = poll again. */
  resultsNext(executionId: string): Promise<Uint8Array>;
  listen(event: string, cb: (payload: unknown) => void): Promise<() => void>;
}

export const isTauri = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

class TauriTransport implements Transport {
  readonly kind = "tauri" as const;
  invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
    return invoke<T>(cmd, args);
  }
  async resultsNext(executionId: string): Promise<Uint8Array> {
    const buf = await invoke<ArrayBuffer>("results_next", { executionId });
    return new Uint8Array(buf);
  }
  listen(event: string, cb: (payload: unknown) => void): Promise<() => void> {
    return listen(event, (e) => cb(e.payload));
  }
}

/** Dev-only WebSocket bridge. Request: {id, cmd, args}. Reply: {id, ok, result|error}. Binary reply: [u32 id][frame]. */
class BridgeTransport implements Transport {
  readonly kind = "bridge" as const;
  private ws: WebSocket;
  private ready: Promise<void>;
  private nextId = 1;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
  private listeners = new Map<string, Set<(p: unknown) => void>>();

  constructor(url: string) {
    this.ws = new WebSocket(url);
    this.ws.binaryType = "arraybuffer";
    this.ready = new Promise((resolve, reject) => {
      this.ws.onopen = () => resolve();
      this.ws.onerror = () => reject(new Error(`dev bridge not reachable at ${url} — run "pnpm dev:bridge"`));
    });
    this.ws.onmessage = (m) => {
      if (m.data instanceof ArrayBuffer) {
        const id = new DataView(m.data).getUint32(0, true);
        this.pending.get(id)?.resolve(new Uint8Array(m.data, 4));
        this.pending.delete(id);
        return;
      }
      const msg = JSON.parse(m.data as string);
      if (msg.event) {
        this.listeners.get(msg.event)?.forEach((cb) => cb(msg.payload));
        return;
      }
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.ok) p.resolve(msg.result);
      else p.reject(msg.error);
    };
    this.ws.onclose = () => {
      for (const p of this.pending.values()) p.reject({ code: -32003, message: "dev bridge closed", data: { code: "sidecarDown" } });
      this.pending.clear();
      this.listeners.get("sidecar://status")?.forEach((cb) => cb({ state: "dead", detail: "bridge closed" }));
    };
  }

  async invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
    await this.ready;
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      this.ws.send(JSON.stringify({ id, cmd, args: args ?? {} }));
    });
  }

  resultsNext(executionId: string): Promise<Uint8Array> {
    return this.invoke<Uint8Array>("results_next", { executionId });
  }

  async listen(event: string, cb: (payload: unknown) => void): Promise<() => void> {
    let set = this.listeners.get(event);
    if (!set) this.listeners.set(event, (set = new Set()));
    set.add(cb);
    return () => set!.delete(cb);
  }
}

let current: Transport | null = null;

export function transport(): Transport {
  if (!current) {
    current = isTauri
      ? new TauriTransport()
      : new BridgeTransport((import.meta.env.VITE_BRIDGE_URL as string | undefined) ?? "ws://127.0.0.1:1421");
  }
  return current;
}

// Which engines this sidecar offers (`system.engines`, Phase D1): exposure, locations, sign-ins and defaults. An engine
// behind the sidecar's hidden-engine flag stays hidden unless the developer setting exposes it; the sidecar decides, the
// webview only reads the answer.
import type { Engine, EngineInfo } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { ENGINE_LIST, isEngine } from "../engines";

/** The sidecar's engine list; null when it cannot answer (an error, an older sidecar), which offers the fallback. */
export async function loadEngineInfo(): Promise<EngineInfo[] | null> {
  try {
    const list = await rpc("system.engines");
    return Array.isArray(list) ? list : null;
  } catch {
    return null;
  }
}

/** Offered when the sidecar cannot list its engines: the engines every build has always exposed. */
const ALWAYS_OFFERED: Engine[] = ["mssql", "pg"];

/**
 * The engines the profile editor's picker offers: those the sidecar marks exposed, in display order; the fallback when
 * the list is missing or names none; plus the profile's own engine, so an existing profile always shows what it is.
 */
export function offeredEngines(info: readonly EngineInfo[] | null | undefined, current?: Engine): Engine[] {
  const exposed = Array.isArray(info) ? info.filter((i) => i?.exposed && isEngine(i.engine)).map((i) => i.engine) : [];
  const list = exposed.length ? ENGINE_LIST.filter((e) => exposed.includes(e)) : ALWAYS_OFFERED;
  return current && !list.includes(current) ? [...list, current] : list;
}

// The dialects whose engines this sidecar offers (D4 S12): a hidden engine's dialect (Oracle until its release gates
// pass) is neither offered in the snippet dialect lists nor shown through its built-in snippets.
import { useEffect, useState } from "react";
import type { Dialect } from "../ipc/contract";
import { loadEngineInfo, offeredEngines } from "../connections/engineInfo";
import { ENGINES } from "../engines";

/** Null while the sidecar has not answered, or when it cannot (then every dialect stays offered, as before). */
export async function loadOfferedDialects(): Promise<Dialect[] | null> {
  const info = await loadEngineInfo();
  return info ? [...new Set(offeredEngines(info).map((e) => ENGINES[e].dialect))] : null;
}

export function useOfferedDialects(): Dialect[] | null {
  const [dialects, setDialects] = useState<Dialect[] | null>(null);
  useEffect(() => {
    let live = true;
    void loadOfferedDialects().then((d) => live && setDialects(d));
    return () => {
      live = false;
    };
  }, []);
  return dialects;
}

/** Whether `dialect` is shown; `keep` (the snippet being edited, the filter in use) always is. */
export const dialectShown = (offered: Dialect[] | null, dialect: Dialect, keep?: Dialect | "" | "all" | null) =>
  !offered || offered.includes(dialect) || dialect === keep;

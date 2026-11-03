// Own-tab blocking banner (B1b): "This tab's open transaction is blocking N sessions for 45 s — Commit / Rollback /
// Open monitor". Non-modal; Commit / Rollback use the tab's own transaction commands and guards; dismiss mutes it for 5
// minutes, after which it shows again if the blocking goes on (O9).
import { useEffect, useState } from "react";
import type { TabState } from "../store/tabs";
import { txAction } from "../app/actions";
import { ensureBlockingRoute, openActivityMonitor, useBlocking } from "./monitorActions";
import { ms } from "./format";

export function BlockingBanner({ tab }: { tab: TabState }) {
  useEffect(() => ensureBlockingRoute(), []);
  const state = useBlocking((s) => (tab.sessionId ? s.bySession[tab.sessionId] : undefined));
  const mutedUntil = useBlocking((s) => (tab.sessionId ? s.muted[tab.sessionId] ?? 0 : 0));
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (mutedUntil <= now) return;
    const t = setTimeout(() => setNow(Date.now()), mutedUntil - now + 10);
    return () => clearTimeout(t);
  }, [mutedUntil, now]);
  if (!tab.sessionId || !state || state.unavailable || state.blockedCount <= 0 || tab.txState !== "open") return null;
  if (mutedUntil > now) return null;
  return (
    <div className="px-3 py-1.5 border-b border-warn/50 bg-warn/15 q-hl text-[11.5px] flex items-center gap-3 shrink-0 [&>button]:shrink-0" role="status" data-testid="blocking-banner">
      <span className="text-fg min-w-0">
        This tab&apos;s open transaction is blocking <b>{state.blockedCount}</b> session{state.blockedCount === 1 ? "" : "s"} for <b>{ms(state.maxWaitMs)}</b>.
      </span>
      <button className="q-btn" onClick={() => void txAction(tab.id, "commit")}>Commit</button>
      <button className="q-btn" onClick={() => void txAction(tab.id, "rollback")}>Rollback</button>
      <button className="q-btn" onClick={() => openActivityMonitor(tab.profileId)}>Open monitor</button>
      <span className="flex-1" />
      <button className="text-muted hover:text-fg whitespace-nowrap" aria-label="Dismiss for 5 minutes" onClick={() => { useBlocking.getState().mute(tab.sessionId!); setNow(Date.now()); }}>
        Dismiss (5 min)
      </button>
    </div>
  );
}

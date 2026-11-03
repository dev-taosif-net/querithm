// What the status region says about tabs out of sight (D23): a background tab's query ends, a server drops with tabs
// on it, an open transaction stays behind when a tool tab takes the screen. Screen-reader users hear them without
// focus moving; at most one a second (store/announce). The "announce background tabs" setting turns them off.
import { announce } from "../store/announce";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { useTabs } from "../store/tabs";
import { envName } from "./TabIdentity";
import { tabTitleText } from "./tabTitle";

const on = () => useSettings.getState().announceBackground;

function where(profileId: string | undefined): string {
  const p = useProfiles.getState().byId(profileId);
  return p ? (envName(p.environment) ?? p.name) : "no connection";
}

/** What a run returned, for the shown tab's announcement. */
export interface RunSummary {
  resultSets: number;
  errors: { message: string; editorLine?: number | null }[];
}

/**
 * A run ended. In a tab that is not the one shown: "Query finished in Orders (Development, 2.4 s)" (D23, behind the
 * setting). In the shown tab, once, what came back (§12 bottom panel): "Run finished: 2 result sets, 1 error. Line 12:
 * Invalid object name 'dbo.Invoce'." — assertive when a statement failed; focus and the view never move.
 */
export function announceQueryDone(tabId: string, state: string, elapsedMs: number | null | undefined, summary?: RunSummary) {
  const s = useTabs.getState();
  const t = s.tabs[tabId];
  if (!t) return;
  const verb = state === "failed" ? "failed" : state === "cancelled" ? "was cancelled" : "finished";
  if (s.activeId !== tabId) {
    if (!on()) return;
    const time = elapsedMs != null ? `, ${(elapsedMs / 1000).toFixed(1)} s` : "";
    announce(`Query ${verb} in ${tabTitleText(t.title)} (${where(t.profileId)}${time})`);
    return;
  }
  if (!summary) return;
  const parts = [`${summary.resultSets} result set${summary.resultSets === 1 ? "" : "s"}`];
  const n = summary.errors.length;
  if (n) parts.push(`${n} error${n === 1 ? "" : "s"}`);
  const first = summary.errors[0];
  const detail = first ? ` ${first.editorLine ? `Line ${first.editorLine}: ` : ""}${first.message.split("\n")[0].slice(0, 160)}` : "";
  announce(`Run ${verb}: ${parts.join(", ")}.${detail}`, n > 0 || state === "failed" ? "assertive" : "polite");
}

/** A tool tab took the screen from a query tab that still holds an open transaction. */
export function announceTxLeftBehind(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  if (!on() || !t || t.txState !== "open") return;
  announce(`Open transaction remains on ${tabTitleText(t.title)} (${where(t.profileId)})`);
}

/** "Server QA Inventory disconnected, 3 tabs affected", once per drop. Returns the unsubscribe. */
export function installAnnouncements(): () => void {
  return useConnections.subscribe((s, prev) => {
    if (!on() || s.disconnected === prev.disconnected) return;
    for (const [id, off] of Object.entries(s.disconnected)) {
      if (!off || prev.disconnected[id]) continue;
      const n = Object.values(useTabs.getState().tabs).filter((t) => t.profileId === id).length;
      const name = useProfiles.getState().byId(id)?.name ?? "A server";
      announce(`Server ${name} disconnected${n ? `, ${n} tab${n === 1 ? "" : "s"} affected` : ""}`);
    }
  });
}

// What an editor tab is, where it runs and what it is doing, drawn the same on the strip, in Ctrl+P and in Ctrl+Tab
// (D18). Line 1: kind or bug icon · TX pill · spinner · lock · VERB · name · folder · Preview. Line 2: engine mark ·
// database · server. The database is drawn in its environment's colour and the environment is not written there
// (owner revision 2): the colour, the Prod band, the tooltip, the top bar and the spoken name say it.
import { useEffect, useLayoutEffect, useRef, type MutableRefObject, type RefObject } from "react";
import { escapeForDisplay } from "../monitor/format";
import type { EnvironmentKind, Profile } from "../ipc/contract";
import type { ToolKind } from "../store/toolTabs";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { EngineLogo } from "../connections/EngineLogo";
import { serverAddress, serverAddressLine } from "../connections/serverAddress";
import { Icon, type IconName } from "./icons";
import { connectionText, TAB_SURFACES, useThemeKey } from "./tabStyle";
import { isDebugTitle, isUntitledTitle, middleCut, tabName, tabTitleText } from "./tabTitle";
import { isSingleLineLayout, type TabLayout } from "../store/settings";
import { txAgeLong, txAgeShort } from "./txAge";
import { useTxNow } from "./txClock";
import { MENU_EVENT, RENAME_EVENT } from "./stripKeyboard";

export type TabKind = "query" | ToolKind;

/** Where a tab (or one side of a compare tab) runs. */
export interface TabTarget {
  profile?: Profile;
  database?: string;
  /** A saved model file instead of a live database (schema compare). */
  file?: string;
}

/** Everything the identity shows; the strip, Ctrl+P and Ctrl+Tab build it from the same stores. */
export interface TabFacts {
  kind: TabKind;
  /** The stored title (".sql" and the debugger marker are dropped for display). */
  title: string;
  profile?: Profile;
  database?: string;
  disconnected?: boolean;
  /** Compare tabs: the side that is written to; `profile`/`database` are then the source. */
  target?: TabTarget;
  busy?: boolean;
  txOpen?: boolean;
  txStartedAt?: number;
  spid?: number;
  dirty?: boolean;
  readOnly?: boolean;
  pinned?: boolean;
  preview?: boolean;
  filePath?: string;
  /** "q3/": the file's folder, when another tab has the same name and the same line 2. */
  suffix?: string;
  /** A SQL notebook (Phase C3). */
  notebook?: boolean;
}

const ENV_NAME: Record<EnvironmentKind, string> = { local: "Local", dev: "Development", test: "Test", staging: "Staging", prod: "Production" };
const KIND_NAME: Record<ToolKind, string> = { erd: "ER diagram", activity: "activity monitor", schemaCompare: "schema compare", dataCompare: "data compare" };
const KIND_ICON: Record<ToolKind, IconName> = { erd: "erd", activity: "activity", schemaCompare: "compare", dataCompare: "dataCompare" };
/** Compare and monitor tabs are named by their kind; their line 2 says what they compare or watch. */
const KIND_TITLE: Partial<Record<ToolKind, string>> = { activity: "Activity monitor", schemaCompare: "Schema compare", dataCompare: "Data compare" };

/** The name a tab shows: tool tabs drop the kind their icon already shows ("ERD · Orders" → "Orders"). */
export function displayTitle(kind: TabKind, title: string): string {
  if (kind === "query") return title;
  if (KIND_TITLE[kind]) return KIND_TITLE[kind]!;
  const prefix = "ERD · ";
  return title.startsWith(prefix) ? title.slice(prefix.length) : title;
}

/** Spoken environment name ("Production"). */
export function envName(env: EnvironmentKind | undefined): string | undefined {
  return env ? ENV_NAME[env] : undefined;
}

/**
 * The tab's accessible name (D22): "fix_invoice_totals, Production, Primary Billing, database Billing, open
 * transaction". Running, disconnected, read-only, modified and pinned are part of it; the transaction's age is in the
 * description.
 */
export function spokenName(f: TabFacts): string {
  const n = tabName(displayTitle(f.kind, f.title));
  const parts = [`${n.verb ? `${n.verb} ` : ""}${n.name}`];
  if (f.kind !== "query") parts.push(KIND_NAME[f.kind]);
  if (f.notebook) parts.push("notebook");
  if (isDebugTitle(f.title)) parts.push("debugger");
  if (f.preview) parts.push("preview");
  if (f.dirty) parts.push("modified");
  if (f.readOnly) parts.push("read-only");
  if (f.profile) {
    const env = envName(f.profile.environment);
    const tgt = f.target?.profile ? envName(f.target.profile.environment) : undefined;
    if (env) parts.push(tgt ? `${env} to ${tgt}` : env);
    parts.push(f.profile.name + (f.disconnected ? ", server disconnected" : ""));
    if (f.database) parts.push(`database ${f.database}`);
    if (f.target?.database) parts.push(`to database ${f.target.database}`);
  } else if (f.kind === "query") parts.push("no connection");
  if (f.busy) parts.push("query running");
  if (f.txOpen) parts.push("open transaction");
  if (f.pinned) parts.push("pinned");
  return parts.join(", ");
}

/** The tooltip (also read when the pointer arrives, so the transaction's age is current). */
export function tooltipText(f: TabFacts, now = Date.now()): string {
  // The stored title in full (".sql" kept), except the debugger marker and an untitled tab's SQLQuery form.
  const lines = [`${isDebugTitle(f.title) || isUntitledTitle(f.title) ? tabTitleText(f.title) : f.title}${f.dirty ? " · modified" : ""}`];
  if (f.filePath) lines.push(f.filePath);
  const where = (t: TabTarget) =>
    t.profile ? `${envLabel[t.profile.environment]?.toUpperCase() ?? ""} ${escapeForDisplay(t.profile.name ?? "")}${t.database ? ` › ${escapeForDisplay(t.database)}` : ""}`.trim() : t.file ?? "";
  if (f.profile) {
    lines.push(where(f) + (f.target ? ` → ${where(f.target)}` : "") + (f.disconnected ? " (disconnected)" : ""));
    // The address the profile points at: its name can say "dev" while it points at production.
    if (!f.target) {
      const line = serverAddressLine(f.profile);
      if (line) lines.push(line);
    } else {
      const from = serverAddress(f.profile);
      const to = f.target.profile ? serverAddress(f.target.profile) : undefined;
      if (from || to) lines.push(`Server ${from ? escapeForDisplay(from) : "(none)"} → ${to ? escapeForDisplay(to) : "(none)"}`);
    }
  } else if (f.kind === "query") lines.push("No connection");
  if (f.txOpen) lines.push([`Transaction open${f.txStartedAt ? ` ${txAgeLong(now - f.txStartedAt)}` : ""}`, f.spid ? `SPID ${f.spid}` : ""].filter(Boolean).join(" · "));
  else if (f.spid) lines.push(`SPID ${f.spid}`);
  if (f.busy) lines.push("Query running");
  if (f.pinned) lines.push("Pinned");
  return lines.join("\n");
}

/** "TX", then "TX 12m", then "TX 2h" (D10); its own clock, so nothing else re-renders as it ages. */
export function TxPill({ startedAt }: { startedAt?: number }) {
  const now = useTxNow();
  const age = startedAt ? txAgeShort(now - startedAt) : "";
  return <span className="q-tx-pill">TX{age ? ` ${age}` : ""}</span>;
}

/** Text that keeps its last `keep` characters when it has to be cut. */
function Cut({ text, keep }: { text: string; keep: number }) {
  const [head, tail] = middleCut(text, keep);
  return (
    <>
      <span className="q-cut-head">{head}</span>
      {tail && <span className="q-cut-tail">{tail}</span>}
    </>
  );
}

/**
 * Line 1. `layout` (the strip's tab layout; Ctrl+P and Ctrl+Tab keep the two-line rows): every layout but the two-line
 * one puts the TX pill after the name, so the name stays where it is when a transaction opens or ends.
 */
export function TabLine1({ f, layout = "twoLine" }: { f: TabFacts; layout?: TabLayout }) {
  const n = tabName(displayTitle(f.kind, f.title));
  const txAfter = layout !== "twoLine";
  return (
    <span className="q-tab-l1">
      {f.kind !== "query" && <Icon name={KIND_ICON[f.kind]} size={12} strokeWidth={1.9} className="q-tab-icon" />}
      {f.notebook && <Icon name="notebook" size={12} strokeWidth={1.9} className="q-tab-icon" />}
      {isDebugTitle(f.title) && <Icon name="bug" size={12} strokeWidth={1.9} className="q-tab-icon" />}
      {f.txOpen && !txAfter && <TxPill startedAt={f.txStartedAt} />}
      {f.busy && <Icon name="spinner" size={12} strokeWidth={2.6} className="q-spin" />}
      {f.readOnly && <Icon name="lock" size={11} strokeWidth={1.9} className="q-tab-icon" />}
      {n.verb && <span className="q-tab-verb" data-danger={n.danger || undefined}>{n.verb}</span>}
      <span className="q-tab-name" style={f.preview ? { fontStyle: "italic" } : undefined} data-q-tab-title>
        <Cut text={n.name} keep={7} />
      </span>
      {f.suffix && <span className="q-tab-suffix">{f.suffix}</span>}
      {f.preview && <span className="q-tab-preview">Preview</span>}
      {f.txOpen && txAfter && <TxPill startedAt={f.txStartedAt} />}
    </span>
  );
}

/** Guard Rail layout: production and staging are written on the tab, not only coloured (PRD solid, STG outlined). */
function EnvChip({ profile }: { profile?: Profile }) {
  const env = profile?.environment;
  if (env !== "prod" && env !== "staging") return null;
  return <span className="q-tab-chip" data-env={env}>{env === "prod" ? "PRD" : "STG"}</span>;
}

/** The colour a connection's text takes on `surfaces` (the tab's three, or the overlay's). */
function useConnectionColor(profile: Profile | undefined, surfaces: readonly string[]): string | undefined {
  useThemeKey();
  const groups = useProfiles((s) => s.groups);
  return profile ? connectionText(envColor(profile, groups), surfaces) : undefined;
}

/**
 * `fullDatabase` (a query tab on the strip): the database is never cut; the tab widens to fit it (useFullDatabase) and
 * the server after it takes the "…". Compare tabs and the Ctrl+P / Ctrl+Tab rows keep the middle cut.
 */
export function TabLine2({ f, surfaces = TAB_SURFACES, fullDatabase = false, layout = "twoLine" }: { f: TabFacts; surfaces?: readonly string[]; fullDatabase?: boolean; layout?: TabLayout }) {
  const color = useConnectionColor(f.profile, surfaces);
  const targetColor = useConnectionColor(f.target?.profile, surfaces);
  // One-line layouts: this is the database at the end of the line; the engine mark and the server are in the tooltip.
  const tail = isSingleLineLayout(layout);
  const guard = layout === "guard";
  const cls = tail ? "q-tab-l2 q-tab-tail" : "q-tab-l2";
  if (!f.profile && !f.target) {
    return <span className={cls} data-q-tab-subtitle>{f.kind === "query" ? "No connection" : ""}</span>;
  }
  const engine = f.profile?.engine ?? f.target?.profile?.engine;
  // The database carries the environment's colour; with no database the server does.
  const place = f.database ?? (f.target ? f.profile?.name : undefined);
  return (
    <span className={cls} data-q-tab-subtitle>
      {/* Disconnected: the plug leads and the state word is never cut (D8); the engine mark gives way, and the
          database is cut at its end, so a 150 px tab still shows where it was. */}
      {f.disconnected && <Icon name="plugOff" size={11} strokeWidth={1.9} className="q-tab-icon" />}
      {guard && !f.disconnected && <EnvChip profile={f.profile} />}
      {engine && !f.disconnected && !tail && <span className="q-tab-mark"><EngineLogo engine={engine} size={10} mono decorative /></span>}
      {f.disconnected && <span className="shrink-0">disconnected</span>}
      {place && fullDatabase && !f.target ? (
        <span className="q-tab-db q-tab-db-full" style={{ color }} data-q-tab-db-full>{place}</span>
      ) : place ? (
        <span className="q-tab-db" style={{ color }}><Cut text={place} keep={f.disconnected ? 0 : 5} /></span>
      ) : f.profile && !f.target ? (
        <span className="q-tab-server" style={{ color }}>{f.profile.name}</span>
      ) : null}
      {f.target && (
        <>
          <span className="shrink-0" aria-hidden>→</span>
          {guard && <EnvChip profile={f.target.profile} />}
          <span className="q-tab-db" style={{ color: targetColor }}>
            <Cut text={f.target.database ?? f.target.file ?? f.target.profile?.name ?? "…"} keep={5} />
          </span>
        </>
      )}
      {/* The separator belongs to the server, so it goes when the server is cut away. */}
      {!f.target && f.profile && place && !tail && <span className="q-tab-server">{`· ${f.profile.name}`}</span>}
    </span>
  );
}

/**
 * Keeps a tab at least as wide as its whole database name (line 2 is clipped, so without this a long name would be cut
 * with no "…"): an inline min-width on Dockview's tab element, over the stylesheet's 120 / 128 / 150 px. Measured when
 * the database text changes size (a new name, a UI scale step, the font arriving), never per frame; the database span
 * does not grow with the tab, so the observer cannot loop.
 */
export function useFullDatabase(root: RefObject<HTMLElement | null>, tabEl: MutableRefObject<HTMLElement | null>, key: string) {
  useLayoutEffect(() => {
    const el = tabEl.current;
    // One line: whatever closes the line ("No connection", a server with no database) is measured as the database is.
    const db = root.current?.querySelector<HTMLElement>("[data-q-tab-db-full]") ?? root.current?.querySelector<HTMLElement>(".q-tab-top > .q-tab-tail");
    if (!el) return;
    if (!db) {
      el.style.removeProperty("min-width");
      return;
    }
    // One-line layouts: the database sits on line 1 after the name, before the close slot's spacer, so the minimum is
    // the whole line's content (name, dot, database and spacer, none of which shrinks), not just up to the database.
    const line = db.closest<HTMLElement>(".q-tab-top");
    const name = line?.querySelector<HTMLElement>(".q-tab-l1");
    const fit = () => {
      const r = db.getBoundingClientRect();
      if (!r.width) return; // not laid out (hidden group, tests)
      el.style.removeProperty("min-width");
      const base = parseFloat(getComputedStyle(el).minWidth) || 0;
      const pad = root.current ? parseFloat(getComputedStyle(root.current).paddingRight) || 0 : 0;
      const left = el.getBoundingClientRect().left;
      const end = line ? line.getBoundingClientRect().left + line.scrollWidth : r.right;
      const need = Math.ceil(end - left + pad);
      if (need > base) el.style.setProperty("min-width", `${need}px`, "important");
    };
    fit();
    const ro = typeof ResizeObserver === "undefined" ? undefined : new ResizeObserver(fit);
    ro?.observe(db);
    // The name moves the database: a rename, the TX pill or the spinner appearing (none of them grows with the tab).
    if (name) ro?.observe(name);
    return () => {
      ro?.disconnect();
      el.style.removeProperty("min-width");
    };
  }, [key]); // eslint-disable-line react-hooks/exhaustive-deps -- refs; re-measured when line 2's text changes
}

/** The transaction's age for the accessible description (D22): "Transaction open 12 min", or nothing. */
export function spokenDescription(f: TabFacts | undefined, now = Date.now()): string {
  return f?.txOpen && f.txStartedAt ? `Transaction open ${txAgeLong(now - f.txStartedAt)}` : "";
}

/**
 * Puts a header's tab semantics on Dockview's tab element, which is what takes focus (D22): the accessible name, the
 * transaction's age as its description (read again whenever the tab is focused), and the keyboard's requests to
 * rename (F2) and to open the menu at the tab (Shift+F10, the Menu key). `tabEl` is set to that element.
 */
export function useTabSemantics(
  root: RefObject<HTMLElement | null>,
  tabEl: MutableRefObject<HTMLElement | null>,
  facts: TabFacts | undefined,
  openMenu: (at: { x: number; y: number }) => void,
  rename?: () => void,
) {
  const name = facts ? spokenName(facts) : "";
  const latest = useRef({ facts, openMenu, rename });
  useLayoutEffect(() => {
    latest.current = { facts, openMenu, rename };
    const el = root.current?.closest<HTMLElement>(".dv-tab") ?? null;
    tabEl.current = el;
    if (el && name) el.setAttribute("aria-label", name);
  });
  const mounted = !!name;
  useEffect(() => {
    const el = tabEl.current;
    if (!el) return;
    const describe = () => {
      const d = spokenDescription(latest.current.facts);
      if (d) el.setAttribute("aria-description", d);
      else el.removeAttribute("aria-description");
    };
    const onRename = () => latest.current.rename?.();
    const onMenu = () => {
      const r = el.getBoundingClientRect();
      latest.current.openMenu({ x: r.left + 8, y: r.bottom });
    };
    describe();
    el.addEventListener("focus", describe);
    el.addEventListener(RENAME_EVENT, onRename);
    el.addEventListener(MENU_EVENT, onMenu);
    return () => {
      el.removeEventListener("focus", describe);
      el.removeEventListener(RENAME_EVENT, onRename);
      el.removeEventListener(MENU_EVENT, onMenu);
    };
  }, [mounted, root, tabEl]);
}

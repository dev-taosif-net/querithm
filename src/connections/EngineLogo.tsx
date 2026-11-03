// Engine marks for connection rows. Inline SVG so the app stays offline (no remote assets).
import type { Profile } from "../ipc/contract";
import { assertNever, ENGINES, knownEngine } from "../engines";

/**
 * `mono`: drawn in currentColor (the Object Explorer's rows), so SQL Server's red mark does not read as Prod or as an
 * error; the cut lines take the row's surface (`--q-row-bg`, set by `.q-tree-row`). `decorative`: hidden from
 * assistive technology and without a title, where the engine is said elsewhere or not needed (tab strip).
 */
export function EngineLogo({ engine, size = 14, mono = false, decorative = false }: { engine: Profile["engine"] | undefined; size?: number; mono?: boolean; decorative?: boolean }) {
  const cut = mono ? "var(--q-row-bg, var(--q-panel))" : "#fff";
  // No engine (no connection) shows the default engine's mark, as before engines had traits.
  const shown = knownEngine(engine);
  const name = ENGINES[shown].label;
  const a11y = decorative ? { "aria-hidden": true as const } : { role: "img", "aria-label": name };
  switch (shown) {
    case "pg":
      // PostgreSQL elephant.
      return (
        <svg width={size} height={size} viewBox="0 0 24 24" {...a11y} className="shrink-0">
          {!decorative && <title>{name}</title>}
          <path d="M13 4.2c5-1.6 9.2 1.6 8.7 6.8-.5 4.6-4.1 7.1-7.7 6.2 1.6-3.1 1.8-8.2-1-13z" fill={mono ? "currentColor" : "#4f86b8"} opacity={mono ? 0.7 : undefined} stroke={mono ? undefined : cut} strokeWidth="0.9" strokeLinejoin="round" />
          <path
            d="M6.6 14.4A6 6 0 1 0 4.2 11c-.6 3-.4 6 .7 8.3.5 1.1 2.1 1.2 2.5.2-.6-.7-.9-1.9-.9-3.1 0-.8 0-1.4.1-2z"
            fill={mono ? "currentColor" : "#336791"}
            stroke={cut}
            strokeWidth="0.9"
            strokeLinejoin="round"
          />
          <circle cx="8.3" cy="8.3" r="0.95" fill={cut} />
        </svg>
      );
    case "mssql":
      // SQL Server: red database stack.
      return (
        <svg width={size} height={size} viewBox="0 0 24 24" {...a11y} className="shrink-0">
          {!decorative && <title>{name}</title>}
          <path d="M4 6v12c0 1.9 3.6 3.5 8 3.5s8-1.6 8-3.5V6" fill={mono ? "currentColor" : "#b8231f"} />
          <path d="M4 10.4c0 1.9 3.6 3.5 8 3.5s8-1.6 8-3.5M4 14.6c0 1.9 3.6 3.5 8 3.5s8-1.6 8-3.5" fill="none" stroke={mono ? cut : "#ffb4ae"} strokeWidth={mono ? 1.2 : 1} />
          <ellipse cx="12" cy="6" rx="8" ry="3.5" fill={mono ? "currentColor" : "#e04a42"} stroke={mono ? cut : undefined} strokeWidth={mono ? 1 : undefined} />
          {!mono && <ellipse cx="12" cy="6" rx="5" ry="1.8" fill="#f37a72" />}
        </svg>
      );
    case "sqlite":
      // Our own mark (not SQLite's logo): a database file, a page with a folded corner and a small cylinder on it.
      return (
        <svg width={size} height={size} viewBox="0 0 24 24" {...a11y} className="shrink-0">
          {!decorative && <title>{name}</title>}
          <path d="M5 2.5h9.5L19 7v14.5H5z" fill={mono ? "currentColor" : "#5c8fc7"} stroke={mono ? cut : undefined} strokeWidth={mono ? 1 : undefined} strokeLinejoin="round" />
          <path d="M14.5 2.5V7H19" fill={mono ? "none" : "#9cc0e6"} stroke={cut} strokeWidth="1" strokeLinejoin="round" />
          <path d="M8.5 12.2v4.6c0 .9 1.6 1.6 3.5 1.6s3.5-.7 3.5-1.6v-4.6" fill="none" stroke={cut} strokeWidth="1.2" />
          <ellipse cx="12" cy="12.2" rx="3.5" ry="1.5" fill="none" stroke={cut} strokeWidth="1.2" />
        </svg>
      );
    case "mySql":
      // Our own mark (not the MySQL or MariaDB logos): a server stack with a status light.
      return (
        <svg width={size} height={size} viewBox="0 0 24 24" {...a11y} className="shrink-0">
          {!decorative && <title>{name}</title>}
          <rect x="3.5" y="3.5" width="17" height="7" rx="1.6" fill={mono ? "currentColor" : "#00758f"} />
          <rect x="3.5" y="13.5" width="17" height="7" rx="1.6" fill={mono ? "currentColor" : "#f29111"} />
          <circle cx="7.2" cy="7" r="1.1" fill={cut} />
          <circle cx="7.2" cy="17" r="1.1" fill={cut} />
          <path d="M11 7h6M11 17h6" stroke={cut} strokeWidth="1.2" strokeLinecap="round" />
        </svg>
      );
    case "duckDb":
      // Our own mark (not the DuckDB logo): the columns of an analytics file.
      return (
        <svg width={size} height={size} viewBox="0 0 24 24" {...a11y} className="shrink-0">
          {!decorative && <title>{name}</title>}
          <rect x="3.5" y="3.5" width="17" height="17" rx="4" fill={mono ? "currentColor" : "#c99400"} />
          <path d="M8.5 16.5v-5M12 16.5v-9M15.5 16.5v-3" stroke={cut} strokeWidth="1.8" strokeLinecap="round" />
        </svg>
      );
    default:
      return assertNever(shown);
  }
}

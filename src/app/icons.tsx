// Inline icons for the top bar, the Object Explorer and their menus: one family (24 px grid, round 1.75 px stroke,
// currentColor), drawn here so the app stays offline and needs no icon package.
import type { ReactNode } from "react";

const paths = {
  back: <path d="M19 12H5M11 18l-6-6 6-6" />,
  forward: <path d="M5 12h14M13 6l6 6-6 6" />,
  plus: <path d="M12 5v14M5 12h14" />,
  chevron: <path d="M6 9l6 6 6-6" />,
  play: <path d="M7 4.5v15l12-7.5z" fill="currentColor" stroke="none" />,
  continue: (
    <>
      <path d="M5 4.5v15" />
      <path d="M9.5 4.5v15l11-7.5z" fill="currentColor" stroke="none" />
    </>
  ),
  stop: (
    <>
      <path d="M21 12a9 9 0 1 1-6.2-8.6" />
      <rect x="9" y="9" width="6" height="6" rx="1" fill="currentColor" stroke="none" />
    </>
  ),
  plan: <path d="M4 4h6v5H4zM14 15h6v5h-6zM7 9v4a2 2 0 0 0 2 2h5" />,
  transaction: <path d="M16 4l4 4-4 4M20 8H4M8 20l-4-4 4-4M4 16h16" />,
  check: <path d="M20 6L9 17l-5-5" />,
  undo: <path d="M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-4" />,
  shield: <path d="M12 3l7 3v5c0 4.5-3 8.5-7 10-4-1.5-7-5.5-7-10V6z" />,
  plug: <path d="M9 3v5M15 3v5M6 8h12v4a6 6 0 0 1-12 0zM12 18v3" />,
  reconnect: <path d="M20 12a8 8 0 1 1-2.34-5.66L20 8M20 3v5h-5" />,
  /** A crossed-out plug: a disconnected server (the hollow ring looked like the Prod shield at 10 px). */
  plugOff: <path d="M9 3v4M15 3v4M7 7h10v3a5 5 0 0 1-10 0zM12 15v6M4 4l16 16" />,
  lock: <path d="M7 11V8a5 5 0 0 1 10 0v3M5 11h14v10H5z" />,
  bug: <path d="M8 9a4 4 0 0 1 8 0v5a4 4 0 0 1-8 0zM12 11v7M4 12h4M16 12h4M5 6l3 2.5M19 6l-3 2.5M5 19l3-2.5M19 19l-3-2.5" />,
  /** Tool tab kinds: ER diagram, activity monitor, schema compare, data compare. */
  erd: <path d="M3 4h7v6H3zM14 14h7v6h-7zM6.5 10v4h11" />,
  activity: <path d="M3 12h4l3-7 4 14 3-7h4" />,
  compare: <path d="M7 4v16M17 4v16M3 8h8M13 16h8" />,
  dataCompare: <path d="M4 6h16M4 10h16M4 14h7M13 14h7M4 18h7M13 18h7M12 12v8" />,
  search: <path d="M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM20 20l-4-4" />,
  history: (
    <>
      <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
      <path d="M3 3v5h5" />
      <path d="M12 7v5l4 2" />
    </>
  ),
  settings: (
    <>
      <path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" />
      <circle cx="12" cy="12" r="3" />
    </>
  ),
  download: <path d="M12 3v12M7 10l5 5 5-5M5 21h14" />,
  // Object Explorer: window buttons, states and object kinds.
  more: (
    <>
      <circle cx="5" cy="12" r="1.5" />
      <circle cx="12" cy="12" r="1.5" />
      <circle cx="19" cy="12" r="1.5" />
    </>
  ),
  pin: <path d="M9 3h6M10 3v6.5L7 13h10l-3-3.5V3M12 13v8" />,
  close: <path d="M6 6l12 12M18 6L6 18" />,
  filter: <path d="M4 5h16l-6 7.5V18l-4 2v-7.5z" />,
  locate: <path d="M12 2v4M12 18v4M2 12h4M18 12h4M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z" />,
  alert: <path d="M12 3.5L21.5 20h-19zM12 10v4.5M12 17.2v.3" />,
  ring: <circle cx="12" cy="12" r="5" />,
  spinner: <path d="M12 3a9 9 0 1 1-9 9" />,
  edit: <path d="M4 20h4L19.5 8.5l-4-4L4 16zM13.5 6.5l4 4" />,
  database: <path d="M5 6c0-1.7 3.1-3 7-3s7 1.3 7 3-3.1 3-7 3-7-1.3-7-3zM5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3" />,
  folder: <path d="M3 7.5A2.5 2.5 0 0 1 5.5 5H9l2 2h7.5A2.5 2.5 0 0 1 21 9.5v7a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 16.5z" />,
  schema: <path d="M12 3l9 4.5-9 4.5-9-4.5zM3 12l9 4.5 9-4.5M3 16.5L12 21l9-4.5" />,
  table: <path d="M4 5h16v14H4zM4 10h16M4 14.5h16M10 10v9" />,
  view: <path d="M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12zM12 9.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z" />,
  proc: <path d="M6 3h9l4 4v14H6zM14 3v5h5M10 11v6l5-3z" />,
  func: <path d="M15 3.5h-1.5a3 3 0 0 0-3 3v11a3 3 0 0 1-3 3H6M7.5 10.5h7" />,
  type: <path d="M5 5h14M12 5v15M8 20h8" />,
  sequence: <path d="M5 9h15M4 15h15M10 4L8 20M16 4l-2 16" />,
  synonym: <path d="M4 8h13l-3-3M20 16H7l3 3" />,
  trigger: <path d="M13 2L4.5 13.5H11L10 22l8.5-11.5H12z" />,
  package: <path d="M12 3l8 4.5v9L12 21l-8-4.5v-9zM4 7.5l8 4.5 8-4.5M12 12v9" />,
  column: <path d="M8.5 8.5h7v7h-7z" />,
  key: <path d="M4 12a3.5 3.5 0 1 0 7 0 3.5 3.5 0 1 0-7 0zM11 12h10M18 12v3M21 12v2" />,
  fk: <path d="M9.5 14.5l5-5M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2" />,
  index: <path d="M4 6h16M4 12h11M4 18h7" />,
  param: <path d="M12 8.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zM15.5 12v1.5a2.5 2.5 0 0 0 5 0V12a8.5 8.5 0 1 0-3.4 6.8" />,
  returns: <path d="M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-4" />,
  // Settings pages
  appearance: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 3.5a8.5 8.5 0 0 1 0 17z" fill="currentColor" stroke="none" />
    </>
  ),
  code: <path d="M8 7l-5 5 5 5M16 7l5 5-5 5M13.5 4.5l-3 15" />,
  format: <path d="M4 6h16M8 10h12M8 14h12M4 18h16" />,
  tabs: <path d="M3 20V6.5A1.5 1.5 0 0 1 4.5 5H10l1.5 3H21v12zM3 8h8.5" />,
  keyboard: <path d="M3 6h18v12H3zM7 10h.01M11 10h.01M15 10h.01M7 14h10" />,
  info: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5.5M12 7.8v.2" />
    </>
  ),
  copy: <path d="M9 9h11v11H9zM5 15H4V4h11v1" />,
  notebook: <path d="M6 3h13v18H6zM9.5 3v18M12.5 8h4M12.5 12h4" />,
  // Bottom panel (§12): its views, layout buttons and edit actions.
  results: <path d="M3 5h18v14H3zM3 10h18M9 10v9" />,
  messages: <path d="M4 6h16M4 12h16M4 18h10" />,
  runtime: <path d="M3 12h4l3-8 4 16 3-8h4" />,
  maximize: <path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5" />,
  restore: <path d="M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5" />,
  dock: <path d="M3 5h18v14H3zM14 5v14" />,
  dockBottom: <path d="M3 5h18v14H3zM3 14h18" />,
  hidePanel: <path d="M6 9l6 6 6-6M5 20h14" />,
  unavailable: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M5.6 5.6l12.8 12.8" />
    </>
  ),
  redo: <path d="M15 14l5-5-5-5M20 9H9a5 5 0 0 0 0 10h4" />,
  trash: <path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13" />,
  chart: <path d="M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6" />,
  goToLine: <path d="M4 12h12M12 8l4 4-4 4M20 5v14" />,
  clock: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </>
  ),
  errorCircle: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M15 9l-6 6M9 9l6 6" />
    </>
  ),
  split: <path d="M3 5h18v14H3zM12 5v14" />,
  // Results layouts (§12): one set behind tabs, sets one under another.
  layoutTabs: <path d="M3 9h18v11H3zM3 9V5h7v4M10 6h6v3" />,
  layoutStacked: <path d="M3 4h18v7H3zM3 13h18v7H3z" />,
  step: <path d="M5 5v14M9 12h10M15 8l4 4-4 4" />,
} satisfies Record<string, ReactNode>;

export type IconName = keyof typeof paths;

export function Icon({ name, size = 16, strokeWidth = 1.75, className = "" }: { name: IconName; size?: number; strokeWidth?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={strokeWidth}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
      className={`shrink-0 ${className}`}
    >
      {paths[name]}
    </svg>
  );
}

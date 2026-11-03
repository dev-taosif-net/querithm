// How long a tab's transaction has been open, in the forms the strip, the overlays, the tooltip and the close dialog
// show (D10): nothing under a minute, then whole minutes, then whole hours. Never rounds up.

const MIN = 60_000;
const HOUR = 60 * MIN;

/** "" under a minute, "12m", "2h" (the TX pill). */
export function txAgeShort(ms: number): string {
  if (!(ms >= MIN)) return "";
  if (ms < HOUR) return `${Math.floor(ms / MIN)}m`;
  return `${Math.floor(ms / HOUR)}h`;
}

/** "less than a minute", "12 min", "2 h 5 min" (tooltip, dialogs, accessible description). */
export function txAgeLong(ms: number): string {
  if (!(ms >= MIN)) return "less than a minute";
  const h = Math.floor(ms / HOUR);
  const m = Math.floor((ms % HOUR) / MIN);
  if (!h) return `${m} min`;
  return m ? `${h} h ${m} min` : `${h} h`;
}

import { useAnnounce } from "../store/announce";

/**
 * The app's live regions (see store/announce): polite, and assertive for a failed statement. Re-keyed per message so a
 * repeated text is read again; only the region of the message's level holds it.
 */
export function Announcer() {
  const text = useAnnounce((s) => s.text);
  const n = useAnnounce((s) => s.n);
  const level = useAnnounce((s) => s.level);
  return (
    <>
      <div role="status" aria-live="polite" className="sr-only" data-q-announcer>
        <span key={n}>{level === "polite" ? text : ""}</span>
      </div>
      <div aria-live="assertive" className="sr-only" data-q-announcer-urgent>
        <span key={n}>{level === "assertive" ? text : ""}</span>
      </div>
    </>
  );
}

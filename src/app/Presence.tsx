import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";

const Exiting = createContext(false);
export const useExiting = () => useContext(Exiting);
const EXIT_MS = 130;

/** Keep removed items mounted for their exit, while making them immediately inert. */
export function PresenceList<T extends object>({ items, children }: { items: readonly T[]; children: (item: T) => ReactNode }) {
  const [state, setState] = useState(() => ({ source: items, entries: items.map((item, key) => ({ item, key })), next: items.length }));
  if (state.source !== items) {
    let next = state.next;
    const entries = state.entries.filter((e) => !items.includes(e.item));
    entries.push(...items.map((item) => state.entries.find((e) => e.item === item) ?? { item, key: next++ }));
    setState({ source: items, entries, next });
  }
  return state.entries.map(({ item, key }) => (
    <PresenceItem key={key} exiting={!items.includes(item)} onExited={() => setState((s) => ({ ...s, entries: s.entries.filter((e) => e.key !== key || s.source.includes(e.item)) }))}>
      {children(item)}
    </PresenceItem>
  ));
}

function PresenceItem({ exiting, onExited, children }: { exiting: boolean; onExited: () => void; children: ReactNode }) {
  const done = useRef(onExited);
  done.current = onExited;
  useEffect(() => {
    if (!exiting) return;
    const timer = setTimeout(() => done.current(), window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? 0 : EXIT_MS);
    return () => clearTimeout(timer);
  }, [exiting]);
  return <Exiting.Provider value={exiting}><div className="contents" data-q-exiting={exiting || undefined} inert={exiting || undefined} aria-hidden={exiting || undefined}>{children}</div></Exiting.Provider>;
}

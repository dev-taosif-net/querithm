// Subscribes a component to some of a monitor model's topics: it re-renders when one of them fires, nothing else does.
import { useCallback, useSyncExternalStore } from "react";
import type { MonitorModel, Topic } from "./model";

export function useTopics(model: MonitorModel | null, topics: Topic[]): number {
  const key = topics.join(",");
  const subscribe = useCallback(
    (notify: () => void) => {
      if (!model) return () => {};
      const offs = key.split(",").map((t) => model.subscribe(t as Topic, notify));
      return () => offs.forEach((o) => o());
    },
    [model, key],
  );
  // The model's emit count for these topics: the subscription runs in a passive effect after the first render, and
  // useSyncExternalStore re-reads this once subscribed, so a snapshot applied in between still re-renders the view.
  const version = useCallback(() => (model ? key.split(",").reduce((n, t) => n + model.version(t as Topic), 0) : 0), [model, key]);
  return useSyncExternalStore(subscribe, version, version);
}

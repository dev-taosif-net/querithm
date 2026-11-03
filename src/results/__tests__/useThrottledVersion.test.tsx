// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";
import { useThrottledVersion, type VersionSource } from "../useThrottledVersion";

function source() {
  const subs = new Set<() => void>();
  let v = 0;
  const src: VersionSource & { notify(): void; count(): number } = {
    subscribe: (cb) => (subs.add(cb), () => void subs.delete(cb)),
    getVersion: () => v,
    notify: () => {
      v++;
      subs.forEach((f) => f());
    },
    count: () => subs.size,
  };
  return src;
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useThrottledVersion", () => {
  it("1,000 notifications cost a handful of renders, and the last one is always shown", () => {
    const src = source();
    let renders = 0;
    let seen = -1;
    function Probe() {
      seen = useThrottledVersion(src);
      renders++;
      return null;
    }
    render(<Probe />);
    const start = renders;
    for (let i = 0; i < 1000; i++) {
      act(() => src.notify());
      if (i % 10 === 0) act(() => void vi.advanceTimersByTime(1)); // ~100 ms of batches
    }
    act(() => void vi.advanceTimersByTime(300));
    expect(renders - start).toBeLessThanOrEqual(3);
    expect(seen).toBe(1000);
  });

  it("unsubscribes on unmount and when the source changes; no source, version 0", () => {
    const a = source();
    const b = source();
    function Probe({ s }: { s?: VersionSource }) {
      useThrottledVersion(s);
      return null;
    }
    const { rerender, unmount } = render(<Probe s={a} />);
    expect(a.count()).toBe(1);
    rerender(<Probe s={b} />);
    expect(a.count()).toBe(0);
    expect(b.count()).toBe(1);
    rerender(<Probe />);
    expect(b.count()).toBe(0);
    unmount();
  });
});

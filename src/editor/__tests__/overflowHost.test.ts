import { describe, expect, it } from "vitest";
import { createOverflowHost } from "../overflowHost";

/** Minimal document stand-in: elements with className/style and a body that records children. */
function fakeDocument() {
  const children: unknown[] = [];
  const body = { appendChild: (c: unknown) => (children.push(c), c) };
  const createElement = (tag: string) => ({ tagName: tag.toUpperCase(), className: "", style: {} as Record<string, string> });
  return { doc: { body, createElement } as unknown as Pick<Document, "createElement" | "body">, children };
}

// Regression: the suggest list rendered far from the cursor because Dockview's `contain: layout` re-anchored
// Monaco's fixed widgets to the panel; they must live in a body-level host instead.
describe("createOverflowHost", () => {
  it("appends a fresh host to <body>, outside any dock panel", () => {
    const { doc, children } = fakeDocument();
    const a = createOverflowHost(doc);
    const b = createOverflowHost(doc);
    expect(children).toEqual([a, b]);
    expect(a).not.toBe(b);
  });

  it("is a .monaco-editor so theme variables and widget styles still apply", () => {
    const { doc } = fakeDocument();
    expect(createOverflowHost(doc).className.split(" ")).toContain("monaco-editor");
  });

  it("is a zero-size origin layer above the dock and below modals", () => {
    const { doc } = fakeDocument();
    const s = createOverflowHost(doc).style;
    expect([s.position, s.top, s.left, s.width, s.height]).toEqual(["absolute", "0", "0", "0", "0"]);
    expect(Number(s.zIndex)).toBeGreaterThan(0);
    expect(Number(s.zIndex)).toBeLessThan(40);
  });
});

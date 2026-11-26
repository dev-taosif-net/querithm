// The tier-2 cache key's hash of the text before the word (completion audit 2026-11-25, M16): the value of a full hash,
// with the work of a keystroke bounded by what changed, not by the script's length.
import { describe, expect, it } from "vitest";
import { PREFIX_STEP, TAIL_WINDOW, prefixHash, prefixHashStats, tailHash, textHash } from "../prefixHash";

describe("prefixHash", () => {
  it("is the hash of the whole text before the word, across edits anywhere in a long script", () => {
    const owner = {};
    let text = Array.from({ length: 3000 }, (_, i) => `SELECT c${i} FROM t${i};`).join("\n");
    let seed = 3;
    const rnd = (n: number) => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n; };
    for (let e = 0; e < 300; e++) {
      const at = rnd(text.length + 1);
      const del = rnd(3) === 0 ? rnd(9000) : 0;
      text = text.slice(0, at) + (rnd(2) ? "x" : "\n") + text.slice(at + del);
      for (const end of [0, at, Math.min(text.length, at + 1), rnd(text.length + 1), text.length]) {
        expect(prefixHash(owner, text, end), `edit ${e}, end ${end}`).toBe(textHash(text.slice(0, end)));
      }
    }
  });

  it("keeps one state per owner: another model's text never serves this one's", () => {
    const a = {}, b = {};
    const t1 = "SELECT 1 FROM x".repeat(1000);
    const t2 = "SELECT 2 FROM y".repeat(1000);
    expect(prefixHash(a, t1, 9000)).toBe(textHash(t1.slice(0, 9000)));
    expect(prefixHash(b, t2, 9000)).toBe(textHash(t2.slice(0, 9000)));
    expect(prefixHash(a, t1, 9000)).toBe(textHash(t1.slice(0, 9000)));
  });

  it("a keystroke in the middle of a 1.3 MB script hashes at most a checkpoint's span again, not the text before it", () => {
    const owner = {};
    let text = "  SELECT c.CustomerId, c.FullName, r.Total FROM dbo.Customers c JOIN recent r ON r.CustomerId = c.CustomerId\n".repeat(12000);
    let at = text.length >> 1;
    prefixHash(owner, text, at);
    let most = 0;
    for (let r = 0; r < 100; r++) {
      text = text.slice(0, at) + "x" + text.slice(at);
      at++;
      const before = prefixHashStats.hashed;
      prefixHash(owner, text, at);
      most = Math.max(most, prefixHashStats.hashed - before);
    }
    // Before: every keystroke hashed all `at` (~650,000) characters before the word, twice.
    expect(most).toBeLessThanOrEqual(2 * PREFIX_STEP);
  });
it("an end past the text is the text's end (the text shrank while a request was in flight)", () => {
    const owner = {};
    expect(prefixHash(owner, "abc", 10)).toBe(textHash("abc"));
    const long = "x".repeat(5000);
    expect(prefixHash(owner, long, 9000)).toBe(textHash(long));
    const grown = long + "y".repeat(5000);
    expect(prefixHash(owner, grown, 9000)).toBe(textHash(grown.slice(0, 9000)));
  });
});

describe("tailHash", () => {
  it("is textHash for a short tail, and hashes two bounded windows of a long one", () => {
    expect(tailHash("abcdef", 1, 4)).toBe(textHash("bcd"));
    expect(tailHash("abc", 2, 2)).toBe("");
    const text = "(1, 'a'),".repeat(120_000); // a 1.3 MB INSERT … VALUES after the cursor
    const before = prefixHashStats.hashed;
    const h = tailHash(text, 0, text.length);
    expect(prefixHashStats.hashed - before).toBeLessThanOrEqual(2 * TAIL_WINDOW);
    // An edit at either end of the tail, or one that changes its length, gives another key (FROM aliases sit there).
    expect(tailHash("x" + text.slice(1), 0, text.length)).not.toBe(h);
    expect(tailHash(text.slice(0, -1) + "x", 0, text.length)).not.toBe(h);
    expect(tailHash(text + " ", 0, text.length + 1)).not.toBe(h);
  });
});

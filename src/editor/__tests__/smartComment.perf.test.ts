import { describe, expect, it } from "vitest";
import { clearTokenCache, getStream } from "../completion/lexer";
import { applyTextEdits, coalesceEditsForRange, computeCommentEdits } from "../smartComment";

const BLOCK = `SELECT o.OrderId, o.CustomerId, o.Total -- trailing note\nFROM dbo.Orders o\n/* multi-line\n   comment */\nWHERE o.Active = 1 AND o.Status = 'open';\n\n`;

function bigFixture(repeats = 2000) {
  // 2000 repeats => ~12,000 lines, ~285 KB, 4,000 comments — same scale as completion/__tests__/perf.test.ts.
  const text = BLOCK.repeat(repeats);
  const mid = text.indexOf("FROM dbo.Orders", Math.floor(text.length / 2));
  return { text, mid };
}

const median = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
const p95 = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length * 0.95)];
const STRICT = process.env.QUERITHM_PERF_STRICT === "1";
const SLACK = STRICT ? 1 : 4;

describe("smartComment performance (~12,000-line SQL script)", () => {
  it("stays under budget on rapid 1-line toggle, 1,000-line toggle, statement toggle, section insert, and strip", () => {
    clearTokenCache();
    const { text, mid } = bigFixture(2000);

    // Cold section insert should never trigger full-document lexing.
    const tSec = performance.now();
    computeCommentEdits(text, [{ start: mid, end: mid }], "tsql", "section");
    const sectionColdMs = performance.now() - tSec;

    // Warm the token stream once (as the editor's highlighting/completion does).
    getStream(text, "tsql");

    // Repeated rapid Ctrl+/ toggling the same line (each edit produces a new text string, like Monaco).
    let cur = text;
    const rapidTimes: number[] = [];
    for (let i = 0; i < 30; i++) {
      const t0 = performance.now();
      const res = computeCommentEdits(cur, [{ start: mid, end: mid }], "tsql", "toggle", { advanceCursor: true });
      rapidTimes.push(performance.now() - t0);
      cur = applyTextEdits(cur, res.edits);
    }

    // Multi-line selection (~1,000 lines) + range coalescing.
    getStream(text, "tsql");
    const selEnd = Math.min(text.length, mid + 24_000);
    const multiTimes: number[] = [];
    for (let i = 0; i < 15; i++) {
      const t0 = performance.now();
      const res = computeCommentEdits(text, [{ start: mid, end: selEnd }], "tsql", "toggle");
      coalesceEditsForRange(text, res.edits);
      multiTimes.push(performance.now() - t0);
    }

    // Statement toggle.
    const stmtTimes: number[] = [];
    for (let i = 0; i < 15; i++) {
      const t0 = performance.now();
      computeCommentEdits(text, [{ start: mid, end: mid }], "tsql", "statement");
      stmtTimes.push(performance.now() - t0);
    }

    // Strip small selection vs strip all 4,000 comments.
    const stripSelTimes: number[] = [];
    for (let i = 0; i < 15; i++) {
      const t0 = performance.now();
      computeCommentEdits(text, [{ start: mid, end: mid + 250 }], "tsql", "strip");
      stripSelTimes.push(performance.now() - t0);
    }
    const stripAllTimes: number[] = [];
    for (let i = 0; i < 5; i++) {
      const t0 = performance.now();
      computeCommentEdits(text, [{ start: 0, end: 0 }], "tsql", "strip");
      stripAllTimes.push(performance.now() - t0);
    }

    const rapidMed = median(rapidTimes);
    const multiMed = median(multiTimes);
    const stmtMed = median(stmtTimes);
    const stripSelMed = median(stripSelTimes);
    const stripAllMed = median(stripAllTimes);

    console.log(
      [
        `12k-line fixture (${(text.length / 1024).toFixed(0)} KB, 4,000 comments):`,
        `  cold section insert:   ${sectionColdMs.toFixed(2)} ms`,
        `  rapid 1-line toggle:   median ${rapidMed.toFixed(2)} ms, p95 ${p95(rapidTimes).toFixed(2)} ms`,
        `  1000-line toggle+coal: median ${multiMed.toFixed(2)} ms, p95 ${p95(multiTimes).toFixed(2)} ms`,
        `  statement toggle:      median ${stmtMed.toFixed(2)} ms, p95 ${p95(stmtTimes).toFixed(2)} ms`,
        `  strip 5-line sel:      median ${stripSelMed.toFixed(2)} ms, p95 ${p95(stripSelTimes).toFixed(2)} ms`,
        `  strip 4000 comments:   median ${stripAllMed.toFixed(2)} ms, p95 ${p95(stripAllTimes).toFixed(2)} ms`,
      ].join("\n"),
    );

    expect(sectionColdMs).toBeLessThan(5 * SLACK);
    expect(rapidMed).toBeLessThan(4 * SLACK);
    expect(multiMed).toBeLessThan(5 * SLACK);
    expect(stmtMed).toBeLessThan(4 * SLACK);
    expect(stripSelMed).toBeLessThan(2 * SLACK);
    expect(stripAllMed).toBeLessThan(15 * SLACK);
  });
});

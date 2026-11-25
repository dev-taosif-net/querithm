// §14 #38, the provider's whole path (audit 2026-11-25): perf.test.ts times semanticSpans() alone; Monaco calls the range
// provider, which also builds the document text (model.getValue()), finds the offsets and encodes the spans. Timed here on
// Monaco's own text buffer (the piece tree TextModel.getValue() reads, getValueInRange over the full range), once as a
// file just opened (one piece: getValue() hands back the loaded string) and once as an edited one (many pieces, joined
// into a new string per call, which the token cache then compares in full).
import { describe, expect, it, vi } from "vitest";
// @ts-expect-error untyped internal module of monaco-editor
import { PieceTreeTextBufferBuilder } from "monaco-editor/esm/vs/editor/common/model/pieceTreeTextBuffer/pieceTreeTextBufferBuilder.js";
// @ts-expect-error untyped internal module of monaco-editor
import { Range } from "monaco-editor/esm/vs/editor/common/core/range.js";
import type * as Monaco from "monaco-editor";
import { IDX } from "../completion/__tests__/fixture";
import { clearTokenCache } from "../completion/lexer";

vi.mock("../monaco", async () => ({ monaco: (await import("./fakeMonaco")).fake.monaco }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
const { semanticTokensProvider, setSemanticEnabled } = await import("../semanticTokens");

const SLACK = process.env.QUERITHM_PERF_STRICT === "1" ? 1 : 3;
const pct = (xs: number[], q: number) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length * q)];

/** ~12,000 lines of CTEs, schema-qualified tables and EXEC targets in one procedure. */
function script(): string {
  const parts = ["CREATE OR ALTER PROCEDURE dbo.Big AS\nBEGIN\n"];
  for (let i = 0; i < 2400; i++) {
    parts.push(`  ;WITH recent${i} (CustomerId, Total) AS (
    SELECT o.CustomerId, SUM(o.TotalAmount) FROM dbo.Orders o WITH (NOLOCK) GROUP BY o.CustomerId
  )
  SELECT c.CustomerId, r.Total FROM dbo.Customers c JOIN recent${i} r ON r.CustomerId = c.CustomerId;
  EXEC dbo.usp_GetCustomerOrders @CustomerId = ${i};
`);
  }
  return parts.join("") + "END\n";
}

/** A model over Monaco's piece tree, built from `pieces` chunks (an edited document is one piece per edit). */
function modelOf(text: string, pieces: number) {
  const b = new PieceTreeTextBufferBuilder();
  const size = Math.ceil(text.length / pieces);
  for (let i = 0; i < text.length; i += size) b.acceptChunk(text.slice(i, i + size));
  const buf = b.finish(true).create(1 /* LF */).textBuffer;
  const lines: number = buf.getLineCount();
  return {
    getValue: (): string => buf.getValueInRange(new Range(1, 1, lines, buf.getLineLength(lines) + 1), 0),
    getOffsetAt: (p: { lineNumber: number; column: number }): number => buf.getOffsetAt(p.lineNumber, p.column),
    getPositionAt: (o: number) => buf.getPositionAt(o),
    getLineCount: () => lines,
  } as unknown as Monaco.editor.ITextModel;
}

describe("performance: the semantic tokens provider", () => {
  it.each([
    ["a file just opened (one piece)", 1],
    ["an edited file (5,000 pieces)", 5000],
  ])("a 60-line viewport of a ~12,000-line procedure: %s", (label, pieces) => {
    const text = script();
    const model = modelOf(text, pieces);
    expect(model.getValue()).toBe(text);
    expect(model.getLineCount()).toBeGreaterThanOrEqual(12000);
    const provider = semanticTokensProvider(() => ({ dialect: "tsql", schema: IDX }));
    setSemanticEnabled(true);
    try {
      clearTokenCache();
      const mid = model.getLineCount() >> 1;
      const ask = (top: number) =>
        provider.provideDocumentRangeSemanticTokens(model, { startLineNumber: top, startColumn: 1, endLineNumber: top + 60, endColumn: 1 } as Monaco.Range, {} as Monaco.CancellationToken) as { data: Uint32Array };
      ask(1); // the editor's highlighting and completion have lexed the text before the first request
      const times: number[] = [];
      for (let r = 0; r < 200; r++) {
        const s = performance.now();
        const out = ask(mid - 2000 + r * 20);
        times.push(performance.now() - s);
        expect(out.data.length).toBeGreaterThan(0);
      }
      const p50 = pct(times, 0.5), p95 = pct(times, 0.95);
      console.log(`semantic provider, ${label}: p50 ${p50.toFixed(3)} ms, p95 ${p95.toFixed(3)} ms`);
      expect(p95).toBeLessThan(4 * SLACK);
    } finally {
      setSemanticEnabled(false);
    }
  });
});

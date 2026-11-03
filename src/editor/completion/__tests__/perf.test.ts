import { describe, it, expect } from "vitest";
import { IDX } from "./fixture";
import { complete } from "../complete";
import { clearTokenCache, getStream, tokenize } from "../lexer";

const BLOCK = (i: number) => `
  DECLARE @v${i} int = ${i};
  -- block ${i}: refresh customer totals
  ;WITH recent${i} (CustomerId, Total) AS (
    SELECT o.CustomerId, SUM(o.TotalAmount)
    FROM dbo.Orders o WITH (NOLOCK)
    WHERE o.OrderDate >= DATEADD(day, -${i}, SYSDATETIME())
    GROUP BY o.CustomerId
  )
  SELECT c.CustomerId, c.FullName, r.Total,
         CASE WHEN r.Total > 1000 THEN 'gold' ELSE 'std' END AS Tier
  FROM dbo.Customers c
  JOIN recent${i} r ON r.CustomerId = c.CustomerId
  WHERE c.Email LIKE N'%@example.com';
  IF @v${i} > 10
  BEGIN
    UPDATE dbo.Orders SET Status = 'checked' WHERE CustomerId = @v${i}
  END
  MERGE dbo.OrderItems AS t
  USING (SELECT OrderId, ProductId FROM dbo.OrderItems) AS s ON t.OrderId = s.OrderId
  WHEN MATCHED THEN UPDATE SET Quantity = t.Quantity + 1;
  /* trailing
     comment */
  EXEC dbo.usp_GetCustomerOrders @CustomerId = @v${i};
`;

function bigScript(): { text: string; cursor: number } {
  const parts: string[] = ["CREATE OR ALTER PROCEDURE dbo.Big @CustomerId int AS\nBEGIN\n"];
  const lines = BLOCK(0).split("\n").length - 1;
  const n = Math.ceil(12000 / lines);
  const mid = Math.floor(n / 2);
  let cursor = -1;
  for (let i = 0; i < n; i++) {
    if (i === mid) {
      const head = parts.join("") + "  SELECT o.";
      cursor = head.length;
      parts.push("  SELECT o. FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId\n");
    }
    parts.push(BLOCK(i));
  }
  parts.push("END\nGO\n");
  return { text: parts.join(""), cursor };
}

const median = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
// The 5 ms budget is benchmark metric #4, measured in isolation by scripts/bench/run.mjs. Inside the full suite on a
// busy machine the test gets slack (QUERITHM_PERF_STRICT=1 removes it); a real regression (re-tokenizing the whole
// text on a keystroke) costs far more than the slack allows.
const SLACK = process.env.QUERITHM_PERF_STRICT === "1" ? 1 : 3;
const p95 = (xs: number[]) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length * 0.95)];

describe("performance", () => {
  it("tier-1 completion on a ~12,000-line procedure while typing", () => {
    clearTokenCache();
    const { text, cursor } = bigScript();
    expect(text.split("\n").length).toBeGreaterThanOrEqual(12000);
    // warm-up (first full tokenization)
    let t = text;
    let off = cursor;
    complete({ text: t, offset: off, dialect: "tsql", schema: IDX });
    const times: number[] = [];
    const word = "TotalAmount";
    for (let r = 0; r < 40; r++) {
      // simulate typing: each keystroke produces a new text string
      const ch = word[r % word.length];
      t = t.slice(0, off) + ch + t.slice(off);
      off++;
      const s = performance.now();
      const res = complete({ text: t, offset: off, dialect: "tsql", schema: IDX });
      times.push(performance.now() - s);
      expect(res.context.qualifier).toBe("o");
      if (r % word.length === word.length - 1) {
        // erase the word again (backspaces collapsed into one edit)
        t = t.slice(0, off - word.length) + t.slice(off);
        off -= word.length;
      }
    }
    const med = median(times), p = p95(times);
    console.log(`tier-1 completion, ${text.split("\n").length} lines: median ${med.toFixed(3)} ms, p95 ${p.toFixed(3)} ms`);
    expect(med).toBeLessThan(5 * SLACK);
  });

  it("incremental token cache matches a fresh tokenization after edits", () => {
    clearTokenCache();
    let { text } = bigScript();
    text = text.slice(0, 60000);
    getStream(text, "tsql");
    const edits: [number, number, string][] = [
      [1000, 0, "x"], [1000, 1, ""], [5000, 0, "'"], [5001, 0, "'"], [8000, 0, "/*"], [20000, 0, "*/"],
      [12000, 0, "\nGO\n"], [12001, 2, "GOX"], [300, 0, "--"], [30000, 5, ""], [40000, 0, "[abc"], [40000, 0, "N'"],
    ];
    for (const [pos, del, ins] of edits) {
      text = text.slice(0, pos) + ins + text.slice(pos + del);
      const s = getStream(text, "tsql");
      const fresh = tokenize(text, "tsql");
      const got = [...s.code, ...s.comments].sort((a, b) => a.start - b.start).map((x) => `${x.kind}:${x.start}:${x.end}`);
      expect(got).toEqual(fresh.map((x) => `${x.kind}:${x.start}:${x.end}`));
      const marks = fresh.filter((x) => x.kind === "go" || ((x.kind === "keyword" || x.kind === "ident") && ["DECLARE", "CREATE", "ALTER"].includes(x.upper)));
      expect(s.marks.map((x) => `${x.kind}:${x.start}`)).toEqual(marks.map((x) => `${x.kind}:${x.start}`));
    }
  });
});

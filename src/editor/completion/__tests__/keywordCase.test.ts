import { describe, it, expect } from "vitest";
import { keywordCaseEdit } from "../keywordCase";
import type { Dialect, KeywordCase } from "../../../ipc/contract";

/** `|` marks where the boundary character was just typed; returns the text after the edit (or unchanged). */
function typed(sql: string, mode: KeywordCase = "upper", dialect: Dialect = "tsql"): string {
  const at = sql.indexOf("|");
  const text = sql.slice(0, at) + sql.slice(at + 1);
  const e = keywordCaseEdit(text, dialect, at, mode);
  return e ? text.slice(0, e.start) + e.text + text.slice(e.end) : text;
}

describe("keywordCaseEdit", () => {
  it("uppercases the keyword just completed", () => {
    expect(typed("SELECT * from| ")).toBe("SELECT * FROM ");
    expect(typed("select| ")).toBe("SELECT ");
    expect(typed("SELECT COUNT(*) FROM t wHeRe|\n")).toBe("SELECT COUNT(*) FROM t WHERE\n");
    expect(typed("select * from t where id in|(")).toBe("select * from t where id IN(");
  });
  it("lowercases in lower mode, leaves alone in preserve mode", () => {
    expect(typed("SELECT * FROM| ", "lower")).toBe("SELECT * from ");
    expect(typed("SeLeCt| ", "preserve")).toBe("SeLeCt ");
  });
  it("only touches the word ending at the cursor", () => {
    expect(typed("select * from| t where x = 1")).toBe("select * FROM t where x = 1");
    expect(typed("select * fr|om")).toBe("select * from");
  });
  it("ignores identifiers, strings, comments, quoted names, variables and member access", () => {
    expect(typed("SELECT name| ")).toBe("SELECT name ");
    expect(typed("SELECT 'from| ")).toBe("SELECT 'from ");
    expect(typed("SELECT 1 -- from| ")).toBe("SELECT 1 -- from ");
    expect(typed("SELECT 1 /* select| */")).toBe("SELECT 1 /* select */");
    expect(typed("SELECT [from]| ")).toBe("SELECT [from] ");
    expect(typed("SELECT @from| ")).toBe("SELECT @from ");
    expect(typed("SELECT t.key| ")).toBe("SELECT t.key ");
  });
  it("uses the dialect's reserved words", () => {
    expect(typed("select * from t limit| ", "upper", "pg")).toBe("select * from t LIMIT ");
    expect(typed("select * from t limit| ", "upper", "tsql")).toBe("select * from t limit ");
    expect(typed("select \"from\"| ", "upper", "pg")).toBe("select \"from\" ");
  });
});

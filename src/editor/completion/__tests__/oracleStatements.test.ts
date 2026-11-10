// D4 S06: the webview's Oracle splitter and bind scan answer what the sidecar's do (one fixture both suites read:
// OracleEditorSupportTests on the .NET side), and the editor features built on them.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it } from "vitest";
import { bindIdentity, oracleBinds, oracleStatementAt, oracleStatements } from "../oracleStatements";
import { statementBoundsAt, undeclaredParameters } from "../sqlText";
import { clearTokenCache } from "../lexer";
import { reservedWords } from "../keywords";
import { quoteIdentifier } from "../quoteIdentifier";

interface Case { name: string; text: string; binds: string[]; statements: { kind: string; start: number; end: number }[] }
const cases = JSON.parse(readFileSync(fileURLToPath(new URL("./fixtures/oracle-statements.json", import.meta.url)), "utf8")) as Case[];

beforeEach(() => clearTokenCache());

describe("Oracle statements and binds: parity with the sidecar (shared fixture)", () => {
  it("the fixture has cases, each with the sidecar's answer", () => {
    expect(cases.length).toBeGreaterThanOrEqual(19);
    for (const c of cases) expect(Array.isArray(c.binds) && Array.isArray(c.statements), c.name).toBe(true);
  });

  it.each(cases.map((c) => [c.name, c] as const))("%s", (_, c) => {
    expect(oracleStatements(c.text).map(({ kind, start, end }) => ({ kind, start, end }))).toEqual(c.statements);
    expect(oracleBinds(c.text).map(bindIdentity)).toEqual(c.binds);
  });
});

describe("Oracle bind names", () => {
  it("are spelled as first written and deduplicated as the server folds them", () => {
    expect(oracleBinds("SELECT :emp, :EMP, :Emp, :\"Emp\" FROM dual")).toEqual([":emp", ':"Emp"']);
    expect(bindIdentity(":emp")).toBe("EMP");
    expect(bindIdentity(':"Emp"')).toBe("Emp");
    expect(bindIdentity(":1")).toBe("1");
    expect(bindIdentity(':"open')).toBe("open");
  });

  it("the run prompts for them (undeclaredParameters), and never for :NEW in a trigger or a SQL*Plus line", () => {
    expect(undeclaredParameters("SELECT * FROM emp WHERE deptno = :dept AND sal > :1", "oracle")).toEqual([":dept", ":1"]);
    expect(undeclaredParameters("CREATE TRIGGER t BEFORE INSERT ON x FOR EACH ROW BEGIN :NEW.id := 1; END;\n/", "oracle")).toEqual([]);
    expect(undeclaredParameters("EXEC p(:a)", "oracle")).toEqual([]);
    expect(undeclaredParameters("BEGIN :r := 1; x := 2; END;", "oracle")).toEqual([":r"]);
  });
});

describe("Oracle: Execute current statement", () => {
  const script = "SELECT 1 FROM dual;\n\nBEGIN\n  NULL;\n  NULL;\nEND;\n/\nSELECT 2 FROM dual; SELECT 3 FROM dual";

  it("a cursor inside a PL/SQL block runs the whole block, END; included, never one of its statements", () => {
    const at = script.indexOf("NULL;\n  NULL") + 2;
    const r = statementBoundsAt(script, at, "oracle");
    expect(script.slice(r.start, r.end)).toBe("BEGIN\n  NULL;\n  NULL;\nEND;");
  });

  it("two statements on one line are two; the cursor between them on the line picks the later one", () => {
    const second = script.indexOf("SELECT 3");
    expect(script.slice(...Object.values(statementBoundsAt(script, second + 3, "oracle")) as [number, number])).toBe("SELECT 3 FROM dual");
    const gap = script.indexOf("; SELECT 3") + 1;
    expect(script.slice(...Object.values(statementBoundsAt(script, gap, "oracle")) as [number, number])).toBe("SELECT 2 FROM dual");
    expect(oracleStatementAt(script, gap + 1)).toEqual({ start: second, end: second + "SELECT 3 FROM dual".length });
  });

  it("on a blank line after a statement, the statement before it; on an empty script, nothing", () => {
    const blank = script.indexOf("\n\nBEGIN") + 1;
    expect(script.slice(...Object.values(statementBoundsAt(script, blank, "oracle")) as [number, number])).toBe("SELECT 1 FROM dual");
    expect(statementBoundsAt("", 0, "oracle")).toEqual({ start: 0, end: 0 });
    expect(statementBoundsAt("  -- only a comment", 5, "oracle")).toEqual({ start: 5, end: 5 });
  });
});

describe("Oracle reserved words: the same list as the sidecar's OracleKeywords", () => {
  const cs = readFileSync(fileURLToPath(new URL("../../../../sidecar/Querithm.Intelligence.Oracle/OracleKeywords.cs", import.meta.url)), "utf8");
  // The two HashSet initialisers' string literals (a C# source file, not SQL).
  const words = [...cs.matchAll(/"([A-Z_$#0-9]+)"/g)].map((m) => m[1]);

  it("holds every word the sidecar reserves, and no other", () => {
    expect(words.length).toBeGreaterThan(100);
    expect([...reservedWords("oracle")].sort()).toEqual([...new Set(words)].sort());
  });

  it("so a catalog name that is one of them is quoted, a plain upper-case one is not", () => {
    for (const w of ["TYPE", "LEVEL", "ROWNUM", "LOOP", "RECORD"]) expect(quoteIdentifier(w, "oracle")).toBe(`"${w}"`);
    for (const w of ["COMMIT", "MERGE", "EMP", "ORDER_LINES"]) expect(quoteIdentifier(w, "oracle")).toBe(w);
    expect(quoteIdentifier("emp", "oracle")).toBe('"emp"');
  });
});

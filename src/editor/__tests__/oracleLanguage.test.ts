// D4 S06: the Oracle editor language, run through Monaco's own Monarch compiler and tokenizer (not a re-implementation),
// for the forms the sidecar's OracleTokenizer reads: q'…' quoting, hints, binds, database links, labels and / lines.
import { describe, expect, it } from "vitest";
import { LANGUAGES, oracleLanguage, oracleLanguageConfiguration, ORACLE_KEYWORDS } from "../languages";
import { reservedWords } from "../completion/keywords";
import { monarch, tokenLines } from "./monarch";

/** Each line's tokens as "text→type" (type without the .oracle postfix), the tokenizer state carried across lines. */
function highlight(text: string): string[][] {
  return tokenLines(monarch("oracle", oracleLanguage()).tokenizer, text).map((line) =>
    line
      .map(([t, type]) => `${t}→${type.replace(/\.oracle$/, "")}`)
      .filter((s) => !s.endsWith("→white") && !s.startsWith(" →")));
}
const types = (line: string[]) => line.map((s) => s.slice(s.lastIndexOf("→") + 1));

describe("Oracle highlighting (Monaco's Monarch)", () => {
  it("q'…' with a bracket pair, any other delimiter, nq'…' and a '.' delimiter is one string, ; and ' inside", () => {
    for (const lit of ["q'[it's; :x]'", "q'{a}'", "q'(a)'", "q'<a>'", "q'!it's!'", "nq'#x#'", "q'.a'b.'"]) {
      const [line] = highlight(`SELECT ${lit} FROM dual`);
      expect(line[0], lit).toBe("SELECT→keyword");
      expect(line.at(-2), lit).toBe("FROM→keyword");
      // Everything between SELECT and FROM is string (the quote marks and the body).
      expect(types(line.slice(1, -2)).every((x) => x.startsWith("string")), `${lit}: ${line.join(" ")}`).toBe(true);
    }
  });

  it("a q'…' literal spans lines, and a / line inside it is text, not a terminator", () => {
    const lines = highlight("SELECT q'[a\n/\nb]' FROM dual\n/");
    expect(types(lines[1])).toEqual(["string"]);
    expect(lines[2][0]).toMatch(/→string/);
    expect(lines[2]).toContain("FROM→keyword");
    expect(lines[3]).toEqual(["/→keyword.batch"]);
  });

  it("a / alone on its line ends a unit; / elsewhere is division", () => {
    expect(highlight("END;\n  /  ")[1]).toEqual(["  /  →keyword.batch"]);
    expect(highlight("SELECT 4 / 2 FROM dual")[0]).toContain("/→operator");
  });

  it("hints are comments of their own; binds are bind variables, := is an operator; @link and <<label>> are names", () => {
    const [line] = highlight(`SELECT /*+ FULL(e) */ :a, :1, :"Mx" FROM emp@hq.example e --+ ordered`);
    expect(line.some((x) => x.startsWith("/*+ FULL(e) */") && x.endsWith("→comment.hint")), line.join(" ")).toBe(true);
    expect(line).toContain(":a→variable.bind");
    expect(line).toContain(":1→variable.bind");
    expect(line).toContain(':"Mx"→variable.bind');
    expect(line).toContain("@hq.example→identifier.link");
    expect(line.at(-1)).toBe("--+ ordered→comment.hint");
    expect(highlight("x := 1;")[0]).toContain(":=→operator");
    expect(highlight("<<outer>> BEGIN")[0][0]).toBe("<<outer>>→identifier.label");
  });

  it("PL/SQL words, types and functions; a plain block comment does not nest", () => {
    const [line] = highlight("DECLARE n PLS_INTEGER := NVL(x, 0); BEGIN NULL; EXCEPTION WHEN OTHERS THEN RAISE; END;");
    for (const k of ["DECLARE→keyword", "PLS_INTEGER→type", "NVL→predefined", "EXCEPTION→keyword", "OTHERS→keyword", "RAISE→keyword", "NULL→keyword.constant"]) expect(line).toContain(k);
    expect(types(highlight("/* a /* b */ c")[0]).at(-1)).toBe("identifier");
  });

  it("PL/SQL TRUE / FALSE are constants but not reserved words; NULL is both; AND … IS are word operators", () => {
    const [line] = highlight("b BOOLEAN := TRUE AND NOT FALSE OR x IS NULL AND y IN (1) AND z LIKE 'a%' AND n BETWEEN 1 AND 2;");
    for (const k of ["TRUE→identifier.constant", "FALSE→identifier.constant", "NULL→keyword.constant", "BOOLEAN→type"]) expect(line).toContain(k);
    for (const k of ["AND", "NOT", "OR", "IS", "IN", "LIKE", "BETWEEN"]) expect(line).toContain(`${k}→keyword.operator`);
  });

  it("every reserved word completion quotes is highlighted as a keyword", () => {
    // NULL (keyword.constant) and AND … IS (keyword.operator) are in ORACLE_KEYWORDS too: a keyword's look without Default.
    const highlighted = new Set(ORACLE_KEYWORDS.map((k) => k.toUpperCase()));
    expect([...reservedWords("oracle")].filter((w) => !highlighted.has(w))).toEqual([]);
  });

  it("is the oracle language's tokens and configuration; only ( ) pair", () => {
    expect(LANGUAGES.oracle.tokens).toBe(oracleLanguage);
    expect(LANGUAGES.oracle.configuration).toBe(oracleLanguageConfiguration);
    expect(oracleLanguageConfiguration.brackets).toEqual([["(", ")"]]);
  });
});

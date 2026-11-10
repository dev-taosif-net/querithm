// D4 S06: the Oracle editor language, run through Monaco's own Monarch compiler and tokenizer (not a re-implementation),
// for the forms the sidecar's OracleTokenizer reads: q'…' quoting, hints, binds, database links, labels and / lines.
import { describe, expect, it } from "vitest";
// @ts-expect-error untyped internal module of monaco-editor
import { compile } from "monaco-editor/esm/vs/editor/standalone/common/monarch/monarchCompile.js";
// @ts-expect-error untyped internal module of monaco-editor
import { MonarchTokenizer } from "monaco-editor/esm/vs/editor/standalone/common/monarch/monarchLexer.js";
import { LANGUAGES, oracleLanguage, oracleLanguageConfiguration, ORACLE_KEYWORDS } from "../languages";
import { reservedWords } from "../completion/keywords";

type Tok = { offset: number; type: string };
const noop = { dispose() {} };

/** Each line's tokens as "text→type" (type without the .oracle postfix), the tokenizer state carried across lines. */
function highlight(text: string): string[][] {
  const lexer = compile("oracle", oracleLanguage());
  const t = new MonarchTokenizer(
    { requestRichLanguageFeatures() {}, isRegisteredLanguageId: () => false },
    { onDidColorThemeChange: () => noop },
    "oracle",
    lexer,
    { getValue: () => 20_000, onDidChangeConfiguration: () => noop },
  );
  let state = t.getInitialState();
  return text.split("\n").map((line) => {
    const r = t.tokenize(line, true, state);
    state = r.endState;
    const toks = r.tokens as Tok[];
    return toks
      .map((k, i) => `${line.slice(k.offset, toks[i + 1]?.offset ?? line.length)}→${k.type.replace(/\.oracle$/, "")}`)
      .filter((s) => !s.endsWith("→white") && !s.startsWith(" →"));
  });
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

  it("hints are comments of their own; binds are variables, := is an operator; @link and <<label>> are names", () => {
    const [line] = highlight(`SELECT /*+ FULL(e) */ :a, :1, :"Mx" FROM emp@hq.example e --+ ordered`);
    expect(line.some((x) => x.startsWith("/*+ FULL(e) */") && x.endsWith("→comment.hint")), line.join(" ")).toBe(true);
    expect(line).toContain(":a→variable");
    expect(line).toContain(":1→variable");
    expect(line).toContain(':"Mx"→variable');
    expect(line).toContain("@hq.example→identifier.link");
    expect(line.at(-1)).toBe("--+ ordered→comment.hint");
    expect(highlight("x := 1;")[0]).toContain(":=→operator");
    expect(highlight("<<outer>> BEGIN")[0][0]).toBe("<<outer>>→identifier.label");
  });

  it("PL/SQL words, types and functions; a plain block comment does not nest", () => {
    const [line] = highlight("DECLARE n PLS_INTEGER := NVL(x, 0); BEGIN NULL; EXCEPTION WHEN OTHERS THEN RAISE; END;");
    for (const k of ["DECLARE→keyword", "PLS_INTEGER→type", "NVL→predefined", "EXCEPTION→keyword", "OTHERS→keyword", "RAISE→keyword"]) expect(line).toContain(k);
    expect(types(highlight("/* a /* b */ c")[0]).at(-1)).toBe("identifier");
  });

  it("every reserved word completion quotes is highlighted as a keyword", () => {
    const highlighted = new Set(ORACLE_KEYWORDS.map((k) => k.toUpperCase()));
    expect([...reservedWords("oracle")].filter((w) => !highlighted.has(w))).toEqual([]);
  });

  it("is the oracle language's tokens and configuration; only ( ) pair", () => {
    expect(LANGUAGES.oracle.tokens).toBe(oracleLanguage);
    expect(LANGUAGES.oracle.configuration).toBe(oracleLanguageConfiguration);
    expect(oracleLanguageConfiguration.brackets).toEqual([["(", ")"]]);
  });
});

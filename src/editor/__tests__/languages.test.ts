// The lexical token kinds of plans/app-themes §5.2, per dialect, through Monaco's own Monarch compiler and tokenizer.
// T1 (highlightGolden.test.ts) proves they look as before under the six syntax themes; these tests pin the kinds.
import { describe, expect, it } from "vitest";
import type * as Monaco from "monaco-editor/esm/vs/editor/editor.api";
import type { Dialect } from "../../ipc/contract";
import { DIALECT_LIST, DIALECTS } from "../../engines";
import { LANGUAGES, LEXICAL_KINDS } from "../languages";
import { monarch, tokenLines } from "./monarch";

/** One line's tokens as "text→type" without the dialect postfix, whitespace dropped. */
function highlight(d: Dialect, text: string): string[] {
  const lang = LANGUAGES[d].tokens();
  const postfix = lang.tokenPostfix!;
  return tokenLines(monarch(DIALECTS[d].language, lang).tokenizer, text)
    .flat()
    .filter(([t, type]) => t.trim() !== "" && !type.startsWith("white"))
    .map(([t, type]) => `${t}→${type.endsWith(postfix) ? type.slice(0, -postfix.length) : type}`);
}
type WordLists = Record<"keywords" | "typeKeywords" | "builtinFunctions" | "constantKeywords" | "wordOperators" | "constantIdents", string[]>;
const lists = (d: Dialect) => LANGUAGES[d].tokens() as Monaco.languages.IMonarchLanguage & WordLists;

describe("lexical kinds: the word lists (T9)", () => {
  it.each(DIALECT_LIST)("%s: word operators and constant keywords are keywords; constant idents are not", (d) => {
    const l = lists(d);
    const keywords = new Set(l.keywords);
    expect(l.wordOperators.filter((w) => !keywords.has(w)), "wordOperators ⊆ keywords").toEqual([]);
    expect(l.constantKeywords.filter((w) => !keywords.has(w)), "constantKeywords ⊆ keywords").toEqual([]);
    expect(l.constantIdents.filter((w) => keywords.has(w)), "constantIdents ∩ keywords = ∅").toEqual([]);
    // Nor a type or a function: the word was a plain identifier before, which is what its legacy role says.
    expect(l.constantIdents.filter((w) => l.typeKeywords.includes(w) || l.builtinFunctions.includes(w))).toEqual([]);
    expect(l.wordOperators).toEqual(expect.arrayContaining(["and", "or", "not", "in", "like", "between", "is"]));
  });

  it("follow the §5.2 table", () => {
    expect(LEXICAL_KINDS.tsql).toEqual({ constantKeywords: ["NULL"], constantIdents: [], wordOperators: ["AND", "OR", "NOT", "IN", "LIKE", "BETWEEN", "IS"] });
    expect(LEXICAL_KINDS.pg.constantKeywords).toEqual(["TRUE", "FALSE", "NULL"]);
    expect(LEXICAL_KINDS.pg.wordOperators.slice(7)).toEqual(["ILIKE", "SIMILAR", "ISNULL", "NOTNULL"]);
    expect(LEXICAL_KINDS.sqlite).toMatchObject({ constantKeywords: ["NULL"], constantIdents: ["TRUE", "FALSE"] });
    expect(LEXICAL_KINDS.sqlite.wordOperators.slice(7)).toEqual(["GLOB", "REGEXP", "MATCH", "ISNULL", "NOTNULL"]);
    expect(LEXICAL_KINDS.mySql.constantKeywords).toEqual(["TRUE", "FALSE", "NULL"]);
    expect(LEXICAL_KINDS.mySql.wordOperators.slice(7)).toEqual(["XOR", "DIV", "MOD", "REGEXP", "RLIKE"]);
    expect(LEXICAL_KINDS.duckDb.constantKeywords).toEqual(["TRUE", "FALSE", "NULL"]);
    expect(LEXICAL_KINDS.duckDb.wordOperators.slice(7)).toEqual(["ILIKE", "GLOB", "SIMILAR"]);
    expect(LEXICAL_KINDS.oracle).toEqual({ constantKeywords: ["NULL"], constantIdents: ["TRUE", "FALSE"], wordOperators: LEXICAL_KINDS.tsql.wordOperators });
  });
});

describe("lexical kinds: what the tokenizer emits", () => {
  it.each(DIALECT_LIST)("%s: every constant and word operator, in any case, gets its kind", (d) => {
    const k = LEXICAL_KINDS[d];
    const words = [...k.constantKeywords, ...k.constantIdents, ...k.wordOperators];
    const line = highlight(d, `SELECT ${words.join(" ")} ${words.map((w) => w.toLowerCase()).join(" ")}`);
    for (const w of [...k.constantKeywords, ...k.constantKeywords.map((x) => x.toLowerCase())]) expect(line).toContain(`${w}→keyword.constant`);
    for (const w of [...k.constantIdents, ...k.constantIdents.map((x) => x.toLowerCase())]) expect(line).toContain(`${w}→identifier.constant`);
    for (const w of [...k.wordOperators, ...k.wordOperators.map((x) => x.toLowerCase())]) expect(line).toContain(`${w}→keyword.operator`);
    // Other words keep their kinds.
    expect(highlight(d, "SELECT x FROM t")).toEqual(["SELECT→keyword", "x→identifier", "FROM→keyword", "t→identifier"]);
  });

  it("T-SQL: TRUE and FALSE stay names; NOT NULL in DDL reads as operator + constant", () => {
    expect(highlight("tsql", "SELECT TRUE, FALSE")).toEqual(["SELECT→keyword", "TRUE→identifier", ",→delimiter", "FALSE→identifier"]);
    expect(highlight("tsql", "id INT NOT NULL")).toEqual(["id→identifier", "INT→type", "NOT→keyword.operator", "NULL→keyword.constant"]);
  });

  it("T-SQL and MySQL: @x is a variable and @@x one system-variable token (Monarch reads @@ in a rule as one @)", () => {
    expect(highlight("tsql", "SELECT @n, @@ROWCOUNT, @#t, @$c, @since")).toEqual([
      "SELECT→keyword", "@n→variable", ",→delimiter", "@@ROWCOUNT→predefined.global", ",→delimiter", "@#t→variable", ",→delimiter",
      "@$c→variable", ",→delimiter", "@since→variable",
    ]);
    expect(highlight("mySql", "SELECT @n, @@version, @@global.max_connections, @a.b")).toEqual([
      "SELECT→keyword", "@n→variable", ",→delimiter", "@@version→predefined.global", ",→delimiter", "@@global.max_connections→predefined.global",
      ",→delimiter", "@a.b→variable",
    ]);
    // A lone @@ is no name: plain text, as before.
    expect(highlight("tsql", "SELECT @@ + 1")).toEqual(["SELECT→keyword", "@@→", "+→operator", "1→number"]);
  });

  it("PostgreSQL: $1 and :name are binds; :: is a cast before its type; dollar bodies use the same word kinds", () => {
    expect(highlight("pg", "SELECT $1, :name")).toEqual(["SELECT→keyword", "$1→variable.bind", ",→delimiter", ":name→variable.bind"]);
    // Monaco merges the adjacent `type` groups (spaces and name) into one token.
    expect(highlight("pg", "a::int, b ::  \"T\"[], c::int4[]")).toEqual([
      "a→identifier", "::→type.cast", "int→type", ",→delimiter", "b→identifier", "::→type.cast", "  \"T\"[]→type", ",→delimiter",
      "c→identifier", "::→type.cast", "int4[]→type",
    ]);
    const body = highlight("pg", "DO $$ BEGIN IF x IS NULL AND TRUE THEN NULL; END IF; END $$");
    for (const k of ["IS→keyword.operator", "NULL→keyword.constant", "AND→keyword.operator", "TRUE→keyword.constant", "BEGIN→keyword"]) expect(body).toContain(k);
    // PL/pgSQL `FOR r IN` takes the operator kind (accepted, §5.2).
    expect(highlight("pg", "FOR r IN SELECT 1")).toContain("IN→keyword.operator");
  });

  it("SQLite: ? ?N :x @x $x are binds; TRUE and FALSE are constants that are not keywords", () => {
    expect(highlight("sqlite", "SELECT ?, ?12, :x, @x, $x, TRUE, NULL")).toEqual([
      "SELECT→keyword", "?→variable.bind", ",→delimiter", "?12→variable.bind", ",→delimiter", ":x→variable.bind", ",→delimiter",
      "@x→variable.bind", ",→delimiter", "$x→variable.bind", ",→delimiter", "TRUE→identifier.constant", ",→delimiter", "NULL→keyword.constant",
    ]);
  });

  it("MySQL: ? is a bind delimiter (it had no token: editor fg); @x stays as it was", () => {
    expect(highlight("mySql", "SELECT a FROM t WHERE id = ? LIMIT ?, ?")).toEqual([
      "SELECT→keyword", "a→identifier", "FROM→keyword", "t→identifier", "WHERE→keyword", "id→identifier", "=→operator", "?→delimiter.bind",
      "LIMIT→keyword", "?→delimiter.bind", ",→delimiter", "?→delimiter.bind",
    ]);
    expect(highlight("mySql", "SELECT 7 DIV 2 XOR TRUE")).toEqual(["SELECT→keyword", "7→number", "DIV→keyword.operator", "2→number", "XOR→keyword.operator", "TRUE→keyword.constant"]);
  });

  it("DuckDB: $1 $name ? are binds; :: is a cast; $$ stays a string", () => {
    expect(highlight("duckDb", "SELECT $1, $name, ?, a::INTEGER, $$ TRUE AND x $$")).toEqual([
      "SELECT→keyword", "$1→variable.bind", ",→delimiter", "$name→variable.bind", ",→delimiter", "?→variable.bind", ",→delimiter",
      "a→identifier", "::→type.cast", "INTEGER→type", ",→delimiter", "$$→string.dollar", " TRUE AND x →string", "$$→string.dollar",
    ]);
  });

  it("Oracle: :name :1 :\"x\" are binds; := stays an operator", () => {
    expect(highlight("oracle", "x := :name + :1 + :\"x\"")).toEqual([
      "x→identifier", ":=→operator", ":name→variable.bind", "+→operator", ":1→variable.bind", "+→operator", ":\"x\"→variable.bind",
    ]);
  });

  it.each(DIALECT_LIST)("%s: language configuration includes block-comment auto-close and Enter continuation rules", (d) => {
    const cfg = LANGUAGES[d].configuration;
    expect(cfg.autoClosingPairs).toContainEqual({ open: "/*", close: " */", notIn: ["string"] });
    expect(cfg.onEnterRules?.length).toBeGreaterThanOrEqual(4);
  });
});

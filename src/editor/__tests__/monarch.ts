// Monaco's own Monarch compiler and tokenizer (not a re-implementation), shared by the highlighting tests.
import type * as Monaco from "monaco-editor/esm/vs/editor/editor.api";
// @ts-expect-error untyped internal module of monaco-editor
import { compile } from "monaco-editor/esm/vs/editor/standalone/common/monarch/monarchCompile.js";
// @ts-expect-error untyped internal module of monaco-editor
import { MonarchTokenizer } from "monaco-editor/esm/vs/editor/standalone/common/monarch/monarchLexer.js";

export type Tok = { offset: number; type: string };
type State = unknown;
export interface Tokenizer {
  getInitialState(): State;
  tokenize(line: string, hasEOL: boolean, state: State): { tokens: Tok[]; endState: State };
  tokenizeEncoded(line: string, hasEOL: boolean, state: State): { tokens: Uint32Array; endState: State };
}
/** A compiled Monarch rule, as monarchCompile.js builds it. */
export interface CompiledRule { name: string; action: unknown }
export interface CompiledLexer { start: string; tokenizer: Record<string, CompiledRule[]> }

const noop = { dispose() {} };
/** The language service bits MonarchTokenizer reads for a language with no embedded languages. */
const languageService = {
  languageIdCodec: { encodeLanguageId: () => 1, decodeLanguageId: () => "" },
  requestRichLanguageFeatures() {},
  isRegisteredLanguageId: () => false,
  getLanguageIdByLanguageName: () => null,
  getLanguageIdByMimeType: () => null,
};
/** Theme service for `tokenize` (types only); `tokenizeEncoded` needs a real one (highlightGolden.test.ts). */
const typesOnlyThemeService = { onDidColorThemeChange: () => noop };

/** Compiles `def` with Monaco's compiler and wraps it in Monaco's tokenizer. */
export function monarch(languageId: string, def: Monaco.languages.IMonarchLanguage, themeService: unknown = typesOnlyThemeService) {
  const lexer = compile(languageId, def) as CompiledLexer;
  const tokenizer = new MonarchTokenizer(languageService, themeService, languageId, lexer, {
    getValue: () => 20_000,
    onDidChangeConfiguration: () => noop,
  }) as Tokenizer;
  return { lexer, tokenizer };
}

/** Splits text into lines the way a Monaco model does (LF or CRLF). */
export const lines = (text: string) => text.split(/\r?\n/);

/** Each line's tokens as [text, type] (type with its postfix), the tokenizer state carried across lines. */
export function tokenLines(tokenizer: Tokenizer, text: string): [string, string][][] {
  let state = tokenizer.getInitialState();
  return lines(text).map((line) => {
    const r = tokenizer.tokenize(line, true, state);
    state = r.endState;
    return r.tokens.map((k, i) => [line.slice(k.offset, r.tokens[i + 1]?.offset ?? line.length), k.type]);
  });
}

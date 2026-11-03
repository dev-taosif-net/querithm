// marked 4.3 ships no type declarations; this covers the part the notebook's markdown cells use.
declare module "marked" {
  export interface MarkedOptions {
    renderer?: Renderer;
    tokenizer?: Tokenizer;
    gfm?: boolean;
    breaks?: boolean;
    headerIds?: boolean;
    mangle?: boolean;
    async?: false;
  }
  export class Renderer {
    html(html: string): string;
    link(href: string | null, title: string | null, text: string): string;
    image(href: string | null, title: string | null, text: string): string;
  }
  export interface TextToken {
    type: "text";
    raw: string;
    text: string;
  }
  /** The lexer sets `rules` on its tokenizer before the first call. */
  export class Tokenizer {
    rules: { inline: { tag: RegExp; text: RegExp } };
    tag(src: string): TextToken | undefined;
    inlineText(src: string, smartypants?: (s: string) => string): TextToken | undefined;
  }
  export const marked: {
    parse(src: string, options?: MarkedOptions): string;
    Renderer: typeof Renderer;
    Tokenizer: typeof Tokenizer;
  };
}

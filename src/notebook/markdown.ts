// Markdown cells (Phase C3): rendered with marked, with raw HTML escaped (shown as text, never parsed), images shown
// as their alt text (no request ever leaves the app) and links kept only for http(s) and mailto. A link is never
// followed by the webview: clicking it copies the address (the app has no URL opener; §13 offline-first).
import { marked } from "marked";

export function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

/** As marked escapes text: an entity the author wrote (`&amp;`, `&copy;`) stays an entity; everything else is escaped. */
function escapeText(s: string): string {
  return s
    .replace(/&(?!#\d{1,7};|#[Xx][a-fA-F0-9]{1,6};|\w+;)/g, "&amp;")
    .replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

/** http(s) and mailto addresses only; anything else (javascript:, data:, file:, relative paths) is not a link. */
export function safeHref(href: string | null | undefined): string | null {
  if (!href) return null;
  const h = href.trim();
  return /^(https?:\/\/|mailto:)/i.test(h) ? h : null;
}

/**
 * M4: marked passes the text after an inline `<pre>`, `<code>`, `<kbd>` or `<script>` tag through unescaped (its
 * "raw block" mode lasts to the matching close tag, or to the end of the document), and a tag it rejects — an `@>`
 * attribute — is text, so `<kbd> <form …@>` drew a live form. Here an inline tag is plain escaped text that never changes
 * the lexer's state, and every text token is escaped whatever that state says.
 */
class EscapingTokenizer extends marked.Tokenizer {
  tag(src: string) {
    const cap = this.rules.inline.tag.exec(src);
    return cap ? { type: "text" as const, raw: cap[0], text: escapeText(cap[0]) } : undefined;
  }
  inlineText(src: string) {
    const cap = this.rules.inline.text.exec(src);
    return cap ? { type: "text" as const, raw: cap[0], text: escapeText(cap[0]) } : undefined;
  }
}

const renderer = new marked.Renderer();
// Block-level HTML (a line that starts with a tag) is shown as text too.
renderer.html = (html: string) => escapeHtml(html);
renderer.image = (_href: string | null, _title: string | null, text: string) => `<span class="q-md-img">[image: ${escapeHtml(text || "untitled")}]</span>`;
renderer.link = (href: string | null, title: string | null, text: string) => {
  const safe = safeHref(href);
  if (!safe) return text;
  return `<a href="${escapeHtml(safe)}" data-q-link="${escapeHtml(safe)}" title="${escapeHtml(title || safe)} — click to copy the address">${text}</a>`;
};

/** A markdown cell's HTML. Safe to place with innerHTML: every piece of input text is escaped. */
export function renderMarkdown(src: string): string {
  // A tokenizer per call: marked binds it to the call's lexer.
  return marked.parse(src, { renderer, tokenizer: new EscapingTokenizer(), gfm: true, breaks: false, headerIds: false, mangle: false, async: false });
}

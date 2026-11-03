// Markdown for the editor's hover (L10): object, column, type and default names come from the server (or a schema file),
// so none of them may add markup — a link, an image, emphasis, a table cell or a code fence of their own. Monaco renders
// hover markdown untrusted (no command links, no HTML); these helpers keep the names themselves inert as well.

/** One line: line breaks (CR, LF, NEL, U+2028/2029) become spaces, so a name cannot start a block of its own. */
export function oneLine(s: string): string {
  return s.replace(/[\r\n\u0085\u2028\u2029]+/g, " ");
}

/**
 * Plain text inside a line (never at its start, where # - + 1. would count): the characters that make emphasis, code,
 * links, images, HTML, entities, strikethrough or table cells are backslash-escaped.
 */
export function mdText(s: string): string {
  return oneLine(s).replace(/[\\`*_[\]()<>|~&!]/g, (c) => `\\${c}`);
}

/** Inline code: the fence is one backtick longer than any run in the text, so the text cannot close it. */
export function mdCode(s: string): string {
  const text = oneLine(s);
  const longest = Math.max(0, ...(text.match(/`+/g) ?? []).map((r) => r.length));
  const fence = "`".repeat(longest + 1);
  const pad = text.startsWith("`") || text.endsWith("`") ? " " : "";
  return `${fence}${pad}${text}${pad}${fence}`;
}

/** A table cell: like mdCode or mdText, with the pipe escaped (GFM splits cells on it even inside code). */
export function mdCell(s: string, code = false): string {
  return (code ? mdCode(s) : mdText(s)).replace(/(?<!\\)\|/g, "\\|");
}

/** A fenced code block: the fence is longer than any backtick run in the content, so a line of backticks cannot end it. */
export function mdFence(lang: string, content: string): string {
  const longest = Math.max(0, ...(content.match(/`+/g) ?? []).map((r) => r.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  return `${fence}${lang}\n${content}\n${fence}`;
}

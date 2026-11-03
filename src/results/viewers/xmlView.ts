// XML value → formatted lines with fold ranges (Phase C2). Parsed with DOMParser as application/xml: an XML document is
// data, never HTML, and nothing it names is fetched or run. The formatted text is built from the DOM and escaped again.
import { LineBuilder, type StructuredView } from "./lines";
import { STRUCTURED_MAX_CHARS } from "./detect";

export const XML_MAX_DEPTH = 512;

export type XmlViewResult =
  | { ok: true; view: StructuredView; /** The root is a SQL Server showplan (`<ShowPlanXML>`). */ showplan: boolean }
  | { ok: false; error: string; tooLarge?: boolean };

/** An element whose only content is text up to this long stays on one line: `<Name>Orders</Name>`. */
const INLINE_TEXT = 120;

const ELEMENT = 1, TEXT = 3, CDATA = 4, PI = 7, COMMENT = 8;

const escText = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const escAttr = (s: string) => escText(s).replace(/"/g, "&quot;");

function openTag(el: Element, selfClose: boolean): string {
  let s = `<${el.nodeName}`;
  for (const a of Array.from(el.attributes)) s += ` ${a.name}="${escAttr(a.value)}"`;
  return s + (selfClose ? "/>" : ">");
}

class TooDeep extends Error {}

function walk(node: Node, depth: number, out: LineBuilder) {
  if (depth > XML_MAX_DEPTH) throw new TooDeep();
  switch (node.nodeType) {
    case ELEMENT: {
      const el = node as Element;
      const kids = Array.from(el.childNodes).filter((k) => !(k.nodeType === TEXT && !k.nodeValue?.trim()));
      if (!kids.length) {
        out.push(depth, openTag(el, true));
        return;
      }
      if (kids.length === 1 && kids[0].nodeType === TEXT) {
        const t = kids[0].nodeValue!.trim();
        if (t.length <= INLINE_TEXT && !t.includes("\n")) {
          out.push(depth, `${openTag(el, false)}${escText(t)}</${el.nodeName}>`);
          return;
        }
      }
      const open = out.push(depth, openTag(el, false));
      for (const k of kids) walk(k, depth + 1, out);
      out.fold(open, out.push(depth, `</${el.nodeName}>`));
      return;
    }
    case TEXT: {
      for (const line of node.nodeValue!.trim().split("\n")) if (line.trim()) out.push(depth, escText(line.trim()));
      return;
    }
    case CDATA:
      out.push(depth, `<![CDATA[${node.nodeValue}]]>`);
      return;
    case COMMENT:
      out.push(depth, `<!--${node.nodeValue}-->`);
      return;
    case PI:
      out.push(depth, `<?${node.nodeName} ${node.nodeValue}?>`);
      return;
  }
}

/** The parser's message from a `<parsererror>` document, or null when the text parsed. */
function parseError(doc: Document): string | null {
  const err = doc.getElementsByTagName("parsererror")[0] ?? doc.getElementsByTagNameNS("*", "parsererror")[0];
  if (!err) return null;
  const msg = (err.textContent ?? "").replace(/\s+/g, " ").trim();
  return msg || "The text is not well-formed XML.";
}

/** Formats an XML text (two-space indent) or says why it is not well-formed XML. */
export function buildXmlView(text: string, maxChars = STRUCTURED_MAX_CHARS): XmlViewResult {
  if (text.length > maxChars) {
    return { ok: false, tooLarge: true, error: `The value is larger than ${Math.round(maxChars / 1024 / 1024)} MB, so it is shown as text without formatting.` };
  }
  let doc: Document;
  try {
    doc = new DOMParser().parseFromString(text, "application/xml");
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
  const err = parseError(doc);
  if (err) return { ok: false, error: err };
  const out = new LineBuilder();
  try {
    for (const k of Array.from(doc.childNodes)) walk(k, 0, out);
  } catch (e) {
    if (e instanceof TooDeep) return { ok: false, error: `The XML nests deeper than ${XML_MAX_DEPTH} levels, so it is shown as text without formatting.` };
    throw e;
  }
  return { ok: true, view: out.build(), showplan: doc.documentElement?.localName === "ShowPlanXML" };
}

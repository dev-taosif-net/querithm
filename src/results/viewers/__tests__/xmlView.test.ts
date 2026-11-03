// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { buildXmlView, XML_MAX_DEPTH } from "../xmlView";
import { lineCount, lineText, type StructuredView } from "../lines";

const lines = (v: StructuredView) => Array.from({ length: lineCount(v) }, (_, i) => lineText(v, i));

describe("buildXmlView", () => {
  it("indents elements, keeps short text inline and escapes text and attributes again", () => {
    const r = buildXmlView('<?xml version="1.0"?><root a="1 &amp; 2" b="&quot;q&quot;"><name>Orders &lt;2026&gt;</name><empty/><!--note--><list><i>1</i><i>2</i></list><![CDATA[<raw>]]></root>');
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(lines(r.view)).toEqual([
      '<root a="1 &amp; 2" b="&quot;q&quot;">',
      "  <name>Orders &lt;2026&gt;</name>",
      "  <empty/>",
      "  <!--note-->",
      "  <list>",
      "    <i>1</i>",
      "    <i>2</i>",
      "  </list>",
      "  <![CDATA[<raw>]]>",
      "</root>",
    ]);
    expect(r.view.foldEnd[0]).toBe(9);
    expect(r.view.foldEnd[4]).toBe(7);
    expect(r.showplan).toBe(false);
  });

  it("keeps namespace declarations and recognises a SQL Server showplan", () => {
    const r = buildXmlView('<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6"><BatchSequence/></ShowPlanXML>');
    expect(r.ok && r.showplan).toBe(true);
    if (r.ok) expect(lineText(r.view, 0)).toContain('xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"');
  });

  it("returns the parser's error for text that is not well-formed", () => {
    for (const bad of ["<a><b></a>", "<a>", "<a></a><b/>", "<a x=1/>"]) {
      const r = buildXmlView(bad);
      expect(r.ok, bad).toBe(false);
      if (!r.ok) expect(r.error.length).toBeGreaterThan(0);
    }
  });

  it("shows oversized and over-deep documents as text with the reason", () => {
    const big = buildXmlView("<a>" + "x".repeat(100) + "</a>", 50);
    expect(big).toMatchObject({ ok: false, tooLarge: true });
    const deep = buildXmlView("<a>".repeat(XML_MAX_DEPTH + 3) + "</a>".repeat(XML_MAX_DEPTH + 3));
    expect(deep.ok).toBe(false);
    if (!deep.ok) expect(deep.error).toContain(`deeper than ${XML_MAX_DEPTH}`);
  });

  it("puts multi-line text on its own lines", () => {
    const r = buildXmlView("<a><b>line one\nline two</b></a>");
    expect(r.ok && lines(r.view)).toEqual(["<a>", "  <b>", "    line one", "    line two", "  </b>", "</a>"]);
  });
});

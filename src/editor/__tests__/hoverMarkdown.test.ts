import { describe, expect, it } from "vitest";
import { mdCell, mdCode, mdFence, mdText } from "../hoverMarkdown";

describe("hover markdown escaping (L10)", () => {
  it("escapes what would make a link, an image, HTML, emphasis or a cell, on one line", () => {
    expect(mdText("[a](u) ![i](u) <b>x</b> *e* _u_ ~s~ a|b &amp;")).toBe("\\[a\\]\\(u\\) \\!\\[i\\]\\(u\\) \\<b\\>x\\</b\\> \\*e\\* \\_u\\_ \\~s\\~ a\\|b \\&amp;");
    expect(mdText("dbo.Orders.OrderId")).toBe("dbo.Orders.OrderId");
    expect(mdText("a\nb\r\nc d")).toBe("a b c d");
  });

  it("fences inline code longer than any backtick run in it", () => {
    expect(mdCode("int")).toBe("`int`");
    expect(mdCode("a`b")).toBe("``a`b``");
    expect(mdCode("`x``")).toBe("``` `x`` ```");
  });

  it("escapes pipes in table cells, code cells included", () => {
    expect(mdCell("a|b")).toBe("a\\|b");
    expect(mdCell("int|x", true)).toBe("`int\\|x`");
  });

  it("fences a code block so a line of backticks inside cannot end it", () => {
    expect(mdFence("sql", "a\nb")).toBe("```sql\na\nb\n```");
    expect(mdFence("sql", "a ``` b")).toBe("````sql\na ``` b\n````");
  });
});

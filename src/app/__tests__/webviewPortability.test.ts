import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import ts from "typescript";
import { describe, expect, it } from "vitest";

// The UI runs in WebView2 (Chromium) on Windows but in WebKit on Linux (WebKitGTK) and macOS (WKWebView).
// These checks fail on any OS for code that only works in Chromium, so portability bugs show up in `pnpm test`.

const SRC = join(process.cwd(), "src");

function files(dir: string, ext: RegExp): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = join(dir, e.name);
    if (e.isDirectory()) return e.name === "__tests__" ? [] : files(p, ext);
    return ext.test(e.name) && !e.name.endsWith(".d.ts") ? [p] : [];
  });
}

/** Browser APIs WebKit does not ship. */
const CHROMIUM_ONLY_APIS: Record<string, string> = {
  requestIdleCallback: "not in WebKit; use setTimeout",
  cancelIdleCallback: "not in WebKit",
  showOpenFilePicker: "File System Access API is Chromium-only; use the Tauri dialog",
  showSaveFilePicker: "File System Access API is Chromium-only; use the Tauri dialog",
  showDirectoryPicker: "File System Access API is Chromium-only; use the Tauri dialog",
  userAgentData: "Chromium-only; use navigator.platform",
};
/** Elements WebKit renders as native controls, which ignore writing-mode. */
const FORM_CONTROLS = new Set(["button", "input", "select", "textarea", "option"]);

function scanSource(name: string, text: string): string[] {
  const sf = ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true, name.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  const found: string[] = [];
  const at = (n: ts.Node) => `${name}:${sf.getLineAndCharacterOfPosition(n.getStart()).line + 1}`;
  const visit = (n: ts.Node) => {
    if (ts.isIdentifier(n) && Object.hasOwn(CHROMIUM_ONLY_APIS, n.text)) found.push(`${at(n)} ${n.text}: ${CHROMIUM_ONLY_APIS[n.text]}`);
    if ((ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) && FORM_CONTROLS.has(n.tagName.getText())) {
      for (const a of n.attributes.properties) {
        if (!ts.isJsxAttribute(a) || !a.initializer) continue;
        const attr = a.name.getText();
        const value = a.initializer.getText();
        if ((attr === "className" && value.includes("writing-mode")) || (attr === "style" && value.includes("writingMode")))
          found.push(`${at(a)} writing-mode on <${n.tagName.getText()}>: WebKit ignores it on form controls; put it on an inner <span className="block ...">`);
      }
    }
    ts.forEachChild(n, visit);
  };
  visit(sf);
  return found;
}

const rel = (f: string) => relative(process.cwd(), f).replace(/\\/g, "/");

describe("webview portability (WebView2 vs WebKitGTK / WKWebView)", () => {
  it("TS/TSX sources use no Chromium-only APIs and no writing-mode on form controls", () => {
    expect(files(SRC, /\.tsx?$/).flatMap((f) => scanSource(rel(f), readFileSync(f, "utf8")))).toEqual([]);
  });

  it("stylesheets use no Chromium-only CSS properties", () => {
    const found = files(SRC, /\.css$/).flatMap((f) =>
      readFileSync(f, "utf8").split("\n").flatMap((line, i) => (/\bfield-sizing\s*:/.test(line) ? [`${rel(f)}:${i + 1} field-sizing is Chromium-only`] : [])),
    );
    expect(found).toEqual([]);
  });

  it("flags the patterns it guards against", () => {
    expect(scanSource("a.tsx", `const x = <button className="[writing-mode:vertical-rl]">A</button>;`)).toHaveLength(1);
    expect(scanSource("a.tsx", `const x = <button style={{ writingMode: "vertical-rl" }}>A</button>;`)).toHaveLength(1);
    expect(scanSource("a.tsx", `const x = <button><span className="block [writing-mode:vertical-rl]">A</span></button>;`)).toEqual([]);
    expect(scanSource("a.ts", `requestIdleCallback(() => {});`)).toHaveLength(1);
    expect(scanSource("a.ts", `// requestIdleCallback in a comment is fine\nconst s = "requestIdleCallback";`)).toEqual([]);
  });
});

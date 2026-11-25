import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// §16 start screen: the window opens in the theme it last had. Three places hold that theme's background (the shell's
// window colour, the page before the app's stylesheet, the app's own token) and the first frame must not depend on
// anything inline, or the shell's CSP handling changes for the whole app.
const file = (p: string) => readFileSync(new URL(`../../${p}`, import.meta.url), "utf8");
const index = file("index.html");
const splashCss = file("public/boot-splash.css");
const tokens = file("src/theme/tokens.css");
const themeRs = file("src-tauri/src/theme.rs");
const mainRs = file("src-tauri/src/main.rs");

/** `--q-bg` of a theme block in tokens.css, as [r, g, b]. */
function tokenBg(theme: "dark" | "light"): number[] {
  const block = tokens.split(`:root[data-theme="${theme}"] {`)[1] ?? "";
  const hex = /--q-bg:\s*#([0-9a-f]{6});/i.exec(block)?.[1] ?? "";
  return [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16));
}

/** A `pub const NAME: (u8, u8, u8) = (0x.., 0x.., 0x..);` of theme.rs, as [r, g, b]. */
function shellBg(name: string): number[] {
  const m = new RegExp(`pub const ${name}: \\(u8, u8, u8\\) = \\(0x([0-9a-f]{2}), 0x([0-9a-f]{2}), 0x([0-9a-f]{2})\\);`).exec(themeRs);
  return m ? m.slice(1).map((h) => parseInt(h, 16)) : [];
}

/** The background a rule of boot-splash.css gives `selector`, as [r, g, b]. */
function pageBg(selector: string): number[] {
  const rule = splashCss.split(`\n${selector} {`)[1]?.split("}")[0] ?? "";
  const hex = /background:\s*#([0-9a-f]{6});/i.exec(rule)?.[1] ?? "";
  return [0, 2, 4].map((i) => parseInt(hex.slice(i, i + 2), 16));
}

describe("start screen: one background per theme, in every layer", () => {
  it.each([
    ["dark", "DARK_BG", "html"],
    ["light", "LIGHT_BG", 'html[data-theme="light"]'],
  ] as const)("%s: the shell's window colour and the page's first frame are the app's --q-bg", (theme, constant, selector) => {
    const bg = tokenBg(theme);
    expect(bg.every(Number.isFinite) && bg.length === 3).toBe(true);
    expect(shellBg(constant)).toEqual(bg);
    expect(pageBg(selector)).toEqual(bg);
  });
});

describe("index.html", () => {
  const head = index.split("</head>")[0];

  it("names the theme and loads the start screen's styles in the head, before the app's bundle", () => {
    expect(head).toContain('<script src="/boot-theme.js"></script>');
    expect(head).toContain('<link rel="stylesheet" href="/boot-splash.css" />');
    expect(index.indexOf('src="/boot-theme.js"')).toBeLessThan(index.indexOf('src="/src/main.tsx"'));
    // Before the stylesheet too: its light rules match from the first frame.
    expect(index.indexOf('src="/boot-theme.js"')).toBeLessThan(index.indexOf('href="/boot-splash.css"'));
  });

  it("ships the start screen itself, at the first step, with a status line", () => {
    expect(index).toContain('<div id="splash" data-stage="1">');
    expect(index).toContain('id="splash-text"');
    expect(index).toContain('id="splash-elapsed"');
    expect(index.indexOf('id="splash"')).toBeLessThan(index.indexOf("/src/main.tsx"));
  });

  // Tauri gives every inline <style> a nonce and adds it to style-src; with a nonce there the webview ignores
  // 'unsafe-inline', which Monaco's injected styles need. script-src allows no inline script at all.
  it("holds no inline <style> and no inline <script>", () => {
    expect(index).not.toMatch(/<style[\s>]/i);
    for (const tag of index.match(/<script\b[^>]*>/gi) ?? []) expect(tag).toMatch(/\ssrc="/);
    expect(index).not.toMatch(/<script\b[^>]*>[^<]*\S[^<]*<\/script>/i);
  });

  it("loads nothing from the network (§13 rule 5)", () => {
    expect(index).not.toMatch(/https?:\/\//);
    expect(splashCss).not.toMatch(/url\(|@import|https?:\/\//);
  });
});

describe("the shell's start colour", () => {
  it("is set on the hidden window before it is shown", () => {
    const setup = mainRs.split(".setup(")[1] ?? "";
    expect(setup.indexOf("theme::apply(&window")).toBeGreaterThan(-1);
    expect(setup.indexOf("theme::apply(&window")).toBeLessThan(setup.indexOf("window_state::restore(&window)"));
  });

  it("keeps the theme through one command that takes only a theme word and two colours", () => {
    expect(mainRs).toContain("theme::app_theme_keep,");
    const command = themeRs.split("pub async fn app_theme_keep")[1]?.split("\n}\n")[0] ?? "";
    // The disk is written by Keeper::keep (one call at a time, the newest winning, temp file + rename); the command
    // reaches it only with the parsed value.
    const keep = themeRs.split("fn keep(&self")[1]?.split("\n    }\n")[0] ?? "";
    // Parsed before anything touches the disk; what is written is made from the parsed values, never the argument.
    expect(command.indexOf("parse(&theme)")).toBeGreaterThan(-1);
    expect(command).not.toContain("std::fs::");
    expect(command.indexOf("parse(&theme)")).toBeLessThan(command.indexOf("KEEPER.keep(ticket, &dir, &kept)"));
    expect(keep).toContain("std::fs::write(&tmp, serialize(kept))");
    expect(keep).toContain("std::fs::rename(&tmp, dir.join(FILE))");
    for (const body of [command, keep]) expect(body).not.toMatch(/write\([^)]*theme\b(?!\.)/);
  });

  it("never knows a theme id: adding an App Theme changes no Rust (plans/app-themes I8)", () => {
    const ids = [...file("src/theme/appThemeData.ts").matchAll(/\bid: "([a-z][a-z0-9-]*)"/g)].map((m) => m[1]).filter((id) => id !== "dark" && id !== "light");
    expect(ids.length).toBeGreaterThan(0);
    for (const id of ids) expect(themeRs).not.toMatch(new RegExp(`\\b${id}\\b`, "i"));
  });
});

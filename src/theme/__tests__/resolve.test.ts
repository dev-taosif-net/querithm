// @vitest-environment jsdom
// Which App Theme and which SQL palette show (plans/app-themes §4.1): Standard is the same on every theme; Default
// follows the App Theme; a wrong or unknown id never mixes tokens.
import { describe, expect, it } from "vitest";
import { appThemeById, installAppThemeStyles, LEGACY_APP_THEME } from "../appThemes";
import { appSqlPalette, resolveAppTheme, resolveDocumentAppTheme, resolveSyntax } from "../resolve";
import { rulesFor, SYNTAX_THEMES } from "../../editor/syntaxTheme";

const t = (id: string) => appThemeById(id)!;

describe("resolveAppTheme", () => {
  it("shows the pick of the mode; Match system follows the OS", () => {
    const c = { darkTheme: "cortado", lightTheme: "vellum" };
    expect(resolveAppTheme({ theme: "dark", ...c }, false).id).toBe("cortado");
    expect(resolveAppTheme({ theme: "light", ...c }, true).id).toBe("vellum");
    expect(resolveAppTheme({ theme: "system", ...c }, true).id).toBe("cortado");
    expect(resolveAppTheme({ theme: "system", ...c }, false).id).toBe("vellum");
  });

  it("an unknown id or a pick of the other mode shows Querithm Dark / Light", () => {
    expect(resolveAppTheme({ theme: "dark", darkTheme: "neon-sunset", lightTheme: "light" }, true)).toBe(LEGACY_APP_THEME.dark);
    expect(resolveAppTheme({ theme: "light", darkTheme: "dark", lightTheme: "cortado" }, true)).toBe(LEGACY_APP_THEME.light);
  });
});

describe("resolveDocumentAppTheme", () => {
  it("reads the mode and the id as the CSS selector does; a mismatch or missing styles fall back", () => {
    const root = document.createElement("html");
    root.dataset.theme = "dark";
    root.dataset.appTheme = "cortado";
    // Before the generated blocks are in the document, the id has no tokens to show: Querithm Dark.
    expect(resolveDocumentAppTheme(root).id).toBe("dark");
    installAppThemeStyles(document);
    expect(resolveDocumentAppTheme(root).id).toBe("cortado");
    root.dataset.theme = "light"; // the notebook export flips the mode only
    expect(resolveDocumentAppTheme(root).id).toBe("light");
    root.dataset.appTheme = "nope";
    expect(resolveDocumentAppTheme(root).id).toBe("light");
    delete root.dataset.theme;
    expect(resolveDocumentAppTheme(root).id).toBe("dark");
  });
});

describe("resolveSyntax", () => {
  it("Standard (and each of the six) is its own palette object in the App Theme's mode, on every theme, with no semantic tokens", () => {
    for (const id of ["dark", "cortado", "fathom", "light", "vellum"]) {
      const app = t(id);
      for (const s of SYNTAX_THEMES) {
        const r = resolveSyntax(s.id, app);
        expect(r.palette).toBe(s[app.mode]);
        expect(r.fontStyle).toEqual(s.fontStyle);
        expect(r.semantic).toBe(false);
      }
    }
    // The same rules on two dark themes: Standard does not follow the App Theme.
    const a = resolveSyntax("standard", t("cortado")), b = resolveSyntax("standard", t("inkwell"));
    expect(rulesFor(a.palette, a.fontStyle, "#ffffff")).toEqual(rulesFor(b.palette, b.fontStyle, "#ffffff"));
  });

  it("Default is the App Theme's own palette: it changes with the theme; word operators are bold; CTE/schema tokens are requested", () => {
    const cortado = resolveSyntax("default", t("cortado")), fathom = resolveSyntax("default", t("fathom"));
    expect(cortado.palette).toEqual(appSqlPalette(t("cortado")));
    expect(cortado.palette.keyword).not.toBe(fathom.palette.keyword);
    expect(rulesFor(cortado.palette, cortado.fontStyle, "#fff")).not.toEqual(rulesFor(fathom.palette, fathom.fontStyle, "#fff"));
    expect(cortado.fontStyle.wordOperator).toBe("bold");
    expect(cortado.fontStyle.keyword).toBe("bold");
    expect(cortado.semantic).toBe(true);
    // An unknown choice is Default too (a newer build's id).
    expect(resolveSyntax("rainbow", t("cortado")).choice).toBe("default");
  });

  it("Default on Querithm Dark keeps Standard's colors for every role Standard colors, and adds its own for the new ones", () => {
    const std = SYNTAX_THEMES[0].dark, d = resolveSyntax("default", t("dark")).palette;
    for (const role of ["keyword", "type", "string", "number", "comment", "variable", "function", "operator", "ident", "bracket1", "bracket2", "bracket3"] as const)
      expect(d[role], role).toBe(std[role]);
    expect(d.constant).toBeDefined();
    expect(d.cte).toBeDefined();
    expect(d.schema).toBeDefined();
  });

  it("Italic comments off draws comments upright in every palette", () => {
    expect(resolveSyntax("standard", t("dark"), true).fontStyle.comment).toBe("");
    expect(resolveSyntax("default", t("vellum"), true).fontStyle.comment).toBe("");
    expect(resolveSyntax("default", t("vellum"), false).fontStyle.comment).toBe("italic");
  });
});

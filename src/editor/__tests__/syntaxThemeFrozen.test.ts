// T2 frozen (plans/app-themes §9 T2, invariant I1): the six syntax themes and the Monaco rules for the 16 tokens they
// style are pinned as literals, so a later slice (Default palettes, new token kinds) cannot move a single hex or font
// style. A deliberate change to one of these themes updates this file in the same change, and T1 with it.
import { describe, expect, it } from "vitest";
import { DEFAULT_SYNTAX_THEME, SYNTAX_THEME_IDS, SYNTAX_THEMES, syntaxRules, syntaxThemeById } from "../syntaxTheme";

const FROZEN_THEMES = [
  {
    id: "standard",
    label: "Standard",
    description: "Balanced colors for everyday SQL. Every color reads on selected text.",
    dark: {
      keyword: "#a5aaff", type: "#6cc0a8", string: "#aed992", number: "#ec9563", comment: "#939ca8",
      variable: "#e6a6d6", function: "#e3c27a", operator: "#66bccc", ident: "#dcdde3", bracket1: "#c2c7d1",
      bracket2: "#d9b360", bracket3: "#c49cf0",
    },
    light: {
      keyword: "#22309a", type: "#205f58", string: "#23703a", number: "#9c4a00", comment: "#5e636d",
      variable: "#8a1f7c", function: "#5f5300", operator: "#0e6584", ident: "#24272d", bracket1: "#4a505a",
      bracket2: "#7d5a00", bracket3: "#7b35b0",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" },
  },
  {
    id: "color-blind",
    label: "Color-blind safe",
    description: "For color blindness: red-green and blue-yellow stay distinct.",
    dark: {
      keyword: "#b9aafc", type: "#81c38e", string: "#c9fdb7", number: "#f8914f", comment: "#b1aaa8",
      variable: "#d09ec7", function: "#e3dd73", operator: "#9fddf9", ident: "#f3f6f3", bracket1: "#c4c8d0",
      bracket2: "#8fb4ff", bracket3: "#e8c35a",
    },
    light: {
      keyword: "#320a7f", type: "#003d3e", string: "#5f5a00", number: "#5f3631", comment: "#585857",
      variable: "#7c32a1", function: "#3a2d01", operator: "#2a5e73", ident: "#080804", bracket1: "#4a505a",
      bracket2: "#1f4fb8", bracket3: "#7a5200",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold", variable: "italic" },
  },
  {
    id: "neon",
    label: "Neon",
    description: "Synthwave-bright: every token glows. For night sessions.",
    dark: {
      keyword: "#ff67c3", type: "#f0e070", string: "#acf56a", number: "#00e4ff", comment: "#aaa596",
      variable: "#bb86ff", function: "#3db5ff", operator: "#fe7b18", ident: "#eceef2", bracket1: "#ffd23f",
      bracket2: "#f084ee", bracket3: "#5cffc0",
    },
    light: {
      keyword: "#b80086", type: "#795d00", string: "#456800", number: "#036680", comment: "#665b64",
      variable: "#4c009f", function: "#0858d6", operator: "#9e4600", ident: "#1a1d2c", bracket1: "#7a5800",
      bracket2: "#962a9c", bracket3: "#006b4b",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" },
  },
  {
    id: "forest",
    label: "Forest",
    description: "Green keywords, berry strings, low glare. Calm for long sessions.",
    dark: {
      keyword: "#4bbf70", type: "#af94d2", string: "#e2a6ba", number: "#f8dc90", comment: "#a1a293",
      variable: "#87deed", function: "#8fbce9", operator: "#fbc2ac", ident: "#ebebe7", bracket1: "#c7aa8e",
      bracket2: "#d2a6cf", bracket3: "#8ab8bf",
    },
    light: {
      keyword: "#006c2e", type: "#735390", string: "#71223b", number: "#573d00", comment: "#5f5f54",
      variable: "#004e62", function: "#093c76", operator: "#5b4036", ident: "#1f251b", bracket1: "#6d5441",
      bracket2: "#633555", bracket3: "#036780",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" },
  },
  {
    id: "ember",
    label: "Ember",
    description: "Warm tones with orange keywords, for anyone who finds blue themes cold.",
    dark: {
      keyword: "#f88f03", type: "#77b3a4", string: "#a3c77b", number: "#a7a5f9", comment: "#af9b83",
      variable: "#f19fe0", function: "#f8d977", operator: "#ffbe98", ident: "#edebe5", bracket1: "#c9b9a3",
      bracket2: "#e3a857", bracket3: "#8fbfcf",
    },
    light: {
      keyword: "#9e4c00", type: "#004745", string: "#456947", number: "#403c7a", comment: "#6c6260",
      variable: "#8a4670", function: "#3f3c00", operator: "#5f3b2a", ident: "#13110f", bracket1: "#5c544c",
      bracket2: "#8a5300", bracket3: "#2f5f7a",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" },
  },
  {
    id: "ocean",
    label: "Ocean",
    description: "Cool blues and aquas with warm literals: the familiar look, sharpened.",
    dark: {
      keyword: "#47a1ff", type: "#f2da63", string: "#6bebb2", number: "#fa9160", comment: "#949fab",
      variable: "#c3baff", function: "#33ecfe", operator: "#30c5f0", ident: "#eceff0", bracket1: "#a9bccf",
      bracket2: "#4fd1c5", bracket3: "#dca6e6",
    },
    light: {
      keyword: "#0057bf", type: "#7b5b00", string: "#00704a", number: "#a24601", comment: "#575b62",
      variable: "#562c8a", function: "#004745", operator: "#13687c", ident: "#13161a", bracket1: "#4b5d70",
      bracket2: "#0b6e68", bracket3: "#8a4598",
    },
    fontStyle: { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" },
  },
];

const FROZEN_RULES: Record<string, string[]> = {
  "standard/dark": [
    "keyword a5aaff bold", "keyword.batch a5aaff bold", "type 6cc0a8", "string aed992",
    "string.quote aed992", "string.dollar aed992", "string.escape aed992 bold", "number ec9563",
    "number.hex ec9563", "comment 939ca8 italic", "variable e6a6d6", "predefined e3c27a",
    "operator 66bccc", "delimiter 123456", "identifier dcdde3", "identifier.temp dcdde3 italic",
  ],
  "standard/light": [
    "keyword 22309a bold", "keyword.batch 22309a bold", "type 205f58", "string 23703a",
    "string.quote 23703a", "string.dollar 23703a", "string.escape 23703a bold", "number 9c4a00",
    "number.hex 9c4a00", "comment 5e636d italic", "variable 8a1f7c", "predefined 5f5300",
    "operator 0e6584", "delimiter 123456", "identifier 24272d", "identifier.temp 24272d italic",
  ],
  "color-blind/dark": [
    "keyword b9aafc bold", "keyword.batch b9aafc bold", "type 81c38e", "string c9fdb7",
    "string.quote c9fdb7", "string.dollar c9fdb7", "string.escape c9fdb7 bold", "number f8914f",
    "number.hex f8914f", "comment b1aaa8 italic", "variable d09ec7 italic", "predefined e3dd73",
    "operator 9fddf9", "delimiter 123456", "identifier f3f6f3", "identifier.temp f3f6f3 italic",
  ],
  "color-blind/light": [
    "keyword 320a7f bold", "keyword.batch 320a7f bold", "type 003d3e", "string 5f5a00",
    "string.quote 5f5a00", "string.dollar 5f5a00", "string.escape 5f5a00 bold", "number 5f3631",
    "number.hex 5f3631", "comment 585857 italic", "variable 7c32a1 italic", "predefined 3a2d01",
    "operator 2a5e73", "delimiter 123456", "identifier 080804", "identifier.temp 080804 italic",
  ],
  "neon/dark": [
    "keyword ff67c3 bold", "keyword.batch ff67c3 bold", "type f0e070", "string acf56a",
    "string.quote acf56a", "string.dollar acf56a", "string.escape acf56a bold", "number 00e4ff",
    "number.hex 00e4ff", "comment aaa596 italic", "variable bb86ff", "predefined 3db5ff",
    "operator fe7b18", "delimiter 123456", "identifier eceef2", "identifier.temp eceef2 italic",
  ],
  "neon/light": [
    "keyword b80086 bold", "keyword.batch b80086 bold", "type 795d00", "string 456800",
    "string.quote 456800", "string.dollar 456800", "string.escape 456800 bold", "number 036680",
    "number.hex 036680", "comment 665b64 italic", "variable 4c009f", "predefined 0858d6",
    "operator 9e4600", "delimiter 123456", "identifier 1a1d2c", "identifier.temp 1a1d2c italic",
  ],
  "forest/dark": [
    "keyword 4bbf70 bold", "keyword.batch 4bbf70 bold", "type af94d2", "string e2a6ba",
    "string.quote e2a6ba", "string.dollar e2a6ba", "string.escape e2a6ba bold", "number f8dc90",
    "number.hex f8dc90", "comment a1a293 italic", "variable 87deed", "predefined 8fbce9",
    "operator fbc2ac", "delimiter 123456", "identifier ebebe7", "identifier.temp ebebe7 italic",
  ],
  "forest/light": [
    "keyword 006c2e bold", "keyword.batch 006c2e bold", "type 735390", "string 71223b",
    "string.quote 71223b", "string.dollar 71223b", "string.escape 71223b bold", "number 573d00",
    "number.hex 573d00", "comment 5f5f54 italic", "variable 004e62", "predefined 093c76",
    "operator 5b4036", "delimiter 123456", "identifier 1f251b", "identifier.temp 1f251b italic",
  ],
  "ember/dark": [
    "keyword f88f03 bold", "keyword.batch f88f03 bold", "type 77b3a4", "string a3c77b",
    "string.quote a3c77b", "string.dollar a3c77b", "string.escape a3c77b bold", "number a7a5f9",
    "number.hex a7a5f9", "comment af9b83 italic", "variable f19fe0", "predefined f8d977",
    "operator ffbe98", "delimiter 123456", "identifier edebe5", "identifier.temp edebe5 italic",
  ],
  "ember/light": [
    "keyword 9e4c00 bold", "keyword.batch 9e4c00 bold", "type 004745", "string 456947",
    "string.quote 456947", "string.dollar 456947", "string.escape 456947 bold", "number 403c7a",
    "number.hex 403c7a", "comment 6c6260 italic", "variable 8a4670", "predefined 3f3c00",
    "operator 5f3b2a", "delimiter 123456", "identifier 13110f", "identifier.temp 13110f italic",
  ],
  "ocean/dark": [
    "keyword 47a1ff bold", "keyword.batch 47a1ff bold", "type f2da63", "string 6bebb2",
    "string.quote 6bebb2", "string.dollar 6bebb2", "string.escape 6bebb2 bold", "number fa9160",
    "number.hex fa9160", "comment 949fab italic", "variable c3baff", "predefined 33ecfe",
    "operator 30c5f0", "delimiter 123456", "identifier eceff0", "identifier.temp eceff0 italic",
  ],
  "ocean/light": [
    "keyword 0057bf bold", "keyword.batch 0057bf bold", "type 7b5b00", "string 00704a",
    "string.quote 00704a", "string.dollar 00704a", "string.escape 00704a bold", "number a24601",
    "number.hex a24601", "comment 575b62 italic", "variable 562c8a", "predefined 004745",
    "operator 13687c", "delimiter 123456", "identifier 13161a", "identifier.temp 13161a italic",
  ],
};

/** The tokens the six themes styled before the new token kinds (syntaxTheme.ts TOKEN_ROLES, 2026-11). */
const PRE_EXISTING = FROZEN_RULES["standard/dark"].map((r) => r.split(" ")[0]);

describe("T2 frozen: the six syntax themes are unchanged", () => {
  it("SYNTAX_THEMES deep-equals the frozen copy: ids, labels, descriptions, every hex in both modes, font styles", () => {
    expect(SYNTAX_THEMES).toEqual(FROZEN_THEMES);
    expect(SYNTAX_THEME_IDS).toEqual(["standard", "color-blind", "neon", "forest", "ember", "ocean"]);
    expect(DEFAULT_SYNTAX_THEME).toBe("standard");
    expect(syntaxThemeById(undefined)).toBe(SYNTAX_THEMES[0]);
  });

  it.each(SYNTAX_THEMES.flatMap((t) => (["dark", "light"] as const).map((m) => [`${t.id}/${m}`, t, m] as const)))(
    "%s: the Monaco rules for the 16 pre-existing tokens are the frozen list (the editor fg is #123456 here)",
    (name, t, mode) => {
      const rules = syntaxRules(t, mode, "#123456").filter((r) => PRE_EXISTING.includes(r.token));
      expect(rules.map((r) => `${r.token} ${r.foreground}${r.fontStyle ? ` ${r.fontStyle}` : ""}`)).toEqual(FROZEN_RULES[name]);
    },
  );
});

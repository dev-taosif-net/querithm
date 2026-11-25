# App Themes and Default syntax colors

Status: **implemented in the working tree** (2026-11-24); not committed. See "Implementation status" below.
- Full design, with `path:line` citations: [app-themes/architecture.md](app-themes/architecture.md).
- Final color data: [app-themes/themes.json](app-themes/themes.json).
- Checker: `node plans/app-themes/check.mjs plans/app-themes/themes.json`. It prints `TOTAL FAILURES: 0` for this set.
- Test corpus seed: [app-themes/stress.sql](app-themes/stress.sql).

## 0. Implementation status

| Slice | State | Notes |
|---|---|---|
| S0 safety net | done | `highlightGolden.test.ts` pins every character of a 6-dialect corpus under all 6 syntax themes, through Monaco's real lexer and theme service; `syntaxThemeFrozen.test.ts` pins Standard |
| S1 editor chrome + component states | done | find/squiggles/word/bracket in `monaco.ts`; button `:active`, state tokens with fallbacks, disabled plain, focus offset, glyph toasts and banners, literal tints → tokens, `text-accent-fg`; ad-hoc icon and link buttons → `.q-icon-btn` / `.q-link` |
| S2 settings per changed key | done | `persist()` re-reads and writes only dirty keys; `syntaxTheme` mirror; adoption |
| S3 lexical token kinds | done | `@@x` is `predefined.global` (the rule is written `@@@@`: Monarch reads `@@` as one `@`). Owner decision 2026-11-24: fix it although the six syntax themes change there: T-SQL/MySQL `@x` now shows in the variable color (was the function color), `@@x` as one function-colored token (its first `@` was plain). Golden diff checked per character: only those characters changed, in those two dialects |
| S4 semantic tokens | done | `semanticSpans` (6 dialects), range provider gated by the palette, stale tokens dropped on palette change; p95 0.24–0.37 ms on the 12k-line fixture |
| S5 theme state + Default + notice | done | `appThemes.ts`, `resolve.ts`, `syntaxColors`, `darkTheme` / `lightTheme`, `italicComments`, one-time migration toast with Use Standard |
| S6 start screen | done; Rust verified in CI only | `querithm.themeBoot` + `boot-theme.js` + `boot-splash.css`; `cachedThemeChoice` before the settings load and `setBootColors` (§6.2, added after review). Shell (`theme.rs`, §6.3): `<word> #dark-bg #light-bg`, strict parse, single-flight writes bounded at 2 s. No Rust toolchain on the dev machine: `cargo check` / `cargo test` run in CI |
| S7 seven themes | done | generated `<style>`; every `contrast.test.ts` rule now runs for all 9 themes; final values in `app-themes/themes.json` |
| S8 picker | done | `AppThemePicker` (Single theme / Match system), Default syntax card, Italic comments row |
| S9 docs | done | ARCHITECTURE.md §12, §14 #38–39, §16 rows; `docs/release-notes/v0.1.7.md` |
| Bench | measured | §14 #39 `39switch`: theme switch with 30 tabs, 12k-line tab in front, p95 31–36 ms in 5 of 6 runs (one 54 ms), target ≤ 50 ms; Windows headless Chrome, without the Docker fixture |

Changes made during implementation, because the repo's own contrast test runs every rule on every surface (stricter than
the design checkers):
- Floe / Vellum: accent 2–4 % darker; warning row tint 0.11.
- Cortado / Fathom / Damson / Juniper: error red slightly lighter, so it reads on its own 15 % tint over popups.
- Every new theme: tuned alphas for a selected error row and for find marks in the JSON/XML viewer.
- Selected rows that used `bg-accent/15` (schema compare, trace, ERD) now use the selection token `--q-row-sel`.

`check.mjs` and `check-states.mjs` report 0 failures on the final data.

## 1. What ships
- **Seven new App Themes** join today's Dark and Light. Each new theme has its own background undertone, so no two
  look alike.
- Today's two themes stay byte-identical, renamed **Querithm Dark** (`dark`) and **Querithm Light** (`light`).
- A new Syntax colors choice, **Default**, becomes the default. It uses the active App Theme's own SQL palette, so
  switching the App Theme also switches the editor colors.
- **Standard is untouched**: same id, same hexes, same font styles, and the same color for every character it colors
  today.
- The other five syntax themes are untouched too: Color-blind safe, Neon, Forest, Ember and Ocean. All six stay at
  ≥ 4.5:1 on every editor layer of every new App Theme.
- Every App Theme, Querithm Dark and Light included, has its own Default palette. It colors these new roles explicitly:
  TRUE/FALSE/NULL, word operators, `::`, bind parameters, CTE names and schema prefixes.
- On Querithm Dark and Light, the Default palette is Standard's 12 hexes plus those new roles.
- Each new theme also has its own button states (rest, hover, pressed, focus, disabled) and its own status family
  (warning, error, success, info) on every surface: toasts, banners, Messages rows, Run/Stop, environment words and the
  warning squiggle (§10).

## 2. The nine App Themes
Surfaces: bg (editor), panel (sidebar and current line), raised (popups). Status: info / success / warning / error.

| Name | id | Mode | bg | panel | raised | fg | muted | accent | status | fg/bg | Identity |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Cortado | `cortado` | dark | `#221a13` | `#2c221a` | `#342921` | `#e2dacb` | `#a99f94` | `#dbb970` | `#79b3d7` / `#73ba59` / `#ffb389` / `#ff8596` | 12.3:1 | Roasted-espresso brown surfaces (h 63), crema-cream text and a golden crema accent |
| Fathom | `fathom` | dark | `#011c22` | `#07242a` | `#0c2c32` | `#d7dedb` | `#8ba0a5` | `#5fccc8` | `#7ab9e5` / `#7ddd9c` / `#eab151` / `#fa787c` | 12.9:1 | Deep-sea teal-navy (h 215, clearly greener than Querithm Dark's blue-grey) with an aqua accent and a neutral sea-mist foreground. |
| Inkwell | `inkwell` | dark | `#1a0621` | `#220e29` | `#2a1531` | `#e0e0e4` | `#968f9f` | `#b082f7` | `#6abad9` / `#89d49a` / `#e8be62` / `#ec7380` | 14.6:1 | Royal violet ink (h 317, L 0.174, C 0.059) |
| Damson | `damson` | dark | `#361d23` | `#3e2329` | `#45282f` | `#ecdfdf` | `#b4a3a6` | `#f0a3c4` | `#8bbfde` / `#73dea4` / `#f9c764` / `#ff9380` | 11.9:1 | Damson-wine dusk (h 5, L 0.27, the lightest of the darks) with a pastel rose accent, rose keywords and honey strings |
| Juniper | `juniper` | dark | `#122216` | `#182a1c` | `#1e3123` | `#dedcd0` | `#9aa9a0` | `#b7d978` | `#94c9e8` / `#56c9a2` / `#f8b05e` / `#ff828b` | 12.0:1 | Evergreen pine (h 151) with a sunlit-lime accent and parchment-tinted text |
| Querithm Dark | `dark` | dark (today, unchanged) | `#0f1117` | `#161922` | `#1e222e` | `#e6edf3` | `#8c949e` | `#60a5fa` | `#60a5fa` / `#10b981` / `#f59e0b` / `#f97b7b` | 16.0:1 | Today's Dark, unchanged |
| Floe | `floe` | light | `#e8f6f6` | `#ddeff0` | `#d5e6e6` | `#1d2832` | `#515f6a` | `#005d6f` | `#135aa7` / `#006444` / `#805000` / `#900d30` | 13.5:1 | Clear-morning sea-ice white |
| Vellum | `vellum` | light | `#ffede0` | `#f7e6da` | `#f0dfd3` | `#37281e` | `#6a5a4c` | `#883770` | `#1c5ba1` / `#006444` / `#805000` / `#8b162c` | 12.4:1 | Warm bisque paper (h 58, C 0.026) with sepia-brown ink and a mulberry accent |
| Querithm Light | `light` | light (today, unchanged) | `#ffffff` | `#f6f8fa` | `#eaeef2` | `#1f2328` | `#59636e` | `#2252cd` | `#2252cd` / `#05694d` / `#97430d` / `#b11c1c` | 15.8:1 | Today's Light, unchanged |

`themes.json` holds every other token: `panel2`, `hover`, `line`, `lineStrong`, `faint`, `mutedHl`, `accentSolid`,
`accentFg`, `errSolid`, `null`, `sel`, `active`, `rowSel`, `overlaySel`, `warnSolid`, `warnSolidFg`, `ident`,
`suggestMatch`, `diffInsert` and `diffRemove`.

Env tags, object icons and chart slots inherit the mode's Querithm family. A theme overrides one of them only where a
contrast test fails on its surfaces. Today that is env-tag text on the selected row.

## 3. Default SQL palettes
| Role | Cortado | Fathom | Inkwell | Damson | Juniper | Querithm Dark | Floe | Vellum | Querithm Light |
|---|---|---|---|---|---|---|---|---|---|
| Keyword (bold) | `#e7a551` | `#5bdcb8` | `#7ad1ff` | `#f79dbc` | `#b7db69` | `#a5aaff` | `#005a62` | `#751349` | `#22309a` |
| Built-in function | `#96cda4` | `#67b9fd` | `#f8ae83` | `#b6a6fb` | `#efa3c7` | `#e3c27a` | `#8c3bb2` | `#2e5aa4` | `#5f5300` |
| Data type | `#76c7cb` | `#afa6d9` | `#bfa0d3` | `#e3ba98` | `#c7b179` | `#6cc0a8` | `#6e5f27` | `#00696a` | `#205f58` |
| String | `#e7b2c1` | `#e9a88f` | `#9ce0b6` | `#e4bc64` | `#78d7d6` | `#aed992` | `#45682e` | `#406a36` | `#23703a` |
| Number | `#e7d686` | `#e0cb74` | `#bcd972` | `#62d9de` | `#c6a7eb` | `#ec9563` | `#891e57` | `#904100` | `#9c4a00` |
| TRUE / FALSE / NULL | `#c898da` | `#d68dd0` | `#ffafd2` | `#c1df9d` | `#eba285` | `#f8acb5` | `#3435a9` | `#543b86` | `#832543` |
| Table / column / alias | `#e5ddce` | `#d9dfdf` | `#e3e2ed` | `#e5dcda` | `#e0dfcd` | `#dcdde3` | `#232b2e` | `#382b22` | `#24272d` |
| Operator, word operator (bold), `::` | `#d7b0a1` | `#8cc4d0` | `#9faece` | `#d4b3df` | `#97c3a4` | `#66bccc` | `#615c85` | `#724f3a` | `#0e6584` |
| Comment (italic) | `#a7a29f` | `#99a6ab` | `#a1a1a1` | `#9aa3a0` | `#a2a5a3` | `#939ca8` | `#65605a` | `#665f57` | `#5e636d` |
| CTE name | `#9adcfc` | `#bae3a4` | `#ffc6c4` | `#b6deea` | `#a6d9f4` | `#8fd5ff` | `#003f72` | `#075063` | `#155ed0` |
| Schema prefix | `#94a7bc` | `#bca995` | `#90b3be` | `#98b798` | `#c3ac9b` | `#c5ac98` | `#73533c` | `#545967` | `#6c5644` |
| Bind parameter `$1 :p ? @p` | `#bbbaff` | `#fec1f0` | `#e3ce77` | `#a5cdfd` | `#9db9f8` | `#e6a6d6` | `#723300` | `#6c4b00` | `#543763` |
| Variable `@x` `@@x` | `#bbbaff` | `#fec1f0` | `#e3ce77` | `#a5cdfd` | `#9db9f8` | `#e6a6d6` | `#723300` | `#6c4b00` | `#8a1f7c` |
| `( )` depth 1 | `#c7bcac` | `#b0c2c3` | `#bdbcca` | `#cab9b8` | `#bcc0b2` | `#c2c7d1` | `#4a585e` | `#5e534a` | `#4a505a` |
| `( )` depth 2 | `#efac8d` | `#dfb585` | `#d1c681` | `#d5ba82` | `#d0c689` | `#d9b360` | `#734c00` | `#006266` | `#7d5a00` |
| `( )` depth 3 | `#81cae1` | `#dfabd2` | `#7fcfc4` | `#97c2f0` | `#e2aace` | `#c49cf0` | `#4a4c90` | `#723c7e` | `#7b35b0` |

Every palette meets these rules. The checker enforces them, and the planned T8 test will too.
- **Readable on every layer:** each role reads at ≥ 4.5:1 on the editor, the current line, the selection (alone and over
  the current line), both diff tints, the find match and the word highlight. On the find match (warn at 10 %), comments
  need only ≥ 4.0.
- **Roles stay apart:** role pairs are ≥ ΔE00 12 apart, and key pairs ≥ 15, in normal vision. Key pairs stay ≥ 8 apart
  under protan, deutan and tritan simulation.
- **Neighbours stay apart:** pairs that sit side by side (constant/operator, constant/function, cte/schema) are ≥ 15
  apart, and ≥ 5 under each simulation.
- **CTE names stand out:** they are ≥ 18 from identifiers.
- **Comments are quiet but legible:** they are ≥ 1.5 contrast quieter than identifiers. On dark themes they reach APCA
  |Lc| ≥ 48.
- **No glare, no false errors:** dark-theme token chroma is ≤ 0.17, and every role is ≥ ΔE 10 from the theme's error red.
- **Themes look different:** keyword hues are ≥ ΔE 19 apart across themes. Any two themes' palettes differ by ≥ 18.7 on
  average.

## 4. How it was designed
Six seats used one checker and one renderer. The checker measured:
- contrast (WCAG 2 and APCA);
- color difference (CIEDE2000);
- color-blind simulation (Machado);
- distance from the inspiration themes.

1. **Color Palette Theorist** proposed the 7 App Themes. It found the key constraint: to keep the six existing syntax
   themes at 4.5:1, every dark editor layer must stay at luminance ≤ 0.031, and every light layer at ≥ 0.77.
2. **SQL Syntax Engineer** made the 9 Default palettes.
3. **Theme State & Schema Architect** designed the data, state, migration and shell work.
4. **Eye Doctor** rejected no theme, but required:
   - brighter comments on four dark themes;
   - squiggles in the theme's own colors;
   - an Italic comments setting;
   - guidance text for each theme.
5. **Devil's Advocate 1 (anti-clone)** rejected two designs:
   - Inkwell, which looked like Catppuccin Mocha with One Dark's keyword;
   - Damson's SQL palette, which reused Mocha's string and function colors.

   It also required these changes:
   - move Vellum away from Solarized Light;
   - rename "Glacier", an existing theme name, to **Floe**;
   - remove near-copies of Night Owl's lime and Monokai Pro Ristretto's function color.
6. **Devil's Advocate 2 (real SQL)** rejected two existing editor defaults, both unreadable today:
   - Monaco's default find match;
   - the warning squiggle on light themes.

   It also required these changes:
   - the side-by-side pair rule, and CTE names ≥ 18 from identifiers;
   - bold word operators;
   - its own Default palette on Querithm Dark and Light: it overruled "Default = Standard on Querithm themes";
   - fixes for six state holes (V7–V12), all accepted.

A merge round answered every finding with 114 changes. The first draft fails 22 of the final rules. The final set
fails none.

## 5. Key design decisions
Details are in architecture.md.

**Settings**
- `theme` keeps its three values: `dark | light | system`.
- New `darkTheme` and `lightTheme` keys hold the picks. Defaults: `dark` and `light`.
- `syntaxTheme` becomes `syntaxColors`: `default` or one of the six themes.
- `syntaxTheme` is still written, as a mirror for older builds.
- New key: `italicComments`.

**Migration**
- A saved `syntaxTheme: "standard"` cannot be told apart from the old default. It becomes `default`, once.
- A one-time notice offers **Use Standard**.
- Explicit non-Standard choices are kept.

**Document attributes**
- `data-theme` keeps meaning the mode.
- New `data-app-theme` names the theme.
- A theme's CSS applies only when both agree. Otherwise the Querithm theme of that mode shows.

**Token data**
- `src/theme/appThemes.ts` is the single source.
- One generated `<style>` is inserted before the first render.
- `tokens.css` stays unchanged.

**Token kinds**
- New Monarch tokens: `keyword.constant`, `keyword.operator`, `type.cast`, `variable.bind`, `delimiter.bind` and
  `predefined.global`.
- New semantic tokens: `identifier.cte` and `identifier.schema`. They come from the tier-1 completion analyzer: no RPC,
  all six dialects, ≤ 4 ms p95.
- Each new token falls back to its old role in the six existing syntax themes.

**Editor chrome follows the theme**
- Find match: warn at 10 %, plus a border.
- Squiggles: the theme's err and info colors, and an amber mark for warnings.
- Word highlight and bracket match use theme tokens.
- Stale semantic colors are cleared on every palette change.

**Start screen and shell**
- The boot cache keeps colors, not theme ids.
- The Rust shell's file becomes `<dark|light|system> #darkbg #lightbg`.
- Adding a theme later touches no Rust.

**Settings saves**
- A save writes only the changed keys, after a re-read.
- This stops two windows from reverting each other.
- It also keeps theme ids from a newer build that this build does not know.

## 6. Slices
| Order | Slice | Ships |
|---|---|---|
| 1 | S0: safety net (golden test + frozen Standard) | first |
| 2 | S1: editor chrome and component states: find, squiggles, glyphs, `accent-fg`, `:active`, state tokens with today's fallbacks, opaque toasts (fixes today's bugs) | alone |
| 3 | S2: settings persistence | alone |
| 4 | S3: lexical tokens | release A, "Default" |
| 5 | S4: semantic tokens | release A |
| 6 | S5: theme state + Default + notice | release A |
| 7 | S6: start screen + shell | release B, "App Themes" |
| 8 | S7: the seven themes, with their `states` values | release B |
| 9 | S8: theme picker | release B |
| 10 | S9: docs (§12, §14 rows 31–32, §16) | with each release |

## 7. Decisions for the owner
1. **Picker model.** Recommended: GitHub-style "Single theme | Match system", with a dark pick and a light pick.
2. **Vellum's paper.** The pick is bisque `#ffede0`, ΔE 6.8 from Solarized Light. Any true cream with chroma ≤ 0.03
   sits within ΔE 5 of Solarized Light, Gruvbox Light or Rosé Pine Dawn. Recommended: keep bisque.
3. **Migrate saved "standard" to Default**, with the notice. Recommended.
4. **Write only changed settings keys.** Recommended. It costs one extra read per save and needs a §16 row.
5. **Name "Floe".** No editor or terminal theme was found under that name (medium confidence).
6. **Querithm Light stays pure white** (frozen). Its guidance line points to Vellum or Floe.

## 8. Already broken today (found during review)
- The current find match drops tokens to 2.3–2.7:1 on Querithm Dark and Light. Fixed in S1.
- The warning squiggle reads at 3.12:1 on Querithm Light's background, and at 2.39:1 on a selection over the current
  line. Fixed in S1.
- Querithm Light's `--q-null` (`#a16207`) reads at 3.92:1 on a hovered row and 4.22:1 on `--q-raised`.
- Theme-keyed caches go stale between two themes of the same mode: `ResultGrid.tsx:241`, `SessionsPane.tsx:31` and
  `PlanView.tsx:206`. Fixed in S5.
- Two app windows revert each other's settings, because each save writes the whole object. Fixed in S2.
- Toasts and banners show error vs warning by color alone. On Querithm Light, warn vs err is ΔE 1.3 for deuteranopes
  (S1 adds glyphs).
- `bg-accent-solid text-white` is hard-coded in five components. It would read at 1.8–2.2:1 on the light-accent themes
  (S1).
- Default and toolbar hover is only ΔE 2.0–2.9 from rest on Querithm Dark and Light. A disabled Primary keeps its label
  at about 2:1 (S1 draws it plain).
- Standard Light has two weak pairs under color-blind simulation:
  - keyword/variable, ΔE 4.2 apart (protan);
  - type/comment, ΔE 6.0 apart (deutan).

  They stay as they are, because Standard is frozen.

## 9. Which theme when
Settings shows each line as the theme card's description.

| Theme | Guidance |
|---|---|
| Cortado | Warm, low-blue dark. A good pick for evenings and dim rooms. |
| Fathom | Cool sea-teal dark with neutral text. Good in dim rooms. |
| Inkwell | Darkest, highest-contrast dark. If text seems to glow, try Damson or Cortado. |
| Damson | Softest dark: lightest background, lowest contrast. Kindest for astigmatism. |
| Juniper | Green-grey dark with a lime accent. Good for dim rooms. |
| Floe | Crisp cool light theme. Best in bright offices and daylight. |
| Vellum | Warm paper light theme with the least blue light. Long days and evenings. |
| Querithm Dark | Classic high-contrast dark. |
| Querithm Light | Classic pure white, the brightest theme. For long sessions, try Vellum or Floe. |

## 10. Buttons, states and status colors (round 2)

### What changes
- Each new theme now defines its own button states: rest, hover, pressed, focus and disabled.
- Each new theme also defines its status family (info, success, warning, error). It is used in status text, toasts,
  banners, Messages rows, Run/Stop, the Connect dialog's environment words, the PROD stripe and the warning squiggle.
- Querithm Dark and Light keep today's colors: the new CSS variables fall back to today's formulas.
- Full design: architecture.md §13.
- Checker: `node plans/app-themes/check-states.mjs plans/app-themes/themes.json` prints `TOTAL FAILURES: 0`. Today's
  formulas on the 7 new themes gave 117 failures.

### Buttons

Values are rest → hover → pressed.

| Theme | Primary | Default | Danger | Ghost / toolbar hover → pressed | Focus ring | Disabled opacity |
|---|---|---|---|---|---|---|
| Cortado | `#d4aa5a` → `#eabc6c` → `#f8cb80` | `#342921` → `#443429` → `#463421` | `#cf3638` → `#bb2729` → `#a91e1e` | `#403025` → `#493226` | `#e8c67d` | 0.45 |
| Fathom | `#54c2be` → `#64d6d2` → `#78e6e2` | `#0c2c32` → `#093944` → `#00384a` | `#cf3638` → `#bb2729` → `#a91e1e` | `#02353f` → `#00394a` | `#5fccc8` | 0.45 |
| Inkwell | `#7145b5` → `#7751ca` → `#7c5ad8` | `#2a1531` → `#392141` → `#38204b` | `#cf3638` → `#bb2729` → `#a91e1e` | `#371f3f` → `#3c1e4a` | `#b082f7` | 0.42 |
| Damson | `#e99dbe` → `#f9b0d2` → `#ffc4df` | `#45282f` → `#562e35` → `#5e2d3c` | `#cf3638` → `#bb2729` → `#a91e1e` | `#4c242c` → `#583037` | `#f2afdb` | 0.45 |
| Juniper | `#abcc6b` → `#bfe180` → `#d5f098` | `#1e3123` → `#273f2e` → `#264127` | `#cf3638` → `#bb2729` → `#a91e1e` | `#233b29` → `#20422b` | `#b7d978` | 0.45 |
| Querithm Dark (today) | `#2563eb` → `#1f54c8` → `#1f54c8` | `#1e222e` → `#262b38` → `#262b38` | `#dc2626` → `#bb2020` → `#bb2020` | `#262b38` → `#262b38` | `#60a5fa` | 0.45 |
| Floe | `#006f83` → `#006071` → `#005261` | `#d5e6e6` → `#c3dddc` → `#b7dfe2` | `#cf3638` → `#bb2729` → `#a91e1e` | `#c5dcdb` → `#badee2` | `#006779` | 0.55 |
| Vellum | `#8b3b74` → `#7a2b64` → `#6b1e57` | `#f0dfd3` → `#e0d5c7` → `#e7d3c2` | `#cf3638` → `#bb2729` → `#a91e1e` | `#e4d4c4` → `#ebd2c1` | `#884183` | 0.55 |
| Querithm Light (today) | `#2563eb` → `#1f54c8` → `#1f54c8` | `#eaeef2` → `#e1e6ec` → `#e1e6ec` | `#dc2626` → `#bb2020` → `#bb2020` | `#e1e6ec` → `#e1e6ec` | `#2252cd` | 0.45 |

**Direction rule.**
- Each step moves the fill away from the surface: lighter on dark themes, darker on light ones.
- Pressed differs from hover by hue or chroma, never by moving toward the label's lightness.
- Light accent fills (Cortado, Fathom, Damson, Juniper) get lighter on hover. Today's 85 %-black mix made them muddy.

**Disabled buttons.**
- A disabled Primary, Run or Stop button looks like the plain button, as Danger already does.
- Primary and Danger focus rings sit 2 px off the fill.

### Status colors

Alphas are percentages.

| Theme | Info | Success | Warning | Error | Warning squiggle | Toast / banner | Error row / warning row | Run / Stop tint (rest→hover→pressed) |
|---|---|---|---|---|---|---|---|---|
| Cortado | `#79b3d7` | `#73ba59` | `#ffb389` | `#ff8596` | `#cca700` | 12 / 12 | 12 / 20 | 8→11.5→15 / 6→9.5→13 |
| Fathom | `#7ab9e5` | `#7ddd9c` | `#eab151` | `#fa787c` | `#cca700` | 12 / 12 | 10 / 15 | 8→12→15 / 7.5→11→14 |
| Inkwell | `#6abad9` | `#89d49a` | `#e8be62` | `#ec7380` | `#cca700` | 12 / 12 | 10 / 15 | 8→11→14 / 6.5→11.5→16 |
| Damson | `#8bbfde` | `#73dea4` | `#f9c764` | `#ff9380` | `#e3cf55` | 12 / 12 | 13 / 15 | 8→11→14 / 4.5→9→13.5 |
| Juniper | `#94c9e8` | `#56c9a2` | `#f8b05e` | `#ff828b` | `#cca700` | 12 / 12 | 10 / 15 | 8→12.5→17 / 7.5→10.5→13 |
| Querithm Dark (today) | `#60a5fa` | `#10b981` | `#f59e0b` | `#f97b7b` | `#cca700` | 15 / 15 | 10 / 15 | 10→15→15 / 10→15→15 |
| Floe | `#135aa7` | `#006444` | `#805000` | `#900d30` | `#a87000` | 10 / 10 | 18 / 11 | 4.5→9.5→14 / 5→8.5→10.5 |
| Vellum | `#1c5ba1` | `#006444` | `#805000` | `#8b162c` | `#a87000` | 10 / 10 | 18 / 11 | 5→8.5→12 / 5→9→13 |
| Querithm Light (today) | `#2252cd` | `#05694d` | `#97430d` | `#b11c1c` | `#a87000` | 15 / 15 | 10 / 15 | 10→15→15 / 10→15→15 |

### Environment words in the Connect dialog

Each reads ≥ 4.5:1 on panel, hovered row, active row and footer. The Production stripe uses the PROD word color.

| Theme | LOCAL | DEV | TEST | STAGING | PROD word / stripe | PROD fill |
|---|---|---|---|---|---|---|
| Cortado | `#b5a89a` | `#60d0a6` | `#b598f2` | `#ffa661` | `#f67c7b` / `#f67c7b` | `#dc2626` |
| Fathom | `#9aafb4` | `#60d0a6` | `#b598f2` | `#f8b65d` | `#f47a79` / `#f47a79` | `#dc2626` |
| Inkwell | `#aea7b8` | `#60d0a6` | `#dba9e6` | `#f8b65d` | `#f47a79` / `#f47a79` | `#dc2626` |
| Damson | `#baa5a8` | `#60d0a6` | `#b598f2` | `#f8b65d` | `#fe8382` / `#fe8382` | `#dc2626` |
| Juniper | `#9eb0a5` | `#50d2a7` | `#b598f2` | `#f8b65d` | `#fb817f` / `#fb817f` | `#dc2626` |
| Querithm Dark (today) | `#94a3b8` | `#34d399` | `#a78bfa` | `#fbbf24` | `#f87171` / `#ef4444` | `#dc2626` |
| Floe | `#565f66` | `#00684c` | `#6b42aa` | `#805500` | `#900d2e` / `#900d2e` | `#b91c1c` |
| Vellum | `#6a5a4c` | `#00684c` | `#5546af` | `#7f5400` | `#900d2e` / `#900d2e` | `#b91c1c` |
| Querithm Light (today) | `#475569` | `#065f46` | `#6d28d9` | `#92400e` | `#b91c1c` / `#dc2626` | `#b91c1c` |

### Rules adopted from the State Critic

These apply to all themes and ship in slice S1.

1. **Never color alone.** Every toast, banner and Messages line shows a visible ✖ / ⚠ / ✓ / ℹ glyph before its text,
   and keeps its screen-reader prefix. Today toasts and banners show their kind by color alone. Error and warning toast
   fills are only ΔE 2.2–4.9 apart for deuteranopes.
2. **No hard-coded white on the accent fill.** `bg-accent-solid text-white` becomes `text-accent-fg` in five components
   (`panelKit.tsx:99`, `:338`, `ChartView.tsx:283`, `CellOutput.tsx:81`, `CellViewerPanel.tsx:88`). A test enforces it.
   On the light-accent themes that white is 1.8–2.2:1.
3. **Pressed keeps labels readable.** `fg`, `err`, `accent` and `muted` labels reach ≥ 4.5 on every hover and pressed
   fill.
4. **The warning squiggle has its own mark.** It stays `--q-warn-mark`, never an alias of `--q-warn`.
5. **Toasts sit on an opaque surface.** A toast is a wash on opaque `--q-raised`, with no backdrop blur over the grid.
   Toasts and banners get a 3 px full-hue left edge, and in-flow status rows a 2 px one.
6. **No literal tints.** About 25 literal tints (`bg-err/10`, `bg-warn/15`, the top-bar Run split) move to the tint
   tokens. A selected error row uses the row tint + 5 %.

## 11. Data file
`themes.json` has this shape:

```
{ "themes": [ { id, name, mode, legacy?, identity, eyeComfort,
                ui: { …30 tokens }, sql: { …16 roles }, fontStyle } ] }
```

- **`ui`:** each key maps to a `--q-*` variable, for example `panel2` → `--q-panel-2` and `info` → `--q-info`. The full
  map is `UI_TOKENS` in architecture.md §2.3.
- **`sql`:** maps to `AppTheme.sql` (architecture.md §2.4):
  - `operator` also fills `wordOperator` and `cast`;
  - `parameter` fills `bind`;
  - `variable` fills `systemVariable`.

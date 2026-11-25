// Start screen (§16): names the theme on the document before the first paint, so the window never opens in one theme
// and jumps to the other. The settings load only after the engine answers; the theme last shown is kept in the
// webview's own storage (src/app/theme.ts, same keys). Nothing kept means the setting's default, dark.
// A file of its own: the CSP allows no inline script, and the app's bundle runs too late for the first frame.
// App Themes (plans/app-themes §6.2): querithm.themeBoot keeps, for the dark and the light pick, the App Theme id and
// its start colours; they are applied only when every field is well formed (the script never knows a theme list: an id
// with no token block falls back to Querithm Dark / Light in the app).
(function () {
  var kept = null;
  var boot = null;
  try {
    kept = localStorage.getItem("querithm.theme");
    boot = JSON.parse(localStorage.getItem("querithm.themeBoot") || "null");
  } catch (e) {
    // Storage refused or damaged: the default.
  }
  var dark = kept === "light" ? false : kept === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches : true;
  var root = document.documentElement;
  var mode = dark ? "dark" : "light";
  root.setAttribute("data-theme", mode);
  var hex = /^#[0-9a-f]{6}$/i;
  function ok(c) {
    return typeof c === "string" && hex.test(c);
  }
  var p = boot && boot.v === 1 ? boot[mode] : null;
  // typeof first: /^[a-z]…$/.test(undefined) tests the string "undefined", which would pass.
  if (p && typeof p.id === "string" && /^[a-z][a-z0-9-]{0,31}$/.test(p.id) && ok(p.bg) && ok(p.fg) && ok(p.muted) && ok(p.idle) && ok(p.track)) {
    root.setAttribute("data-app-theme", p.id);
    root.style.setProperty("--boot-bg", p.bg);
    root.style.setProperty("--boot-fg", p.fg);
    root.style.setProperty("--boot-muted", p.muted);
    root.style.setProperty("--boot-idle", p.idle);
    root.style.setProperty("--boot-track", p.track);
  }
  // Version 1 only, as for the picks: the app reads nothing from another version (cachedThemeChoice, src/app/theme.ts).
  if (boot && boot.v === 1 && typeof boot.syntax === "string" && /^[a-z][a-z-]{0,23}$/.test(boot.syntax)) root.setAttribute("data-syntax", boot.syntax);
})();

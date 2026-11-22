// Start screen (§16): names the theme on the document before the first paint, so the window never opens in one theme
// and jumps to the other. The settings load only after the engine answers; the theme last shown is kept in the
// webview's own storage (src/app/theme.ts, same key). Nothing kept means the setting's default, dark.
// A file of its own: the CSP allows no inline script, and the app's bundle runs too late for the first frame.
(function () {
  var kept = null;
  try {
    kept = localStorage.getItem("querithm.theme");
  } catch (e) {
    // Storage refused: the default.
  }
  var dark = kept === "light" ? false : kept === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches : true;
  document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
})();

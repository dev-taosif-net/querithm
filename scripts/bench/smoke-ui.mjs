#!/usr/bin/env node
// UI smoke test: built webview + dev bridge + real sidecar + docker fixture, driven by headless Chrome.
// Captures console errors and screenshots of the main screens.  node scripts/bench/smoke-ui.mjs <out-dir>
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import puppeteer from "puppeteer-core";
import { collectLayout, findLayoutIssues } from "./layout-check.mjs";
import { fixturePassword } from "../fixture/fixture-secrets.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
// The fixture's passwords, read from scripts/bench/docker/compose.yml (L33); passed to the page as arguments, never logged.
const MSSQL_PW = fixturePassword("mssql");
const PG_PW = fixturePassword("pg");
const MYSQL_PW = fixturePassword("mysql");
const appVersion = JSON.parse(readFileSync(path.join(root, "src-tauri", "tauri.conf.json"), "utf8")).version;
const out = path.resolve(process.argv[2] ?? path.join(os.tmpdir(), "querithm-smoke"));
mkdirSync(out, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
// QUERITHM_UI_PORT / QUERITHM_BRIDGE_PORT move the smoke run off 1420/1421 while `pnpm dev:web` + `dev:bridge` run.
const uiPort = Number(process.env.QUERITHM_UI_PORT ?? 1420);
const bridgePort = Number(process.env.QUERITHM_BRIDGE_PORT ?? 1421);
// The SQL Server fixture's host, as for the integration tests (ARCHITECTURE §16).
const mssqlHost = process.env.QUERITHM_IT_MSSQL_HOST || "localhost";
if (!process.argv.includes("--no-build"))
  spawnSync("pnpm", ["build:web"], { cwd: root, stdio: "inherit", env: { ...process.env, VITE_BRIDGE_URL: `ws://127.0.0.1:${bridgePort}` } });

const bridge = spawn(process.execPath, [path.join(root, "scripts", "dev-bridge.mjs")], {
  // QUERITHM_FAKE_UPDATE: the bridge offers a fake release whose install fails (see dev-bridge.mjs). No QUERITHM_DEV_ENGINES:
  // the engine picker must offer SQLite, MySQL and DuckDB in a default build (they left the hidden-engine flag in 0.2.0).
  env: { ...process.env, QUERITHM_BENCH: "1", QUERITHM_FAKE_UPDATE: "9.9.9", QUERITHM_DEV_ENGINES: "", QUERITHM_DATA_DIR: mkdtempSync(path.join(os.tmpdir(), "qsmoke-")) }, stdio: ["ignore", "inherit", "inherit"],
});
const preview = spawn(process.execPath, [path.join(path.dirname(fileURLToPath(import.meta.resolve("vite/package.json"))), "bin", "vite.js"), "preview", "--port", String(uiPort), "--strictPort", "--host", "127.0.0.1"], { cwd: root, stdio: "ignore" });
const errors = [];
/** Layout audit findings (layout-check.mjs), one entry per issue with every screen it was seen on. */
const layoutIssues = new Map();
/** Running requests, their blockers and open transactions on the fixture SQL Server (diagnostics for a stuck step). */
function mssqlActivity() {
  const sql = "SET NOCOUNT ON; SELECT s.session_id, r.blocking_session_id AS blocked_by, r.wait_type, r.wait_time, r.command, s.program_name, " +
    "(SELECT COUNT(*) FROM sys.dm_tran_session_transactions x WHERE x.session_id = s.session_id) AS open_tx, LEFT(t.text, 160) AS sql " +
    "FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t " +
    "WHERE s.is_user_process = 1 AND s.session_id <> @@SPID ORDER BY s.session_id";
  const r = spawnSync("docker", ["exec", "querithm-fixture-mssql-1", "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", MSSQL_PW, "-C", "-W", "-s", "|", "-Q", sql], { encoding: "utf8", timeout: 15_000 });
  return r.status === 0 ? r.stdout : `(sqlcmd failed: ${r.error?.message ?? r.stderr})`;
}
/** A foreign session on the fixture SQL Server (sqlcmd in the container), left running in the background. */
function sqlBg(sql) {
  return spawn("docker", ["exec", "querithm-fixture-mssql-1", "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", MSSQL_PW, "-C", "-d", "SalesDB", "-Q", sql],
    { stdio: "ignore", env: { ...process.env, MSYS_NO_PATHCONV: "1" } });
}
/** A short statement on the fixture SQL Server; returns its single-column output. */
function sqlNow(sql, db = "SalesDB") {
  const r = spawnSync("docker", ["exec", "querithm-fixture-mssql-1", "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", MSSQL_PW, "-C", "-d", db, "-h", "-1", "-W", "-Q", "SET NOCOUNT ON; " + sql],
    { encoding: "utf8", timeout: 60_000, env: { ...process.env, MSYS_NO_PATHCONV: "1" } });
  return (r.stdout ?? "").trim();
}
/** One value from the MySQL 8.4 fixture, read outside Querithm. */
function mysqlNow(sql) {
  const r = spawnSync("docker", ["exec", "querithm-fixture-mysql84-1", "mysql", "-uroot", `-p${MYSQL_PW}`, "-N", "-B", "-e", sql], { encoding: "utf8", timeout: 30_000, env: { ...process.env, MSYS_NO_PATHCONV: "1" } });
  return (r.stdout ?? "").trim();
}
function pgNow(sql) {
  const r = spawnSync("docker", ["exec", "querithm-fixture-postgres-1", "psql", "-U", "postgres", "-Atc", sql], { encoding: "utf8", timeout: 30_000, env: { ...process.env, MSYS_NO_PATHCONV: "1" } });
  return (r.stdout ?? "").trim();
}
const background = [];
let browser;
try {
  await sleep(3000);
  // A busy port must fail the run, not silently test whatever server already listens there.
  if (preview.exitCode !== null || bridge.exitCode !== null) throw new Error(`port ${uiPort} or ${bridgePort} is in use; set QUERITHM_UI_PORT / QUERITHM_BRIDGE_PORT`);
  const chrome = ["/usr/bin/google-chrome", "/usr/bin/chromium", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "C:/Program Files/Google/Chrome/Application/chrome.exe"].find(existsSync);
  browser = await puppeteer.launch({ executablePath: chrome, headless: true, args: ["--window-size=1440,900"], defaultViewport: { width: 1440, height: 900 } });
  const page = await browser.newPage();
  page.on("console", (m) => {
    if (m.type() === "error") errors.push(m.text());
    else if (m.type() === "warn" || m.type() === "warning") console.log(`[page warn] ${m.text()}`);
  });
  page.on("response", (r) => { if (r.status() >= 400) errors.push(`HTTP ${r.status()} ${r.url()}`); });
  page.on("pageerror", (e) => {
    errors.push(`pageerror: ${e.message}`);
    console.log(`[pageerror] ${e.stack ?? e.message}`);
  });
  await page.goto(`http://127.0.0.1:${uiPort}/?bench`, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => window.__bench?.ready === true, { timeout: 30_000 });
  await page.waitForSelector(".monaco-editor", { timeout: 30_000 });
  // Layout audit at every screenshot, in both themes (the second one toggled and restored), plus the minimum window
  // (960×560, tauri.conf.json) for the screens in NARROW. Screens with findings are saved again as layout-<screen>-<theme>-<width>.png, findings marked.
  const auditLayout = async (name) => {
    const vp = page.viewport();
    const start = await page.evaluate(() => document.documentElement.dataset.theme || "dark");
    for (const theme of [start, start === "light" ? "dark" : "light"]) {
      if (theme !== start) {
        await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
        await sleep(200);
      }
      const found = findLayoutIssues(await page.evaluate(collectLayout));
      for (const i of found) {
        const key = `${i.kind}|${i.control}|${i.other ?? ""}`;
        const entry = layoutIssues.get(key) ?? { ...i, where: [] };
        entry.where.push(`${name} ${theme} ${vp.width}×${vp.height}`);
        layoutIssues.set(key, entry);
      }
      if (found.length) {
        await page.evaluate((rects) => {
          for (const r of rects) {
            const d = document.createElement("div");
            d.dataset.layoutMark = "";
            Object.assign(d.style, { position: "fixed", left: `${r.l - 2}px`, top: `${r.t - 2}px`, width: `${r.r - r.l + 4}px`, height: `${r.b - r.t + 4}px`, outline: "2px solid #ff00ff", zIndex: "2147483647", pointerEvents: "none" });
            document.body.append(d);
          }
        }, found.map((i) => i.at));
        await page.screenshot({ path: path.join(out, `layout-${name}-${theme}-${vp.width}.png`) });
        await page.evaluate(() => document.querySelectorAll("[data-layout-mark]").forEach((d) => d.remove()));
      }
    }
    await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, start);
    await sleep(200);
  };
  const NARROW = new Set(["00-welcome", "02-connected", "02b-explorer-search", "02c-explorer-200", "02d-explorer-600", "03-results", "01a-update-dialog", "03d-import-file", "03g-schema-compare", "08-prod-first", "10-postgres", "11-ssh-hostkey", "13-monitor-sessions", "18-monitor-banner"]);
  const shot = async (name) => {
    await page.screenshot({ path: path.join(out, `${name}.png`) });
    await auditLayout(name);
    if (NARROW.has(name)) {
      const vp = page.viewport();
      await page.setViewport({ width: 960, height: 560 });
      await sleep(500);
      await page.screenshot({ path: path.join(out, `${name}-960.png`) });
      await auditLayout(name);
      await page.setViewport(vp);
      await sleep(500);
    }
  };
  // Each popup a bottom-panel row opens (filter columns, Copy as, Export, …), audited open: the rows are 28 px and clip
  // what overflows them, so a popup drawn inside one shows a sliver or nothing (popup-clipped).
  const auditRowPopups = async (name) => {
    const labels = await page.$$eval("[data-q-panel-row] [aria-haspopup]:not([aria-disabled='true'])", (els) =>
      els.filter((e) => e.getClientRects().length).map((e, i) => { e.dataset.smokePopup = String(i); return e.getAttribute("aria-label") || e.textContent.trim(); }));
    if (!labels.length) errors.push(`${name}: no popup in the panel rows to audit`);
    for (const [i, label] of labels.entries()) {
      const expanded = (want) => page.waitForFunction((i, want) => (document.querySelector(`[data-smoke-popup="${i}"]`)?.getAttribute("aria-expanded") === "true") === want, { timeout: 3_000 }, i, want).then(() => true, () => false);
      await page.$eval(`[data-smoke-popup="${i}"]`, (e) => e.click());
      if (!(await expanded(true))) {
        errors.push(`${name}: "${label}" did not open its popup`);
        continue;
      }
      await sleep(250);
      await auditLayout(`${name}-popup-${i}`);
      await page.keyboard.press("Escape");
      if (!(await expanded(false))) errors.push(`${name}: "${label}" did not close on Escape`);
    }
    await page.$$eval("[data-smoke-popup]", (els) => els.forEach((e) => delete e.dataset.smokePopup));
  };
  // The audit must see what it claims to: a canary with one of each issue, removed again before the real screens.
  {
    await page.evaluate(() => {
      const c = document.createElement("div");
      c.id = "layout-canary";
      Object.assign(c.style, { position: "fixed", left: "16px", top: "120px", width: "320px", height: "180px", background: "#fff", color: "#000", font: "12px sans-serif", zIndex: "2147483000" });
      const btn = "position:absolute;padding:2px 6px;background:#eee;color:#000;border:0";
      c.innerHTML = `
        <span style="position:absolute;left:8px;top:10px">canary text under a button</span>
        <button style="${btn};left:40px;top:6px">canary over text</button>
        <button style="${btn};left:8px;top:40px">canary covered</button>
        <div style="position:absolute;left:4px;top:36px;width:130px;height:28px;background:rgba(255,0,0,.2)"></div>
        <button style="${btn};left:180px;top:40px;background:#1e3a8a;color:#1e40af">canary dark on dark</button>
        <div style="position:absolute;left:0;top:90px;width:150px;border-top:1px solid #000"></div>
        <button style="${btn};left:8px;top:80px;padding:4px 6px">canary on a line</button>
        <button style="${btn};left:180px;top:80px">canary A</button>
        <button style="${btn};left:200px;top:86px">canary B</button>
        <div style="position:absolute;left:8px;top:130px;width:60px;height:26px;overflow:hidden"><button style="width:140px;background:#eee;color:#000;border:0">canary clipped</button></div>
        <button style="${btn};left:180px;top:130px;width:30px;padding:0;white-space:nowrap">canary spilling label</button>
        <button style="${btn};left:240px;top:120px;width:60px;white-space:normal">canary wrapping label</button>
        <span style="position:absolute;left:8px;top:160px;color:#bbb">canary pale text</span>
        <div style="position:absolute;left:220px;top:150px;width:90px;height:20px;overflow:hidden"><div role="listbox" style="position:absolute;left:0;top:18px;width:90px;height:60px;background:#eee;color:#000">canary popup</div></div>`;
      document.body.append(c);
    });
    const found = findLayoutIssues(await page.evaluate(collectLayout)).filter((i) => i.control.includes("canary"));
    const kinds = new Set(found.map((i) => i.kind));
    for (const k of ["covered", "overlap", "text-overlap", "line-through", "clipped", "text-spill", "label-wrap", "contrast", "popup-clipped"]) if (!kinds.has(k)) errors.push(`layout audit missed its ${k} canary`);
    if (!found.some((i) => i.kind === "contrast" && i.control.includes("canary pale text"))) errors.push("layout audit missed its contrast canary outside a control");
    await page.evaluate(() => document.getElementById("layout-canary")?.remove());
  }
  // Welcome screen (no tab open): close the start tab, audit, then open a query tab from its primary button.
  await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label")?.startsWith("Close SQLQuery"))?.click());
  await page.waitForFunction(() => document.body.innerText.includes("A SQL IDE for SQL Server and PostgreSQL"), { timeout: 5_000 }).catch(() => errors.push("welcome screen did not appear after closing the last tab"));
  await sleep(300);
  await shot("00-welcome");
  await page.evaluate(() => [...document.querySelectorAll(".q-btn-primary")].find((b) => b.textContent?.startsWith("+ New Query"))?.click());
  await page.waitForSelector(".monaco-editor", { timeout: 10_000 }).catch(() => errors.push("New Query on the welcome screen did not open a tab"));
  await sleep(500);
  await shot("01-empty");
  // In-app update: Settings → Check for updates → Update button right of Settings → dialog → a failed install
  // leaves the app running, the tabs saved, and the button offering a retry.
  // What the topmost dialog says (for the log), and closing every dialog so one failed step does not block the
  // keyboard shortcuts of all later steps.
  const openDialogText = () => page.evaluate(() => [...document.querySelectorAll(".fixed.inset-0")].at(-1)?.innerText.replace(/\s+/g, " ").slice(0, 600) ?? "(no dialog)");
  const closeDialogs = async () => {
    for (let i = 0; i < 4 && (await page.evaluate(() => !!document.querySelector(".fixed.inset-0"))); i++) {
      await page.keyboard.press("Escape");
      await sleep(200);
    }
  };
  const clickButton = (label) => page.evaluate((l) => [...document.querySelectorAll("button")].find((b) => b.textContent?.trim() === l || b.getAttribute("aria-label") === l)?.click(), label);
  // Settings (redesign 2026-10-30): every page is audited in both themes and at 960×560; About shows the version the
  // shell reports (the bridge reads it from tauri.conf.json) and the credit; search finds a setting.
  await clickButton("Settings");
  await page.waitForSelector('[role="dialog"] [role="tablist"]', { timeout: 5_000 }).catch(() => errors.push("Settings did not open"));
  for (const [i, name] of ["Appearance", "Editor", "Formatting", "Query execution", "Tabs & recovery", "Object Explorer", "Keyboard shortcuts", "About"].entries()) {
    // Scoped to the dialog: editor and result tabs are tabs too.
    await page.evaluate((n) => [...document.querySelectorAll('[role="dialog"] [role="tab"]')].find((t) => t.textContent?.trim() === n)?.click(), name);
    await sleep(250);
    if (!(await page.evaluate((n) => document.querySelector('[role="dialog"] [role="tab"][aria-selected="true"]')?.textContent?.trim() === n, name))) errors.push(`Settings page ${name} did not open`);
    NARROW.add(`01s${i}-settings`);
    await shot(`01s${i}-settings`);
  }
  {
    const shown = await page.evaluate(() => document.querySelector("[data-about-version]")?.textContent ?? "");
    if (shown !== `Version ${appVersion}`) errors.push(`About shows "${shown}", expected "Version ${appVersion}"`);
    if (!(await page.evaluate(() => document.body.innerText.includes("Made by Taosif")))) errors.push("About does not show the credit");
  }
  await page.waitForFunction(() => document.body.innerText.includes("Check for updates automatically"), { timeout: 5_000 }).catch(() => errors.push("update setting missing"));
  await clickButton("Check for updates");
  await page.waitForSelector('button[aria-label="Update to Querithm 9.9.9"]', { timeout: 5_000 }).catch(() => errors.push("Update button did not appear"));
  if (!(await page.evaluate(() => document.querySelector('button[aria-label="Settings"]')?.nextElementSibling?.getAttribute("aria-label") === "Update to Querithm 9.9.9")))
    errors.push("Update button is not right of Settings");
  {
    await page.focus('input[aria-label="Search settings"]');
    await page.keyboard.type("font");
    await sleep(300);
    if (!(await page.evaluate(() => !!document.querySelector('[role="region"] input[aria-label="Font size"], [role="region"] #setting-editorFontSize'))))
      errors.push("Settings search did not find Font size");
    await page.keyboard.press("Escape"); // clears the search, keeps the dialog
    if (!(await page.evaluate(() => !!document.querySelector('[role="dialog"] [role="tablist"]')))) errors.push("Escape in the settings search closed the dialog");
  }
  await clickButton("Done");
  await clickButton("Update to Querithm 9.9.9");
  await page.waitForFunction(() => document.body.innerText.toLowerCase().includes("querithm 9.9.9 is available"), { timeout: 5_000 }).catch(() => errors.push("update dialog did not open"));
  if (await page.evaluate(() => !document.body.innerText.includes("<b>not html</b>"))) errors.push("release notes were not shown as plain text");
  await shot("01a-update-dialog");
  await clickButton("Update & Restart");
  await page.waitForFunction(() => document.querySelector('[role="alert"]')?.textContent?.includes("administrator prompt was cancelled"), { timeout: 10_000 })
    .catch(() => errors.push("failed update install was not reported"));
  if (await page.evaluate(() => !localStorage.getItem("querithm.workspace"))) errors.push("tabs were not saved before the update install");
  await shot("01b-update-failed");
  await clickButton("Later");
  if (await page.evaluate(() => !document.querySelector('button[aria-label="Update to Querithm 9.9.9"]'))) errors.push("Update button vanished after a failed install");
  const fx = await page.evaluate((host, password) => window.__bench.setupFixture({ host, port: 14333, user: "sa", password, db: "SalesDB" }), mssqlHost, MSSQL_PW);
  console.log("fixture", fx);
  await sleep(1500);
  await shot("02-connected");
  // Object Explorer (§10, §12): the server the user connected opens one level; open SalesDB › Tables, the table menu
  // from the keyboard (Shift+F10, with its target header), search scoped to the tab's database, Ctrl+Enter from the
  // search box inserts the name (it used to run the query), then the narrowest and widest docks, each audited in both
  // themes and in the 960×560 window.
  {
    const clickRow = (label) => page.evaluate((label) => {
      const r = [...document.querySelectorAll('[data-q-explorer] [role="treeitem"]')].find((x) => x.getAttribute("aria-label")?.startsWith(label));
      r?.click();
      return !!r;
    }, label);
    if (!(await clickRow("Database SalesDB"))) errors.push("explorer: the SalesDB row is missing after connecting");
    await page.waitForFunction(() => [...document.querySelectorAll('[data-q-explorer] [role="treeitem"]')].some((x) => x.getAttribute("aria-label")?.startsWith("Tables,")), { timeout: 15_000 }).catch(() => errors.push("explorer: SalesDB did not list its categories"));
    await clickRow("Tables,");
    await sleep(300);
    if (!(await clickRow("table dbo.Customers"))) errors.push("explorer: dbo.Customers is missing under Tables");
    await page.focus('[data-q-explorer] [role="tree"]');
    await page.keyboard.down("Shift"); await page.keyboard.press("F10"); await page.keyboard.up("Shift");
    const menuName = await page.waitForSelector('[role="menu"][aria-label^="Actions for"]', { timeout: 5_000 }).then((m) => m.evaluate((e) => e.getAttribute("aria-label"))).catch(() => null);
    if (menuName !== "Actions for DEV Bench fixture › SalesDB · dbo.Customers") errors.push(`explorer: Shift+F10 menu is "${menuName}"`);
    await page.keyboard.press("Escape");
    await sleep(200);
    await page.focus('[data-q-explorer] input');
    await page.keyboard.type("Customers");
    await page.waitForFunction(() => [...document.querySelectorAll('[data-q-explorer] [role="treeitem"]')].some((x) => x.getAttribute("aria-label")?.startsWith("TABLES · ")), { timeout: 5_000 }).catch(() => errors.push("explorer: search found no table in SalesDB"));
    await shot("02b-explorer-search");
    await page.keyboard.down("Control"); await page.keyboard.press("Enter"); await page.keyboard.up("Control");
    await sleep(300);
    const inserted = await page.evaluate(() => document.querySelector(".monaco-editor .view-lines")?.textContent ?? "");
    if (!inserted.includes("dbo.Customers")) errors.push(`explorer: Ctrl+Enter in search did not insert the name (editor: "${inserted.slice(0, 60)}")`);
    if (await page.evaluate(() => !!document.querySelector('[data-q-chip="s0"]'))) errors.push("explorer: Ctrl+Enter in search ran the query");
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
    await page.keyboard.press("Backspace");
    await page.focus('[data-q-explorer] input');
    await page.keyboard.press("Escape");
    for (const [w, name] of [[200, "02c-explorer-200"], [600, "02d-explorer-600"]]) {
      await page.evaluate((px) => window.__bench.explorerWidth(px), w);
      await sleep(400);
      await shot(name);
    }
    await page.evaluate(() => window.__bench.explorerWidth(280));
    await sleep(300);
  }
  // Type a query and run it with F5.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.type("SELECT TOP 200 o.OrderId, o.CustomerId, o.OrderDate, o.TotalAmount, o.Status FROM dbo.Orders o;\nSELECT name, object_id, type_desc FROM sys.objects WHERE is_ms_shipped = 0 ORDER BY name;");
  await page.keyboard.press("Escape");
  await page.keyboard.press("F5");
  // The bottom panel's ledger (§12): one chip per result set; the second one is picked like any tab.
  await page.waitForFunction(() => !!document.querySelector('[data-q-chip="s1"]'), { timeout: 30_000 }).catch(() => errors.push("results did not appear"));
  await sleep(800);
  await page.evaluate(() => document.querySelector('[data-q-chip="s1"]')?.click());
  await page.waitForFunction(() => document.querySelector('[data-q-chip="s1"]')?.getAttribute("aria-selected") === "true", { timeout: 5_000 })
    .catch(() => errors.push("ledger: picking result set 2 did not select it"));
  await sleep(600);
  await shot("03-results");
  await auditRowPopups("03-results");
  // Compare: Ctrl+click the first chip shows it beside the second, with its own grid.
  await page.keyboard.down("Control");
  await page.click('[data-q-chip="s0"]');
  await page.keyboard.up("Control");
  await page.waitForFunction(() => document.querySelectorAll("[data-q-compare-pane]").length === 2, { timeout: 5_000 }).catch(() => errors.push("compare panes did not open"));
  await sleep(500);
  await shot("03c-results-compare");
  await page.evaluate(() => document.querySelector('[aria-label="Stop comparing"]')?.click());
  await sleep(200);
  // The chip click took the keyboard into the panel: give it back to the editor for the next query.
  await page.click(".monaco-editor .view-lines");
  // Grid editing: a single-table result is editable; stage a value, review the SQL, discard.
  // The Read-only button carries data-q-edit-toggle too (focus returns to it), so every wait here asks for the enabled one:
  // otherwise the previous run's Read-only button matches before the new result arrives.
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT CustomerId, FullName, Email FROM dbo.Customers ORDER BY CustomerId;");
  await page.keyboard.press("F5");
  await page.waitForFunction(() => !!document.querySelector("[data-q-edit-toggle]:not([aria-disabled])"), { timeout: 15_000 }).catch(() => errors.push("grid Edit button did not appear"));
  if (await page.evaluate(() => !!document.querySelector("[data-q-grid-edit]"))) errors.push("grid was editable before Edit was clicked");
  await page.evaluate(() => document.querySelector("[data-q-edit-toggle]:not([aria-disabled])")?.click());
  await page.waitForFunction(() => document.body.innerText.includes("Type or double-click to edit"), { timeout: 5_000 }).catch(() => errors.push("grid edit bar did not appear"));
  await sleep(500);
  const grid = await page.evaluate(() => {
    const c = document.querySelector("[data-q-grid-edit] canvas")?.getBoundingClientRect();
    return c ? { x: c.left, y: c.top } : null;
  });
  if (grid) {
    await page.mouse.click(grid.x + 220, grid.y + 28 + 12); // FullName of the first row
    await sleep(200);
    await page.keyboard.press("Enter"); // open the cell editor, then type into it
    await page.waitForFunction(() => document.activeElement?.tagName === "TEXTAREA" && !document.activeElement.closest(".monaco-editor"), { timeout: 5_000 })
      .catch(() => errors.push("grid cell editor did not open"));
    await page.keyboard.type("Smoke");
    await sleep(200);
    await page.keyboard.press("Enter");
    await sleep(300);
    await page.waitForFunction(() => document.body.innerText.includes("1 changed"), { timeout: 5_000 }).catch(() => errors.push("grid edit was not staged"));
    await sleep(300);
    await shot("03a-grid-staged");
    // Review SQL and Discard all live in the edit row's ⋯ menu (Prod and Stage have one "Review & apply" button).
    const editMenu = async (label) => {
      await page.evaluate(() => document.querySelector('[aria-label="More edit actions"]')?.click());
      await sleep(150);
      await page.evaluate((l) => [...document.querySelectorAll('[role="menuitem"]')].find((b) => b.textContent?.trim().startsWith(l))?.click(), label);
    };
    await editMenu("Review SQL");
    await page.waitForFunction(() => document.body.innerText.includes("Changes to dbo.Customers: 1 updated"), { timeout: 10_000 }).catch(() => errors.push("grid review did not open"));
    await sleep(800);
    await shot("03b-grid-review");
    await page.evaluate(() => [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent === "Cancel")?.click());
    await sleep(200);
    // Cancel is the edit row's one way out (no Done); with a staged change it asks first, and Keep editing keeps it.
    if (await page.evaluate(() => [...document.querySelectorAll("[data-q-panel-row] button")].some((b) => b.textContent?.trim() === "Done")))
      errors.push("the edit row still shows a Done button");
    await page.evaluate(() => document.querySelector("[data-q-edit-cancel]")?.click());
    await page.waitForFunction(() => document.body.innerText.includes("Discard 1 staged change to dbo.Customers?"), { timeout: 5_000 })
      .catch(() => errors.push("Cancel did not ask before discarding"));
    // The discard question is an alert dialog (role=alertdialog): a [role=dialog] selector missed it, the dialog stayed
    // open and swallowed the next click.
    await page.evaluate(() => [...document.querySelectorAll('[role="alertdialog"] button')].find((b) => b.textContent === "Keep editing")?.click());
    await page.waitForFunction(() => !document.querySelector('[role="alertdialog"]'), { timeout: 5_000 }).catch(() => errors.push("Keep editing did not close the question"));
    await sleep(200);
    if (!(await page.evaluate(() => document.body.innerText.includes("1 changed")))) errors.push("Keep editing lost the staged change");
    await editMenu("Discard all");
    await sleep(200);
    // Duplicate: select the first row by its number, Ctrl+D copies it as a new row (one undo step).
    await page.mouse.click(grid.x + 10, grid.y + 28 + 12);
    await sleep(150);
    await page.keyboard.down("Control"); await page.keyboard.press("d"); await page.keyboard.up("Control");
    await page.waitForFunction(() => document.body.innerText.includes("1 new"), { timeout: 5_000 }).catch(() => errors.push("Ctrl+D did not duplicate the row"));
    await shot("03c-grid-duplicate");
    await page.evaluate(() => document.querySelector("[data-q-edit-cancel]")?.click());
    await sleep(200);
    await page.evaluate(() => [...document.querySelectorAll('[role="alertdialog"] button')].find((b) => b.textContent === "Discard and leave")?.click());
    await page.waitForFunction(() => !document.querySelector("[data-q-grid-edit]"), { timeout: 5_000 }).catch(() => errors.push("Cancel → Discard and leave did not leave edit mode"));
    // Leaving edit mode is not sticky: the grid is read-only until Edit is clicked again.
    await page.evaluate(() => document.querySelector("[data-q-edit-toggle]:not([aria-disabled])")?.click());
    await sleep(200);
  } else errors.push("editable grid canvas not found");
  // Reviewed Gemini workflows: header selection must not crash; a query error is shown without hiding the results.
  if (grid) {
    await page.mouse.click(grid.x + 80, grid.y + 12);
    await page.waitForFunction(() => !!document.querySelector("[data-q-selection-stats]"), { timeout: 5_000 })
      .catch(() => errors.push("header selection statistics did not appear"));
  }
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT CONVERT(int, N'not_an_integer');");
  await page.keyboard.press("Escape"); await page.keyboard.press("F5");
  await page.waitForFunction(() => document.body.innerText.includes("Conversion failed"), { timeout: 15_000 })
    .catch(() => errors.push("query failure was not shown in the results panel"));
  if (await page.evaluate(() => document.querySelector('[data-q-panel-tab="messages"]')?.getAttribute("aria-selected") === "true")) errors.push("a query error switched the panel to Messages");
  await shot("03m-query-error");
  // The Messages view lists the error in its log; F4 moves the editor to the error line.
  await page.evaluate(() => document.querySelector('[data-q-panel-tab="messages"]')?.click());
  await page.waitForFunction(() => document.querySelector("[data-q-messages]")?.textContent?.includes("Conversion failed"), { timeout: 5_000 }).catch(() => errors.push("Messages did not list the error"));
  await sleep(300);
  await shot("03n-messages");
  await auditRowPopups("03n-messages");
  await page.evaluate(() => document.querySelector('[data-q-panel-tab="results"]')?.click());
  await sleep(200);
  // Expand a qualified star in a join through the real command palette.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT o.* FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId=o.CustomerId");
  await page.keyboard.press("Escape"); await page.keyboard.press("Home");
  for (let i = 0; i < 10; i++) await page.keyboard.press("ArrowRight");
  await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("P"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
  // Type only once the palette has focus; earlier keystrokes land in the editor.
  await page.waitForFunction(() => document.activeElement?.tagName === "INPUT" && !document.activeElement.closest(".monaco-editor"), { timeout: 5_000 })
    .catch(() => errors.push("command palette did not open"));
  await page.keyboard.type("Expand '*' to column list"); await page.keyboard.press("Enter");
  await page.waitForFunction(() => [...document.querySelectorAll(".monaco-editor .view-lines")].some((v) => v.textContent?.includes("o.OrderId")), { timeout: 5_000 })
    .catch(() => errors.push("qualified wildcard expansion did not preserve the alias"));
  // ER diagram: Ctrl+Shift+E on a statement opens the diagram of its tables.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
  await page.keyboard.press("Escape");
  await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("E"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
  await page.waitForFunction(() => document.querySelectorAll(".react-flow__node").length >= 2, { timeout: 15_000 }).catch(() => errors.push("ER diagram did not render"));
  await sleep(800);
  await shot("03c-erd");
  await page.evaluate(() => [...document.querySelectorAll("select")].find((x) => [...x.options].some((o) => o.value === "schema"))?.dispatchEvent(new Event("change")));
  await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
  await sleep(400);
  if (await page.evaluate(() => document.querySelectorAll(".react-flow__node").length > 0)) errors.push("Ctrl+W did not close the ER diagram tab");
  console.log("[step] Schema compare");
  // Schema compare: Ctrl+Shift+D takes the tab's database as the source; compare it with another one.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("D"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
  await page.waitForSelector('select[aria-label="Target connection"]', { timeout: 10_000 }).catch(() => errors.push("schema compare tab did not open"));
  const sourceProfile = await page.evaluate(() => document.querySelector('select[aria-label="Source connection"]')?.value);
  if (sourceProfile) {
    await page.select('select[aria-label="Target connection"]', sourceProfile);
    await page.waitForFunction(() => [...(document.querySelector('select[aria-label="Target database"]')?.options ?? [])].some((o) => o.value === "querithm_edit"), { timeout: 10_000 })
      .catch(() => errors.push("schema compare target databases did not load"));
    await page.select('select[aria-label="Target database"]', "querithm_edit");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Compare")?.click());
    await page.waitForFunction(() => document.querySelectorAll("[data-q-compare-item]").length > 0, { timeout: 30_000 }).catch(() => errors.push("schema compare returned no items"));
    await sleep(800);
    await shot("03g-schema-compare");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Migration")?.click());
    await page.waitForFunction(() => [...document.querySelectorAll(".monaco-editor .view-lines")].some((v) => v.textContent?.replace(/\u00a0/g, " ").includes("Querithm schema migration")), { timeout: 15_000 })
      .catch(() => errors.push("schema migration script did not appear"));
    await sleep(500);
    await shot("03h-schema-migration");
  } else errors.push("schema compare source was not prefilled from the tab");
  await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
  await sleep(400);
  console.log("[step] Data compare");
  // Data compare: a query (every even customer's name changed) against the table it reads.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("P"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
  await sleep(300);
  await page.keyboard.type("Data compare");
  await sleep(300);
  await page.keyboard.press("Enter");
  await page.waitForSelector('select[aria-label="Source kind"]', { timeout: 10_000 }).catch(() => errors.push("data compare tab did not open"));
  if (await page.$('select[aria-label="Source kind"]')) {
    await page.select('select[aria-label="Source kind"]', "query");
    await page.waitForSelector('textarea[aria-label="Source query"]', { timeout: 5_000 });
    await page.type('textarea[aria-label="Source query"]',
      "SELECT CustomerId, CASE WHEN CustomerId % 2 = 0 THEN FullName + N'!' ELSE FullName END AS FullName FROM dbo.Customers");
    await page.type('input[aria-label="Key columns"]', "CustomerId");
    await page.type('input[aria-label="Target table"]', "dbo.Customers");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Compare")?.click());
    await page.waitForFunction(() => /\d+ ms\b/.test(document.querySelector("[data-q-data-progress]")?.textContent ?? ""), { timeout: 30_000 })
      .catch(() => errors.push("data compare did not finish"));
    await sleep(800);
    const progressText = await page.evaluate(() => document.querySelector("[data-q-data-progress]")?.textContent ?? "");
    const differentPill = await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Different"))?.textContent ?? "");
    if (!/^(\d[\d,]*) \/ \1 rows/.test(progressText) || !/Different [1-9]/.test(differentPill)) errors.push(`data compare counts unexpected: ${progressText} / ${differentPill}`);
    await shot("03i-data-compare");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Open sync script")?.click());
    await page.waitForFunction(() => [...document.querySelectorAll(".monaco-editor .view-lines")].some((v) => v.textContent?.replace(/\u00a0/g, " ").includes("Querithm data sync")), { timeout: 10_000 })
      .catch(() => errors.push("data sync script did not open"));
    await sleep(400);
    await shot("03j-data-sync");
    await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
    await sleep(300);
  }
  await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
  await sleep(400);
  console.log("[step] Find usages / rename");
  // Find usages: Shift+F12 on a column, then F2 opens the previewed rename.
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT c.FullName FROM dbo.Customers c");
  await page.keyboard.press("Escape");
  await page.keyboard.press("Home");
  for (let i = 0; i < 11; i++) await page.keyboard.press("ArrowRight");
  await page.keyboard.down("Shift"); await page.keyboard.press("F12"); await page.keyboard.up("Shift");
  await page.waitForFunction(() => (document.querySelector("[data-q-usages]")?.textContent ?? "").includes("Usages of column") && document.querySelectorAll("[data-q-usage-hit]").length > 0, { timeout: 20_000 })
    .catch(() => errors.push("find usages did not list the column's usages"));
  await sleep(400);
  await shot("03k-usages");
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.press("Home");
  for (let i = 0; i < 11; i++) await page.keyboard.press("ArrowRight");
  await page.keyboard.press("F2");
  await page.waitForSelector('input[aria-label="New name"]', { timeout: 10_000 }).catch(() => errors.push("rename dialog did not open"));
  if (await page.$('input[aria-label="New name"]')) {
    await page.keyboard.type("DisplayName"); // the old name is selected on open
    await page.waitForFunction(() => (document.querySelector("[data-q-rename-objects]")?.textContent ?? "").length > 0, { timeout: 20_000 })
      .catch(() => errors.push("rename preview did not appear"));
    await sleep(800);
    await shot("03l-rename");
    await page.evaluate(() => [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent === "Cancel")?.click());
    await sleep(300);
  }
  console.log("[step] Import wizard");
  // Import wizard: the dev bridge stands in for the native dialog; profile, create the table, load.
  const csvPath = path.join(out, "smoke_import.csv");
  writeFileSync(csvPath, "id,name,amount,born\n1,Alpha,1.50,2026-10-21\n2,Beta,2.25,2026-10-22\n3,Gamma,,\n");
  await page.click(".monaco-editor .view-lines");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("DROP TABLE IF EXISTS dbo.smoke_import;");
  await page.keyboard.press("Escape");
  await page.keyboard.press("F5");
  await sleep(1200);
  await page.keyboard.down("Control"); await page.keyboard.down("Alt"); await page.keyboard.press("I"); await page.keyboard.up("Alt"); await page.keyboard.up("Control");
  await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent === "Choose file…"), { timeout: 10_000 }).catch(() => errors.push("import wizard did not open"));
  const [chooser] = await Promise.all([
    page.waitForFileChooser({ timeout: 10_000 }),
    page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Choose file…")?.click()),
  ]).catch((e) => { errors.push(`file chooser: ${e.message}`); return []; });
  if (chooser) {
    await chooser.accept([csvPath]);
    await page.waitForFunction(() => document.body.innerText.includes("3 rows · 4 columns"), { timeout: 15_000 }).catch(() => errors.push("import inspect did not finish"));
    await sleep(600);
    await shot("03d-import-file");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Next: target"))?.click());
    await page.waitForFunction(() => [...document.querySelectorAll("textarea")].some((t) => t.value.includes("CREATE TABLE")), { timeout: 10_000 }).catch(() => errors.push("import target step did not open"));
    await shot("03e-import-target");
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Create table and import"))?.click());
    const imported = await page.waitForFunction(() => document.body.innerText.includes("Imported 3 rows"), { timeout: 20_000 }).then(() => true, () => false);
    await shot("03f-import-done");
    if (!imported) {
      errors.push(`import did not complete: ${await openDialogText()}`);
      console.log(`[import blocked?]\n${mssqlActivity()}`);
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Cancel import")?.click());
      await page.waitForFunction(() => ![...document.querySelectorAll("button")].some((b) => b.textContent === "Cancel import"), { timeout: 10_000 })
        .catch(() => errors.push("import cancel did not finish"));
    }
    await closeDialogs();
  }
  // Autocomplete popup.
  await page.keyboard.down("Control"); await page.keyboard.press("End"); await page.keyboard.up("Control");
  await page.keyboard.type("\nSELECT * FROM dbo.Orders o WHERE o.");
  await sleep(900);
  await shot("04-completion");
  const suggestions = await page.evaluate(() => [...document.querySelectorAll(".suggest-widget .monaco-list-row")].map((r) => r.getAttribute("aria-label")).slice(0, 8));
  console.log("suggestions", suggestions);
  await page.keyboard.press("Escape");
  // Estimated plan.
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT c.FullName, o.TotalAmount FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE o.OrderDate >= '2026-01-01'");
  await page.keyboard.down("Control"); await page.keyboard.press("L"); await page.keyboard.up("Control");
  await page.waitForSelector(".react-flow__node", { timeout: 20_000 }).catch(() => errors.push("plan did not render"));
  await sleep(800);
  await shot("05-plan");
  await auditRowPopups("05-plan");
  // ---- Phase C: a result as a chart (Ctrl+2), a JSON value in the side viewer, and a notebook run.
  {
    const typeQuery = async (sql) => {
      await page.click(".monaco-editor .view-lines");
      await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
      await page.keyboard.type(sql);
      await page.keyboard.press("Escape");
      await page.keyboard.press("F5");
    };
    // Generated rows (fixture tables may be empty): a date and an amount, which default to a line chart.
    await typeQuery("SELECT TOP 60 DATEADD(day, ROW_NUMBER() OVER (ORDER BY object_id), CAST('2026-01-01' AS date)) AS day, object_id % 997 AS amount FROM sys.all_objects ORDER BY day;");
    await page.waitForFunction(() => /\b60 rows\b/.test(document.body.innerText), { timeout: 20_000 }).catch(() => errors.push("chart query did not return"));
    await page.keyboard.down("Control"); await page.keyboard.press("2"); await page.keyboard.up("Control");
    await page.waitForSelector("[data-q-chart] canvas", { timeout: 15_000 }).catch(() => errors.push("Ctrl+2 did not draw the result as a chart"));
    if (!(await page.evaluate(() => document.querySelector('[role="radiogroup"][aria-label="Chart type"] [aria-checked="true"]')?.textContent === "Line"))) errors.push("a date + amount result did not default to a line chart");
    await sleep(800);
    await shot("03n-chart");
    await page.keyboard.down("Control"); await page.keyboard.press("1"); await page.keyboard.up("Control");
    await page.waitForFunction(() => !document.querySelector("[data-q-chart]"), { timeout: 5_000 }).catch(() => errors.push("Ctrl+1 did not bring the grid back"));
    // A JSON value opens formatted in the side viewer (double-click on the cell).
    await typeQuery("SELECT (SELECT TOP 3 name, object_id FROM sys.objects ORDER BY object_id FOR JSON PATH) AS objects_json;");
    await page.waitForFunction(() => /\b1 rows\b/.test(document.body.innerText), { timeout: 20_000 }).catch(() => errors.push("JSON query did not return"));
    await sleep(600);
    const cellAt = await page.evaluate(() => {
      const c = document.querySelector('[data-focus-region="results"] canvas')?.getBoundingClientRect();
      return c ? { x: c.left + 90, y: c.top + 28 + 12 } : null;
    });
    if (cellAt) {
      // Select the cell, then Enter activates it (as a double-click does) and opens it in the viewer.
      await page.mouse.click(cellAt.x, cellAt.y);
      await sleep(200);
      await page.keyboard.press("Enter");
      await page.waitForSelector("[data-q-cell-viewer]", { timeout: 5_000 }).catch(() => errors.push("activating a cell did not open the value viewer"));
      await page.waitForFunction(() => document.querySelector("[data-q-cell-viewer]")?.textContent?.includes('"object_id"'), { timeout: 5_000 })
        .catch(() => errors.push("the value viewer did not show the JSON formatted"));
      await sleep(400);
      await shot("03o-cell-viewer");
      await page.evaluate(() => document.querySelector('[data-q-cell-viewer] button[aria-label="Close the value viewer"]')?.click());
    } else errors.push("result grid canvas not found for the value viewer");
    // Notebook: New ▾ → New notebook, two SQL cells and a text cell, Run all, both results visible.
    await page.click('button[aria-label="More ways to open a query"]');
    await sleep(200);
    await page.evaluate(() => [...document.querySelectorAll('[role="menuitem"]')].find((b) => b.textContent?.includes("New notebook"))?.click());
    await page.waitForSelector("[data-q-notebook] [data-q-cell-editor] .monaco-editor", { timeout: 10_000 }).catch(() => errors.push("New notebook did not open a notebook with an editor cell"));
    await sleep(400);
    const notebookButton = (label) => page.evaluate((l) => [...document.querySelectorAll("[data-q-notebook-toolbar] button")].find((b) => b.textContent?.trim() === l)?.click(), label);
    await page.click("[data-q-notebook] [data-q-cell-editor] .view-lines");
    await page.keyboard.type("SELECT TOP 5 name FROM sys.objects ORDER BY name;");
    await page.keyboard.press("Escape");
    await notebookButton("+ Text");
    await page.waitForSelector('[data-q-notebook] textarea[aria-label="Markdown"]', { timeout: 5_000 }).catch(() => errors.push("+ Text did not add a markdown cell"));
    await page.focus('[data-q-notebook] textarea[aria-label="Markdown"]');
    await page.keyboard.type("## Smoke notebook\n\nTwo **queries** and this note.");
    await notebookButton("+ SQL");
    await page.waitForFunction(() => document.querySelectorAll("[data-q-notebook] [data-q-cell]").length === 3, { timeout: 5_000 }).catch(() => errors.push("+ SQL did not add a third cell"));
    await sleep(300);
    await page.click("[data-q-notebook] [data-q-cell-editor] .view-lines");
    await page.keyboard.type("SELECT 42 AS answer;");
    await page.keyboard.press("Escape");
    await notebookButton("Run all");
    await page.waitForFunction(() => {
      const outs = [...document.querySelectorAll("[data-q-notebook] [data-q-cell-output]")].map((o) => o.textContent ?? "");
      return outs.length === 2 && outs[0].includes("5 rows") && outs[1].includes("1 row");
    }, { timeout: 30_000 }).catch(async () => errors.push(`notebook Run all did not show both results: ${await page.evaluate(() => [...document.querySelectorAll("[data-q-cell-output]")].map((o) => o.textContent?.slice(0, 80)).join(" | "))}`));
    if (!(await page.evaluate(() => document.querySelector("[data-q-notebook] .q-markdown h2")?.textContent === "Smoke notebook"))) errors.push("the markdown cell did not render after Run all");
    await sleep(800);
    await shot("24-notebook");
    // Close it (Ctrl+W): the query tab gets the shared editor back.
    await page.click("[data-q-notebook] [data-q-cell-editor] .view-lines").catch(() => {});
    await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
    await page.waitForFunction(() => !document.querySelector("[data-q-notebook]"), { timeout: 5_000 }).catch(() => errors.push("Ctrl+W did not close the notebook"));
    await page.waitForSelector(".monaco-editor .view-lines", { timeout: 5_000 }).catch(() => errors.push("the query tab's editor did not come back after the notebook closed"));
    await sleep(400);
  }
  // ---- Activity monitor (Phase B): open → identify the blocker → why → kill it safely → resulting state;
  // own tab → Go to tab → banner → Rollback; deadlock graph; pause / resume; tab close; disconnect.
  {
    const waitText = (t, timeout = 20_000) => page.waitForFunction((x) => document.body.innerText.includes(x), { timeout }, t);
    // Dockview activates a tab on pointer events: a real click, not a synthetic DOM event.
    const activateTab = async (text) => {
      for (const h of await page.$$(".dv-tab")) if ((await h.evaluate((e) => e.textContent ?? "")).includes(text)) { await h.click(); await sleep(300); return true; }
      return false;
    };
    const clickIn = (sel, label) => page.evaluate((s, l) => [...document.querySelectorAll(`${s} button`)].find((b) => b.textContent?.trim() === l)?.click(), sel, label);
    sqlNow("IF OBJECT_ID('dbo.smoke_mon') IS NULL CREATE TABLE dbo.smoke_mon(id int PRIMARY KEY, v int, pad char(4000) NOT NULL DEFAULT 'x'); IF NOT EXISTS (SELECT 1 FROM dbo.smoke_mon WHERE id = 1) INSERT dbo.smoke_mon(id, v) VALUES (1,1),(2,2); DELETE dbo.smoke_mon WHERE id > 2;");
    // A foreign holder with a large open write transaction (high rollback risk) and a foreign session it blocks.
    background.push(sqlBg("BEGIN TRAN; UPDATE dbo.smoke_mon SET v = v + 1 WHERE id = 1; INSERT dbo.smoke_mon(id, v) SELECT TOP (30000) 1000 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 0 FROM sys.all_objects a CROSS JOIN sys.all_objects b; WAITFOR DELAY '00:03:00'; ROLLBACK;"));
    await sleep(4000);
    background.push(sqlBg("UPDATE dbo.smoke_mon SET v = v + 10 WHERE id = 1;"));
    await page.evaluate((id) => window.__bench.openMonitor(id), fx.profileId);
    await page.waitForSelector('[data-testid="activity-tab"]', { timeout: 20_000 }).catch(() => errors.push("activity tab did not open"));
    await page.waitForSelector('[data-testid="sessions-grid"]', { timeout: 30_000 }).catch(() => errors.push("monitor sessions did not load"));
    await shot("13-monitor-sessions");
    await page.evaluate(() => [...document.querySelectorAll('[role="tab"]')].find((b) => b.textContent === "Blocking")?.click());
    await page.waitForFunction(() => /blocks 1/.test(document.querySelector('[data-testid="blocking-tree"]')?.textContent ?? ""), { timeout: 30_000 })
      .catch(() => errors.push("blocking pair not shown in the tree"));
    await shot("14-monitor-blocking");
    const head = await page.evaluate(() => [...document.querySelectorAll('[data-testid="blocking-tree"] [data-session]')].find((n) => n.textContent?.includes("blocks 1"))?.getAttribute("data-session"));
    // Why is it slow? on the blocked session names the blocker.
    await page.evaluate((h) => {
      const victim = [...document.querySelectorAll('[data-testid="blocking-tree"] [data-session]')].find((n) => n.getAttribute("data-session") !== h);
      [...(victim?.querySelectorAll("button") ?? [])].find((b) => b.textContent === "Why?")?.click();
    }, head);
    await page.waitForFunction((h) => document.querySelector('[data-testid="diagnosis"]')?.textContent?.includes(`Blocked by session ${h}`), { timeout: 20_000 }, head)
      .catch(() => errors.push("diagnosis did not name the blocker"));
    await shot("15-monitor-why");
    // Two more ticks (2 s interval) before the kill ends the pair: the history step scrubs back to it, and on a fast run
    // the kill came within a second of the first tick, so no retained tick held the pair.
    await sleep(4_500);
    // Kill the foreign head blocker: the dialog shows the high rollback risk and needs the extra confirm.
    await page.evaluate((h) => {
      const node = [...document.querySelectorAll('[data-testid="blocking-tree"] [data-session]')].find((n) => n.getAttribute("data-session") === h);
      [...(node?.querySelectorAll("button") ?? [])].find((b) => b.textContent === "Kill")?.click();
    }, head);
    await waitText("high — the rollback may take long", 20_000).catch(() => errors.push("kill dialog did not show the high rollback risk"));
    if (await page.evaluate(() => !document.querySelector('[data-testid="kill-rollback"]'))) errors.push("high-risk kill did not ask for the rollback confirm");
    if (await page.evaluate(() => !document.querySelector('[data-testid="kill-submit"]')?.disabled)) errors.push("kill was possible without the confirms");
    await shot("16-monitor-kill-dialog");
    await page.click('[data-testid="kill-confirm"]');
    await page.click('[data-testid="kill-rollback"]');
    await page.click('[data-testid="kill-submit"]');
    await waitText("was terminated", 20_000).catch(async () => errors.push(`kill did not succeed: ${await openDialogText()}`));
    await page.waitForFunction(() => document.body.innerText.includes("No blocking right now"), { timeout: 60_000 }).catch(() => errors.push("blocking did not clear after the kill"));
    await shot("17-monitor-after-kill");
    // Own tab: an open transaction in a query tab blocks a foreign session → badge, Go to tab, banner, Rollback.
    await page.evaluate((id, pw) => window.__bench.useProd(id, pw), fx.profileId, MSSQL_PW);
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
    await page.keyboard.type("BEGIN TRAN; UPDATE dbo.smoke_mon SET v = v + 1 WHERE id = 2;");
    await page.keyboard.press("F5");
    await sleep(1500);
    background.push(sqlBg("UPDATE dbo.smoke_mon SET v = v + 10 WHERE id = 2;"));
    await page.waitForSelector('[data-testid="blocking-banner"]', { timeout: 40_000 }).catch(() => errors.push("own-tab blocking banner did not appear"));
    await shot("18-monitor-banner");
    if (!(await activateTab("Activity"))) errors.push("the Activity tab was not found");
    await page.waitForFunction(() => [...document.querySelectorAll('[data-testid="blocking-tree"] button')].some((b) => b.textContent === "Go to tab"), { timeout: 30_000 })
      .catch(() => errors.push("own-tab blocker did not offer Go to tab"));
    await shot("19-monitor-own-tab");
    await page.evaluate(() => [...document.querySelectorAll('[data-testid="blocking-tree"] button')].find((b) => b.textContent === "Go to tab")?.click());
    await page.waitForSelector('[data-testid="blocking-banner"]', { visible: true, timeout: 10_000 }).catch(() => errors.push("Go to tab did not focus the query tab"));
    await clickIn('[data-testid="blocking-banner"]', "Rollback");
    await page.waitForFunction(() => !document.querySelector('[data-testid="blocking-banner"]'), { timeout: 20_000 }).catch(() => errors.push("Rollback did not clear the banner"));
    // A real deadlock → one toast (X16, not repeated by the next poll), and the Deadlocks pane lists it and draws its graph.
    // Counted per toast element (its text starts with a screen-reader "Warning: "); a repeat joined into a shown toast
    // ("×2", M20) counts too.
    await page.evaluate(() => {
      window.__deadlockToasts = 0;
      const shown = new Map();
      new MutationObserver(() => {
        for (const t of document.querySelectorAll("[data-q-toast]")) {
          if (!t.textContent?.includes("Deadlock in")) continue;
          const times = Number(/×(\d+)/.exec(t.textContent)?.[1] ?? 1);
          shown.set(t, Math.max(shown.get(t) ?? 0, times));
        }
        window.__deadlockToasts = [...shown.values()].reduce((a, b) => a + b, 0);
      }).observe(document.body, { childList: true, subtree: true, characterData: true });
    });
    background.push(sqlBg("BEGIN TRAN; UPDATE dbo.smoke_mon SET v = v WHERE id = 1; WAITFOR DELAY '00:00:02'; UPDATE dbo.smoke_mon SET v = v WHERE id = 2; COMMIT;"));
    await sleep(500);
    background.push(sqlBg("BEGIN TRAN; UPDATE dbo.smoke_mon SET v = v WHERE id = 2; WAITFOR DELAY '00:00:02'; UPDATE dbo.smoke_mon SET v = v WHERE id = 1; COMMIT;"));
    await sleep(9000);
    await activateTab("Activity");
    await page.evaluate(() => [...document.querySelectorAll('[role="tab"]')].find((b) => b.textContent === "Deadlocks")?.click());
    await page.waitForSelector('[data-testid="deadlock-graph"]', { timeout: 30_000 }).catch(() => errors.push("deadlock graph did not render"));
    await shot("20-monitor-deadlock");
    // The deadlock poll runs every 30 s: the toast comes with the first poll after it, and the next poll does not repeat it.
    await page.waitForFunction(() => window.__deadlockToasts >= 1, { timeout: 45_000 }).catch(() => errors.push("no toast for the new deadlock"));
    await sleep(35_000);
    const toasts = await page.evaluate(() => window.__deadlockToasts);
    if (toasts !== 1) errors.push(`deadlock toast shown ${toasts} times (expected once)`);
    // Pause / resume.
    await page.evaluate(() => [...document.querySelectorAll('[role="tab"]')].find((b) => b.textContent === "Sessions")?.click());
    await clickIn('[data-testid="monitor-header"]', "Pause");
    await waitText("Paused at", 10_000).catch(() => errors.push("pause was not shown"));
    await shot("21-monitor-paused");
    await clickIn('[data-testid="monitor-header"]', "Resume");
    await page.waitForFunction(() => !document.body.innerText.includes("Paused at"), { timeout: 10_000 }).catch(() => errors.push("resume did not clear the paused state"));
    // History (B-next): scrub back to a tick where the foreign blocking pair stood; the snapshot shows its head blocker.
    await page.evaluate(() => [...document.querySelectorAll('[role="tab"]')].find((b) => b.textContent === "History")?.click());
    await page.waitForSelector('[data-testid="history-scrubber"]', { timeout: 10_000 }).catch(() => errors.push("history pane did not open"));
    // As a user scrubs: focus the slider, Home (oldest tick), then real arrow-key steps. Values set from script with a
    // synthetic input event stopped reaching React's change handler (every position still read "live").
    const scrubber = '[data-testid="history-scrubber"]';
    let scrubbed = "no scrubber";
    if (await page.$(scrubber)) {
      await page.focus(scrubber);
      await page.keyboard.press("Home");
      const max = await page.$eval(scrubber, (e) => Number(e.max));
      const labels = new Set();
      for (let i = 0; i <= max && scrubbed !== "ok"; i++) {
        if (i > 0) await page.keyboard.press("ArrowRight");
        const label = await page.$eval(scrubber, (e) => e.parentElement?.innerText ?? "");
        labels.add(label.match(/\d+ blocked/)?.[0] ?? "live");
        if (!/ [1-9]\d* blocked/.test(label)) continue;
        const shown = await page.waitForFunction((h) => {
          const text = document.querySelector('[data-testid="history-snapshot"]')?.textContent ?? "";
          return text.includes("Head blockers:") && text.split("Head blockers:")[1].split("·")[0].split(",").map((x) => x.trim()).includes(String(h));
        }, { timeout: 5_000 }, head).then(() => true, () => false);
        if (shown) scrubbed = "ok";
      }
      if (scrubbed !== "ok") scrubbed = `no tick shows the blocking pair (head ${head}; labels seen: ${[...labels].join(" | ")})`;
    }
    if (scrubbed !== "ok") errors.push(`history scrub: ${scrubbed}`);
    await shot("21b-monitor-history");
    // Tab close stops the monitor and closes its connection on the server.
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label")?.startsWith("Close Activity"))?.click());
    await sleep(4000);
    const left = sqlNow("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = 'Querithm Monitor'", "master");
    if (left !== "0") errors.push(`monitor connection left open after the tab closed: ${left}`);
    // Disconnect stops a running monitor with the reason.
    await page.evaluate((id) => window.__bench.openMonitor(id), fx.profileId);
    await page.waitForSelector('[data-testid="sessions-grid"]', { timeout: 30_000 }).catch(() => errors.push("monitor did not reopen"));
    await page.evaluate((id) => window.__bench.disconnect(id), fx.profileId);
    await waitText("Disconnected from the server.", 15_000).catch(() => errors.push("disconnect did not stop the monitor"));
    await shot("22-monitor-disconnected");
    await sleep(3000);
    const after = sqlNow("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = 'Querithm Monitor'", "master");
    if (after !== "0") errors.push(`monitor connection left open after disconnect: ${after}`);
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.getAttribute("aria-label")?.startsWith("Close Activity"))?.click());
  }
  // Command palette.
  await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("P"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
  await sleep(400);
  await shot("06-palette");
  await page.keyboard.press("Escape");
  // Production guard: a Prod profile, then DELETE without WHERE must show the sidecar-enforced dialogs.
  await page.evaluate(async (host) => {
    const b = window.__bench;
    const p = await b.rpc("profile.save", { id: "", name: "Primary Billing", engine: "mssql", host, port: 14333, auth: "sql", username: "sa", environment: "prod", tlsMode: "mandatory", trustCert: true, databaseDefault: "SalesDB" });
    window.__prodProfile = p.id;
  }, mssqlHost);
  await page.evaluate((pw) => window.__bench.useProd(window.__prodProfile, pw), MSSQL_PW);
  await sleep(1500);
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT 1;\nDELETE FROM dbo.OrderTotals;");
  await page.keyboard.press("F5");
  await page.waitForFunction(() => document.body.innerText.includes("FIRST EXECUTION ON PRODUCTION"), { timeout: 15_000 }).catch(() => errors.push("prod first-run guard did not appear"));
  await shot("08-prod-first");
  await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.includes("Confirm & execute on PROD"))?.click());
  await page.waitForFunction(() => document.body.innerText.includes("DESTRUCTIVE STATEMENT DETECTED ON PRODUCTION"), { timeout: 15_000 }).catch(() => errors.push("destructive guard did not appear"));
  await sleep(300);
  await shot("09-prod-destructive");
  await page.keyboard.press("Escape");
  await sleep(300);
  // PostgreSQL tab.
  await page.evaluate(async (pw) => {
    const b = window.__bench;
    const p = await b.rpc("profile.save", { id: "", name: "Analytics Cluster", engine: "pg", host: "localhost", port: 54329, auth: "password", username: "postgres", environment: "dev", tlsMode: "prefer", trustCert: false, databaseDefault: "postgres" });
    await b.useProd(p.id, pw, true);
  }, PG_PW);
  await sleep(2500);
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT customer_id, full_name, now() AS ts, interval '2 days' AS iv FROM public.customers;");
  await page.keyboard.press("F5");
  // The grid draws on a canvas, so check the footer row count rather than cell text.
  await page.waitForFunction(() => /\b1 rows\b/.test(document.body.innerText), { timeout: 20_000 }).catch(() => errors.push("pg results did not appear"));
  await sleep(500);
  await shot("10-postgres");
  // PostgreSQL tier 2 (sidecar): a PL/pgSQL variable declared in the body is offered; then a built-in snippet by prefix.
  const suggestLabels = () => page.evaluate(() => [...document.querySelectorAll(".suggest-widget .monaco-list-row")].map((r) => r.getAttribute("aria-label") ?? ""));
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("DO $$ DECLARE v_smoke_total numeric; BEGIN v_smoke");
  await page.waitForFunction(() => [...document.querySelectorAll(".suggest-widget .monaco-list-row")].some((r) => (r.getAttribute("aria-label") ?? "").includes("v_smoke_total")), { timeout: 5_000 })
    .catch(async () => errors.push(`pg tier-2 completion did not offer the declared variable: ${(await suggestLabels()).slice(0, 8).join(" | ")}`));
  await shot("10b-postgres-tier2");
  await page.keyboard.press("Escape");
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("upsert");
  await page.waitForFunction(() => [...document.querySelectorAll(".suggest-widget .monaco-list-row")].some((r) => (r.getAttribute("aria-label") ?? "").includes("upsert")), { timeout: 5_000 })
    .catch(async () => errors.push(`built-in snippet was not offered: ${(await suggestLabels()).slice(0, 8).join(" | ")}`));
  await page.keyboard.press("Escape");
  // SSH tunnel: PostgreSQL behind the fixture bastion; the unknown host key is shown, trusted, then used.
  const keyPath = path.join(root, "scripts", "bench", "docker", "ssh", "test_ed25519");
  const sshProfile = await page.evaluate(async (key, pw) => {
    const p = await window.__bench.rpc("profile.save", {
      id: "", name: "Via bastion", engine: "pg", host: "postgres", port: 5432, auth: "password", username: "postgres", environment: "dev", tlsMode: "prefer", trustCert: false,
      databaseDefault: "postgres", ssh: { host: "127.0.0.1", port: 22220, username: "querithm", auth: "privateKey", keyPath: key },
    });
    void window.__bench.useProd(p.id, pw, true);
    return p.id;
  }, keyPath, PG_PW);
  await page.waitForFunction(() => document.body.innerText.includes("Trust this SSH server?") || document.body.innerText.includes("TRUST THIS SSH SERVER?"), { timeout: 20_000 })
    .catch(() => errors.push("SSH host key dialog did not appear"));
  await sleep(300);
  await shot("11-ssh-hostkey");
  await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Trust and connect")?.click());
  await sleep(2500);
  await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
  await page.keyboard.type("SELECT inet_server_addr() IS NOT NULL AS through_tunnel, current_database();");
  await page.keyboard.press("F5");
  await page.waitForFunction(() => /\b1 rows\b/.test(document.body.innerText), { timeout: 20_000 }).catch(() => errors.push("query through the SSH tunnel did not return"));
  await shot("12-ssh-query");
  void sshProfile;
  // SQLite (Phase D file engine): a file profile made in the profile editor on a staged empty database (an empty file is
  // a valid empty SQLite database; the bridge keeps its own copy), writes allowed there, connected; CREATE / INSERT /
  // SELECT on it, and an ATTACH the sidecar refuses, with the reason in the tab's Messages pane.
  /**
   * Phase D: the import wizard on the active tab (a SQLite or MySQL connection): profile the CSV, create the table from
   * the proposed CREATE TABLE, load; `count` reads the row count outside Querithm.
   */
  const importOnActiveTab = async (label, csvFile, count) => {
    // The wizard names the new table after the file (smoke_import_<label>.csv → smoke_import_<label>).
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
    await page.keyboard.type(`DROP TABLE IF EXISTS ${path.basename(csvFile, ".csv")};`);
    await page.keyboard.press("Escape");
    await page.keyboard.press("F5");
    await sleep(1200);
    await page.keyboard.down("Control"); await page.keyboard.down("Alt"); await page.keyboard.press("I"); await page.keyboard.up("Alt"); await page.keyboard.up("Control");
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent === "Choose file…"), { timeout: 10_000 }).catch(() => errors.push(`${label}: import wizard did not open`));
    const [chooser] = await Promise.all([
      page.waitForFileChooser({ timeout: 10_000 }),
      page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Choose file…")?.click()),
    ]).catch((e) => { errors.push(`${label}: import file chooser: ${e.message}`); return []; });
    if (!chooser) return;
    await chooser.accept([csvFile]);
    await page.waitForFunction(() => document.body.innerText.includes("3 rows · 4 columns"), { timeout: 15_000 }).catch(() => errors.push(`${label}: import inspect did not finish`));
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Next: target"))?.click());
    await page.waitForFunction(() => [...document.querySelectorAll("textarea")].some((t) => t.value.includes("CREATE TABLE")), { timeout: 10_000 })
      .catch(async () => errors.push(`${label}: import target step did not open: ${await openDialogText()}`));
    await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Create table and import"))?.click());
    const imported = await page.waitForFunction(() => document.body.innerText.includes("Imported 3 rows"), { timeout: 20_000 }).then(() => true, () => false);
    await shot(`${label}-import-done`);
    if (!imported) errors.push(`${label}: import did not complete: ${await openDialogText()}`);
    else if (count() !== "3") errors.push(`${label}: the imported table holds ${count()} rows, not 3`);
    await closeDialogs();
  };

  /** A file engine's critical workflow: profile on a picked empty file with writes allowed → execute → grid edit → import → a refusal. */
  const fileEngine = async (e) => {
    console.log(`[step] ${e.label} file database`);
    const dbFile = path.join(out, `smoke-empty.${e.ext}`);
    writeFileSync(dbFile, "");
    await closeDialogs();
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("P"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
    await page.waitForFunction(() => document.activeElement?.tagName === "INPUT" && !document.activeElement.closest(".monaco-editor"), { timeout: 5_000 })
      .catch(() => errors.push(`${e.key}: command palette did not open`));
    await page.keyboard.type("New connection profile");
    await sleep(300);
    await page.keyboard.press("Enter");
    await page.waitForSelector('[role="dialog"] button[role="combobox"][aria-label="Server type"]', { timeout: 10_000 }).catch(() => errors.push(`${e.key}: the profile editor did not open`));
    await sleep(800); // system.engines answers: the picker then offers the exposed engines
    await page.evaluate(() => {
      const name = [...document.querySelectorAll('[role="dialog"] label')].find((l) => l.textContent?.startsWith("Connection name"))?.querySelector("input");
      name?.focus();
      name?.select();
    });
    await page.keyboard.type(`Smoke ${e.label}`);
    await page.click('[role="dialog"] button[role="combobox"][aria-label="Server type"]');
    const offered = await page.waitForFunction((label) => [...document.querySelectorAll('[role="option"]')].some((o) => o.textContent?.trim() === label), { timeout: 5_000 }, e.label).then(() => true, () => false);
    if (!offered) errors.push(`${e.key}: the engine picker does not offer ${e.label} in a default build: ${await page.evaluate(() => [...document.querySelectorAll('[role="option"]')].map((o) => o.textContent?.trim()).join(", "))}`);
    await page.evaluate((label) => [...document.querySelectorAll('[role="option"]')].find((o) => o.textContent?.trim() === label)?.click(), e.label);
    await page.waitForFunction(() => [...document.querySelectorAll('[role="dialog"] button')].some((b) => b.textContent === "Pick file…"), { timeout: 5_000 })
      .catch(() => errors.push(`${e.key}: the file section did not appear`));
    const [dbChooser] = await Promise.all([
      page.waitForFileChooser({ timeout: 10_000 }),
      clickButton("Pick file…"),
    ]).catch((err) => { errors.push(`${e.key}: file chooser: ${err.message}`); return []; });
    if (dbChooser) {
      await dbChooser.accept([dbFile]);
      await page.waitForFunction((name) => document.querySelector('[data-testid="picked-file"]')?.textContent?.trim() === name, { timeout: 10_000 }, path.basename(dbFile))
        .catch(() => errors.push(`${e.key}: the staged database was not shown as the picked file`));
      await clickButton("Allow writes…");
      await page.waitForFunction(() => document.querySelector('[role="dialog"]')?.textContent?.includes("Writes allowed"), { timeout: 5_000 })
        .catch(() => errors.push(`${e.key}: Allow writes… did not report write access`));
      await shot(`${e.shot}-${e.key}-profile`);
      await page.evaluate(() => [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent?.trim() === "Save")?.click());
      await page.waitForFunction(() => !document.querySelector('[role="dialog"] button[role="combobox"][aria-label="Server type"]'), { timeout: 10_000 })
        .catch(async () => errors.push(`${e.key}: the profile did not save: ${await openDialogText()}`));
      const sqliteProfile = await page.evaluate((name) => window.__bench.rpc("profile.list").then((ps) => ps.find((p) => p.name === name)), `Smoke ${e.label}`);
      if (!sqliteProfile) errors.push(`${e.key}: the saved profile is not in profile.list`);
      else if (sqliteProfile.engine !== e.engine || sqliteProfile.location !== "file" || !sqliteProfile.file?.grantId) errors.push(`${e.key}: the saved profile is not a file profile: ${JSON.stringify({ engine: sqliteProfile.engine, location: sqliteProfile.location, file: !!sqliteProfile.file })}`);
      if (sqliteProfile) {
        await page.evaluate((id) => window.__bench.useProd(id, ""), sqliteProfile.id);
        await sleep(1000);
        await page.click(".monaco-editor .view-lines");
        await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
        await page.keyboard.type(e.setup);
        await page.keyboard.press("Escape");
        await page.keyboard.press("F5");
        await page.waitForFunction(() => /\b2 rows\b/.test(document.body.innerText), { timeout: 20_000 })
          .catch(async () => errors.push(`${e.key}: the grid did not show 2 rows: ${await page.evaluate(() => document.querySelector("[data-q-messages]")?.textContent?.slice(0, 300) ?? document.body.innerText.slice(-300))}`));
        await sleep(500);
        await shot(`${e.shot}a-${e.key}-results`);
        // Grid edit on SQLite (dynamically typed column v): applied, then read from the file outside Querithm. The table was
        // created in the same script, so the result is read again once the DDL-triggered metadata refresh has it.
        await sleep(1500);
        await page.click(".monaco-editor .view-lines");
        await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
        await page.keyboard.type("SELECT id, v FROM t ORDER BY id;");
        await page.keyboard.press("Escape");
        await page.keyboard.press("F5");
        await page.waitForFunction(() => !!document.querySelector("[data-q-edit-toggle]:not([aria-disabled])"), { timeout: 15_000 })
          .catch(() => errors.push(`${e.key}: the grid Edit button did not appear (the keyed table should be editable)`));
        await page.evaluate(() => document.querySelector("[data-q-edit-toggle]:not([aria-disabled])")?.click());
        await page.waitForFunction(() => document.body.innerText.includes("Type or double-click to edit"), { timeout: 5_000 }).catch(() => errors.push(`${e.key}: grid edit bar did not appear`));
        await sleep(1000);
        const sqGrid = await page.evaluate(() => {
          const c = document.querySelector("[data-q-grid-edit] canvas")?.getBoundingClientRect();
          return c ? { x: c.left, y: c.top } : null;
        });
        if (sqGrid) {
          await page.mouse.click(sqGrid.x + 180, sqGrid.y + 28 + 12, { clickCount: 2 }); // double-click v of the first row
          // The grid takes focus after the click; keys typed before that reach the editor instead.
          await page.waitForFunction(() => !!document.activeElement?.closest("[data-q-grid-edit]"), { timeout: 3_000 })
            .catch(() => errors.push(`${e.key}: the grid did not take focus after the double-click`));
          // Typing opens the grid's overlay editor (glide's .gdg-input) over the selected cell with what was typed.
          await page.keyboard.type("edited");
          const editorText = await page.waitForFunction(() => document.querySelector(".gdg-input")?.value, { timeout: 5_000 }).then((h) => h.jsonValue(), () => null);
          if (editorText !== "edited") errors.push(`${e.key}: the grid cell editor does not hold the typed value (${editorText})`);
          // Evidence for a failure: is the cell editor read-only, and what did staging say at that moment?
          const atOpen = await page.evaluate(async () => ({ readOnly: document.querySelector(".gdg-input")?.readOnly ?? null, inDialog: !!document.activeElement?.closest('[role="dialog"]'), stagings: await window.__bench.stagingInfo() }));
          await shot(`${e.shot}b-${e.key}-typed`);
          await page.keyboard.press("Enter");
          const staged = await page.waitForFunction(() => document.body.innerText.includes("1 changed"), { timeout: 5_000 }).then(() => true, () => false);
          if (!staged) {
            await shot(`${e.shot}c-${e.key}-not-staged`);
            errors.push(`${e.key}: grid edit was not staged; at open: ${JSON.stringify({ readOnly: atOpen.readOnly, inDialog: atOpen.inDialog, t: atOpen.stagings.find((x) => x.table === "t") })}`);
          } else {
            await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Apply")?.click());
            await sleep(500);
            await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Apply changes")?.click());
            await sleep(1500);
            await shot(`${e.shot}c-${e.key}-edited`);
            await closeDialogs();
            // The bridge works on its own copy of the picked file: read the value back through Querithm (the grid's row count).
            const rowsOf = async (sql, n, what) => {
              await page.click(".monaco-editor .view-lines");
              await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
              await page.keyboard.type(sql);
              await page.keyboard.press("Escape");
              await page.keyboard.press("F5");
              const ok = await page.waitForFunction((k) => new RegExp(`\\b${k} rows\\b`).test(document.body.innerText), { timeout: 15_000 }, n).then(() => true, () => false);
              if (!ok) errors.push(`${e.key}: ${what}`);
            };
            await rowsOf("SELECT * FROM t WHERE v = 'edited';", 1, "the applied edit is not in the database");
            const csv = path.join(out, `smoke_import_${e.key}.csv`);
            writeFileSync(csv, "id,name,amount,born\n1,Alpha,1.50,2026-10-21\n2,Beta,2.25,2026-10-22\n3,Gamma,,\n");
            await importOnActiveTab(e.key, csv, () => "3");
            await rowsOf(`SELECT * FROM smoke_import_${e.key};`, 3, "the imported table does not hold 3 rows");
          }
        } else errors.push(`${e.key}: editable grid canvas not found`);
        await page.click(".monaco-editor .view-lines");
        await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
        await page.keyboard.type(e.refused);
        await page.keyboard.press("Escape");
        await page.keyboard.press("F5");
        await page.waitForFunction((reason) => document.querySelector("[data-q-messages]")?.textContent?.includes(reason), { timeout: 15_000 }, e.reason)
          .catch(async () => errors.push(`${e.key}: the refused statement did not show its reason in Messages: ${await page.evaluate(() => document.querySelector("[data-q-messages]")?.textContent?.slice(0, 300) ?? "(no Messages pane)")}`));
        await sleep(300);
        await shot(`${e.shot}d-${e.key}-refused`);
      }
    }
    await closeDialogs();
  };
  await fileEngine({
    key: "sqlite", label: "SQLite", engine: "sqlite", ext: "sqlite", shot: "25",
    setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (1),('a'); SELECT * FROM t;",
    refused: "ATTACH DATABASE 'other.db' AS other;", reason: "ATTACH and DETACH are not available",
  });
  // DuckDB (Phase D3, behind the dev flag): an empty picked file with writes allowed becomes a new database.
  await fileEngine({
    key: "duckdb", label: "DuckDB", engine: "duckDb", ext: "duckdb", shot: "26",
    setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, '1'), (2, 'a'); SELECT * FROM t;",
    refused: "COPY t TO 'out.csv';", reason: "COPY … TO writes a file outside the database",
  });
  // MySQL (Phase D2, behind the dev flag): connect → execute → results, then a grid edit applied and read back on the server.
  console.log("[step] MySQL connect, execute, grid edit");
  if (mysqlNow("SELECT 1") !== "1") errors.push("mysql: the mysql84 fixture is not reachable (docker compose up mysql84)");
  else {
    mysqlNow("CREATE DATABASE IF NOT EXISTS q_smoke; DROP TABLE IF EXISTS q_smoke.smoke_t; CREATE TABLE q_smoke.smoke_t (id INT PRIMARY KEY, name VARCHAR(20)); INSERT INTO q_smoke.smoke_t VALUES (1, 'one'), (2, 'two');");
    await closeDialogs();
    await page.evaluate(async (pw) => {
      const b = window.__bench;
      // The fixture's certificate is self-signed: the explicit Required downgrade, as the integration tests take.
      const p = await b.rpc("profile.save", { id: "", name: "Smoke MySQL", engine: "mySql", host: "127.0.0.1", port: 33084, auth: "password", username: "root", environment: "dev", tlsMode: "require", databaseDefault: "q_smoke" });
      await b.useProd(p.id, pw, true);
    }, MYSQL_PW);
    await sleep(2500);
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
    await page.keyboard.type("SELECT id, name FROM smoke_t ORDER BY id;");
    await page.keyboard.press("Escape");
    await page.keyboard.press("F5");
    await page.waitForFunction(() => /\b2 rows\b/.test(document.body.innerText), { timeout: 20_000 })
      .catch(async () => errors.push(`mysql: the grid did not show 2 rows: ${await page.evaluate(() => document.querySelector("[data-q-messages]")?.textContent?.slice(0, 300) ?? document.body.innerText.slice(-300))}`));
    await page.waitForFunction(() => !!document.querySelector("[data-q-edit-toggle]:not([aria-disabled])"), { timeout: 15_000 })
      .catch(() => errors.push("mysql: the grid Edit button did not appear (the result should be editable)"));
    await page.evaluate(() => document.querySelector("[data-q-edit-toggle]:not([aria-disabled])")?.click());
    await page.waitForFunction(() => document.body.innerText.includes("Type or double-click to edit"), { timeout: 5_000 }).catch(() => errors.push("mysql: grid edit bar did not appear"));
    await sleep(500);
    const myGrid = await page.evaluate(() => {
      const c = document.querySelector("[data-q-grid-edit] canvas")?.getBoundingClientRect();
      return c ? { x: c.left, y: c.top } : null;
    });
    if (myGrid) {
      await page.mouse.click(myGrid.x + 180, myGrid.y + 28 + 12); // name of the first row
      await sleep(200);
      await page.keyboard.press("Enter");
      await page.waitForFunction(() => document.activeElement?.tagName === "TEXTAREA" && !document.activeElement.closest(".monaco-editor"), { timeout: 5_000 })
        .catch(() => errors.push("mysql: grid cell editor did not open"));
      await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
      await page.keyboard.type("uno");
      await sleep(200);
      await page.keyboard.press("Enter");
      await page.waitForFunction(() => document.body.innerText.includes("1 changed"), { timeout: 5_000 }).catch(() => errors.push("mysql: grid edit was not staged"));
      await shot("26-mysql-staged");
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Apply")?.click());
      await sleep(500);
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Apply changes")?.click());
      let applied = "";
      for (let i = 0; i < 50 && (applied = mysqlNow("SELECT name FROM q_smoke.smoke_t WHERE id = 1")) !== "uno"; i++) await sleep(200);
      if (applied !== "uno") errors.push(`mysql: the applied edit did not reach the server (name = ${applied}): ${await openDialogText()}`);
      await shot("26a-mysql-applied");
      await closeDialogs();
      mkdirSync(path.join(out, "mysql"), { recursive: true });
      const csv = path.join(out, "mysql", "smoke_import.csv"); // the wizard names the table after the file
      writeFileSync(csv, "id,name,amount,born\n1,Alpha,1.50,2026-10-21\n2,Beta,2.25,2026-10-22\n3,Gamma,,\n");
      await importOnActiveTab("mysql", csv, () => mysqlNow("SELECT COUNT(*) FROM q_smoke.smoke_import"));
    } else errors.push("mysql: editable grid canvas not found");
    await closeDialogs();

    // Find usages and the rename preview on a MySQL column (MySqlReferenceFinder).
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.press("A"); await page.keyboard.up("Control");
    await page.keyboard.type("SELECT t.name FROM smoke_t t");
    await page.keyboard.press("Escape");
    await page.keyboard.press("Home");
    for (let i = 0; i < 10; i++) await page.keyboard.press("ArrowRight");
    await page.keyboard.down("Shift"); await page.keyboard.press("F12"); await page.keyboard.up("Shift");
    await page.waitForFunction(() => (document.querySelector("[data-q-usages]")?.textContent ?? "").includes("Usages of column") && document.querySelectorAll("[data-q-usage-hit]").length > 0, { timeout: 20_000 })
      .catch(() => errors.push("mysql: find usages did not list the column's usages"));
    await sleep(400);
    await shot("26b-mysql-usages");
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.press("Home");
    for (let i = 0; i < 10; i++) await page.keyboard.press("ArrowRight");
    await page.keyboard.press("F2");
    await page.waitForSelector('input[aria-label="New name"]', { timeout: 10_000 }).catch(() => errors.push("mysql: rename dialog did not open"));
    if (await page.$('input[aria-label="New name"]')) {
      await page.keyboard.type("label");
      await page.waitForFunction(() => (document.querySelector("[data-q-rename-objects]")?.textContent ?? "").length > 0, { timeout: 20_000 })
        .catch(() => errors.push("mysql: rename preview did not appear"));
      await sleep(500);
      await shot("26c-mysql-rename");
      await page.evaluate(() => [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent === "Cancel")?.click());
      await sleep(300);
    }

    // Schema compare: the tab's database (q_smoke) against the fixture's querithm database on the same server.
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("D"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
    await page.waitForSelector('select[aria-label="Target connection"]', { timeout: 10_000 }).catch(() => errors.push("mysql: schema compare tab did not open"));
    const mySource = await page.evaluate(() => document.querySelector('select[aria-label="Source connection"]')?.value);
    if (mySource) {
      await page.select('select[aria-label="Target connection"]', mySource);
      await page.waitForFunction(() => [...(document.querySelector('select[aria-label="Target database"]')?.options ?? [])].some((o) => o.value === "querithm"), { timeout: 10_000 })
        .catch(() => errors.push("mysql: schema compare target databases did not load"));
      await page.select('select[aria-label="Target database"]', "querithm");
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Compare")?.click());
      await page.waitForFunction(() => document.querySelectorAll("[data-q-compare-item]").length > 0, { timeout: 30_000 }).catch(() => errors.push("mysql: schema compare returned no items"));
      await sleep(600);
      await shot("26d-mysql-schema-compare");
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Migration")?.click());
      await page.waitForFunction(() => [...document.querySelectorAll(".monaco-editor .view-lines")].some((v) => v.textContent?.replace(/\u00a0/g, " ").includes("Querithm schema migration")), { timeout: 15_000 })
        .catch(() => errors.push("mysql: schema migration script did not appear"));
      await sleep(400);
    } else errors.push("mysql: schema compare source was not prefilled from the tab");
    await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
    await sleep(400);

    // Data compare: a query that changes every even row's name against the table it reads.
    await page.click(".monaco-editor .view-lines");
    await page.keyboard.down("Control"); await page.keyboard.down("Shift"); await page.keyboard.press("P"); await page.keyboard.up("Shift"); await page.keyboard.up("Control");
    await sleep(300);
    await page.keyboard.type("Data compare");
    await sleep(300);
    await page.keyboard.press("Enter");
    await page.waitForSelector('select[aria-label="Source kind"]', { timeout: 10_000 }).catch(() => errors.push("mysql: data compare tab did not open"));
    if (await page.$('select[aria-label="Source kind"]')) {
      await page.select('select[aria-label="Source kind"]', "query");
      await page.waitForSelector('textarea[aria-label="Source query"]', { timeout: 5_000 });
      await page.type('textarea[aria-label="Source query"]', "SELECT id, CASE WHEN id % 2 = 0 THEN CONCAT(name, '!') ELSE name END AS name FROM smoke_t");
      await page.type('input[aria-label="Key columns"]', "id");
      await page.type('input[aria-label="Target table"]', "q_smoke.smoke_t");
      await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent === "Compare")?.click());
      await page.waitForFunction(() => /\d+ ms\b/.test(document.querySelector("[data-q-data-progress]")?.textContent ?? ""), { timeout: 30_000 })
        .catch(() => errors.push("mysql: data compare did not finish"));
      await sleep(600);
      const myDifferent = await page.evaluate(() => [...document.querySelectorAll("button")].find((b) => b.textContent?.startsWith("Different"))?.textContent ?? "");
      if (!/Different [1-9]/.test(myDifferent)) errors.push(`mysql: data compare found no difference: ${myDifferent}`);
      await shot("26e-mysql-data-compare");
    }
    await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
    await sleep(400);

    // Activity monitor on MySQL (performance_schema): the sessions grid loads and shows this server's sessions.
    const myProfile = await page.evaluate(() => window.__bench.rpc("profile.list").then((ps) => ps.find((p) => p.name === "Smoke MySQL")?.id));
    await page.evaluate((id) => window.__bench.openMonitor(id), myProfile);
    await page.waitForSelector('[data-testid="sessions-grid"]', { timeout: 30_000 }).catch(() => errors.push("mysql: the activity monitor did not open"));
    await sleep(2500);
    await shot("26f-mysql-monitor");
    await page.keyboard.down("Control"); await page.keyboard.press("W"); await page.keyboard.up("Control");
    await sleep(400);
    await closeDialogs();
  }
  // Light theme.
  await page.evaluate(() => { document.documentElement.dataset.theme = "light"; });
  await sleep(300);
  await shot("07-light");
  // Sidecar crash (L8): a PostgreSQL monitor shows "monitor stopped"; its connection does not outlive the sidecar.
  {
    const pgProfile = await page.evaluate(() => window.__bench.rpc("profile.list").then((ps) => ps.find((p) => p.name === "Analytics Cluster")?.id));
    await page.evaluate((id) => window.__bench.openMonitor(id), pgProfile);
    await page.waitForSelector('[data-testid="sessions-grid"]', { timeout: 30_000 }).catch(() => errors.push("PostgreSQL monitor did not open"));
    if (pgNow("SELECT count(*) FROM pg_stat_activity WHERE application_name = 'Querithm Monitor'") === "0") errors.push("PostgreSQL monitor connection not seen on the server");
    const pid = await page.evaluate(() => window.__bench.rpc("system.ping").then((r) => r.pid));
    process.kill(pid);
    await page.waitForFunction(() => document.body.innerText.includes("The sidecar restarted"), { timeout: 15_000 }).catch(() => errors.push("monitor did not report the sidecar restart"));
    await shot("23-monitor-sidecar-restart");
    await sleep(5000);
    const orphans = pgNow("SELECT count(*) FROM pg_stat_activity WHERE application_name = 'Querithm Monitor'");
    if (orphans !== "0") errors.push(`orphan monitor connection after the sidecar died: ${orphans}`);
  }
} catch (e) {
  errors.push(`smoke failed: ${e.stack ?? e}`);
} finally {
  for (const b of background) try { b.kill(); } catch { /* ended */ }
  await browser?.close();
  preview.kill();
  bridge.kill();
}
console.log(`screenshots in ${out}`);
writeFileSync(path.join(out, "layout-issues.json"), JSON.stringify([...layoutIssues.values()], null, 2));
for (const i of layoutIssues.values())
  errors.push(`layout ${i.kind}: ${i.control}${i.other ? ` ↔ ${i.other}` : ""}${i.detail ? ` (${i.detail})` : ""} — ${i.where.slice(0, 3).join(", ")}${i.where.length > 3 ? ` +${i.where.length - 3} more` : ""}`);
if (errors.length) {
  console.log("ERRORS:\n" + errors.join("\n"));
  process.exit(1);
}

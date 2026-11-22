import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// §13 rule 6 / Phase D0c: the webview's fs scopes stay under $DATA/querithm, so the shell's database-file grant
// registry (<app config dir>/grants.json) is out of the webview's reach; no permission was added for file databases.
const caps = JSON.parse(readFileSync(new URL("../../src-tauri/capabilities/default.json", import.meta.url), "utf8"));
const conf = JSON.parse(readFileSync(new URL("../../src-tauri/tauri.conf.json", import.meta.url), "utf8"));

describe("Tauri capabilities", () => {
  const scoped = caps.permissions.filter((p: unknown) => typeof p === "object") as { identifier: string; allow: { path: string }[] }[];

  it("scopes every fs permission to the app's data folder", () => {
    for (const p of scoped) {
      expect(p.identifier.startsWith("fs:")).toBe(true);
      for (const a of p.allow) expect(a.path === "$DATA/querithm" || a.path.startsWith("$DATA/querithm/"), `${p.identifier} ${a.path}`).toBe(true);
    }
  });

  it("gives the webview no fs access to the config folder where the grant registry lives", () => {
    const text = JSON.stringify(caps);
    expect(text).not.toMatch(/\$(APP)?CONFIG|\$HOME|\$DESKTOP|\$DOCUMENT|\$DOWNLOAD/);
  });

  it("adds to core:default only the webview's own page zoom (the Interface scale setting, L26 / §16)", () => {
    const ids = caps.permissions.map((p: string | { identifier: string }) => (typeof p === "string" ? p : p.identifier));
    expect(ids.filter((i: string) => i.startsWith("core:"))).toEqual(["core:default", "core:webview:allow-set-webview-zoom"]);
  });

  it("holds no http, shell or updater permission", () => {
    const ids = caps.permissions.map((p: string | { identifier: string }) => (typeof p === "string" ? p : p.identifier));
    expect(ids.some((i: string) => /^(http|shell|updater):/.test(i))).toBe(false);
  });

  it("gives the webview no window-state permission: the shell alone restores and saves the window's place (§16)", () => {
    const ids = caps.permissions.map((p: string | { identifier: string }) => (typeof p === "string" ? p : p.identifier));
    expect(ids.some((i: string) => i.startsWith("window-state:"))).toBe(false);
    // The plugin's script is injected only with the global API on.
    expect(conf.app.withGlobalTauri ?? false).toBe(false);
  });
});

// §16 window placement: the main window is created hidden so it never shows at the default place and jumps; the shell
// restores it and must then show it, or the app would start with no window at all.
const shellSource = (file: string) => readFileSync(new URL(`../../src-tauri/src/${file}`, import.meta.url), "utf8");

describe("main window placement", () => {
  const main = conf.app.windows.find((w: { label: string }) => w.label === "main");

  it("creates the main window hidden and centred, at the default size for a first start", () => {
    expect(main).toMatchObject({ visible: false, center: true, width: 1440, height: 900 });
  });

  it("restores and shows the main window in the shell's setup", () => {
    const setup = shellSource("main.rs").split(".setup(")[1] ?? "";
    expect(setup).toContain("window_state::restore(&window)");
    expect(shellSource("main.rs")).toContain(".plugin(window_state::plugin())");
    const restore = shellSource("window_state.rs").split("pub fn restore")[1]?.split("\n}\n")[0] ?? "";
    expect(restore).toContain("window.show()");
  });

  it("saves the window's place on the two ways out that never reach the plugin's own save at exit", () => {
    const closeNow = shellSource("close.rs").split("fn close_now")[1]?.split("\n}\n")[0] ?? "";
    expect(closeNow.indexOf("window_state::save(")).toBeGreaterThan(-1);
    expect(closeNow.indexOf("window_state::save(")).toBeLessThan(closeNow.indexOf(".destroy()"));
    // The install runs on a worker thread: the save goes through the main thread (a direct one can deadlock).
    const install = shellSource("updater.rs").split("pub async fn update_install")[1] ?? "";
    expect(install).not.toContain("window_state::save(");
    expect(install.indexOf("window_state::save_from_worker(")).toBeGreaterThan(-1);
    expect(install.indexOf("window_state::save_from_worker(")).toBeLessThan(install.indexOf("update.install("));
  });
});

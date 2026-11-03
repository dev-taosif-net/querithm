import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// §13 rule 6 / Phase D0c: the webview's fs scopes stay under $DATA/querithm, so the shell's database-file grant
// registry (<app config dir>/grants.json) is out of the webview's reach; no permission was added for file databases.
const caps = JSON.parse(readFileSync(new URL("../../src-tauri/capabilities/default.json", import.meta.url), "utf8"));

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
});

import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// Settings → About shows the Tauri package version (app_info), the updater compares it, and the dev bridge reports it:
// the three manifests that carry the app version must agree.
const read = (p: string) => readFileSync(new URL(`../../${p}`, import.meta.url), "utf8");

describe("app version", () => {
  it("package.json, tauri.conf.json and Cargo.toml carry the same version", () => {
    const tauri = JSON.parse(read("src-tauri/tauri.conf.json")).version;
    const pkg = JSON.parse(read("package.json")).version;
    const cargo = /^\[package\][\s\S]*?^version\s*=\s*"([^"]+)"/m.exec(read("src-tauri/Cargo.toml"))?.[1];
    expect(tauri).toMatch(/^\d+\.\d+\.\d+/);
    expect({ pkg, cargo }).toEqual({ pkg: tauri, cargo: tauri });
  });
});

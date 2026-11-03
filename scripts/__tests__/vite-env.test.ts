import { describe, expect, it } from "vitest";
import config from "../../vite.config";

// Vite copies every env var whose name starts with an envPrefix into import.meta.env, so a prefix that matches a
// secret's name can put the secret into the bundle (the TAURI_SIGNING_PRIVATE_KEY case of CVE-2023-46115).
const SECRET_ENV = [
  "TAURI_SIGNING_PRIVATE_KEY", "TAURI_SIGNING_PRIVATE_KEY_PASSWORD", "RELEASES_REPO_TOKEN", "GH_TOKEN", "GITHUB_TOKEN",
  // optional platform signing (release.yml)
  "APPLE_CERTIFICATE", "APPLE_CERTIFICATE_PASSWORD", "APPLE_ID", "APPLE_PASSWORD", "WINDOWS_CERTIFICATE", "WINDOWS_CERTIFICATE_PASSWORD",
];

describe("vite envPrefix", () => {
  const prefixes = ([] as string[]).concat(config.envPrefix ?? "VITE_");

  it("exposes none of the CI secrets to import.meta.env", () => {
    for (const name of SECRET_ENV) expect(prefixes.some((p) => name.startsWith(p)), name).toBe(false);
  });

  it("still exposes the bridge URL and Tauri's build env", () => {
    expect(prefixes.some((p) => "VITE_BRIDGE_URL".startsWith(p))).toBe(true);
    expect(prefixes.some((p) => "TAURI_ENV_PLATFORM".startsWith(p))).toBe(true);
  });
});

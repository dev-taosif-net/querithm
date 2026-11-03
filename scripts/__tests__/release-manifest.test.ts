import { createHash, generateKeyPairSync, randomBytes, sign, type KeyObject } from "node:crypto";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { buildManifest, configuredPubkey, parsePublicKey, parseSignature, RELEASES_REPO, TARGETS, verifyFile } from "../release-manifest.mjs";

// fixtures/updater: a file signed by `tauri signer sign --app-version 0.1.2` with a throwaway key whose private half
// was deleted. It pins the format Tauri writes; the other tests sign with keys made here.
const fixtures = path.join(__dirname, "fixtures", "updater");
const tauriPub = readFileSync(path.join(fixtures, "test.key.pub"), "utf8");
const tauriSig = readFileSync(path.join(fixtures, "a.bin.sig"), "utf8");

interface Key { pubkey: string; priv: KeyObject; keyId: Buffer }

function makeKey(): Key {
  const { publicKey, privateKey } = generateKeyPairSync("ed25519");
  const raw = publicKey.export({ format: "der", type: "spki" }).subarray(12);
  const keyId = randomBytes(8);
  const pub = `untrusted comment: minisign public key\n${Buffer.concat([Buffer.from("Ed"), keyId, raw]).toString("base64")}\n`;
  return { pubkey: Buffer.from(pub).toString("base64"), priv: privateKey, keyId };
}

/** A signature in the layout Tauri writes (prehashed minisign, base64 of the whole .sig text). */
function signLikeTauri(key: Key, bytes: Buffer, comment: string) {
  const sig = sign(null, createHash("blake2b512").update(bytes).digest(), key.priv);
  const global = sign(null, Buffer.concat([sig, Buffer.from(comment)]), key.priv);
  const text = `untrusted comment: signature from tauri secret key\n${Buffer.concat([Buffer.from("ED"), key.keyId, sig]).toString("base64")}\ntrusted comment: ${comment}\n${global.toString("base64")}\n`;
  return Buffer.from(text).toString("base64");
}

let dir: string;
let key: Key;

function writeRelease(version: string, k = key, files: Record<string, string> = {}) {
  for (const asset of new Set(Object.values(TARGETS))) {
    const bytes = Buffer.from(files[asset] ?? `installer ${asset}`);
    writeFileSync(path.join(dir, asset), bytes);
    writeFileSync(path.join(dir, `${asset}.sig`), signLikeTauri(k, bytes, `timestamp:1\tfile:${asset}\tversion:${version}`));
  }
}

beforeEach(() => {
  dir = mkdtempSync(path.join(os.tmpdir(), "qmanifest-"));
  key = makeKey();
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

describe("signature format", () => {
  it("reads a public key and signature written by the Tauri CLI", () => {
    const pub = parsePublicKey(tauriPub);
    const s = parseSignature(tauriSig);
    expect(s.keyId.equals(pub.keyId)).toBe(true);
    expect(s.prehashed).toBe(true);
    expect(s.version).toBe("0.1.2");
  });

  it("verifies the Tauri-signed fixture, and only for its signed version", async () => {
    const pub = parsePublicKey(tauriPub);
    await expect(verifyFile(pub, tauriSig, path.join(fixtures, "a.bin"), "0.1.2")).resolves.toBeUndefined();
    await expect(verifyFile(pub, tauriSig, path.join(fixtures, "a.bin"), "0.1.3")).rejects.toThrow("signed for version 0.1.2, release is 0.1.3");
  });

  it("rejects malformed keys and signatures", () => {
    expect(() => parsePublicKey("bm90IGEga2V5")).toThrow("not a minisign file");
    expect(() => parsePublicKey(Buffer.from("untrusted comment: x\nAAAA\n").toString("base64"))).toThrow("not an Ed25519 minisign key");
    expect(() => parseSignature(Buffer.from("untrusted comment: x\nAAAA\ntrusted comment: y\n").toString("base64"))).toThrow("not an Ed25519 minisign signature");
    const noTrusted = Buffer.from(tauriSig, "base64").toString().replace("\ntrusted comment: ", "\ncomment: ");
    expect(() => parseSignature(Buffer.from(noTrusted).toString("base64"))).toThrow("no trusted comment");
  });

  it("a signature without a version is refused (requireSignedVersion)", async () => {
    const f = path.join(dir, "x.bin");
    writeFileSync(f, "x");
    await expect(verifyFile(parsePublicKey(key.pubkey), signLikeTauri(key, Buffer.from("x"), "timestamp:1\tfile:x.bin"), f, "1.0.0")).rejects.toThrow("signed for version (none)");
  });
});

describe("buildManifest", () => {
  it("lists every updater target with its signature and the versioned download URL", async () => {
    writeRelease("0.2.0");
    const m = await buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey, notes: "  Faster grid\n", date: new Date("2026-10-25T00:00:00Z") });
    expect(m).toMatchObject({ version: "0.2.0", notes: "Faster grid", pub_date: "2026-10-25T00:00:00.000Z" });
    expect(Object.keys(m.platforms).sort()).toEqual(Object.keys(TARGETS).sort());
    expect(m.platforms["linux-x86_64-deb"]).toEqual({
      signature: readFileSync(path.join(dir, "Querithm-linux-x64.deb.sig"), "utf8").trim(),
      url: `https://github.com/${RELEASES_REPO}/releases/download/v0.2.0/Querithm-linux-x64.deb`,
    });
    expect(m.platforms["linux-x86_64-appimage"].url).toMatch(/Querithm-linux-x64\.AppImage$/);
    expect(m.platforms["darwin-aarch64-app"].url).toMatch(/Querithm-macos-arm64\.app\.tar\.gz$/);
    expect(m.platforms["windows-x86_64-msi"].url).toMatch(/Querithm-windows-x64\.msi$/);
  });

  it("covers both Windows installers, both Macs and the three Linux packages", () => {
    expect(TARGETS).toMatchObject({
      "windows-x86_64-nsis": "Querithm-windows-x64-setup.exe",
      "windows-x86_64-msi": "Querithm-windows-x64.msi",
      "darwin-aarch64-app": "Querithm-macos-arm64.app.tar.gz",
      "darwin-x86_64-app": "Querithm-macos-x64.app.tar.gz",
      "linux-x86_64-appimage": "Querithm-linux-x64.AppImage",
      "linux-x86_64-deb": "Querithm-linux-x64.deb",
      "linux-x86_64-rpm": "Querithm-linux-x64.rpm",
    });
  });

  it("fails when a package was signed with a different key than the app trusts", async () => {
    writeRelease("0.2.0", makeKey());
    await expect(buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey })).rejects.toThrow("not the key in tauri.conf.json");
  });

  it("fails when a package changed after signing", async () => {
    writeRelease("0.2.0");
    writeFileSync(path.join(dir, "Querithm-linux-x64.rpm"), "tampered");
    await expect(buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey })).rejects.toThrow("Querithm-linux-x64.rpm: signature does not match the file");
  });

  it("fails when the trusted comment was edited", async () => {
    writeRelease("0.2.0");
    const f = path.join(dir, "Querithm-windows-x64.msi.sig");
    const text = Buffer.from(readFileSync(f, "utf8"), "base64").toString().replace("timestamp:1", "timestamp:2");
    writeFileSync(f, Buffer.from(text).toString("base64"));
    await expect(buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey })).rejects.toThrow("trusted comment was altered");
  });

  it("fails when a .sig is left over from another version", async () => {
    writeRelease("0.1.9");
    await expect(buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey })).rejects.toThrow("signed for version 0.1.9, release is 0.2.0");
  });

  it("fails when a platform's package or signature is missing", async () => {
    writeRelease("0.2.0");
    rmSync(path.join(dir, "Querithm-linux-x64.deb.sig"));
    await expect(buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey })).rejects.toThrow("missing Querithm-linux-x64.deb or Querithm-linux-x64.deb.sig");
  });

  it("cuts very long notes", async () => {
    writeRelease("0.2.0");
    const m = await buildManifest({ dir, tag: "v0.2.0", pubkey: key.pubkey, notes: "n".repeat(9000) });
    expect(m.notes).toHaveLength(8001);
  });
});

describe("configuredPubkey", () => {
  it("refuses the placeholder so a release cannot ship without the real key", () => {
    const root = mkdtempSync(path.join(os.tmpdir(), "qroot-"));
    const conf = (pubkey: string) => {
      writeFileSync(path.join(root, "src-tauri", "tauri.conf.json"), JSON.stringify({ plugins: { updater: { pubkey } } }));
    };
    try {
      mkdirSync(path.join(root, "src-tauri"));
      conf("REPLACE_WITH_TAURI_SIGNING_PUBLIC_KEY");
      expect(() => configuredPubkey(root)).toThrow("is not set");
      conf("");
      expect(() => configuredPubkey(root)).toThrow("is not set");
      conf(key.pubkey);
      expect(configuredPubkey(root)).toBe(key.pubkey);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it("the app's public key is a real minisign key, not the placeholder", () => {
    const root = path.join(__dirname, "..", "..");
    expect(() => parsePublicKey(configuredPubkey(root))).not.toThrow();
  });

  it("package.json, tauri.conf.json and Cargo.toml name the same version, and it has release notes", () => {
    const root = path.join(__dirname, "..", "..");
    const app = JSON.parse(readFileSync(path.join(root, "src-tauri", "tauri.conf.json"), "utf8")).version;
    expect(JSON.parse(readFileSync(path.join(root, "package.json"), "utf8")).version).toBe(app);
    expect(readFileSync(path.join(root, "src-tauri", "Cargo.toml"), "utf8")).toMatch(new RegExp(`^version = "${app.replace(/\./g, "\\.")}"$`, "m"));
    expect(readFileSync(path.join(root, "docs", "release-notes", `v${app}.md`), "utf8").trim().length).toBeGreaterThan(0);
  });

  it("the app's config points the updater at this repository's latest.json and requires signed versions", () => {
    const conf = JSON.parse(readFileSync(path.join(__dirname, "..", "..", "src-tauri", "tauri.conf.json"), "utf8"));
    expect(conf.bundle.createUpdaterArtifacts).toBe(true);
    expect(conf.plugins.updater).toMatchObject({
      endpoints: [`https://github.com/${RELEASES_REPO}/releases/latest/download/latest.json`],
      requireSignedVersion: true,
    });
  });
});

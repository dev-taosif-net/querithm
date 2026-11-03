#!/usr/bin/env node
// Builds latest.json, the updater manifest (§16), from the renamed release assets and their .sig files.
// Every signature is verified against the public key in tauri.conf.json and must carry the release version, so a
// release signed with another key, or a stale .sig, fails here instead of breaking every user's update.
//   node scripts/release-manifest.mjs <assets-dir> <tag> [notes-file]
import { createHash, createPublicKey, verify } from "node:crypto";
import { createReadStream, existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const RELEASES_REPO = "dev-taosif-net/querithm-releases";

/** Updater target ({os}-{arch}[-{bundle}], tauri-plugin-updater) → stable asset name (release.yml). */
export const TARGETS = {
  "windows-x86_64-nsis": "Querithm-windows-x64-setup.exe",
  "windows-x86_64-msi": "Querithm-windows-x64.msi",
  "windows-x86_64": "Querithm-windows-x64-setup.exe",
  "darwin-aarch64-app": "Querithm-macos-arm64.app.tar.gz",
  "darwin-aarch64": "Querithm-macos-arm64.app.tar.gz",
  "darwin-x86_64-app": "Querithm-macos-x64.app.tar.gz",
  "darwin-x86_64": "Querithm-macos-x64.app.tar.gz",
  "linux-x86_64-appimage": "Querithm-linux-x64.AppImage",
  "linux-x86_64-deb": "Querithm-linux-x64.deb",
  "linux-x86_64-rpm": "Querithm-linux-x64.rpm",
  "linux-x86_64": "Querithm-linux-x64.AppImage",
};

const ED25519_SPKI = Buffer.from("302a300506032b6570032100", "hex");
const MAX_NOTES = 8000;

const b64lines = (b64, what) => {
  const text = Buffer.from(String(b64).trim(), "base64").toString("utf8");
  const lines = text.split(/\r?\n/);
  if (!lines[0]?.startsWith("untrusted comment:")) throw new Error(`${what} is not a minisign file`);
  return lines;
};

/** The base64 `pubkey` of tauri.conf.json (a minisign .pub file). */
export function parsePublicKey(b64) {
  const raw = Buffer.from(b64lines(b64, "public key")[1] ?? "", "base64");
  if (raw.length !== 42 || raw.subarray(0, 2).toString() !== "Ed") throw new Error("public key is not an Ed25519 minisign key");
  return { keyId: raw.subarray(2, 10), key: createPublicKey({ key: Buffer.concat([ED25519_SPKI, raw.subarray(10)]), format: "der", type: "spki" }) };
}

/** A .sig file as Tauri writes it: base64 of a minisign signature. */
export function parseSignature(b64) {
  const lines = b64lines(b64, "signature");
  const sig = Buffer.from(lines[1] ?? "", "base64");
  const trusted = lines[2] ?? "";
  if (sig.length !== 74 || !["Ed", "ED"].includes(sig.subarray(0, 2).toString())) throw new Error("signature is not an Ed25519 minisign signature");
  if (!trusted.startsWith("trusted comment: ")) throw new Error("signature has no trusted comment");
  const comment = trusted.slice("trusted comment: ".length);
  const version = comment.split("\t").find((f) => f.startsWith("version:"))?.slice("version:".length) ?? null;
  return { prehashed: sig.subarray(0, 2).toString() === "ED", keyId: sig.subarray(2, 10), sig: sig.subarray(10), comment, version, global: Buffer.from(lines[3] ?? "", "base64") };
}

async function blake2b(file) {
  const hash = createHash("blake2b512");
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest();
}

/** Throws unless `sigB64` is a valid signature of `file` by `pub`, bound to `version`. */
export async function verifyFile(pub, sigB64, file, version) {
  const s = parseSignature(sigB64);
  const name = path.basename(file);
  if (!s.keyId.equals(pub.keyId)) throw new Error(`${name}: signed with key ${Buffer.from(s.keyId).reverse().toString("hex").toUpperCase()}, not the key in tauri.conf.json`);
  const message = s.prehashed ? await blake2b(file) : readFileSync(file);
  if (!verify(null, message, pub.key, s.sig)) throw new Error(`${name}: signature does not match the file`);
  if (!verify(null, Buffer.concat([s.sig, Buffer.from(s.comment)]), pub.key, s.global)) throw new Error(`${name}: trusted comment was altered`);
  if (s.version !== version) throw new Error(`${name}: signed for version ${s.version ?? "(none)"}, release is ${version} (requireSignedVersion)`);
}

export async function buildManifest({ dir, tag, pubkey, notes = "", date = new Date() }) {
  const version = tag.replace(/^v/, "");
  const pub = parsePublicKey(pubkey);
  const platforms = {};
  for (const [target, asset] of Object.entries(TARGETS)) {
    const file = path.join(dir, asset);
    if (!existsSync(file) || !existsSync(`${file}.sig`)) throw new Error(`missing ${asset} or ${asset}.sig for ${target}`);
    const signature = readFileSync(`${file}.sig`, "utf8").trim();
    await verifyFile(pub, signature, file, version);
    // The versioned URL, not latest/: a manifest only ever points at its own release's files.
    platforms[target] = { signature, url: `https://github.com/${RELEASES_REPO}/releases/download/${encodeURIComponent(tag)}/${asset}` };
  }
  const trimmed = notes.trim();
  return { version, notes: trimmed.length > MAX_NOTES ? `${trimmed.slice(0, MAX_NOTES)}…` : trimmed, pub_date: date.toISOString(), platforms };
}

export function configuredPubkey(root) {
  const conf = JSON.parse(readFileSync(path.join(root, "src-tauri", "tauri.conf.json"), "utf8"));
  const key = conf.plugins?.updater?.pubkey ?? "";
  if (!key || key.startsWith("REPLACE_")) throw new Error("plugins.updater.pubkey in tauri.conf.json is not set; paste the public key of TAURI_SIGNING_PRIVATE_KEY");
  return key;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [dir, tag, notesFile] = process.argv.slice(2);
  if (!dir || !tag) {
    console.error("usage: release-manifest.mjs <assets-dir> <tag> [notes-file]");
    process.exit(2);
  }
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  try {
    const manifest = await buildManifest({ dir, tag, pubkey: configuredPubkey(root), notes: notesFile ? readFileSync(notesFile, "utf8") : "" });
    writeFileSync(path.join(dir, "latest.json"), JSON.stringify(manifest, null, 2) + "\n");
    console.log(`latest.json: ${manifest.version}, ${Object.keys(manifest.platforms).length} targets, all signatures verified`);
  } catch (e) {
    console.error(`::error::${e.message}`);
    process.exit(1);
  }
}

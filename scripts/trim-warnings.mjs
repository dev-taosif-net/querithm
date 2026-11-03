#!/usr/bin/env node
// L23: the Host keeps SuppressTrimAnalysisWarnings=true (sidecar/Querithm.Host/Querithm.Host.csproj) because SMO,
// ScriptDom, SqlClient and StreamJsonRpc produce trim warnings Querithm cannot fix and roots instead (TrimMode=partial).
// That also hides any new one. This publishes the trimmed sidecar once with the warnings shown, counts the distinct
// IL2xxx / IL3xxx warnings, and fails when there are more than the committed baseline (scripts/trim-warnings-baseline.json),
// listing the ones not in it. Fewer passes and says to lower the baseline.
//
//   node scripts/trim-warnings.mjs [--rid linux-x64] [--list] [--update]   # --update rewrites the baseline from this run
//
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const BASELINE_FILE = path.join(root, "scripts", "trim-warnings-baseline.json");

/**
 * The trim and single-file warnings of a publish log, normalised so runs on different machines and after unrelated
 * edits compare: the repository root, the NuGet cache folder, the "(line,col)" position and the "[project]" suffix
 * removed, "/" separators, sorted. A warning MSBuild repeats (its summary) counts once; two at different places in one
 * file count twice and read the same.
 */
export function parseTrimWarnings(log, repoRoot = root) {
  const seen = new Map();
  const repo = repoRoot.replaceAll("\\", "/") + "/";
  for (const raw of log.split(/\r?\n/)) {
    if (!/\bwarning IL[23]\d{3}\b/.test(raw)) continue;
    const line = raw.trim().replace(/\s+\[[^\]]*\.csproj(::[^\]]*)?\]$/, "").replaceAll("\\", "/").replaceAll(repo, "");
    if (!seen.has(line)) seen.set(line, normalise(line));
  }
  return [...seen.values()].sort();
}

/** A warning line without where it was found: the package cache's location and the source position. */
export function normalise(line) {
  return line.replace(/^\S*\/\.nuget\/packages\//, "<nuget>/").replace(/\(\d+,\d+\):/, ":");
}

/** Compares a run with the baseline: `ok` unless the count grew; `added` = warnings beyond the baseline's (as a multiset). */
export function compareWarnings(found, baseline) {
  const left = new Map();
  for (const w of baseline.warnings ?? []) left.set(w, (left.get(w) ?? 0) + 1);
  const added = [];
  for (const w of found) {
    const n = left.get(w) ?? 0;
    if (n > 0) left.set(w, n - 1);
    else added.push(w);
  }
  return { ok: found.length <= baseline.count, count: found.length, baseline: baseline.count, added };
}

function hostRid() {
  const arch = os.arch() === "arm64" ? "arm64" : "x64";
  return process.platform === "win32" ? `win-${arch}` : process.platform === "darwin" ? `osx-${arch}` : `linux-${arch}`;
}

function main(argv) {
  const rid = argv.includes("--rid") ? argv[argv.indexOf("--rid") + 1] : hostRid();
  const out = mkdtempSync(path.join(os.tmpdir(), "querithm-trim-"));
  try {
    // The release publish of scripts/build-sidecar.sh (dotnet publish = restore + Publish), with the trim analysis shown.
    // Rebuild first: an up-to-date compile or link prints no warnings at all, so every run compiles and links afresh and
    // counts the same ones.
    const r = spawnSync("dotnet", [
      "msbuild", path.join(root, "sidecar", "Querithm.Host", "Querithm.Host.csproj"), "-restore", "-t:Rebuild;Publish", "-p:Configuration=Release",
      `-p:RuntimeIdentifier=${rid}`, "-p:SelfContained=true", "-p:_IsPublishing=true", "-p:PublishSingleFile=true", "-p:PublishTrimmed=true",
      "-p:SuppressTrimAnalysisWarnings=false", `-p:PublishDir=${out}${path.sep}`, "-nologo", "-v:q",
    ], { cwd: root, encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
    const log = `${r.stdout ?? ""}\n${r.stderr ?? ""}`;
    if (r.status !== 0) {
      console.error(log);
      console.error(`dotnet publish failed (${r.status ?? r.error?.message})`);
      return 2;
    }
    const found = parseTrimWarnings(log);
    if (argv.includes("--list")) for (const w of found) console.log(w);
    if (argv.includes("--update")) {
      writeFileSync(BASELINE_FILE, JSON.stringify({ rid, count: found.length, warnings: found }, null, 2) + "\n");
      console.log(`trim warnings: ${found.length}; baseline written to ${path.relative(root, BASELINE_FILE)}`);
      return 0;
    }
    const baseline = JSON.parse(readFileSync(BASELINE_FILE, "utf8"));
    const c = compareWarnings(found, baseline);
    console.log(`trim warnings (${rid}): ${c.count}, baseline ${c.baseline}`);
    if (!c.ok) {
      console.error(`::error::${c.count - c.baseline} more trim warning(s) than the baseline. Fix them (or add a root, §3), or — if one is unavoidable — run node scripts/trim-warnings.mjs --update and say why in the change.`);
      for (const w of c.added) console.error(`  new: ${w}`);
      return 1;
    }
    if (c.count < c.baseline) console.log(`fewer than the baseline: run node scripts/trim-warnings.mjs --update to lower it to ${c.count}.`);
    return 0;
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isMain) process.exit(main(process.argv.slice(2)));

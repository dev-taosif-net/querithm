#!/usr/bin/env node
// Runs scripts/<name>.ps1 on Windows and scripts/<name>.sh elsewhere, forwarding arguments (pnpm entry point, §3).
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const [name, ...args] = process.argv.slice(2);
const dir = path.dirname(fileURLToPath(import.meta.url));
const r = process.platform === "win32"
  ? spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(dir, `${name}.ps1`), ...args], { stdio: "inherit" })
  : spawnSync("bash", [path.join(dir, `${name}.sh`), ...args], { stdio: "inherit" });
process.exit(r.status ?? 1);

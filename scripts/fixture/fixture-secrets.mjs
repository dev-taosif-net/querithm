// L33: the dev fixture's passwords live only in scripts/bench/docker (compose.yml, ssh/). The bench, smoke and loader
// scripts read them from the compose file instead of repeating them; never print them.
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const COMPOSE_FILE = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "bench", "docker", "compose.yml");

const indentOf = (line) => line.length - line.trimStart().length;
const meaningful = (line) => line.trim() !== "" && !line.trimStart().startsWith("#");

/**
 * A value in one service's block of the compose file (`environment` or `build.args`): a reader for this file's own plain
 * layout (block mappings, `KEY: value`, optional quotes), not a YAML parser. Throws when the service or key is missing.
 */
export function composeValue(service, key, text = readFileSync(COMPOSE_FILE, "utf8")) {
  const lines = text.split(/\r?\n/);
  const services = lines.findIndex((l) => /^services:\s*$/.test(l));
  if (services < 0) throw new Error("compose file: no services");
  const start = lines.findIndex((l, i) => i > services && indentOf(l) === 2 && l.trim() === `${service}:`);
  if (start < 0) throw new Error(`compose file: no service ${service}`);
  for (let i = start + 1; i < lines.length && (!meaningful(lines[i]) || indentOf(lines[i]) > 2); i++) {
    const m = /^\s+([A-Za-z0-9_]+):\s*(.*?)\s*$/.exec(lines[i]);
    if (!m || m[1] !== key) continue;
    let v = m[2];
    const q = v[0];
    if ((q === '"' || q === "'") && v.length >= 2 && v.endsWith(q)) v = v.slice(1, -1);
    else v = v.replace(/\s+#.*$/, "");
    if (v === "") break;
    return v;
  }
  throw new Error(`compose file: no ${key} for ${service}`);
}

/** The fixture's sign-in secret per engine, as the compose file sets it (the server engines all use their admin user). */
export function fixturePassword(engine, text) {
  switch (engine) {
    case "mssql": return composeValue("mssql", "MSSQL_SA_PASSWORD", text);
    case "pg": return composeValue("postgres", "POSTGRES_PASSWORD", text);
    case "mysql": return composeValue("mysql84", "MYSQL_ROOT_PASSWORD", text);
    case "mariadb": return composeValue("mariadb118", "MARIADB_ROOT_PASSWORD", text);
    case "ssh": return composeValue("ssh", "SSH_PASSWORD", text);
    default: throw new Error(`no fixture password for ${engine}`);
  }
}

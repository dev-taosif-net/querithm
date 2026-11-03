// Loads the hand-written PostgreSQL test database (pg-testdb.sql) into the docker fixture as `querithm_test`.
// The database is dropped and recreated each run, so it is always in the seeded state.
//
//   docker compose -p querithm-fixture -f scripts/bench/docker/compose.yml up -d postgres
//   node scripts/fixture/load-testdb.mjs [database]
//
// Connect from Querithm: localhost:54329, user postgres, the password in scripts/bench/docker/compose.yml, database
// querithm_test.
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { waitForFixture } from "./wait-ready.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
export const DEFAULT_DB = "querithm_test";
export const SCRIPT = path.join(here, "pg-testdb.sql");
const COMPOSE = ["compose", "-p", "querithm-fixture", "-f", path.join(here, "..", "bench", "docker", "compose.yml")];

/** Rejects anything that isn't a plain lower-case identifier, so the name can be inlined into DDL unquoted. */
export function checkDbName(name) {
  if (!/^[a-z_][a-z0-9_]{0,62}$/.test(name)) throw new Error(`invalid database name: ${name}`);
  return name;
}

/** The docker invocations, in order: recreate the database, then run the script inside it. */
export function loaderCommands(db = DEFAULT_DB) {
  checkDbName(db);
  const psql = (database, ...rest) => [...COMPOSE, "exec", "-T", "postgres", "psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-q", ...rest];
  return [
    { args: psql("postgres", "-c", `DROP DATABASE IF EXISTS ${db} WITH (FORCE)`) },
    { args: psql("postgres", "-c", `CREATE DATABASE ${db}`) },
    { args: psql(db), stdin: SCRIPT },
  ];
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isMain) {
  const db = process.argv[2] ?? DEFAULT_DB;
  waitForFixture(["postgres"]);
  for (const { args, stdin } of loaderCommands(db)) {
    const r = spawnSync("docker", args, { input: stdin ? readFileSync(stdin) : undefined, stdio: [stdin ? "pipe" : "ignore", "inherit", "inherit"] });
    if (r.status !== 0) process.exit(r.status ?? 1);
  }
  console.log(`${db} ready on localhost:54329 (user postgres, password as in scripts/bench/docker/compose.yml)`);
}

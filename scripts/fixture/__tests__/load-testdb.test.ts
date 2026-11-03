import { existsSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import { checkDbName, DEFAULT_DB, loaderCommands, SCRIPT } from "../load-testdb.mjs";

describe("test database loader", () => {
  it("drops, recreates, then loads the script into the target database", () => {
    const [drop, create, load] = loaderCommands();
    expect(drop.args.slice(-2)).toEqual(["-c", `DROP DATABASE IF EXISTS ${DEFAULT_DB} WITH (FORCE)`]);
    expect(create.args.slice(-2)).toEqual(["-c", `CREATE DATABASE ${DEFAULT_DB}`]);
    expect(load.stdin).toBe(SCRIPT);
    expect(load.args).toContain(DEFAULT_DB);
    for (const c of [drop, create, load]) {
      expect(c.args.slice(0, 3)).toEqual(["compose", "-p", "querithm-fixture"]);
      expect(c.args).toContain("ON_ERROR_STOP=1");
    }
  });

  it("targets a custom database name", () => {
    const [, create, load] = loaderCommands("qa_copy");
    expect(create.args.at(-1)).toBe("CREATE DATABASE qa_copy");
    expect(load.args[load.args.indexOf("-d") + 1]).toBe("qa_copy");
  });

  it("refuses names that would need quoting or could inject DDL", () => {
    for (const bad of ["", "Upper", "1abc", "x; DROP DATABASE postgres", "a-b", "a".repeat(64)])
      expect(() => checkDbName(bad)).toThrow(/invalid database name/);
    expect(checkDbName("querithm_test")).toBe("querithm_test");
  });

  it("has no shebang line, so vitest can import it from a Windows (CRLF) checkout", () => {
    const self = new URL("../load-testdb.mjs", import.meta.url);
    expect(readFileSync(self, "utf8").startsWith("#!")).toBe(false);
  });

  it("ships the script it loads, stopping on the first error", () => {
    expect(existsSync(SCRIPT)).toBe(true);
    expect(readFileSync(SCRIPT, "utf8")).toContain("\\set ON_ERROR_STOP on");
  });
});

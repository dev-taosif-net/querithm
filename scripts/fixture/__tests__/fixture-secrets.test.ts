import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import { COMPOSE_FILE, composeValue, fixturePassword } from "../fixture-secrets.mjs";

const compose = `# comment
name: example
services:
  db:
    image: example
    environment:
      ACCEPT: "Y"
      DB_PASSWORD: "quoted value!"
  other:
    environment:
      DB_PASSWORD: plain-value  # a comment
  bastion:
    build:
      context: .
      args:
        # a comment inside the block
        SSH_PASSWORD: 'single'
    ports: ["127.0.0.1:1:2"]
`;

describe("fixture secrets (L33)", () => {
  it("reads a key from one service's block, unquoting it", () => {
    expect(composeValue("db", "DB_PASSWORD", compose)).toBe("quoted value!");
    expect(composeValue("other", "DB_PASSWORD", compose)).toBe("plain-value");
    expect(composeValue("bastion", "SSH_PASSWORD", compose)).toBe("single");
  });

  it("refuses a missing service or a key of another service, naming neither value", () => {
    expect(() => composeValue("missing", "DB_PASSWORD", compose)).toThrow(/no service missing/);
    expect(() => composeValue("other", "SSH_PASSWORD", compose)).toThrow(/no SSH_PASSWORD for other/);
    expect(() => fixturePassword("db2", compose)).toThrow(/no fixture password/);
  });

  it("finds every engine's password and the bastion's in the real compose file", () => {
    const text = readFileSync(COMPOSE_FILE, "utf8");
    for (const engine of ["mssql", "pg", "mysql", "mariadb", "ssh", "oracle"]) expect(fixturePassword(engine, text).length).toBeGreaterThanOrEqual(6);
  });
});

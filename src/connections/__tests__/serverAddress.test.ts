import { describe, expect, it } from "vitest";
import { serverAddress, serverAddressLine } from "../serverAddress";

describe("serverAddress", () => {
  it.each([
    ["mssql", 1433, 1444],
    ["pg", 5432, 5433],
    ["mySql", 3306, 3307],
  ] as const)("%s: shows the host as typed, the port only when it is not the default", (engine, dflt, other) => {
    expect(serverAddress({ engine, host: "10.0.4.12", port: null })).toBe("10.0.4.12");
    expect(serverAddress({ engine, host: "10.0.4.12", port: dflt })).toBe("10.0.4.12");
    expect(serverAddress({ engine, host: "db01.local", port: other })).toBe(`db01.local:${other}`);
    expect(serverAddress({ engine, location: "server", host: " sql-prd-02 ", port: null })).toBe("sql-prd-02");
  });

  it.each(["sqlite", "duckDb"] as const)("%s: a file or in-memory database has no server address", (engine) => {
    expect(serverAddress({ engine, location: "file", host: null, port: null })).toBeUndefined();
    expect(serverAddress({ engine, location: "memory", host: null, port: null })).toBeUndefined();
    // A stale host left on a local profile is not shown: the database is on this computer.
    expect(serverAddress({ engine, location: "file", host: "localhost", port: null })).toBeUndefined();
  });

  it("a server profile with no host, or no profile, shows nothing", () => {
    expect(serverAddress(undefined)).toBeUndefined();
    expect(serverAddress({ engine: "pg", host: null, port: 5433 })).toBeUndefined();
    expect(serverAddress({ engine: "pg", host: "  ", port: null })).toBeUndefined();
  });

  it("an IPv6 literal takes brackets before a port", () => {
    expect(serverAddress({ engine: "pg", host: "fd00::5", port: 5433 })).toBe("[fd00::5]:5433");
    expect(serverAddress({ engine: "pg", host: "fd00::5", port: null })).toBe("fd00::5");
    expect(serverAddress({ engine: "pg", host: "[fd00::5]", port: 5433 })).toBe("[fd00::5]:5433");
  });
});

describe("serverAddressLine", () => {
  it("names the SSH bastion the connection goes through (the host is as the bastion sees it)", () => {
    expect(serverAddressLine({ engine: "mssql", host: "10.0.4.12", port: 1444 })).toBe("Server 10.0.4.12:1444");
    expect(serverAddressLine({ engine: "pg", host: "10.0.4.12", port: null, ssh: { host: "jump-01" } as any })).toBe("Server 10.0.4.12 via SSH jump-01");
    expect(serverAddressLine({ engine: "sqlite", location: "file", host: null, port: null })).toBeUndefined();
  });
});

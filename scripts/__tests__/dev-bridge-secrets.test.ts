import { describe, expect, it } from "vitest";
import { AuthKindValues } from "../../src/ipc/contract";
import { needsSecretFor } from "../../src/connections/auth";
// @ts-expect-error plain ES module without types
import { wantedSecrets, sameEndpoint, createProfileLocks, validCredentialAccount } from "../dev-bridge-secrets.mjs";

describe("dev bridge secret injection (Phase D0a, mirrors the shell)", () => {
  it("looks up a database secret exactly for the sign-ins that need one", () => {
    for (const auth of AuthKindValues) expect(wantedSecrets({ auth })[0], auth).toBe(needsSecretFor(auth));
    expect(wantedSecrets({})).toEqual([true, false]);
  });

  it("looks up the SSH secret only for a tunnelled profile, and everything for an unknown one", () => {
    expect(wantedSecrets({ auth: "none", ssh: { host: "b" } })).toEqual([false, true]);
    expect(wantedSecrets({ auth: "windows", ssh: null })).toEqual([false, false]);
    expect(wantedSecrets(undefined)).toEqual([true, true]);
  });
});


describe("credential endpoint guards", () => {
  const p = { id: "p", engine: "pg", host: "db.invalid", auth: "password", tlsMode: "verifyFull", username: "u" };
  it.each([
    { host: "attacker.invalid" }, { port: 5433 }, { username: "other" }, { tlsMode: "disable" },
    { trustCert: true }, { caPath: "/tmp/attacker.pem" }, { options: { hostNameInCertificate: "attacker.invalid" } },
    { ssh: { host: "attacker.invalid", username: "j", auth: "password" } },
  ])("binds credentials to changed connection settings %j", (patch) => {
    expect(sameEndpoint(p, { ...p, ...patch })).toBe(false);
  });
  it("keeps cosmetic edits and equivalent enum spellings on the same endpoint", () => {
    expect(sameEndpoint(p, { ...p, name: "renamed", environment: "prod", passwordRetention: "once", host: "DB.INVALID", auth: "Password" })).toBe(true);
  });
  it("serializes a profile's save and injection without holding other profiles", async () => {
    const lock = createProfileLocks();
    const held = await lock("profile.save", { id: "p" });
    let entered = false;
    const waiting = lock("session.open", { profileId: "p" }).then((unlock) => { entered = true; return unlock; });
    await Promise.resolve();
    expect(entered).toBe(false);
    const other = await lock("profile.test", { profileId: "other" });
    other();
    held();
    (await waiting)();
    expect(entered).toBe(true);
  });
});


it.each(["victim:timed", "victim:ssh:timed", "victim:ssh:ssh", "../victim", "victim\n", "victim\0x", ""])("rejects reserved or malformed credential account %j", (account) => {
  expect(validCredentialAccount(account)).toBe(false);
});


it.each(["/tmp/ProdSocket", "@ProdSocket", "localhost,/tmp/ProdSocket"])("binds credentials to the case-sensitive socket path %s", (host) => {
  const p = { engine: "pg", host, auth: "password" };
  expect(sameEndpoint(p, { ...p, host: host.toLowerCase() })).toBe(false);
});

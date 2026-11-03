import { describe, expect, it } from "vitest";
import { AuthKindValues } from "../../src/ipc/contract";
import { needsSecretFor } from "../../src/connections/auth";
// @ts-expect-error plain ES module without types
import { wantedSecrets } from "../dev-bridge-secrets.mjs";

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

import { describe, expect, it } from "vitest";
import { AuthKindValues, type AuthKind } from "../../ipc/contract";
import { isLocal, locationOf, needsSecretFor } from "../auth";

describe("profile sign-in rules (Phase D0a)", () => {
  it("decides every auth kind the contract has, matching the sidecar's ProfileRules.NeedsSecret", () => {
    const needing = AuthKindValues.filter((a) => needsSecretFor(a));
    expect(needing.sort()).toEqual(["entraToken", "password", "sql"]);
    expect(needsSecretFor("none")).toBe(false);
  });

  it("treats auth text from a newer build as needing no secret instead of throwing", () => {
    expect(needsSecretFor("passkey" as AuthKind)).toBe(false);
  });

  it("reads a missing location as a server and file/memory as local", () => {
    expect(locationOf({})).toBe("server");
    expect(locationOf({ location: null })).toBe("server");
    expect(isLocal({ location: "server" })).toBe(false);
    expect(isLocal({ location: "file" })).toBe(true);
    expect(isLocal({ location: "memory" })).toBe(true);
  });
});

describe("duplicating a profile", () => {
  it("gives a new id and name and never keeps the file grant (the shell binds one grant to one profile)", async () => {
    const { duplicateProfile } = await import("../auth");
    const copy = duplicateProfile({
      id: "p1", name: "Sales file", engine: "sqlite", location: "file", file: { grantId: "g1", displayName: "sales.db" }, auth: "none",
      environment: "prod", tlsMode: "default", trustCert: false, createdAt: 5, updatedAt: 6, groupName: "Team",
    });
    expect(copy).toMatchObject({ id: "", name: "Sales file (copy)", file: null, location: "file", engine: "sqlite", environment: "prod", groupName: "Team", createdAt: 0, updatedAt: 0 });
  });
});

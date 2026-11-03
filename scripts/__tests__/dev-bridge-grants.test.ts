import { mkdtempSync, writeFileSync, renameSync, rmSync, symlinkSync, statSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
// @ts-expect-error plain ES module without types
import { createGrants, identityOf } from "../dev-bridge-grants.mjs";

const dirs: string[] = [];
function db(name = "sales.db") {
  const dir = mkdtempSync(path.join(os.tmpdir(), "qbridge-grants-"));
  dirs.push(dir);
  const p = path.join(dir, name);
  writeFileSync(p, "SQLite format 3\0");
  return p;
}
afterEach(() => {
  for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
});

const fileProfile = (id: string, grantId: string) => ({ id, auth: "none", location: "file", file: { grantId } });

describe("dev bridge file grants (Phase D0c, mirrors the shell)", () => {
  it("computes the identity as the shell and sidecar do", () => {
    const p = db();
    const s = statSync(p, { bigint: true });
    expect(identityOf(p)).toBe(`u:${s.dev}:${s.ino}`);
  });

  it("injects a file profile's grant read-only and drops a forged path", () => {
    const g = createGrants();
    const id = g.register(db());
    g.remember(fileProfile("p1", id));
    const params: any = { profileId: "p1", fileGrant: { path: "/etc/shadow", mode: "readWrite" } };
    expect(g.inject("session.open", params)).toBeNull();
    expect(params.fileGrant.grantId).toBe(id);
    expect(params.fileGrant.mode).toBe("readOnly");
    expect(params.fileGrant.path).not.toBe("/etc/shadow");
  });

  it("session.open takes the grant from the stored profile only, never from a profile the webview adds (M1)", () => {
    const g = createGrants();
    const id = g.register(db());
    g.remember({ id: "srv", auth: "sql" });
    const params: any = { profileId: "srv", profile: fileProfile("srv", id) };
    expect(g.inject("session.open", params)).toBeNull();
    expect(params.fileGrant).toBeUndefined();
    const test: any = { profile: fileProfile("", id) };
    expect(g.inject("profile.test", test)).toBeNull();
    expect(test.fileGrant.grantId).toBe(id);
  });

  it("drops a fileGrant from every other method", () => {
    const g = createGrants();
    const params: any = { sessionId: "s", fileGrant: { path: "/x" } };
    expect(g.inject("query.execute", params)).toBeNull();
    expect(params.fileGrant).toBeUndefined();
  });

  it("binds a grant to the profile that saved it and refuses it to any other", () => {
    const g = createGrants();
    const id = g.register(db());
    expect(g.inject("profile.save", { ...fileProfile("", id) })).toBeNull();
    g.settle("profile.save", {}, fileProfile("p1", id));
    expect(g.inject("profile.save", { ...fileProfile("p2", id) })).toMatchObject({ data: { code: "fileGrantBound" } });
    g.remember(fileProfile("p2", id));
    expect(g.inject("session.open", { profileId: "p2" })).toMatchObject({ data: { code: "fileGrantBound" } });
  });

  it("refuses a replaced, missing or symlinked file with the re-pick codes", () => {
    const g = createGrants();
    const p = db();
    const id = g.register(p);
    const other = path.join(path.dirname(p), "other.db");
    writeFileSync(other, "x");
    renameSync(other, p);
    expect(g.inject("profile.test", { profile: fileProfile("", id) })).toMatchObject({ data: { code: "fileReplaced" } });
    rmSync(p);
    expect(g.inject("profile.test", { profile: fileProfile("", id) })).toMatchObject({ data: { code: "fileMissing" } });
    if (process.platform !== "win32") {
      const g2 = createGrants();
      const q = db("q.db");
      const id2 = g2.register(q);
      const moved = path.join(path.dirname(q), "moved.db");
      renameSync(q, moved);
      symlinkSync(moved, q);
      expect(g2.inject("profile.test", { profile: fileProfile("", id2) })).toMatchObject({ data: { code: "fileMoved" } });
    }
  });

  it("a forged grant id and a revoked or deleted profile's grant are refused", () => {
    const g = createGrants();
    expect(g.inject("profile.test", { profile: fileProfile("", "forged") })).toMatchObject({ data: { code: "fileGrantUnknown" } });
    const id = g.register(db());
    g.settle("profile.save", {}, fileProfile("p1", id));
    g.remember(fileProfile("p1", id));
    g.settle("grant.revoke", { profileId: "p1" }, {});
    expect(g.inject("session.open", { profileId: "p1" })).toMatchObject({ data: { code: "fileGrantUnknown" } });
  });

  it("writes only after the shell-side consent call", () => {
    const g = createGrants();
    const id = g.register(db());
    g.allowWrites(id);
    const params: any = { profile: fileProfile("", id) };
    g.inject("profile.test", params);
    expect(params.fileGrant.mode).toBe("readWrite");
    g.readOnly(id);
    const again: any = { profile: fileProfile("", id) };
    g.inject("profile.test", again);
    expect(again.fileGrant.mode).toBe("readOnly");
  });
});

import { describe, expect, it } from "vitest";
import { createFileTokens, refuseShellKeys } from "../dev-bridge-files.mjs";

describe("dev bridge picked-file tokens (mirror of src-tauri/src/files.rs)", () => {
  it("injects the path of a live token and strips forged paths", () => {
    const f = createFileTokens();
    const token = f.register("/data/orders.csv");
    const p: Record<string, unknown> = { fileToken: token, filePath: "/etc/shadow" };
    expect(f.inject("import.inspect", p)).toBeNull();
    expect(p.filePath).toBe("/data/orders.csv");
  });

  it("refuses an import without a token instead of passing it on (M1)", () => {
    const f = createFileTokens();
    for (const method of ["import.inspect", "import.run"]) {
      for (const p of [{ filePath: "/etc/shadow" }, {}, { fileToken: null }, { fileToken: 7 }, null]) {
        expect(f.inject(method, p as any), `${method} ${JSON.stringify(p)}`).toMatchObject({ data: { code: "fileToken" } });
      }
    }
    expect(f.inject("import.checkTarget", { sessionId: "s" })).toBeNull();
  });

  it("refuses unknown tokens and a token whose import already ran", () => {
    const f = createFileTokens();
    expect(f.inject("import.inspect", { fileToken: "deadbeef" })).toMatchObject({ data: { code: "fileToken" } });
    const token = f.register("/data/a.csv");
    expect(f.inject("import.inspect", { fileToken: token })).toBeNull();
    expect(f.inject("import.run", { fileToken: token })).toBeNull();
    f.settleRun(token, true);
    expect(f.inject("import.run", { fileToken: token })).toMatchObject({ data: { code: "fileToken" } });
    expect(f.inject("import.inspect", { fileToken: token })).not.toBeNull();
  });

  it("keeps the token for the confirmed retry after a Production guard block, then spends it", () => {
    const f = createFileTokens();
    const token = f.register("/data/a.csv");
    expect(f.inject("import.run", { fileToken: token, confirmedProd: false })).toBeNull();
    f.settleRun(token, false); // reply was { blocked: prodWrite }, no jobId
    const retry: Record<string, unknown> = { fileToken: token, confirmedProd: true };
    expect(f.inject("import.run", retry)).toBeNull();
    expect(retry.filePath).toBe("/data/a.csv");
    f.settleRun(token, true); // the import started
    expect(f.inject("import.run", { fileToken: token })).toMatchObject({ data: { code: "fileToken" } });
  });

  it("refuses a second import.run while one is in flight", () => {
    const f = createFileTokens();
    const token = f.register("/data/a.csv");
    expect(f.inject("import.run", { fileToken: token })).toBeNull();
    expect(f.inject("import.run", { fileToken: token })).toMatchObject({ data: { code: "fileToken" } });
    f.settleRun(token, false);
    expect(f.inject("import.run", { fileToken: token })).toBeNull();
  });

  it("leaves other methods alone", () => {
    const f = createFileTokens();
    const p: Record<string, unknown> = { filePath: "/x", fileToken: "t" };
    expect(f.inject("query.execute", p)).toBeNull();
    expect(p.filePath).toBe("/x");
  });
  it("injects paths into the sides of a schema compare; the token stays usable", () => {
    const f = createFileTokens();
    const token = f.register("/data/sales.qschema.json");
    const p: Record<string, any> = { source: { fileToken: token, filePath: "/etc/shadow" }, target: { sessionId: "s", filePath: "/etc/passwd" } };
    expect(f.inject("schemaCompare.run", p)).toBeNull();
    expect(p.source.filePath).toBe("/data/sales.qschema.json");
    expect(p.target.filePath).toBeUndefined();
    expect(f.inject("schemaCompare.run", { source: { fileToken: token } })).toBeNull();
    expect(f.inject("schemaCompare.run", { target: { fileToken: "nope" } })).toMatchObject({ data: { code: "fileToken" } });
    expect(f.inject("schemaCompare.run", { source: { fileToken: 7 } })).toMatchObject({ data: { code: "fileToken" } });
    expect(f.inject("schemaCompare.run", { source: { sessionId: "a", fileToken: null }, target: { sessionId: "b" } })).toBeNull();
  });
});

describe("dev bridge refuses shell-owned names (mirror of injection.rs refuse_shell_keys, M1)", () => {
  const refused = (p: unknown) => refuseShellKeys(p)?.data?.code === "shellOwned";
  it("refuses any other spelling of a shell-owned or shared name, at the top, in a side and in the profile", () => {
    for (const key of ["FilePath", "FILEPATH", "filepath", "FileGrant", "FileToken", "Secret", "SSHSECRET", "f\u0131leToken", "\u017Fecret"]) {
      const inner = { [key]: "/etc/shadow" };
      expect(refused({ ...inner }), key).toBe(true);
      for (const side of ["source", "target", "profile"]) expect(refused({ [side]: { ...inner } }), `${side}.${key}`).toBe(true);
    }
  });
  it("refuses a file path or grant the webview names; drops their nulls; leaves its own keys and deeper data", () => {
    expect(refused({ fileToken: "t", filePath: "/etc/shadow" })).toBe(true);
    expect(refused({ profileId: "p", fileGrant: { path: "/etc/shadow" } })).toBe(true);
    expect(refused({ source: { filePath: "/x" } })).toBe(true);
    const ok: any = { fileToken: "t", filePath: null, secret: "pw", fileGrant: null, profile: { options: { FilePath: "x" } } };
    expect(refuseShellKeys(ok)).toBeNull();
    expect(ok).toEqual({ fileToken: "t", secret: "pw", profile: { options: { FilePath: "x" } } });
    expect(refuseShellKeys(null)).toBeNull();
  });
});

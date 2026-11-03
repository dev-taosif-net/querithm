import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import { BASELINE_FILE, compareWarnings, parseTrimWarnings } from "../trim-warnings.mjs";

const repo = "/work/querithm";
const log = [
  "  Querithm.Core -> /work/querithm/sidecar/Querithm.Core/bin/Release/net10.0/Querithm.Core.dll",
  "/work/querithm/sidecar/Querithm.Host/Program.cs(43,16): warning IL2026: Using member 'Serialize' which has 'RequiresUnreferencedCodeAttribute' [/work/querithm/sidecar/Querithm.Host/Querithm.Host.csproj]",
  "/work/querithm/sidecar/Querithm.Host/Program.cs(110,19): warning IL2026: Using member 'Serialize' which has 'RequiresUnreferencedCodeAttribute' [/work/querithm/sidecar/Querithm.Host/Querithm.Host.csproj]",
  "/home/runner/.nuget/packages/azure.core/1.50.0/lib/net8.0/Azure.Core.dll : warning IL2104: Assembly 'Azure.Core' produced trim warnings. [/work/querithm/sidecar/Querithm.Host/Querithm.Host.csproj::TargetFramework=net10.0]",
  "ILLink : Trim analysis warning IL2026: StreamJsonRpc.X: Using member 'Y' which has 'RequiresUnreferencedCodeAttribute'",
  "/work/querithm/sidecar/Querithm.Host/Program.cs(43,16): warning CS8618: not a trim warning",
  // MSBuild's summary repeats each warning.
  "/work/querithm/sidecar/Querithm.Host/Program.cs(43,16): warning IL2026: Using member 'Serialize' which has 'RequiresUnreferencedCodeAttribute' [/work/querithm/sidecar/Querithm.Host/Querithm.Host.csproj]",
].join("\n");

describe("trim warning check (L23)", () => {
  it("counts each trim or single-file warning once, wherever the machine keeps the repo and the package cache", () => {
    const found = parseTrimWarnings(log, repo);
    expect(found).toEqual([
      "<nuget>/azure.core/1.50.0/lib/net8.0/Azure.Core.dll : warning IL2104: Assembly 'Azure.Core' produced trim warnings.",
      "ILLink : Trim analysis warning IL2026: StreamJsonRpc.X: Using member 'Y' which has 'RequiresUnreferencedCodeAttribute'",
      "sidecar/Querithm.Host/Program.cs: warning IL2026: Using member 'Serialize' which has 'RequiresUnreferencedCodeAttribute'",
      "sidecar/Querithm.Host/Program.cs: warning IL2026: Using member 'Serialize' which has 'RequiresUnreferencedCodeAttribute'",
    ]);
    // A Windows checkout reads the same.
    const windows = "C:\\a\\querithm\\sidecar\\Querithm.Core\\X.cs(1,2): warning IL3000: 'Assembly.Location' always returns an empty string";
    expect(parseTrimWarnings(windows, "C:\\a\\querithm")).toEqual(["sidecar/Querithm.Core/X.cs: warning IL3000: 'Assembly.Location' always returns an empty string"]);
  });

  it("fails only when the count grows, and names the warnings beyond the baseline", () => {
    const base = parseTrimWarnings(log, repo);
    expect(compareWarnings(base, { count: base.length, warnings: base })).toMatchObject({ ok: true, added: [] });
    // Moved lines read the same: no false "new" warnings.
    const moved = parseTrimWarnings(log.replaceAll("(110,19)", "(120,3)"), repo);
    expect(compareWarnings(moved, { count: base.length, warnings: base }).added).toEqual([]);
    const more = [...base, "sidecar/Querithm.Core/New.cs: warning IL2075: 'this' argument does not satisfy"];
    const c = compareWarnings(more, { count: base.length, warnings: base });
    expect(c).toMatchObject({ ok: false, count: base.length + 1, baseline: base.length });
    expect(c.added).toEqual(["sidecar/Querithm.Core/New.cs: warning IL2075: 'this' argument does not satisfy"]);
    expect(compareWarnings(base.slice(1), { count: base.length, warnings: base }).ok).toBe(true);
  });

  it("has a committed baseline whose count matches its list and holds no machine path", () => {
    const text = readFileSync(BASELINE_FILE, "utf8");
    const b = JSON.parse(text);
    expect(b.count).toBe(b.warnings.length);
    expect(b.count).toBeGreaterThan(0);
    expect(text).not.toMatch(/\/home\/|\/Users\/|[A-Z]:\\\\/);
  });
});

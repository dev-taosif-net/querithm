import path from "node:path";
import { fileURLToPath } from "node:url";
import { beforeEach, describe, expect, it, vi } from "vitest";

// gen-notices.mjs is a top-level script: each run is a fresh import against a virtual file system and a fake
// `cargo` / `realpath`, so the real THIRD-PARTY-NOTICES.md is never touched.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const J = (...p: string[]) => path.join(root, ...p);

const fsState = vi.hoisted(() => ({
  files: new Map<string, string>(),
  dirs: new Map<string, string[]>(),
  written: new Map<string, string>(),
  cargo: undefined as undefined | (() => string),
  realpathFails: false,
  realpathCalls: [] as string[],
}));

vi.mock("node:fs", () => {
  const api = {
    existsSync: (p: string) => fsState.files.has(p) || fsState.dirs.has(p),
    readFileSync: (p: string) => {
      const v = fsState.files.get(p);
      if (v == null) throw new Error("ENOENT " + p);
      return v;
    },
    readdirSync: (p: string) => fsState.dirs.get(p) ?? [],
    writeFileSync: (p: string, c: string) => void fsState.written.set(p, c),
  };
  return { ...api, default: api };
});
vi.mock("node:child_process", () => {
  const execFileSync = (cmd: string, args: string[]) => {
    if (cmd === "realpath") {
      fsState.realpathCalls.push(args[0]);
      if (fsState.realpathFails) throw new Error("no realpath");
      return Buffer.from(args[0] + "\n");
    }
    if (cmd === "cargo") {
      if (!fsState.cargo) throw new Error("cargo not found\nsecond line");
      return Buffer.from(fsState.cargo());
    }
    throw new Error("unexpected " + cmd);
  };
  return { execFileSync, default: { execFileSync } };
});

const pkg = (o: Record<string, unknown>) => JSON.stringify(o);

async function run(env: Record<string, string | undefined> = {}) {
  vi.resetModules();
  const saved = { ...process.env };
  Object.assign(process.env, { NUGET_PACKAGES: "/nuget", ...env });
  const log = vi.spyOn(console, "log").mockImplementation(() => {});
  try {
    await import("../gen-notices.mjs");
  } finally {
    process.env = saved;
    log.mockRestore();
  }
  return fsState.written.get(J("THIRD-PARTY-NOTICES.md"))!;
}

beforeEach(() => {
  fsState.files.clear(); fsState.dirs.clear(); fsState.written.clear(); fsState.cargo = undefined; fsState.realpathFails = false; fsState.realpathCalls = [];
  fsState.files.set(J("package.json"), pkg({ name: "querithm", dependencies: { alpha: "1", missing: "1" } }));
});

describe("npm section", () => {
  it("lists production dependencies transitively with their licenses, once each, sorted by name", async () => {
    fsState.files.set(J("node_modules/alpha/package.json"), pkg({ name: "alpha", version: "1.0.0", license: "MIT", dependencies: { beta: "1", gamma: "1", delta: "1", eps: "1" } }));
    fsState.files.set(J("node_modules/beta/package.json"), pkg({ name: "beta", version: "2.0.0", license: { type: "ISC" }, dependencies: { alpha: "1" } }));
    fsState.files.set(J("node_modules/gamma/package.json"), pkg({ name: "gamma", version: "3.0.0", licenses: [{ type: "MIT" }, { type: "Apache-2.0" }] }));
    fsState.files.set(J("node_modules/delta/package.json"), pkg({ name: "delta", version: "4.0.0" }));
    // found through the sibling lookup (hoisted next to the parent), not under the parent's node_modules
    fsState.files.set(J("node_modules/eps/package.json"), pkg({ name: "eps", version: "5.0.0", license: "0BSD" }));
    const out = await run();
    expect(out).toContain("## Webview (npm)");
    const rows = out.split("\n").filter((l) => /^\| (alpha|beta|gamma|delta|eps) /.test(l));
    expect(rows).toEqual([
      "| alpha | 1.0.0 | MIT |", "| beta | 2.0.0 | ISC |", "| delta | 4.0.0 | UNKNOWN |", "| eps | 5.0.0 | 0BSD |", "| gamma | 3.0.0 | MIT OR Apache-2.0 |",
    ]);
    expect(out).toContain("Querithm itself is MIT licensed.");
  });

  it("keeps the path itself when realpath is unavailable", async () => {
    fsState.realpathFails = true;
    fsState.files.set(J("node_modules/alpha/package.json"), pkg({ name: "alpha", version: "1.0.0", license: "MIT" }));
    const out = await run();
    expect(out).toContain("| alpha | 1.0.0 | MIT |");
  });

  it("works without a dependencies section at all", async () => {
    fsState.files.set(J("package.json"), pkg({ name: "querithm" }));
    const out = await run();
    expect(out).toContain("| Package | Version | License |");
  });
});

describe("NuGet section", () => {
  const assets = J("sidecar", "Querithm.Host", "obj", "project.assets.json");

  it("asks to restore when the sidecar has not been restored", async () => {
    expect(await run()).toContain("(run `dotnet restore sidecar` first)");
  });

  it("reads each package's license expression, file or URL, skipping projects, and includes SMO's text verbatim", async () => {
    fsState.files.set(assets, JSON.stringify({ libraries: {
      "Foo/1.0.0": { type: "package" }, "Bar/2.0.0": { type: "package" }, "Baz/3.0.0": { type: "package" }, "Plain/1.0.0": { type: "package" },
      "Ghost/9.9.9": { type: "package" }, "Querithm.Contract/1.0.0": { type: "project" },
      "Microsoft.SqlServer.SqlManagementObjects/170.0.0": { type: "package" },
    } }));
    const d = (n: string, v: string) => path.join("/nuget", n.toLowerCase(), v);
    fsState.dirs.set(d("Foo", "1.0.0"), ["foo.nuspec"]);
    fsState.files.set(path.join(d("Foo", "1.0.0"), "foo.nuspec"), '<license type="expression">Apache-2.0</license>');
    fsState.dirs.set(d("Bar", "2.0.0"), ["bar.nuspec"]);
    fsState.files.set(path.join(d("Bar", "2.0.0"), "bar.nuspec"), '<license type="file">LICENSE.txt</license>');
    fsState.dirs.set(d("Baz", "3.0.0"), ["baz.nuspec"]);
    fsState.files.set(path.join(d("Baz", "3.0.0"), "baz.nuspec"), "<licenseUrl>https://example.com/l</licenseUrl>");
    fsState.dirs.set(d("Plain", "1.0.0"), ["plain.nuspec"]);
    fsState.files.set(path.join(d("Plain", "1.0.0"), "plain.nuspec"), "<id>Plain</id>");
    const smo = d("Microsoft.SqlServer.SqlManagementObjects", "170.0.0");
    fsState.dirs.set(smo, ["smo.nuspec", "LICENSE.txt"]);
    fsState.files.set(path.join(smo, "smo.nuspec"), '<license type="file">LICENSE.txt</license>');
    fsState.files.set(path.join(smo, "LICENSE.txt"), "  SMO LICENSE TEXT  ");
    const out = await run();
    expect(out).toContain("| Foo | 1.0.0 | Apache-2.0 |");
    expect(out).toContain("| Bar | 2.0.0 | file: LICENSE.txt |");
    expect(out).toContain("| Baz | 3.0.0 | https://example.com/l |");
    expect(out).toContain("| Plain | 1.0.0 | see package |");
    expect(out).toContain("| Ghost | 9.9.9 | see package |");
    expect(out).not.toContain("Querithm.Contract");
    expect(out).toContain("## License texts included verbatim");
    expect(out).toContain("### Microsoft.SqlServer.SqlManagementObjects 170.0.0");
    expect(out).toContain("```\nSMO LICENSE TEXT\n```");
    // a non-SMO package's license file is not copied
    expect(out).not.toContain("### Bar");
  });

  it("falls back to the home directory's NuGet cache when NUGET_PACKAGES is unset", async () => {
    fsState.files.set(assets, JSON.stringify({ libraries: { "Foo/1.0.0": { type: "package" } } }));
    const out = await run({ NUGET_PACKAGES: undefined });
    expect(out).toContain("| Foo | 1.0.0 | see package |");
  });
});

describe("Rust section", () => {
  it("lists crates from cargo metadata (without the app itself), with file and unknown licenses", async () => {
    fsState.cargo = () => JSON.stringify({ packages: [
      { name: "serde", version: "1.0.0", license: "MIT OR Apache-2.0" }, { name: "querithm", version: "0.1.0", license: "MIT" },
      { name: "ring", version: "0.17.0", license: null, license_file: "LICENSE" }, { name: "odd", version: "0.1.0", license: null },
    ] });
    const out = await run();
    expect(out).toContain("| serde | 1.0.0 | MIT OR Apache-2.0 |");
    expect(out).toContain("| ring | 0.17.0 | file |");
    expect(out).toContain("| odd | 0.1.0 | UNKNOWN |");
    expect(out).not.toContain("| querithm |");
    expect(out.indexOf("| odd ")).toBeLessThan(out.indexOf("| ring "));
  });

  it("records the failure on one line when cargo is unavailable (offline build machines)", async () => {
    const out = await run();
    expect(out).toContain("(cargo metadata failed: cargo not found)");
    expect(out).not.toContain("second line");
  });
});

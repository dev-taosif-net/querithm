// @vitest-environment jsdom
// Encryption in every mode the editor offers, for every server engine (§13.2): the folded summary and its tone, the
// auto-open on a warning, the CA field, the Production acknowledgement, and the saved payload. File engines have none.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile, TlsMode } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  engines: null as EngineInfo[] | null,
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, RpcError: class extends Error {},
  shell: { secretDelete: vi.fn(async () => {}), secretSet: vi.fn(async () => {}), secretExists: vi.fn(async () => true) },
}));
vi.mock("../../ipc/transport", () => ({ isTauri: false }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: vi.fn(async () => true) }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn(async () => null) }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => m.engines }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: vi.fn(async () => ({ grantId: "g1", name: "a.db", size: 1 })), requestWriteAccess: vi.fn(async () => true), needsRepick: () => false }));

import { ProfileEditor, transportSummary } from "../ProfileEditor";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

type Tone = "ok" | "warn" | "muted";
type Risk = "plain" | "unchecked" | null;
/** engine, mode, trustCert, summary, tone, risk, CA field shown */
type Row = [Engine, TlsMode, boolean, string, Tone, Risk, boolean];

const CHECKED = "Encrypted · certificate checked";
const UNCHECKED = "Encrypted · certificate not checked";
const PLAIN = "Not encrypted";
const FALLBACK = "May fall back to plain text";

// What the sidecar builds for each (TlsMatrixTests): SqlClient Strict/Mandatory validate the certificate unless
// TrustServerCertificate, and Strict validates even with it; Npgsql Require does not validate; MySqlConnector Required
// does not validate. PostgreSQL's default (Require) is flagged as unchecked: shown in warning colour (owner, 2026-11-14) but
// folded, and only Production asks about it.
const ROWS: Row[] = [
  ["mssql", "default", false, CHECKED, "ok", null, false],
  ["mssql", "default", true, UNCHECKED, "warn", "unchecked", false],
  ["mssql", "strict", false, CHECKED, "ok", null, false],
  // SqlClient ignores TrustServerCertificate under Encrypt=Strict ("validation is always performed").
  ["mssql", "strict", true, CHECKED, "ok", null, false],
  ["mssql", "mandatory", false, CHECKED, "ok", null, false],
  ["mssql", "mandatory", true, UNCHECKED, "warn", "unchecked", false],
  ["mssql", "optional", false, PLAIN, "warn", "plain", false],
  ["mssql", "optional", true, PLAIN, "warn", "plain", false],
  ["pg", "default", false, "Encrypted (Require) · certificate not checked", "muted", "unchecked", false],
  ["pg", "verifyFull", false, CHECKED, "ok", null, true],
  ["pg", "verifyCa", false, CHECKED, "ok", null, true],
  ["pg", "prefer", false, FALLBACK, "warn", "plain", false],
  ["pg", "disable", false, PLAIN, "warn", "plain", false],
  ["mySql", "default", false, CHECKED, "ok", null, true],
  ["mySql", "verifyCa", false, CHECKED, "ok", null, true],
  ["mySql", "require", false, UNCHECKED, "warn", "unchecked", false],
  ["mySql", "prefer", false, FALLBACK, "warn", "plain", false],
  ["mySql", "disable", false, PLAIN, "warn", "plain", false],
];
const RISKY = ROWS.filter((r) => r[5] !== null);
const SAFE = ROWS.filter((r) => r[5] === null);
const OFFERED: Record<"mssql" | "pg" | "mySql", TlsMode[]> = {
  mssql: ["default", "strict", "mandatory", "optional"],
  pg: ["default", "verifyFull", "verifyCa", "prefer", "disable"],
  mySql: ["default", "verifyCa", "require", "prefer", "disable"],
};

const info = (engine: Engine, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed: true, defaultAuth: "none", authKinds: [], locations,
});
const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "db.local", port: null, auth: engine === "mssql" ? "sql" : "password", username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
  createdAt: 0, updatedAt: 0, ...over,
});

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const row = () => screen.getByRole("button", { name: /^Encryption/ });
const summarySpan = () => document.getElementById(row().getAttribute("aria-describedby")!) as HTMLElement;
const modeBox = () => screen.getByLabelText("Encryption") as HTMLSelectElement;
const trustBox = () => screen.queryByRole("checkbox", { name: /Trust server certificate/ }) as HTMLInputElement | null;
const caBox = () => screen.queryByPlaceholderText("/etc/ssl/certs/root.crt") as HTMLInputElement | null;
const ackBox = () => screen.queryByRole("checkbox", { name: /I accept that this Production connection/ });
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const env = (name: string) => screen.getByRole("button", { name: name === "Prod" ? "Prod (Production)" : name });
const openEnc = async () => {
  if (row().getAttribute("aria-expanded") !== "true") await userEvent.click(row());
};
/** Opens the row and sets the mode and (SQL Server) the trust tick. */
const setTransport = async (mode: TlsMode, trust: boolean) => {
  await openEnc();
  if (modeBox().value !== mode) await userEvent.selectOptions(modeBox(), mode);
  const t = trustBox();
  if (t && t.checked !== trust) await userEvent.click(t);
};
/** The row shows an unchecked certificate in warning colour even where transportSummary's own tone is muted (PG Require). */
const expectSummary = (text: string, tone: Tone) => {
  const shown = tone === "muted" && text.includes("certificate not checked") ? "warn" : tone;
  expect(row().textContent).toContain(shown === "warn" ? `⚠ ${text}` : text);
  if (shown !== "warn") expect(row().textContent).not.toContain("⚠");
  expect(summarySpan().className).toContain(shown === "warn" ? "text-warn" : shown === "ok" ? "text-ok" : "text-muted");
};
const label = (r: Row) => `${r[0]} ${r[1]}${r[0] === "mssql" ? ` trust=${r[2]}` : ""}`;

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  m.engines = [info("mssql"), info("pg"), info("mySql"), info("sqlite", ["file", "memory"]), info("duckDb", ["file", "memory"])];
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

describe("TLS matrix: the summary is transportSummary, and transportSummary matches the driver", () => {
  it.each(ROWS.map((r) => [label(r), r] as const))("%s", (_l, [engine, mode, trust, text, tone, risk]) => {
    expect(transportSummary({ engine, tlsMode: mode, trustCert: trust, location: null })).toEqual({ text, tone, risk });
  });

  // Expected by the driver: Microsoft.Data.SqlClient 6.1.7 always validates under Encrypt=Strict, TrustServerCertificate or not
  // (TlsMatrixTests: Strict + trust builds Encrypt=Strict;TrustServerCertificate=True), and the UI says so.
  it("mssql strict trust=true: SqlClient checks the certificate anyway, so the UI should not say it does not", () => {
    expect(transportSummary({ engine: "mssql", tlsMode: "strict", trustCert: true, location: null })).toEqual({ text: CHECKED, tone: "ok", risk: null });
  });

  it("the editor offers exactly these modes per engine, and every one is in the matrix", async () => {
    for (const engine of ["mssql", "pg", "mySql"] as const) {
      render(<ProfileEditor preset={engine} onClose={() => {}} />);
      await openEnc();
      expect([...modeBox().options].map((o) => o.value)).toEqual(OFFERED[engine]);
      for (const mode of OFFERED[engine]) expect(ROWS.some((r) => r[0] === engine && r[1] === mode)).toBe(true);
      // trustCert is SQL Server's only
      expect(!!trustBox()).toBe(engine === "mssql");
      cleanup();
    }
  });
});

describe("TLS matrix: the rendered Encryption row (new profile)", () => {
  it.each(ROWS.map((r) => [label(r), r] as const))("%s: summary, tone, CA field", async (_l, [engine, mode, trust, text, tone, , ca]) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await setTransport(mode, trust);
    expectSummary(text, tone);
    expect(!!caBox()).toBe(ca);
    expect(!!screen.queryByText(/cross the network as plain text/)).toBe(text === PLAIN || text === FALLBACK);
    expect(!!screen.queryByText(/server certificate is not validated/)).toBe(engine === "mssql" && trust);
    // Folded, the row still says it.
    await userEvent.click(row());
    expect(row().getAttribute("aria-expanded")).toBe("false");
    expectSummary(text, tone);
  });

  it.each(ROWS.map((r) => [label(r), r] as const))("%s: a pasted string that sets it opens the row only on a warning", async (_l, [engine, mode, trust, text, tone]) => {
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.parseConnectionString") return undefined;
      const { profile } = params as { profile: Profile };
      return { profile: { ...profile, tlsMode: mode, trustCert: trust }, secret: null, unknown: [], notApplied: [], notes: [] };
    });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(row().getAttribute("aria-expanded")).toBe("false");
    await userEvent.click(screen.getByRole("button", { name: "Paste a connection string…" }));
    fireEvent.paste(screen.getByLabelText("Connection string"), { clipboardData: { getData: () => "Server=x" } });
    await waitFor(() => expect(screen.queryByLabelText("Connection string")).toBeNull());
    expectSummary(text, tone);
    // Off Production only the warn tone opens it (PostgreSQL's muted Require stays folded).
    await waitFor(() => expect(row().getAttribute("aria-expanded")).toBe(tone === "warn" ? "true" : "false"));
  });

  it.each(ROWS.map((r) => [label(r), r] as const))("%s: a saved profile opens with the row unfolded when it is not the default", (_l, [engine, mode, trust, text, tone]) => {
    useProfiles.setState({ profiles: [base("a", engine, { tlsMode: mode, trustCert: trust })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expectSummary(text, tone);
    expect(row().getAttribute("aria-expanded")).toBe(tone === "warn" || mode !== "default" || trust ? "true" : "false");
  });

  it.each(ROWS.map((r) => [label(r), r] as const))("%s: Save sends tlsMode, trustCert and caPath", async (_l, [engine, mode, trust, , , , ca]) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(env("Dev"));
    await setTransport(mode, trust);
    if (ca) await userEvent.type(caBox()!, "/ca/root.pem");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ engine, tlsMode: mode, trustCert: trust, caPath: ca ? "/ca/root.pem" : null });
  });

  it("a CA path stays on the profile when the mode moves off a verify mode, and is still shown", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.click(env("Dev"));
    await setTransport("verifyFull", false);
    await userEvent.type(caBox()!, "/ca.pem");
    await userEvent.selectOptions(modeBox(), "default");
    expect(caBox()!.value).toBe("/ca.pem");
    await userEvent.clear(caBox()!);
    expect(caBox()).toBeNull(); // emptied: back to null, field hidden off the verify modes
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ tlsMode: "default", caPath: null });
  });

  it("switching the server type resets the mode and the trust tick to the new engine's default", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await setTransport("optional", true);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await userEvent.click(await screen.findByRole("option", { name: "PostgreSQL" }));
    expectSummary("Encrypted (Require) · certificate not checked", "muted");
    await openEnc();
    expect(modeBox().value).toBe("default");
  });
});

describe("TLS matrix: Production acknowledgement", () => {
  it.each(RISKY.map((r) => [label(r), r] as const))("%s: a new Prod profile needs the tick; Save waits for it", async (_l, [engine, mode, trust, text, , risk]) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(env("Prod"));
    await setTransport(mode, trust);
    // While the tick is needed the summary warns, whatever its own tone (PostgreSQL's Require included).
    expectSummary(text, "warn");
    const tick = ackBox();
    expect(tick?.parentElement?.textContent).toContain(risk === "plain" ? "is not encrypted" : "does not check the server certificate");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain(`Confirm that this Production connection is ${risk === "plain" ? "not encrypted" : "not checking the certificate"}`);
    await userEvent.click(ackBox()!);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ environment: "prod", tlsMode: mode, trustCert: trust });
  });

  it.each(SAFE.map((r) => [label(r), r] as const))("%s: a new Prod profile needs no tick", async (_l, [engine, mode, trust, , , , ca]) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(env("Prod"));
    await setTransport(mode, trust);
    expect(ackBox()).toBeNull();
    if (ca) await userEvent.type(caBox()!, "/ca.pem");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
  });

  it.each(RISKY.map((r) => [label(r), r] as const))("%s: a saved Prod profile with this transport is not asked again", async (_l, [engine, mode, trust, text, tone]) => {
    useProfiles.setState({ profiles: [base("a", engine, { environment: "prod", tlsMode: mode, trustCert: trust })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    // Not asked, so the row shows its own tone and opens only for a warning or a non-default mode.
    expectSummary(text, tone);
    expect(row().getAttribute("aria-expanded")).toBe(tone === "warn" || mode !== "default" || trust ? "true" : "false");
    expect(ackBox()).toBeNull();
    // An unrelated edit (a CA path, the host) does not ask either.
    await userEvent.type(screen.getByLabelText("Host"), "2");
    expect(ackBox()).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
  });

  it.each(RISKY.map((r) => [label(r), r] as const))("%s: changing a saved Prod profile's weak transport to another weak one asks again", async (_l, [engine, mode, trust]) => {
    const others = RISKY.filter((r) => r[0] === engine && (r[1] !== mode || r[2] !== trust));
    const [, mode2, trust2] = others[0];
    useProfiles.setState({ profiles: [base("a", engine, { environment: "prod", tlsMode: mode, trustCert: trust })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await setTransport(mode2, trust2);
    expect(ackBox()).not.toBeNull();
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    // Going back to the saved transport needs no tick again.
    await setTransport(mode, trust);
    expect(ackBox()).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
  });

  it.each(RISKY.map((r) => [label(r), r] as const))("%s: a saved Dev profile with this transport moved to Prod needs the tick", async (_l, [engine, mode, trust]) => {
    useProfiles.setState({ profiles: [base("a", engine, { environment: "dev", tlsMode: mode, trustCert: trust })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(ackBox()).toBeNull();
    await userEvent.click(env("Prod"));
    expect(ackBox()).not.toBeNull();
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
  });

  it.each(SAFE.map((r) => [label(r), r] as const))("%s: a saved Prod profile weakened to a risky mode asks", async (_l, [engine, mode, trust]) => {
    const weak = RISKY.find((r) => r[0] === engine)!;
    useProfiles.setState({ profiles: [base("a", engine, { environment: "prod", tlsMode: mode, trustCert: trust })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await setTransport(weak[1], weak[2]);
    expect(ackBox()).not.toBeNull();
    // the tick resets when the transport changes again
    await userEvent.click(ackBox()!);
    const other = RISKY.find((r) => r[0] === engine && (r[1] !== weak[1] || r[2] !== weak[2]))!;
    await setTransport(other[1], other[2]);
    expect((ackBox() as HTMLInputElement).checked).toBe(false);
  });
});

describe("TLS matrix: PostgreSQL's default (Require) on Production", () => {
  it("a new profile: folded with no warning on Dev; on Prod the row opens by itself, warns and asks for the tick", async () => {
    const text = "Encrypted (Require) · certificate not checked";
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.click(env("Dev"));
    expect(row().getAttribute("aria-expanded")).toBe("false");
    expectSummary(text, "muted");
    expect(ackBox()).toBeNull();
    await userEvent.click(env("Prod"));
    await waitFor(() => expect(row().getAttribute("aria-expanded")).toBe("true"));
    expectSummary(text, "warn");
    expect(ackBox()?.parentElement?.textContent).toContain("does not check the server certificate");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await userEvent.click(ackBox()!);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ environment: "prod", tlsMode: "default" });
  });

  it("a saved Prod profile on Require is not asked, and its row stays folded and muted", async () => {
    useProfiles.setState({ profiles: [base("a", "pg", { environment: "prod" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(row().getAttribute("aria-expanded")).toBe("false");
    expectSummary("Encrypted (Require) · certificate not checked", "muted");
    expect(ackBox()).toBeNull();
  });
});

describe("TLS matrix: import notes and file engines", () => {
  it.each<[Engine, string]>([
    ["mssql", "Encrypt=False: only the sign-in is encrypted; queries and results cross the network in clear text unless the server forces encryption."],
    ["pg", "SSL Mode=Disable: the connection is not encrypted, so the password and every result cross the network in clear text."],
    ["mySql", "SslMode=Required: the connection is encrypted, but the server's certificate is not checked, so it does not prove which server answered."],
  ])("%s: the sidecar's M3 note shows as a warning", async (engine, note) => {
    m.rpc.mockImplementation(async (method: unknown, params: unknown) =>
      method === "profile.parseConnectionString" ? { profile: (params as { profile: Profile }).profile, secret: null, unknown: [], notApplied: [], notes: [note] } : undefined);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Paste a connection string…" }));
    fireEvent.paste(screen.getByLabelText("Connection string"), { clipboardData: { getData: () => "x=y" } });
    const shown = await screen.findByText(note);
    expect(shown.className).toContain("text-warn");
  });

  it.each<Engine>(["sqlite", "duckDb"])("%s: no Encryption row in any location, and no transport risk on Prod", async (engine) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(screen.queryByRole("button", { name: /^Encryption/ })).toBeNull();
    await userEvent.click(env("Prod"));
    expect(ackBox()).toBeNull();
    for (const location of ["file", "memory"] as const)
      for (const mode of ["disable", "optional", "prefer", "require"] as TlsMode[])
        expect(transportSummary({ engine, tlsMode: mode, trustCert: true, location })).toEqual({ text: "Not used for a database on this computer", tone: "muted", risk: null });
    const memory = screen.queryByRole("button", { name: /in-memory database instead/ });
    if (memory) {
      await userEvent.click(memory);
      expect(screen.queryByRole("button", { name: /^Encryption/ })).toBeNull();
    }
  });
});

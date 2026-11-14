// @vitest-environment jsdom
// D4 S06: the profile dialog for Oracle (hidden engine): TCPS by default with a warned downgrade, a service name or an
// explicit SID, the password only through the keyring, wallet / OS sign-in shown unavailable, and an engine the sidecar
// cannot run here (Intel macOS, O6) shown disabled with its reason. Mocks as in ProfileEditor.test.tsx.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  secretDelete: vi.fn(async (..._a: unknown[]) => {}),
  secretSet: vi.fn(async (..._a: unknown[]) => {}),
  secretExists: vi.fn(async (..._a: unknown[]) => true),
  confirmDeleteProfile: vi.fn(async (..._a: unknown[]) => true),
  openDialog: vi.fn(async (..._a: unknown[]): Promise<unknown> => "/keys/id_rsa"),
  tauri: false,
  // system.engines: null (the sidecar could not answer) offers the fallback engines, as before engines were listed.
  engines: null as EngineInfo[] | null,
  pick: vi.fn(async (): Promise<{ grantId: string; name: string; size: number } | null> => ({ grantId: "g1", name: "sales.db", size: 10 })),
  allowWrites: vi.fn(async (..._a: unknown[]) => true),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, shell: { secretDelete: m.secretDelete, secretSet: m.secretSet, secretExists: m.secretExists }, RpcError: class extends Error {},
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.tauri; } }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: m.confirmDeleteProfile }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: m.openDialog }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => m.engines }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: m.pick, requestWriteAccess: m.allowWrites, needsRepick: () => false }));

import { AUTH_UNAVAILABLE, defaultPortOf, ProfileEditor, profileFieldErrors, transportSummary } from "../ProfileEditor";
import { unavailableEngines } from "../engineInfo";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "db.local", port: null, auth: engine === "pg" ? "password" : "sql", username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0, ...over,
});
const info = (engine: Engine, exposed: boolean, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed, defaultAuth: "none", authKinds: [], locations,
});
const ALL = (): EngineInfo[] => [info("mssql", true), info("pg", true), info("sqlite", true, ["file", "memory"]), info("mySql", true), info("duckDb", true, ["file", "memory"])];

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const nameBox = () => screen.getByLabelText("Name") as HTMLInputElement;
const hostBox = () => screen.getByLabelText("Host") as HTMLInputElement;
const portBox = () => screen.getByLabelText("Port") as HTMLInputElement;
const combo = () => screen.getByRole("combobox", { name: "Server type" });
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const env = (name: string) => screen.getByRole("button", { name: name === "Prod" ? "Prod (Production)" : name });
const pickEnv = (name = "Dev") => userEvent.click(env(name));
const openRow = async (title: RegExp) => {
  const row = screen.getByRole("button", { name: title });
  if (row.getAttribute("aria-expanded") !== "true") await userEvent.click(row);
};

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  Object.values(m).forEach((f) => typeof f === "function" && (f as { mockClear?: () => void }).mockClear?.());
  m.tauri = false;
  m.engines = null;
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  m.secretExists.mockResolvedValue(true);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);


const ORACLE = (): EngineInfo[] => [...ALL(), { ...info("oracle", true), defaultAuth: "password", authKinds: ["password"], defaultPort: 2484 }];

describe("ProfileEditor: Oracle", () => {
  it("defaults to TCPS (verify full) on 2484; TCP is the explicit, warned downgrade that defaults to 1521", async () => {
    m.engines = ORACLE();
    render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    expect(portBox().placeholder).toBe("2484");
    await openRow(/^Encryption/);
    const tls = screen.getByLabelText("Encryption") as HTMLSelectElement;
    expect([...tls.options].map((o) => o.value)).toEqual(["default", "verifyCa", "disable"]);
    expect(tls.value).toBe("default");
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("Encrypted · certificate checked");
    expect(screen.getByPlaceholderText("/etc/ssl/certs/root.crt")).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: /Trust server certificate/ })).toBeNull();
    await userEvent.selectOptions(tls, "disable");
    expect(portBox().placeholder).toBe("1521");
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("Not encrypted");
    expect(screen.getByText(/cross the network as plain text/)).toBeTruthy();
  });

  it("TCPS on 1521 says that is usually the plain TCP listener", async () => {
    m.engines = ORACLE();
    const { container } = render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    await userEvent.type(portBox(), "1521");
    expect(container.querySelector("[data-q-oracle-port]")?.textContent).toMatch(/TCPS listens on 2484/);
    await userEvent.clear(portBox());
    await userEvent.type(portBox(), "2484");
    expect(container.querySelector("[data-q-oracle-port]")).toBeNull();
  });

  it("asks for a service name (or, explicitly, a SID) and checks its characters as the sidecar does", async () => {
    m.engines = ORACLE();
    render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    const service = screen.getByLabelText("Service name") as HTMLInputElement;
    expect(service.placeholder).toBe("FREEPDB1");
    await userEvent.type(nameBox(), "ora");
    await pickEnv("Dev");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getAllByText(/Enter the service name/).length).toBeGreaterThan(0));
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect by SID/ }));
    expect(screen.getByLabelText("SID")).toBeTruthy();
    await userEvent.type(screen.getByLabelText("SID"), "ORCL");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ engine: "oracle", databaseDefault: "ORCL", options: { oracleSid: "true" }, tlsMode: "default", auth: "password" });
  });

  it("the password goes to the keyring through save, never into the profile", async () => {
    m.engines = ORACLE();
    render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Service name"), "FREEPDB1");
    await userEvent.type(nameBox(), "ora");
    await userEvent.type(screen.getByLabelText("Password"), "s3cret-pw");
    await pickEnv("Dev");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [profile, secret] = saveSpy.mock.calls[0];
    expect(JSON.stringify(profile)).not.toContain("s3cret-pw");
    expect(secret).toBe("s3cret-pw");
  });

  it("shows wallet and OS sign-in as unavailable, with the reason, and offers only the password", async () => {
    m.engines = ORACLE();
    const { container } = render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    expect(container.querySelector("select#" + CSS.escape(screen.getByText("Password", { selector: "span" }).id))).toBeNull();
    const list = container.querySelector("[data-q-auth-unavailable]")!;
    for (const a of AUTH_UNAVAILABLE.oracle) expect(list.textContent).toContain(`${a.label}: ${a.reason}`);
    expect(list.textContent).toMatch(/Oracle wallet.*not available yet/);
    expect(list.textContent).toMatch(/OS authentication.*not available yet/);
  });

  it("an engine the sidecar cannot run here (Intel macOS) is listed disabled with its reason and cannot be chosen", async () => {
    const why = "Oracle's driver (ODP.NET) does not run on Intel Macs. Oracle connections work in Querithm on Apple-silicon Macs, Windows and Linux.";
    m.engines = ORACLE().map((i) => (i.engine === "oracle" ? { ...i, unavailable: why } : i));
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(combo());
    const option = await screen.findByRole("option", { name: /Oracle Database/ });
    expect(option.getAttribute("aria-disabled")).toBe("true");
    expect(option.querySelector("[data-q-engine-unavailable]")?.textContent).toBe(why);
    await userEvent.click(option);
    // Still open, nothing chosen.
    expect(screen.getByRole("listbox", { name: "Server type" })).toBeTruthy();
    expect(screen.queryByLabelText("Service name")).toBeNull();
    await userEvent.click(screen.getByRole("option", { name: /PostgreSQL/ }));
    expect(hostBox()).toBeTruthy();
  });
});

describe("ProfileEditor: Oracle through SSH (D4 S07)", () => {
  it("offers the SSH tunnel and saves it with the listener as the bastion reaches it", async () => {
    m.engines = ORACLE();
    render(<ProfileEditor preset="oracle" onClose={() => {}} />);
    await userEvent.type(nameBox(), "ora via bastion");
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "db-internal");
    await userEvent.type(screen.getByLabelText("Service name"), "ORCLPDB1");
    await pickEnv("Dev");
    await openRow(/^SSH tunnel/);
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "jump.example.com");
    await userEvent.type(screen.getByPlaceholderText("deploy"), "ops");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/keys/id_ed25519");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const saved = saveSpy.mock.calls[0][0];
    expect(saved).toMatchObject({ engine: "oracle", host: "db-internal", databaseDefault: "ORCLPDB1", tlsMode: "default" });
    expect(saved.ssh).toMatchObject({ host: "jump.example.com", port: 22, username: "ops", auth: "privateKey", keyPath: "/keys/id_ed25519" });
    // The tunnel's routing options are the sidecar's own; the dialog never writes them.
    expect(saved.options?.tunnelHost).toBeUndefined();
    expect(saved.options?.tunnelPort).toBeUndefined();
  });
});

describe("Oracle profile rules (pure)", () => {
  const ora = (over: Partial<Profile> = {}): Profile => base("o", "oracle", { auth: "password", databaseDefault: "FREEPDB1", ...over });

  it("the default port follows the transport", () => {
    expect(defaultPortOf(ora())).toBe(2484);
    expect(defaultPortOf(ora({ tlsMode: "verifyCa" }))).toBe(2484);
    expect(defaultPortOf(ora({ tlsMode: "disable" }))).toBe(1521);
    expect(defaultPortOf(base("p", "pg"))).toBe(5432);
  });

  it("service name or SID: required, letters, digits and _ . $ # - only", () => {
    expect(profileFieldErrors(ora())).toEqual({});
    expect(profileFieldErrors(ora({ databaseDefault: "ORCL.example_1$#-x" }))).toEqual({});
    expect(profileFieldErrors(ora({ databaseDefault: null })).service).toMatch(/service name/);
    expect(profileFieldErrors(ora({ databaseDefault: null, options: { oracleSid: "true" } })).service).toBe("Enter the SID.");
    expect(profileFieldErrors(ora({ databaseDefault: "a)(b" })).service).toMatch(/letters, digits/);
    expect(profileFieldErrors(ora({ databaseDefault: "x".repeat(129) })).service).toBeTruthy();
    // Other engines have no service field.
    expect(profileFieldErrors(base("p", "pg", { databaseDefault: null })).service).toBeUndefined();
  });

  it("the transport summary: TCPS checks the certificate; TCP is plain", () => {
    expect(transportSummary(ora())).toMatchObject({ tone: "ok", risk: null });
    expect(transportSummary(ora({ tlsMode: "verifyCa" }))).toMatchObject({ risk: null });
    expect(transportSummary(ora({ tlsMode: "disable" }))).toMatchObject({ tone: "warn", risk: "plain" });
  });

  it("unavailableEngines reads the sidecar's reasons and ignores anything else", () => {
    expect(unavailableEngines(null)).toEqual({});
    expect(unavailableEngines([{ ...info("oracle", true), unavailable: "why" }, { ...info("pg", true), unavailable: "  " }, info("mssql", true)])).toEqual({ oracle: "why" });
    expect(unavailableEngines([{ ...info("oracle", true), engine: "nope" as Engine, unavailable: "x" }])).toEqual({});
  });
});

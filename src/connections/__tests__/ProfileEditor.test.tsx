// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile, TlsMode } from "../../ipc/contract";

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

import {
  ProfileEditor, looksLikeConnectionString, looksLikeProduction, profileFieldErrors, splitHostPort, suggestedName, transportSummary, withDefaultHost,
} from "../ProfileEditor";
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
const toasts = () => useUi.getState().toasts.map((t) => `${t.kind}:${t.text}`);
const nameBox = () => screen.getByLabelText("Name") as HTMLInputElement;
const hostBox = () => screen.getByLabelText("Host") as HTMLInputElement;
const portBox = () => screen.getByLabelText("Port") as HTMLInputElement;
const combo = () => screen.getByRole("combobox", { name: "Server type" });
const testBtn = () => screen.getByRole("button", { name: "Test connection" });
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const status = () => screen.getByRole("status");
const env = (name: string) => screen.getByRole("button", { name: name === "Prod" ? "Prod (Production)" : name });
const pickEnv = (name = "Dev") => userEvent.click(env(name));
const openRow = async (title: RegExp) => {
  const row = screen.getByRole("button", { name: title });
  if (row.getAttribute("aria-expanded") !== "true") await userEvent.click(row);
};
const optionNames = () => within(screen.getByRole("listbox", { name: "Server type" })).getAllByRole("option").map((o) => o.textContent);
const chooseEngine = async (label: string) => {
  await userEvent.click(combo());
  await waitFor(() => expect(optionNames()).toContain(label));
  await userEvent.click(screen.getByRole("option", { name: label }));
};
const paste = (el: HTMLElement, text: string) => fireEvent.paste(el, { clipboardData: { getData: () => text } });

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

describe("ProfileEditor: a new profile preselects nothing", () => {
  it("names no server type and no environment, offers the engines as tiles, and hides the fields, Test and Save until a type is chosen", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    expect(screen.getByText("New connection")).toBeTruthy();
    expect(combo().textContent).toContain("Choose a server type…");
    // No environment pill until one is chosen.
    expect(screen.queryByText("Dev")).toBeNull();
    expect(screen.queryByRole("group", { name: "Environment" })).toBeNull();
    expect(screen.queryByLabelText("Host")).toBeNull();
    expect(screen.queryByLabelText("Name")).toBeNull();
    expect(within(screen.getByRole("group", { name: "On a server" })).getAllByRole("button").map((b) => b.getAttribute("data-q-engine-tile"))).toEqual(["mssql", "pg"]);
    expect(screen.getByRole("button", { name: /^Paste a connection string ?Server=/ })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Paste a connection string…" })).toBeNull();
    expect(status().textContent).toBe("Pick a server type to continue.");
    expect(screen.queryByText(/Not tested yet/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Test connection" })).toBeNull();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();

    await chooseEngine("PostgreSQL");
    expect(screen.getByRole("button", { name: "Paste a connection string…" })).toBeTruthy();
    expect(screen.getByRole("group", { name: "Environment" })).toBeTruthy();
    expect(status().textContent).toContain("Not tested yet. You can save without testing.");
    expect(testBtn()).toBeTruthy();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeTruthy();
    expect(saveBtn()).toBeTruthy();
  });

  it("Save lists what is missing and focuses the field when clicked in the alert; nothing is saved", async () => {
    m.engines = ALL();
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine("SQLite");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain("Pick the database file first.");
    expect(alert.textContent).toContain("Choose the environment");
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("button", { name: "Pick file…" })));
    await userEvent.click(within(alert).getByRole("button", { name: /Choose the environment/ }));
    await waitFor(() => expect(document.activeElement).toBe(env("Local")));
  });

  it("marks recently used server types on their tiles; a tile picks the type and moves focus to Host", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { updatedAt: 1 }), base("b", "pg", { updatedAt: 5 }), base("c", "mssql", { updatedAt: 3 })] });
    render(<ProfileEditor onClose={() => {}} />);
    const tile = (e: string) => document.querySelector(`[data-q-engine-tile="${e}"]`) as HTMLButtonElement;
    expect(tile("pg").textContent).toContain("· recent");
    expect(tile("mssql").textContent).toContain("· recent");
    await userEvent.click(tile("pg"));
    expect(combo().textContent).toContain("PostgreSQL");
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText("Host")));
    expect(document.querySelector("[data-q-engine-tile]")).toBeNull();
    // the first choice is not a switch: nothing to undo
    expect(screen.queryByRole("button", { name: "Undo" })).toBeNull();
  });

  it("arrow keys move between the tiles without picking one; Enter picks", async () => {
    m.engines = ALL();
    render(<ProfileEditor onClose={() => {}} />);
    const tile = (e: string) => document.querySelector(`[data-q-engine-tile="${e}"]`) as HTMLButtonElement;
    await waitFor(() => expect(tile("sqlite")).toBeTruthy());
    tile("mssql").focus();
    await userEvent.keyboard("{ArrowRight}");
    expect(document.activeElement).toBe(tile("pg"));
    expect(combo().textContent).toContain("Choose a server type…");
    await userEvent.keyboard("{Enter}");
    expect(combo().textContent).toContain("PostgreSQL");
  });

  it.each<[Engine, string]>([["mssql", "Microsoft SQL Server"], ["pg", "PostgreSQL"], ["mySql", "MySQL / MariaDB"], ["sqlite", "SQLite"], ["duckDb", "DuckDB"]])(
    "%s: choosing the type keeps the environment unchosen", async (_engine, label) => {
      m.engines = ALL();
      render(<ProfileEditor onClose={() => {}} />);
      await chooseEngine(label);
      expect(combo().textContent).toContain(label);
      expect(screen.getAllByRole("button", { name: /^(Local|Dev|Test|Staging|Prod \(Production\))$/ }).filter((r) => r.getAttribute("aria-pressed") === "true")).toEqual([]);
      await userEvent.click(saveBtn());
      expect(saveSpy).not.toHaveBeenCalled();
      expect(screen.getByRole("group", { name: "Environment" }).getAttribute("aria-invalid")).toBe("true");
    },
  );

  it.each<[string, string, string]>([["PostgreSQL", "postgres", "postgres"], ["MySQL / MariaDB", "root", ""], ["Microsoft SQL Server", "sa", ""]])(
    "the first type chosen (%s) starts from its own defaults, not another engine's", async (label, user, db) => {
      m.engines = ALL();
      render(<ProfileEditor onClose={() => {}} />);
      await chooseEngine(label);
      await pickEnv("Test");
      expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe(user);
      expect((screen.getByLabelText("Database") as HTMLInputElement).value).toBe(db);
      expect((env("Test").getAttribute("aria-pressed") === "true")).toBe(true);
    },
  );

  it("a duplicate keeps the original's type and environment", () => {
    useProfiles.setState({ profiles: [base("a", "pg", { environment: "staging" })] });
    render(<ProfileEditor copyOf="a" onClose={() => {}} />);
    expect(combo().textContent).toContain("PostgreSQL");
    expect((env("Staging").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(nameBox().value).toBe("Name-a (copy)");
  });

  it("reveals sections and footer controls with smooth reveal classes when a server type is chosen", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine("PostgreSQL");
    expect(screen.getByLabelText("Host").closest(".q-profile-reveal")).not.toBeNull();
    expect(screen.getByRole("group", { name: "Environment" }).closest(".q-profile-reveal")).not.toBeNull();
    expect(testBtn().classList.contains("q-profile-reveal")).toBe(true);
    expect(saveBtn().classList.contains("q-profile-reveal")).toBe(true);
  });
});

describe.each<[Engine, string, string, string, string]>([
  ["mssql", "Microsoft SQL Server", "sa", "1433", "(login default)"],
  ["pg", "PostgreSQL", "postgres", "5432", "postgres"],
  ["mySql", "MySQL / MariaDB", "root", "3306", "(login default)"],
])("ProfileEditor new %s profile", (engine, label, user, port, dbPlaceholder) => {
  beforeEach(() => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
  });

  it("starts with the engine's defaults and no environment", () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(combo().textContent).toContain(label);
    expect(combo().textContent).toContain(`default port ${port}`);
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe(user);
    expect(portBox().placeholder).toBe(port);
    expect((screen.getByLabelText("Database") as HTMLInputElement).placeholder).toBe(dbPlaceholder);
    expect(screen.queryByText("Delete")).toBeNull();
    expect(screen.getAllByRole("button", { name: /^(Local|Dev|Test|Staging|Prod \(Production\))$/ }).filter((b) => b.getAttribute("aria-pressed") === "true")).toEqual([]);
    if (engine === "mySql") {
      expect(screen.queryByRole("combobox", { name: "Sign in" })).toBeNull();
      expect(document.querySelector('[id$="-auth"]')!.textContent).toBe("Password");
    } else expect((screen.getByLabelText("Sign in") as HTMLSelectElement).selectedOptions[0].textContent).toBe(engine === "pg" ? "Password / SCRAM" : "SQL Server login");
  });

  it("refuses to save until an environment is chosen, then saves with the suggested name", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Choose the environment");
    await waitFor(() => expect(document.activeElement).toBe(env("Local")));
    await pickEnv("Dev");
    expect(screen.queryByRole("alert")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const p = saveSpy.mock.calls[0][0] as Profile;
    expect(p).toMatchObject({ engine, environment: "dev", name: engine === "pg" ? "localhost / postgres" : "localhost" });
  });

  it("the suggested name follows host and database until a name is typed", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "srv9");
    await userEvent.clear(screen.getByLabelText("Database"));
    await userEvent.type(screen.getByLabelText("Database"), "sales");
    expect(nameBox().value).toBe("srv9 / sales");
    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "Mine");
    await userEvent.type(hostBox(), "0");
    expect(nameBox().value).toBe("Mine");
  });

  it("an emptied name shows the suggestion as its placeholder and saves with it", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.clear(nameBox());
    expect(nameBox().value).toBe("");
    expect(nameBox().placeholder).toBe(engine === "pg" ? "localhost / postgres" : "localhost");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ name: engine === "pg" ? "localhost / postgres" : "localhost" }), null, ""));
  });

  it("saves the typed fields and the password (typed secrets go to the keyring path)", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset={engine} onClose={onClose} />);
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "srv1");
    await userEvent.type(portBox(), "4000");
    await userEvent.type(screen.getByLabelText("Database"), "Sales");
    await pickEnv("Test");
    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "My DB");
    await userEvent.type(screen.getByLabelText("Password"), "s3cret");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const [p, secret, ssh] = saveSpy.mock.calls[0] as unknown as [Profile, string | null, string | null];
    expect(p).toMatchObject({ name: "My DB", host: "srv1", port: 4000, engine, environment: "test", databaseDefault: engine === "pg" ? "postgres" + "Sales" : "Sales" });
    expect(secret).toBe("s3cret");
    expect(ssh).toBe(""); // no tunnel: its keyring entry is cleared
  });

  it("clearing the port and the database stores null", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.type(portBox(), "12");
    await userEvent.clear(portBox());
    await userEvent.clear(screen.getByLabelText("Database"));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect((saveSpy.mock.calls[0][0] as Profile).port).toBeNull();
    expect((saveSpy.mock.calls[0][0] as Profile).databaseDefault).toBeNull();
  });

  it("defaults to one connection and saves a custom password duration on the one-row control", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect((screen.getByLabelText("Remember password") as HTMLSelectElement).value).toBe("once");
    expect(screen.getByRole("option", { name: "Until disconnected" })).toBeTruthy();
    await pickEnv();
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
    await userEvent.selectOptions(screen.getByLabelText("Duration"), "custom");
    expect((screen.getByLabelText("Unit") as HTMLSelectElement).value).toBe("days");
    await userEvent.selectOptions(screen.getByLabelText("Unit"), "minutes");
    await userEvent.clear(screen.getByLabelText("Length"));
    await userEvent.type(screen.getByLabelText("Length"), "45");
    await userEvent.click(saveBtn());
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ passwordRetention: "duration", passwordRetentionMinutes: 45 }), null, "");
  });

  it("a preset duration needs no length", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
    await userEvent.selectOptions(screen.getByLabelText("Duration"), "10080");
    expect(screen.queryByLabelText("Length")).toBeNull();
    await userEvent.click(saveBtn());
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ passwordRetention: "duration", passwordRetentionMinutes: 10080 }), null, "");
  });

  it("password can be shown and hidden", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    const pw = () => screen.getByLabelText("Password") as HTMLInputElement;
    expect(pw().type).toBe("password");
    const eye = screen.getByRole("button", { name: "Show password" });
    await userEvent.click(eye);
    expect(pw().type).toBe("text");
    // One fixed name; the pressed state says it is shown.
    expect(eye.getAttribute("aria-pressed")).toBe("true");
    await userEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(pw().type).toBe("password");
    expect(eye.getAttribute("aria-pressed")).toBe("false");
  });

  it("the folded Encryption row says what the default does", () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    const row = screen.getByRole("button", { name: /^Encryption/ });
    expect(row.getAttribute("aria-expanded")).toBe("false");
    expect(row.textContent).toContain(engine === "pg" ? "Encrypted (Require) · certificate not checked" : "Encrypted · certificate checked");
  });

  it("a plain-text TLS mode warns on the folded row, which stays readable when folded", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openRow(/^Encryption/);
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), engine === "mssql" ? "optional" : "disable");
    const row = screen.getByRole("button", { name: /^Encryption/ });
    expect(row.textContent).toContain("⚠ Not encrypted");
    expect(screen.getByText(/cross the network as plain text/)).toBeTruthy();
    await userEvent.click(row);
    expect(row.getAttribute("aria-expanded")).toBe("false");
    expect(row.textContent).toContain("⚠ Not encrypted");
  });

  it("Enter in a field saves; Enter in the server-type search only picks", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.click(combo());
    await userEvent.keyboard("{Enter}");
    expect(saveSpy).not.toHaveBeenCalled();
    await userEvent.type(hostBox(), "x{Enter}");
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
  });
});

describe("ProfileEditor sign-in methods", () => {
  it("SQL Server: integrated and device-code sign-ins hide the user and password; interactive takes a hint; a token is a secret", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    const method = screen.getByLabelText("Sign in") as HTMLSelectElement;
    expect([...method.options].map((o) => o.textContent)).toContain("Microsoft Entra — device code");
    await userEvent.selectOptions(method, "windows");
    expect(screen.queryByLabelText("Password")).toBeNull();
    expect(screen.queryByLabelText("User name")).toBeNull();
    expect(screen.getByText("Signs in as your Windows account.")).toBeTruthy();
    expect(screen.queryByLabelText("Remember password")).toBeNull();
    await userEvent.selectOptions(method, "entraInteractive");
    expect(screen.getByLabelText("User (optional hint)")).toBeTruthy();
    await userEvent.selectOptions(method, "entraDeviceCode");
    expect(screen.queryByLabelText("User name")).toBeNull();
    await userEvent.selectOptions(method, "entraToken");
    expect(screen.getByLabelText("Access token")).toBeTruthy();
  });

  it("PostgreSQL: no Windows sign-in; Kerberos needs no password", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    const method = screen.getByLabelText("Sign in") as HTMLSelectElement;
    expect([...method.options].map((o) => o.textContent)).not.toContain("Windows integrated");
    await userEvent.selectOptions(method, "kerberos");
    expect(screen.queryByLabelText("Password")).toBeNull();
    await userEvent.selectOptions(method, "entraToken");
    expect(screen.getByLabelText("Access token")).toBeTruthy();
  });
});

describe("ProfileEditor encryption per engine", () => {
  it("SQL Server: its modes, trust-certificate warns, failover and read-only intent land in options", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await pickEnv();
    await openRow(/^Encryption/);
    expect([...(screen.getByLabelText("Encryption") as HTMLSelectElement).options].map((o) => o.value)).toEqual(["default", "strict", "mandatory", "optional"]);
    expect(screen.queryByPlaceholderText("/etc/ssl/certs/root.crt")).toBeNull();
    await userEvent.click(screen.getByRole("checkbox", { name: /Trust server certificate/ }));
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("⚠ Encrypted · certificate not checked");
    expect(screen.getByText(/server certificate is not validated/)).toBeTruthy();
    // Failover and read-only intent live under Availability, not Timeouts.
    await openRow(/^Availability/);
    await userEvent.click(screen.getByRole("checkbox", { name: /Multi-subnet failover/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Read-only intent/ }));
    expect(screen.getByRole("button", { name: /^Availability/ }).textContent).toContain("Read-only intent · multi-subnet failover");
    expect(screen.getByRole("button", { name: /^Timeouts/ }).textContent).not.toContain("failover");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const p = saveSpy.mock.calls[0][0] as Profile;
    expect(p.trustCert).toBe(true);
    expect(p.options).toEqual({ multiSubnetFailover: "true", applicationIntent: "readOnly" });
  });

  it("PostgreSQL: its modes; the CA file is asked for by the verify modes and stored", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await openRow(/^Encryption/);
    expect([...(screen.getByLabelText("Encryption") as HTMLSelectElement).options].map((o) => o.value)).toEqual(["default", "verifyFull", "verifyCa", "prefer", "disable"]);
    expect(screen.queryByRole("checkbox", { name: /Trust server certificate/ })).toBeNull();
    expect(screen.queryByPlaceholderText("/etc/ssl/certs/root.crt")).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), "verifyFull");
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("Encrypted · certificate checked");
    await userEvent.type(screen.getByPlaceholderText("/etc/ssl/certs/root.crt"), "/ca.pem");
    await openRow(/^Timeouts/);
    expect(screen.queryByRole("button", { name: /^Availability/ })).toBeNull();
    expect(screen.queryByRole("checkbox", { name: /Multi-subnet failover/ })).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ tlsMode: "verifyFull", caPath: "/ca.pem" });
  });

  it("MySQL / MariaDB: its modes; Require warns that the certificate is not checked", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
    render(<ProfileEditor preset="mySql" onClose={() => {}} />);
    await openRow(/^Encryption/);
    const tls = screen.getByLabelText("Encryption") as HTMLSelectElement;
    expect([...tls.options].map((o) => o.value)).toEqual(["default", "verifyCa", "require", "prefer", "disable"]);
    expect(screen.getByPlaceholderText("/etc/ssl/certs/root.crt")).toBeTruthy();
    expect(screen.queryByText(/host\\instance/)).toBeNull();
    await userEvent.selectOptions(tls, "require");
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("⚠ Encrypted · certificate not checked");
  });

  it("an existing profile with non-default encryption opens with the row unfolded", () => {
    useProfiles.setState({ profiles: [base("a", "pg", { tlsMode: "verifyCa", caPath: "/ca" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("true");
    expect((screen.getByPlaceholderText("/etc/ssl/certs/root.crt") as HTMLInputElement).value).toBe("/ca");
  });
});

describe("ProfileEditor Production safety", () => {
  it("choosing Prod paints the dialog and says what Prod does", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv("Prod");
    expect((env("Prod").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.getByText(/tabs and the status bar are painted/)).toBeTruthy();
    expect(screen.queryByText("No environment")).toBeNull();
  });

  it.each<[Engine, TlsMode | null, boolean, string]>([
    ["mssql", null, true, "does not check the server certificate"],
    ["pg", "disable", false, "is not encrypted"],
    ["mySql", "require", false, "does not check the server certificate"],
  ])("%s: a weak transport on a new Prod profile needs a tick where it is chosen", async (engine, mode, trust, words) => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv("Prod");
    await openRow(/^Encryption/);
    if (mode) await userEvent.selectOptions(screen.getByLabelText("Encryption"), mode);
    if (trust) await userEvent.click(screen.getByRole("checkbox", { name: /Trust server certificate/ }));
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Confirm that this Production connection");
    const tick = screen.getByRole("checkbox", { name: new RegExp(`I accept that this Production connection ${words}`) });
    await waitFor(() => expect(document.activeElement).toBe(tick));
    await userEvent.click(tick);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
  });

  it("PostgreSQL's default (Require, certificate not checked) is asked about on a new Prod profile, and only there", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv("Dev");
    expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("false");
    // Shown in warning colour on every environment, but it opens and asks only on Prod.
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("⚠");
    await pickEnv("Prod");
    await waitFor(() => expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("true"));
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("⚠");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("checkbox", { name: /I accept that this Production connection does not check the server certificate/ }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
  });

  it("SQL Server Strict checks the certificate even with Trust ticked: no warning, no tick", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await pickEnv("Prod");
    await openRow(/^Encryption/);
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), "strict");
    await userEvent.click(screen.getByRole("checkbox", { name: /Trust server certificate/ }));
    expect(screen.getByRole("button", { name: /^Encryption/ }).textContent).toContain("Encrypted · certificate checked");
    expect(screen.queryByRole("checkbox", { name: /I accept/ })).toBeNull();
  });

  it.each<[Engine, TlsMode]>([["pg", "disable"], ["pg", "prefer"], ["mySql", "disable"]])("%s %s on Prod warns on Save like SQL Server's Optional", async (engine, mode) => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
    useProfiles.setState({ profiles: [base("a", engine, { auth: "password", environment: "prod", tlsMode: mode })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(toasts()).toContain("warn:Production profile without encryption.");
  });

  it("a saved Prod profile is not asked again while its transport is unchanged, and still warns on Save", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { environment: "prod", tlsMode: "optional" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.queryByRole("checkbox", { name: /I accept/ })).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(toasts()).toContain("warn:Production profile without encryption.");
  });

  it("a host name that says prod offers Production; it never chooses an environment by itself", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect(screen.queryByText(/Is this Production/)).toBeNull(); // localhost suggests nothing either way
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "db-prod-01.corp");
    expect(screen.getByText(/contains “prod”. Is this Production/)).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /^(Local|Dev|Test|Staging|Prod \(Production\))$/ }).filter((r) => r.getAttribute("aria-pressed") === "true")).toEqual([]);
    await userEvent.click(screen.getByRole("button", { name: "It isn’t" }));
    expect(screen.queryByText(/Is this Production/)).toBeNull();
    await userEvent.type(hostBox(), "2");
    await userEvent.click(screen.getByRole("button", { name: "Mark as Production" }));
    expect((env("Prod").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.queryByText(/Is this Production/)).toBeNull();
  });

  it("lowering a saved Production profile asks first", async () => {
    useProfiles.setState({ profiles: [base("a", "pg", { environment: "prod" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await pickEnv("Dev");
    expect((env("Prod").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.getByText(/Lower this profile from Production to Dev/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Keep Production" }));
    expect((env("Prod").getAttribute("aria-pressed") === "true")).toBe(true);
    await pickEnv("Staging");
    await userEvent.click(screen.getByRole("button", { name: "Lower it" }));
    expect((env("Staging").getAttribute("aria-pressed") === "true")).toBe(true);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ environment: "staging" }), null, ""));
  });
});

describe("ProfileEditor groups and environment", () => {
  const live = () => useProfiles.setState({ groups: [{ name: "Live", environment: "prod", color: null } as never, { name: "Lab", environment: "test", color: null } as never] });

  it("a group's default environment fills an environment nobody chose, and says so", async () => {
    live();
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Group"), "Live");
    expect((env("Prod").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.getByText(/Set from group “Live”/)).toBeTruthy();
    // another group may replace what a group filled
    await userEvent.clear(screen.getByLabelText("Group"));
    await userEvent.type(screen.getByLabelText("Group"), "Lab");
    expect((env("Test").getAttribute("aria-pressed") === "true")).toBe(true);
  });

  it("never overwrites an environment the user chose (regression: a Prod choice became the group's)", async () => {
    live();
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv("Dev");
    await userEvent.type(screen.getByLabelText("Group"), "Live");
    expect((env("Dev").getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.queryByText(/Set from group/)).toBeNull();
  });

  it("an existing profile keeps its environment", async () => {
    live();
    useProfiles.setState({ profiles: [base("a", "pg", { environment: "dev" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Group"), "Live");
    expect((env("Dev").getAttribute("aria-pressed") === "true")).toBe(true);
  });
});

describe("ProfileEditor switching the server type", () => {
  it("resets sign-in and TLS, gives an untouched default user the new engine's, moves a default port along, and can be undone", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(portBox(), "1433");
    await userEvent.click(combo());
    await userEvent.click(within(screen.getByRole("listbox")).getByText("PostgreSQL"));
    expect(combo().textContent).toContain("PostgreSQL");
    // "sa" was SQL Server's default, never typed: it is not carried over as if it were the user's.
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("postgres");
    expect((screen.getByLabelText("Sign in") as HTMLSelectElement).value).toBe("password");
    expect(portBox().value).toBe("");
    expect(portBox().placeholder).toBe("5432");
    expect(screen.getByText("Switched to PostgreSQL: sign-in is Password / SCRAM; user is postgres.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    expect(combo().textContent).toContain("Microsoft SQL Server");
    expect(portBox().value).toBe("1433");
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("sa");
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("keeps a typed user and database across a switch and says so; untouched defaults follow the new engine (regression)", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("postgres");
    await userEvent.click(combo());
    await userEvent.click(within(screen.getByRole("listbox")).getByText("Microsoft SQL Server"));
    // PostgreSQL's defaults were never typed: SQL Server's take their place.
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("sa");
    expect((screen.getByLabelText("Database") as HTMLInputElement).value).not.toBe("postgres");
    await userEvent.clear(screen.getByLabelText("User name"));
    await userEvent.type(screen.getByLabelText("User name"), "app_rw");
    await userEvent.clear(screen.getByLabelText("Database"));
    await userEvent.type(screen.getByLabelText("Database"), "billing");
    await userEvent.click(combo());
    await userEvent.click(within(screen.getByRole("listbox")).getByText("PostgreSQL"));
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("app_rw");
    expect((screen.getByLabelText("Database") as HTMLInputElement).value).toBe("billing");
    expect(screen.getByText(/your user and database were kept\./)).toBeTruthy();
  });

  it("a CA file chosen for one engine does not follow the profile to another", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await openRow(/^Encryption/);
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), "verifyFull");
    await userEvent.type(screen.getByPlaceholderText("/etc/ssl/certs/root.crt"), "/ca.pem");
    await chooseEngine("Microsoft SQL Server");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect((saveSpy.mock.calls[0][0] as Profile).caPath).toBeNull();
  });

  it("choosing the current type changes nothing", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("User name"), "2");
    await userEvent.click(combo());
    await userEvent.click(within(screen.getByRole("listbox")).getByText("PostgreSQL"));
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("postgres2");
    expect(screen.queryByRole("button", { name: "Undo" })).toBeNull();
  });

  it("a port that belongs to another engine offers that engine", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(portBox(), "1433");
    await userEvent.tab();
    expect(screen.getByText("1433 is the usual SQL Server port.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Switch to SQL Server" }));
    expect(combo().textContent).toContain("Microsoft SQL Server");
    expect(portBox().value).toBe("1433");
  });

  it.each<[Engine, boolean]>([["pg", true], ["mySql", true], ["mssql", false]])("%s: host:port typed in Host is split on leaving it: %s", async (engine, splits) => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "db1:5433");
    await userEvent.tab();
    if (splits) {
      expect(hostBox().value).toBe("db1");
      expect(portBox().value).toBe("5433");
      expect(screen.getByText("Moved 5433 from Host into Port.")).toBeTruthy();
    } else expect(hostBox().value).toBe("db1:5433");
  });
});

describe("ProfileEditor server-type picker", () => {
  it("opens from the keyboard into a search box; arrows move, Enter picks, Escape closes back to the button", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="mssql" onClose={onClose} />);
    combo().focus();
    await userEvent.keyboard("{ArrowDown}");
    const search = screen.getByRole("combobox", { name: "Search server types" });
    await waitFor(() => expect(document.activeElement).toBe(search));
    expect(search.getAttribute("aria-activedescendant")).toMatch(/-opt-mssql$/);
    await userEvent.keyboard("{ArrowDown}");
    expect(search.getAttribute("aria-activedescendant")).toMatch(/-opt-pg$/);
    await userEvent.keyboard("{ArrowDown}"); // clamped
    expect(search.getAttribute("aria-activedescendant")).toMatch(/-opt-pg$/);
    await userEvent.keyboard("{Home}");
    expect(search.getAttribute("aria-activedescendant")).toMatch(/-opt-mssql$/);
    await userEvent.keyboard("{End}{Enter}");
    expect(combo().textContent).toContain("PostgreSQL");
    expect(screen.queryByRole("listbox")).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(combo()));

    await userEvent.keyboard("{Enter}");
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Search server types" }), { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(combo()));
    await userEvent.keyboard("{ArrowUp}");
    fireEvent.keyDown(screen.getByRole("combobox", { name: "Search server types" }), { key: "Tab" });
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("finds a type by name or alias, groups server and file engines, and says when nothing matches", async () => {
    m.engines = ALL();
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(combo());
    await waitFor(() => expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL", "MySQL / MariaDB", "SQLite", "DuckDB"]));
    expect(within(screen.getByRole("listbox")).getByRole("group", { name: "On a server" })).toBeTruthy();
    expect(within(screen.getByRole("listbox")).getByRole("group", { name: "File on this computer" })).toBeTruthy();
    const search = screen.getByRole("combobox", { name: "Search server types" });
    await userEvent.type(search, "maria");
    expect(optionNames()).toEqual(["MySQL / MariaDB"]);
    await userEvent.clear(search);
    await userEvent.type(search, "postgres");
    expect(optionNames()).toEqual(["PostgreSQL"]);
    await userEvent.keyboard("{Enter}");
    expect(combo().textContent).toContain("PostgreSQL");
    await userEvent.click(combo());
    await userEvent.type(screen.getByRole("combobox", { name: "Search server types" }), "zzz");
    expect(screen.getByText("No server type matches “zzz”.")).toBeTruthy();
  });

  it("clicking toggles it; hovering highlights; an outside press or a resize closes it", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.click(combo());
    expect(combo().getAttribute("aria-expanded")).toBe("true");
    const opts = within(screen.getByRole("listbox")).getAllByRole("option");
    expect(opts[0].getAttribute("aria-selected")).toBe("true");
    await userEvent.hover(opts[1]);
    expect(screen.getByRole("combobox", { name: "Search server types" }).getAttribute("aria-activedescendant")).toBe(opts[1].id);
    await userEvent.click(combo());
    expect(screen.queryByRole("listbox")).toBeNull();
    await userEvent.click(combo());
    fireEvent.mouseDown(screen.getByRole("listbox"));
    expect(screen.getByRole("listbox")).toBeTruthy();
    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("listbox")).toBeNull();
    await userEvent.click(combo());
    fireEvent(window, new Event("resize"));
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("opens upward when there is no room below", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    vi.spyOn(combo(), "getBoundingClientRect").mockReturnValue({ left: 5, width: 100, top: window.innerHeight - 20, bottom: window.innerHeight - 4 } as DOMRect);
    await userEvent.click(combo());
    expect(screen.getByRole("listbox").parentElement!.style.bottom).not.toBe("");
  });
});

describe("ProfileEditor engines the sidecar exposes (system.engines)", () => {
  it("offers only the exposed engines, servers first", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("sqlite", true, ["file", "memory"]), info("mySql", false)];
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(combo());
    await waitFor(() => expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL", "SQLite"]));
  });

  it("falls back to SQL Server and PostgreSQL when the sidecar cannot say", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(combo());
    expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL"]);
  });

  it("an existing profile always shows its own engine, exposed or not", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("sqlite", false, ["file", "memory"])];
    useProfiles.setState({ profiles: [base("s1", "sqlite", { location: "memory", auth: "none", host: null, username: null })], groups: [], save: saveSpy as never });
    render(<ProfileEditor profileId="s1" onClose={() => {}} />);
    await userEvent.click(combo());
    await waitFor(() => expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL", "SQLite"]));
  });
});

describe.each<Engine>(["mssql", "pg"])("ProfileEditor editing an existing %s profile", (engine) => {
  beforeEach(() => useProfiles.setState({ profiles: [base("a", engine)] }));

  it("shows the profile and that its secret is saved, without showing it", () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByText("Edit profile — Name-a")).toBeTruthy();
    expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
    expect(screen.queryByLabelText("Password")).toBeNull();
    expect((env("Dev").getAttribute("aria-pressed") === "true")).toBe(true);
  });

  it("saves without touching the keyring when no new password was typed", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBeNull();
  });

  it("Change takes a new password; Keep saved goes back without one", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    expect(screen.getByText(/Leave it empty to keep the saved one/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Keep saved" }));
    expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText("Password"), "new");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("new");
  });

  it("Forget waits for Save: nothing is deleted at once, Undo keeps it, Save removes it", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    expect(m.secretDelete).not.toHaveBeenCalled();
    expect(screen.getByText("Forgotten when you save.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("");
  });

  it("Forget then Cancel asks, and discarding keeps the secret", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(onClose).toHaveBeenCalled();
    expect(saveSpy).not.toHaveBeenCalled();
    expect(m.secretDelete).not.toHaveBeenCalled();
  });

  it("switching between a password and an access token never reuses the saved one", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(screen.getByLabelText("Sign in"), "entraToken");
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    expect(screen.getByText(/The saved password is not used as an access token/)).toBeTruthy();
    m.rpc.mockResolvedValue({ ok: false, error: "no token", elapsedMs: 1, notes: [] });
    await userEvent.click(testBtn());
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ secret: "" })));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("");
  });

  it("Change, type, then clear keeps the saved secret, as the hint says", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText("Password"), "x");
    await userEvent.clear(screen.getByLabelText("Password"));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBeNull();
  });

  it("a typed password never appears in the page's HTML", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText("Password"), "Zq1-very-secret");
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("Zq1-very-secret");
    expect(document.body.innerHTML).not.toContain("Zq1-very-secret");
  });

  it("with no secret in the keyring it asks for the password instead", async () => {
    m.secretExists.mockResolvedValue(false);
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await waitFor(() => expect(screen.getByLabelText("Password")).toBeTruthy());
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
  });

  it("switching to integrated/Kerberos auth says the saved secret goes, and removes it on Save", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(screen.getByLabelText("Sign in"), "kerberos");
    expect(screen.getByText(/saved password is removed when you save/)).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("");
  });

  it("reminds to reconnect when the server is connected", async () => {
    useConnections.setState({ servers: { a: { profileId: "a", status: "connected", databases: [] } } as never, order: ["a"] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(toasts()).toContain("info:Saved. Reconnect to apply connection changes."));
  });

  it("reports a failed save and stays open", async () => {
    saveSpy.mockRejectedValueOnce(new Error("disk full"));
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(toasts()).toContain("error:disk full"));
    expect(onClose).not.toHaveBeenCalled();
    expect((saveBtn() as HTMLButtonElement).disabled).toBe(false);
  });

  it("Delete asks for confirmation and closes only when deleted", async () => {
    const onClose = vi.fn();
    m.confirmDeleteProfile.mockResolvedValueOnce(false);
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(m.confirmDeleteProfile).toHaveBeenCalledWith(expect.objectContaining({ id: "a" }));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });
});

describe.each<Engine>(["mssql", "pg"])("ProfileEditor test connection (%s)", (engine) => {
  const ok = { ok: true, serverVersion: "16.0", elapsedMs: 12.4, notes: ["tls ok"] };

  it("reports success with version, time and notes, and sends the typed secret", async () => {
    m.rpc.mockResolvedValue(ok);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(status().textContent).toContain("Not tested yet");
    await userEvent.type(screen.getByLabelText("Password"), "pw");
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 16.0 (12 ms) — tls ok"));
    expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ profileId: null, secret: "pw", sshSecret: null, profile: expect.objectContaining({ engine }) }));
    await userEvent.click(within(status()).getByLabelText("Dismiss"));
    expect(status().textContent).toContain("Not tested yet");
  });

  it("marks the result out of date when a connection setting changes, but not for the name", async () => {
    m.rpc.mockResolvedValue(ok);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected"));
    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "Renamed");
    expect(status().textContent).not.toContain("Settings changed since this test");
    await userEvent.type(hostBox(), "2");
    expect(status().textContent).toContain("Settings changed since this test");
  });

  it("a blank host (sent as the default) does not make a fresh result look out of date", async () => {
    m.rpc.mockResolvedValue(ok);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.clear(hostBox());
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected"));
    expect(status().textContent).not.toContain("Settings changed since this test");
  });

  it("reports the server's error, or 'Failed' when none is given", async () => {
    m.rpc.mockResolvedValueOnce({ ok: false, error: "login failed", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("login failed"));
    m.rpc.mockResolvedValueOnce({ ok: false, elapsedMs: 1, notes: [] });
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Failed"));
  });

  it("never shows an empty error (regression: blank red box)", async () => {
    m.rpc.mockResolvedValueOnce({ ok: false, error: "", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Failed"));
    m.rpc.mockRejectedValueOnce(new Error(""));
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Failed"));
  });

  it("reports a thrown error", async () => {
    m.rpc.mockRejectedValueOnce(new Error("sidecar gone"));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("sidecar gone"));
    expect(testBtn().hasAttribute("disabled")).toBe(false);
  });

  it("an existing profile tests with its saved secrets unless new ones were typed", async () => {
    useProfiles.setState({ profiles: [base("a", engine)] });
    m.rpc.mockResolvedValue(ok);
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    expect(m.rpc.mock.calls.find((c) => c[0] === "profile.test")![1]).toMatchObject({ profileId: "a", secret: null, sshSecret: null });
  });
});

describe("ProfileEditor SSH tunnel", () => {
  const enable = async () => {
    await openRow(/^SSH tunnel/);
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
  };
  const fillTunnel = async (host = "b", user = "ops") => {
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), host);
    await userEvent.type(screen.getByPlaceholderText("deploy"), user);
  };

  it("a tunnel needs its bastion and user: Test and Save say so and go to the field", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await enable();
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Enter the SSH bastion host.");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
    await waitFor(() => expect(document.activeElement).toBe(screen.getByPlaceholderText("bastion.example.com")));
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "b");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Enter the SSH user name.");
    await waitFor(() => expect(document.activeElement).toBe(screen.getByPlaceholderText("deploy")));
  });

  it("switching a saved tunnel between password and key file does not reuse the saved secret", async () => {
    useProfiles.setState({ profiles: [base("a", "pg", { ssh: { host: "b", port: 22, username: "u", auth: "password", keyPath: null } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(screen.getByDisplayValue("Password"), "privateKey");
    expect(screen.queryByText("Leave empty to keep the one saved in the OS keyring.")).toBeNull();
    // Test sends "" (never null, which the shell would fill from the saved password)
    m.rpc.mockResolvedValue({ ok: false, error: "x", elapsedMs: 1, notes: [] });
    await userEvent.click(testBtn());
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ sshSecret: "" })));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][2]).toBe("");
  });

  it("a saved tunnel with nothing in the keyring offers nothing to keep or forget", async () => {
    m.secretExists.mockImplementation(async (id: unknown) => id !== "a:ssh");
    useProfiles.setState({ profiles: [base("a", "pg", { ssh: { host: "b", port: 22, username: "u", auth: "privateKey", keyPath: "/k" } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await waitFor(() => expect(screen.getByText("Only if the key is protected.")).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Forget saved" })).toBeNull();
  });

  it.each<Engine>(["mssql", "pg"])("%s: enabling the tunnel shows the bastion fields and a summary; disabling removes it", async (engine) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(screen.getByRole("button", { name: /^SSH tunnel/ }).textContent).toContain("Off");
    await enable();
    expect(screen.getByRole("button", { name: /^SSH tunnel/ }).textContent).toContain("Through bastion");
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "jump.example.com");
    expect(screen.getByRole("button", { name: /^SSH tunnel/ }).textContent).toContain("Through jump.example.com");
    await userEvent.type(screen.getByPlaceholderText("deploy"), "ops");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k");
    const port = screen.getByLabelText("SSH port") as HTMLInputElement;
    await userEvent.clear(port);
    // An emptied number is a draft: flagged, never stored (it used to snap back to 22 or save as 0).
    expect(port.getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByRole("button", { name: /^SSH tunnel/ }).textContent).toContain("Through jump.example.com");
    await userEvent.type(port, "2200");
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    expect(screen.queryByPlaceholderText("bastion.example.com")).toBeNull();
  });

  it("password sign-in hides the key file; the unsupported agent is not offered", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await enable();
    const sel = screen.getByDisplayValue("Key file") as HTMLSelectElement;
    expect([...sel.options].map((o) => o.value)).toEqual(["privateKey", "password"]);
    expect(screen.getByText("Passphrase", { selector: "label" })).toBeTruthy();
    await userEvent.selectOptions(sel, "password");
    expect(screen.queryByPlaceholderText("/home/me/.ssh/id_ed25519")).toBeNull();
    expect(screen.getAllByText("Password", { selector: "label" })).toHaveLength(2); // the database's and the tunnel's
  });

  it("a profile that already uses the agent keeps it listed, with the warning", () => {
    useProfiles.setState({ profiles: [base("a", "pg", { ssh: { host: "b", port: 22, username: "u", auth: "agent", keyPath: null } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByText(/SSH agent sign-in is not supported yet/)).toBeTruthy();
    expect(screen.queryByText("Passphrase", { selector: "label" })).toBeNull();
  });

  it("Browse is offered only inside Tauri and fills in the chosen key path", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await enable();
    expect(screen.queryByText("Browse…")).toBeNull();
    cleanup();
    m.tauri = true;
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await enable();
    await userEvent.click(screen.getByText("Browse…"));
    await waitFor(() => expect((screen.getByPlaceholderText("/home/me/.ssh/id_ed25519") as HTMLInputElement).value).toBe("/keys/id_rsa"));
    m.openDialog.mockResolvedValueOnce(null);
    await userEvent.click(screen.getByText("Browse…"));
    await waitFor(() => expect(m.openDialog).toHaveBeenCalledTimes(2));
    expect((screen.getByPlaceholderText("/home/me/.ssh/id_ed25519") as HTMLInputElement).value).toBe("/keys/id_rsa");
  });

  it("saves the tunnel and its typed secret; the profile carries the ssh settings", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await enable();
    await fillTunnel();
    const secret = (screen.getByLabelText("Passphrase") as HTMLInputElement);
    await userEvent.type(secret, "phrase");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, , ssh] = saveSpy.mock.calls[0] as unknown as [Profile, unknown, string | null];
    expect(p.ssh).toMatchObject({ host: "b", port: 22, auth: "privateKey" });
    expect(ssh).toBe("phrase");
  });

  it("an existing tunnel opens unfolded and keeps its saved secret; Forget saved waits for Save", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { ssh: { host: "b", port: 22, username: "u", auth: "password", keyPath: null } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByRole("button", { name: /^SSH tunnel/ }).getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByText("Leave empty to keep the one saved in the OS keyring.")).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][2]).toBeNull();
    saveSpy.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Forget saved" }));
    expect(m.secretDelete).not.toHaveBeenCalled();
    expect(screen.getByText("The saved one is forgotten when you save.")).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][2]).toBe("");
  });

  it("the test says the tunnel was used, and an untrusted host key is shown and trusted on approval", async () => {
    const hostKey = { host: "b", port: 22, algorithm: "ssh-ed25519", fingerprint: "SHA256:x" };
    m.rpc.mockImplementation(async (method: unknown) => {
      if (method === "profile.test") {
        return m.rpc.mock.calls.filter((c) => c[0] === "profile.test").length === 1
          ? { ok: false, elapsedMs: 0, notes: [], untrustedHostKey: hostKey }
          : { ok: true, serverVersion: "15", elapsedMs: 3, notes: [] };
      }
      return undefined;
    });
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Password"), "pw");
    await enable();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "hostKey", hostKey }));
    (useUi.getState().dialogs.at(-1) as unknown as { resolve: (v: boolean) => void }).resolve(true);
    await waitFor(() => expect(status().textContent).toContain("through the SSH tunnel"));
    expect(m.rpc).toHaveBeenCalledWith("ssh.trustHostKey", hostKey);
    expect(m.rpc.mock.calls.filter((c) => c[0] === "profile.test")).toHaveLength(2);
  });

  it("declining an untrusted host key shows the failure and does not trust it", async () => {
    const hostKey = { host: "b", port: 22, algorithm: "ssh-ed25519", fingerprint: "SHA256:x" };
    m.rpc.mockResolvedValue({ ok: false, error: "host key not trusted", elapsedMs: 0, notes: [], untrustedHostKey: hostKey });
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await enable();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "hostKey" }));
    (useUi.getState().dialogs.at(-1) as unknown as { resolve: (v: boolean) => void }).resolve(false);
    await waitFor(() => expect(status().textContent).toContain("host key not trusted"));
    expect(m.rpc).not.toHaveBeenCalledWith("ssh.trustHostKey", expect.anything());
  });
});

describe.each<[Engine, string]>([
  ["mssql", "Server=localhost,1433;Database=master;User ID=sa;Password=x"],
  ["pg", "postgres://u:p@host/db"],
  ["mySql", "Server=localhost;Port=3306;User ID=root;Password=x"],
])("ProfileEditor connection string (%s)", (engine, cs) => {
  const result = (over: object = {}) => ({
    profile: base("", engine, { host: "parsed-host", databaseDefault: "parsed-db", name: "", auth: engine === "mssql" ? "sql" : "password" }), secret: "from-string", unknown: ["Foo"], notApplied: ["Bar"], ...over,
  });
  const box = () => screen.getByLabelText("Connection string") as HTMLInputElement;
  const openBox = () => userEvent.click(screen.getByRole("button", { name: "Paste a connection string…" }));
  const label = { mssql: "Microsoft SQL Server", pg: "PostgreSQL", mySql: "MySQL / MariaDB" }[engine as "mssql" | "pg" | "mySql"];
  beforeEach(() => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
  });

  it("a paste fills the fields at once and names the server type; the string is masked, then cleared", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    expect(box().type).toBe("password");
    paste(box(), cs);
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.parseConnectionString", expect.objectContaining({ connectionString: cs })));
    await waitFor(() => expect(combo().textContent).toContain(label));
    expect(screen.queryByLabelText("Connection string")).toBeNull();
    expect(hostBox().value).toBe("parsed-host");
    expect(nameBox().value).toBe("parsed-host / parsed-db");
    expect(screen.getByText(/Read as/).textContent).toContain(label);
    expect(screen.getByText("Not recognised: Foo")).toBeTruthy();
    expect(screen.getByText(/Not applied .*: Bar/)).toBeTruthy();
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("from-string");
    // the parse report stays until Save
    expect(screen.queryByLabelText("Dismiss", { selector: "[aria-live] button" })).toBeNull();
  });

  it("Enter or Fill fields reads typed text; a typed name and the previous secret stay when the string has none", async () => {
    m.rpc.mockResolvedValue(result({ secret: null, unknown: [], notApplied: [] }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "Mine");
    await userEvent.type(screen.getByLabelText("Password"), "typed");
    await openBox();
    await userEvent.type(box(), "x{Enter}");
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.parseConnectionString", expect.objectContaining({ connectionString: "x" })));
    await waitFor(() => expect(nameBox().value).toBe("Mine"));
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("typed");
    expect(screen.queryByText(/Not recognised/)).toBeNull();
    expect(saveSpy).not.toHaveBeenCalled();
  });

  it("warns about a TLS setting the string names that is weaker than the default (M3)", async () => {
    const note = "SslMode=None: the connection is not encrypted, so the password and every result cross the network in clear text.";
    m.rpc.mockResolvedValue(result({ unknown: [], notApplied: [], notes: [note] }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(screen.getByText(note)).toBeTruthy());
    expect(screen.getByText(note).className).toContain("text-warn");
  });

  it("a parse error is reported and the box stays with the text", async () => {
    m.rpc.mockRejectedValue(new Error("bad keyword"));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    paste(box(), "garbage");
    await waitFor(() => expect(status().textContent).toContain("bad keyword"));
    expect(box().value).toBe("garbage");
  });

  it("Test and Save read text still in the box first", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? { ok: true, serverVersion: "9", elapsedMs: 1, notes: [] } : result({ unknown: [], notApplied: [] })));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 9"));
    expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ secret: "from-string", profile: expect.objectContaining({ host: "parsed-host" }) }));
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret] = saveSpy.mock.calls[0] as unknown as [Profile, string];
    expect(p.host).toBe("parsed-host");
    expect(p.name).toBe("parsed-host / parsed-db");
    expect(secret).toBe("from-string");
  });

  it("a string pasted into Host goes to the masked box and waits to be read", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    paste(hostBox(), cs);
    expect(hostBox().value).toBe("");
    expect(screen.getByText(/That looks like a connection string/)).toBeTruthy();
    expect(box().value).toBe(cs);
    expect(m.rpc).not.toHaveBeenCalledWith("profile.parseConnectionString", expect.anything());
    await userEvent.click(screen.getByRole("button", { name: "Fill fields" }));
    await waitFor(() => expect(hostBox().value).toBe("parsed-host"));
  });

  it("the string never appears in the page's HTML while it is in the box", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    fireEvent.change(box(), { target: { value: `${cs};Application Name=Zq1-marker` } });
    expect(box().value).toContain("Zq1-marker");
    expect(document.body.innerHTML).not.toContain("Zq1-marker");
  });

  it("Cancel closes the box and clears its text", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(within(box().parentElement!).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Connection string")).toBeNull();
    await openBox();
    expect(box().value).toBe("");
  });
});

describe("ProfileEditor timeouts and closing", () => {
  it("connect and command timeouts are editable numbers", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    expect(screen.getByRole("button", { name: /^Timeouts/ }).textContent).toContain("Connect 15 s · queries no limit");
    await openRow(/^Timeouts/);
    fireEvent.change(screen.getByRole("spinbutton", { name: /^Connect timeout/ }), { target: { value: "30" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: /^Command timeout/ }), { target: { value: "120" } });
    expect(screen.getByRole("button", { name: /^Timeouts/ }).textContent).toContain("Connect 30 s · queries 120 s");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ timeoutS: 30, commandTimeoutS: 120 });
  });

  it("a new dialog starts at the server type, and the discard question takes focus", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await waitFor(() => expect(document.activeElement).toBe(combo()));
    cleanup();
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv("Dev");
    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("button", { name: "Keep editing" })));
  });

  it("Cancel on an untouched dialog closes at once", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="pg" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalled();
    expect(saveSpy).not.toHaveBeenCalled();
  });

  it("Cancel, Escape and a click outside ask before typed changes are dropped", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="pg" onClose={onClose} />);
    await userEvent.type(hostBox(), "x");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText("Discard your changes to this profile?")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Keep editing" }));
    expect(hostBox().value).toBe("x");
    fireEvent.keyDown(hostBox(), { key: "Escape" });
    expect(screen.getByText("Discard your changes to this profile?")).toBeTruthy();
    fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
    expect(screen.queryByText("Discard your changes to this profile?")).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.mouseDown(document.querySelector(".fixed.inset-0")!);
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

describe.each<[Engine, string]>([["sqlite", "SQLite"], ["duckDb", "DuckDB"]])("ProfileEditor local engine (%s)", (engine, label) => {
  beforeEach(() => {
    m.engines = ALL();
  });

  it("asks where the database is instead of a server, sign-in, TLS, SSH or a connection string", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    expect(combo().textContent).toContain("File on this computer");
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    expect(screen.getByRole("button", { name: "Use an in-memory database instead" })).toBeTruthy();
    expect(screen.queryByLabelText("Host")).toBeNull();
    expect(screen.queryByText("Sign in")).toBeNull();
    expect(screen.queryByRole("button", { name: /^Encryption/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /^SSH tunnel/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Paste a connection string…" })).toBeNull();
    expect(screen.getByRole("button", { name: /^Timeout/ }).textContent).toContain("queries no limit");
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
  });

  it("picks the file through the shell, names the profile after it, and asks the shell before allowing writes", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(m.pick).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId("picked-file").textContent).toBe("sales.db");
    expect(screen.getByText("Read-only")).toBeTruthy();
    expect(nameBox().value).toBe("sales.db");
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    expect(m.allowWrites).toHaveBeenCalledWith("g1");
    expect(screen.getByText("Writes allowed")).toBeTruthy();
    m.allowWrites.mockResolvedValueOnce(false);
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    expect(screen.getByText("Read-only")).toBeTruthy();
    await pickEnv("Local");
    await userEvent.click(saveBtn());
    expect(saveSpy).toHaveBeenCalledWith(
      expect.objectContaining({ engine, name: "sales.db", environment: "local", location: "file", file: { grantId: "g1", displayName: "sales.db" }, auth: "none", host: null, username: null }),
      null, "",
    );
  });

  it("refuses to save or test a file profile without a file", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await pickEnv("Local");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    const pick = screen.getByRole("button", { name: "Pick file…" });
    // The button is described by the picked file's line and, after Save, by the message.
    expect(pick.getAttribute("aria-describedby")!.split(" ").map((id) => document.getElementById(id)!.textContent)).toEqual(["No file picked", "Pick the database file first."]);
    expect(screen.getByRole("alert").textContent).toContain("Pick the database file first.");
    // the name comes from the file: no second message for it
    expect(screen.getByRole("alert").textContent).not.toContain("Give the profile a name.");
    expect(nameBox().getAttribute("aria-invalid")).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(pick));
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Pick the database file first.");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
  });

  it("leaving the file for in-memory and back drops the old access badge", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    expect(screen.getByText("Writes allowed")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Use an in-memory database instead" }));
    await userEvent.click(screen.getByRole("button", { name: "Use a database file instead" }));
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    expect(screen.queryByText("Writes allowed")).toBeNull();
  });

  it("Undo after a switch to the other file engine brings back the file and its access (regression)", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    expect(screen.getByText("Writes allowed")).toBeTruthy();
    await userEvent.click(combo());
    await userEvent.click(within(screen.getByRole("listbox")).getByText(label === "SQLite" ? "DuckDB" : "SQLite"));
    expect(screen.queryByText("Writes allowed")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    expect(combo().textContent).toContain(label);
    expect(screen.getByText("Writes allowed")).toBeTruthy();
  });

  it("an in-memory database needs no file and says each tab gets its own", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await userEvent.click(screen.getByRole("button", { name: "Use an in-memory database instead" }));
    expect(screen.getByText(/Each tab gets its own empty database/)).toBeTruthy();
    expect(nameBox().value).toBe(`${label} (in-memory)`);
    await pickEnv("Local");
    await userEvent.click(saveBtn());
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ engine, location: "memory", file: null }), null, "");
  });

  it("a cancelled pick changes nothing; a failed one says why", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    m.pick.mockResolvedValueOnce(null);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    m.pick.mockRejectedValueOnce(new Error("The file is gone."));
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(toasts()).toContain("error:The file is gone.");
  });

  it("switching back to a server engine restores its server fields and drops the file", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await chooseEngine("PostgreSQL");
    expect(hostBox().value).toBe("");
    expect(hostBox().placeholder).toBe("localhost");
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("postgres");
    await pickEnv();
    await userEvent.click(saveBtn());
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ engine: "pg", location: null, file: null, host: null, auth: "password", databaseDefault: "postgres" }), null, "");
  });

  it("a duplicate of a file profile drops the file grant (one grant binds one profile)", () => {
    useProfiles.setState({
      profiles: [base("f1", engine, { name: "Sales file", location: "file", file: { grantId: "g9", displayName: "sales.db" }, auth: "none", host: null, username: null })],
      groups: [], save: saveSpy as never,
    });
    render(<ProfileEditor copyOf="f1" onClose={() => {}} />);
    expect(screen.getByText("New connection")).toBeTruthy();
    expect(nameBox().value).toBe("Sales file (copy)");
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
  });
});

// L25: Host and Port are labelled fields; a port outside 1–65535 is shown at its field (aria-invalid, aria-describedby)
// and blocks Save and Test, for every server engine; the sidecar's refusal of a field shows there too. W9: an empty host
// is the engine's default (localhost), as the sidecar takes it.
describe.each<[Engine, string]>([["mssql", "1433"], ["pg", "5432"], ["mySql", "3306"]])("ProfileEditor address fields (%s)", (engine, dflt) => {
  const message = (el: HTMLElement) => {
    const ids = (el.getAttribute("aria-describedby") ?? "").split(" ").filter(Boolean);
    return ids.map((id) => document.getElementById(id)?.textContent).join(" | ");
  };
  beforeEach(() => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
  });

  it("labels Host and Port; Host starts empty with localhost as its placeholder (regression)", () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(hostBox().value).toBe("");
    expect(hostBox().placeholder).toBe("localhost");
    expect(portBox().placeholder).toBe(dflt);
    expect(portBox().getAttribute("aria-invalid")).toBeNull();
  });

  it.each([["0"], ["70000"], ["-5"], ["1.5"]])("an out-of-range port (%s) shows its message at the field and blocks Save and Test", async (bad) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.type(portBox(), bad);
    await userEvent.tab();
    expect(portBox().getAttribute("aria-invalid")).toBe("true");
    expect(message(portBox())).toBe(`Enter a port from 1 to 65535, or leave it empty for ${dflt}.`);
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(portBox()));
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Enter a port from 1 to 65535");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
    await userEvent.clear(portBox());
    await userEvent.type(portBox(), "65535");
    expect(portBox().getAttribute("aria-invalid")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ port: 65535 }), null, ""));
  });

  it("an empty host is the engine's default, as the sidecar takes it: Save sends no host (W9)", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.clear(hostBox());
    await userEvent.type(hostBox(), "   ");
    expect(hostBox().getAttribute("aria-required")).toBeNull();
    expect(hostBox().placeholder).toBe("localhost");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ host: null }), null, ""));
    expect(hostBox().getAttribute("aria-invalid")).toBeNull();
  });

  it("a host with a control character is refused at the field", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    fireEvent.change(hostBox(), { target: { value: "db\u0007host" } });
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(hostBox()));
    expect(message(hostBox())).toContain("without line breaks or control characters");
  });

  it("shows the sidecar's refusal of a field at that field until the field changes", async () => {
    saveSpy.mockRejectedValueOnce(Object.assign(new Error("The port must be a whole number from 1 to 65535."), { appCode: "invalidProfile", data: { field: "port" } }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(portBox().getAttribute("aria-invalid")).toBe("true"));
    expect(message(portBox())).toBe("The port must be a whole number from 1 to 65535.");
    expect(toasts()).toEqual([]);
    await userEvent.type(portBox(), "1");
    expect(portBox().getAttribute("aria-invalid")).toBeNull();
  });
});

describe("transportSummary: the one source of every encryption summary", () => {
  const s = (engine: Engine, tlsMode: TlsMode, trustCert = false) => transportSummary({ engine, tlsMode, trustCert, location: null });
  it.each<[Engine, TlsMode, boolean, string, "ok" | "warn" | "muted", "plain" | "unchecked" | null]>([
    ["mssql", "default", false, "Encrypted · certificate checked", "ok", null],
    ["mssql", "strict", false, "Encrypted · certificate checked", "ok", null],
    ["mssql", "mandatory", false, "Encrypted · certificate checked", "ok", null],
    ["mssql", "mandatory", true, "Encrypted · certificate not checked", "warn", "unchecked"],
    ["mssql", "default", true, "Encrypted · certificate not checked", "warn", "unchecked"],
    ["mssql", "strict", true, "Encrypted · certificate checked", "ok", null], // SqlClient checks under Strict regardless
    ["mssql", "optional", false, "Not encrypted", "warn", "plain"],
    ["pg", "default", false, "Encrypted (Require) · certificate not checked", "muted", "unchecked"],
    ["pg", "default", true, "Encrypted (Require) · certificate not checked", "muted", "unchecked"], // trustCert is SQL Server's only
    ["pg", "verifyCa", false, "Encrypted · certificate checked", "ok", null],
    ["pg", "verifyFull", false, "Encrypted · certificate checked", "ok", null],
    ["pg", "prefer", false, "May fall back to plain text", "warn", "plain"],
    ["pg", "disable", false, "Not encrypted", "warn", "plain"],
    ["mySql", "default", false, "Encrypted · certificate checked", "ok", null],
    ["mySql", "verifyCa", false, "Encrypted · certificate checked", "ok", null],
    ["mySql", "require", false, "Encrypted · certificate not checked", "warn", "unchecked"],
    ["mySql", "prefer", false, "May fall back to plain text", "warn", "plain"],
    ["mySql", "disable", false, "Not encrypted", "warn", "plain"],
  ])("%s %s (trust %s): %s", (engine, mode, trust, text, tone, risk) => {
    expect(s(engine, mode, trust)).toEqual({ text, tone, risk });
  });

  it.each<Engine>(["sqlite", "duckDb"])("%s: a database on this computer has no transport", (engine) => {
    expect(transportSummary({ engine, tlsMode: "default", trustCert: false, location: "file" })).toMatchObject({ tone: "muted", risk: null });
  });
});

describe("profile editor helpers", () => {
  it("suggestedName: host / database, the host alone, the file, or the in-memory engine", () => {
    expect(suggestedName(base("", "pg", { host: "db1", databaseDefault: "sales" }))).toBe("db1 / sales");
    expect(suggestedName(base("", "mssql", { host: "  ", databaseDefault: null }))).toBe("localhost");
    expect(suggestedName(base("", "sqlite", { location: "file", file: { grantId: "g", displayName: "a.db" } }))).toBe("a.db");
    expect(suggestedName(base("", "sqlite", { location: "file", file: null }))).toBe("");
    expect(suggestedName(base("", "duckDb", { location: "memory" }))).toBe("DuckDB (in-memory)");
  });

  it("looksLikeProduction only finds whole words that mean Production", () => {
    expect(looksLikeProduction("db-prod-01.corp")).toBe("prod");
    expect(looksLikeProduction("localhost", "billing_prd")).toBe("prd");
    expect(looksLikeProduction("LIVE.example.com")).toBe("live");
    expect(looksLikeProduction("prod2")).toBe("prod2");
    expect(looksLikeProduction("product-db", "reproduce")).toBeNull();
    expect(looksLikeProduction("localhost", null, undefined)).toBeNull();
  });

  it("looksLikeConnectionString: URIs and keyword lists, not host names", () => {
    expect(looksLikeConnectionString("postgres://u@h/db")).toBe(true);
    expect(looksLikeConnectionString("Server=x;Database=y")).toBe(true);
    expect(looksLikeConnectionString("db1.corp.local")).toBe(false);
    expect(looksLikeConnectionString("host\\instance")).toBe(false);
  });

  it("splitHostPort: host:port, never for SQL Server or an IPv6 literal", () => {
    expect(splitHostPort("db1:5433", "pg")).toEqual({ host: "db1", port: 5433 });
    expect(splitHostPort(" db1:3307 ", "mySql")).toEqual({ host: "db1", port: 3307 });
    expect(splitHostPort("db1:1433", "mssql")).toBeNull();
    expect(splitHostPort("fd00::5", "pg")).toBeNull();
    expect(splitHostPort("db1", "pg")).toBeNull();
  });
});

describe("withDefaultHost (W9)", () => {
  it("sends a blank host of a server profile as none; a file engine's profile has no host and is left as it is", () => {
    expect(withDefaultHost({ engine: "pg", host: "  " } as Profile).host).toBeNull();
    expect(withDefaultHost({ engine: "mssql", host: "" } as Profile).host).toBeNull();
    expect(withDefaultHost({ engine: "mySql", host: "db" } as Profile).host).toBe("db");
    for (const engine of ["sqlite", "duckDb"] as Engine[]) {
      const local = { engine, host: "", location: "file", file: "/a.db", name: "x" } as unknown as Profile;
      expect(withDefaultHost(local)).toBe(local);
      expect(profileFieldErrors(local)).toEqual({});
      expect(profileFieldErrors({ ...local, file: null } as unknown as Profile).file).toBe("Pick the database file first.");
    }
    expect(profileFieldErrors({ engine: "pg", host: null, name: "x" } as unknown as Profile)).toEqual({});
  });
});

describe("ProfileEditor redesign regressions (2026-11-14)", () => {
  it("an emptied connect timeout is never saved as 0 (no connect limit); the last valid value is kept", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv("Dev");
    await userEvent.click(screen.getByRole("button", { name: /^Timeouts/ }));
    const box = screen.getByLabelText("Connect timeout") as HTMLInputElement;
    await userEvent.clear(box);
    expect(box.getAttribute("aria-invalid")).toBe("true");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const saved = saveSpy.mock.calls[0][0] as Profile;
    expect(saved.timeoutS).toBe(15);
    expect(saved.commandTimeoutS).toBe(0);
  });

  it("arrow keys move focus between the environments but never choose one (no Prod by accident)", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    env("Local").focus();
    await userEvent.keyboard("{ArrowLeft}{End}");
    expect(document.activeElement).toBe(env("Prod"));
    expect(env("Prod").getAttribute("aria-pressed")).toBe("false");
    expect(screen.queryByText(/tabs and the status bar are painted/)).toBeNull();
    await userEvent.keyboard("{Enter}");
    expect(env("Prod").getAttribute("aria-pressed")).toBe("true");
  });

  it("a row's hint describes its field but is not part of the field's name", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /^SSH tunnel/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    const bastion = screen.getByRole("textbox", { name: "Bastion" });
    expect(document.getElementById(bastion.getAttribute("aria-describedby")!)!.textContent).toContain("Host above is the address as the bastion sees it");
  });

  it("a folded row's button is named by its title only; its state is the description, and it controls a panel only while open", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    const row = screen.getByRole("button", { name: "Timeouts" });
    expect(row.getAttribute("aria-controls")).toBeNull();
    expect(document.getElementById(row.getAttribute("aria-describedby")!)!.textContent).toBe("Connect 15 s · queries no limit");
    await userEvent.click(row);
    expect(document.getElementById(row.getAttribute("aria-controls")!)).not.toBeNull();
  });

  it("the footer keeps one status region: what Save waits for replaces it, and a test result shows there", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect(status().textContent).toBe("Not tested yet. You can save without testing.");
    await userEvent.click(saveBtn());
    expect(screen.getByRole("alert").textContent).toContain("1 thing needs attention: Choose the environment");
    // Said in the footer only: the alert sits inside the footer's status line, not above the form.
    expect(screen.getByRole("alert").closest("[role=status]")).not.toBeNull();
  });
});

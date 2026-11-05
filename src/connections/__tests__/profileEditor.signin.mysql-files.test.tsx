// @vitest-environment jsdom
// Sign-in matrix (UI half): MySQL / MariaDB's one sign-in, and SQLite / DuckDB, which sign in with a shell file grant
// (read-only, then "Allow writes…") or an in-memory database.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
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

import { ProfileEditor } from "../ProfileEditor";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const info = (engine: Engine, exposed: boolean, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed, defaultAuth: "none", authKinds: [], locations,
});
const ALL = (): EngineInfo[] => [info("mssql", true), info("pg", true), info("sqlite", true, ["file", "memory"]), info("mySql", true), info("duckDb", true, ["file", "memory"])];

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const toasts = () => useUi.getState().toasts.map((t) => `${t.kind}:${t.text}`);
const combo = () => screen.getByRole("combobox", { name: "Server type" });
const testBtn = () => screen.getByRole("button", { name: "Test connection" });
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const status = () => screen.getByRole("status");
const env = (name: string) => screen.getByRole("radio", { name: name === "Prod" ? "Prod (Production)" : name }) as HTMLInputElement;
const pickEnv = (name = "Dev") => userEvent.click(env(name));
const optionNames = () => within(screen.getByRole("listbox", { name: "Server type" })).getAllByRole("option").map((o) => o.textContent);
const chooseEngine = async (label: string) => {
  await userEvent.click(combo());
  await waitFor(() => expect(optionNames()).toContain(label));
  await userEvent.click(screen.getByRole("option", { name: label }));
};
const savedCall = () => saveSpy.mock.calls[0] as unknown as [Profile, string | null, string | null];
const testCall = () => m.rpc.mock.calls.find((c) => c[0] === "profile.test")![1] as { profileId: string | null; profile: Profile; secret: string | null; sshSecret: string | null };
const ok = { ok: true, serverVersion: "8.4.0", elapsedMs: 3, notes: [] };

const mysql = (over: Partial<Profile> = {}): Profile => ({
  id: "a", name: "Shop", engine: "mySql", host: "db.local", port: null, auth: "password", username: "app", databaseDefault: "shop",
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0, ...over,
});
const fileProfile = (engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id: "f", name: "sales.db", engine, host: null, port: null, auth: "none", username: null, databaseDefault: null, location: "file", file: { grantId: "g1", displayName: "sales.db" },
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0, ...over,
});

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  Object.values(m).forEach((f) => typeof f === "function" && (f as { mockClear?: () => void }).mockClear?.());
  m.tauri = false;
  m.engines = ALL();
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  m.secretExists.mockResolvedValue(true);
  m.pick.mockResolvedValue({ grantId: "g1", name: "sales.db", size: 10 });
  m.allowWrites.mockResolvedValue(true);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

// ================================================================================================ MySQL / MariaDB

describe("sign-in matrix: MySQL / MariaDB", () => {
  it("chosen from the picker: the one method is static text, user root, port 3306", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine("MySQL / MariaDB");
    expect(screen.queryByRole("combobox", { name: "Sign in" })).toBeNull();
    const method = document.querySelector('[id$="-auth"]')!;
    expect(method.tagName).toBe("SPAN");
    expect(method.textContent).toBe("Password");
    // The "Sign in" label still names it.
    expect(screen.getByText("Sign in").getAttribute("for")).toBe(method.id);
    expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("root");
    expect((screen.getByLabelText("Port") as HTMLInputElement).placeholder).toBe("3306");
    expect((screen.getByLabelText("Port") as HTMLInputElement).value).toBe("");
    expect(screen.getByLabelText("Password")).toBeTruthy();
    expect(screen.getByLabelText("Remember password")).toBeTruthy();
    expect(combo().textContent).toContain("default port 3306");
  });

  it("switched to from SQL Server with Windows sign-in: back to the password sign-in, with user and password fields", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.selectOptions(screen.getByLabelText("Sign in"), "windows");
    expect(screen.queryByLabelText("Password")).toBeNull();
    await chooseEngine("MySQL / MariaDB");
    expect(document.querySelector('[id$="-auth"]')!.textContent).toBe("Password");
    expect(screen.getByLabelText("User name")).toBeTruthy();
    expect(screen.getByLabelText("Password")).toBeTruthy();
    expect(screen.getByText(/sign-in is Password/)).toBeTruthy();
    await pickEnv();
    await userEvent.type(screen.getByLabelText("Password"), "pw");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(savedCall()[0]).toMatchObject({ engine: "mySql", auth: "password" });
    expect(savedCall()[1]).toBe("pw");
  });

  it("new profile: Test sends the typed password and the profile as the sidecar builds it", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? ok : undefined));
    render(<ProfileEditor preset="mySql" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Password"), "pw");
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 8.4.0"));
    const t = testCall();
    expect(t.profileId).toBeNull();
    expect(t.secret).toBe("pw");
    expect(t.sshSecret).toBeNull();
    expect(t.profile).toMatchObject({ engine: "mySql", auth: "password", username: "root", host: "localhost", port: null, databaseDefault: null, tlsMode: "default" });
    expect(t.profile.ssh ?? null).toBeNull();
    expect(t.profile.location ?? null).toBeNull();
    expect(t.profile.file ?? null).toBeNull();
  });

  it("new profile: Save gives the typed password to the keyring path and clears the SSH entry", async () => {
    render(<ProfileEditor preset="mySql" onClose={() => {}} />);
    await userEvent.clear(screen.getByLabelText("User name"));
    await userEvent.type(screen.getByLabelText("User name"), "app");
    await userEvent.type(screen.getByLabelText("Password"), "s3cret");
    await pickEnv("Test");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret, ssh] = savedCall();
    expect(p).toMatchObject({ id: "", engine: "mySql", auth: "password", username: "app", host: "localhost", port: null, environment: "test" });
    expect(secret).toBe("s3cret");
    expect(ssh).toBe("");
  });

  it("new profile without a password typed: Save leaves the keyring alone (null), Test sends an empty password", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? ok : undefined));
    render(<ProfileEditor preset="mySql" onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    expect(testCall().secret).toBe("");
    await pickEnv();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(savedCall()[1]).toBeNull();
  });

  describe("an existing profile", () => {
    beforeEach(() => useProfiles.setState({ profiles: [mysql()] }));

    it("shows the saved password state and the static method", async () => {
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      expect(document.querySelector('[id$="-auth"]')!.textContent).toBe("Password");
      expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
      expect(screen.queryByLabelText("Password")).toBeNull();
      expect((screen.getByLabelText("User name") as HTMLInputElement).value).toBe("app");
    });

    it("Test uses the saved secret (profileId, secret null) until a new one is typed", async () => {
      m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? ok : undefined));
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(testBtn());
      await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
      expect(testCall()).toMatchObject({ profileId: "a", secret: null, sshSecret: null, profile: expect.objectContaining({ id: "a", engine: "mySql", auth: "password" }) });
      m.rpc.mockClear();
      await userEvent.click(screen.getByRole("button", { name: "Change" }));
      await userEvent.type(screen.getByLabelText("Password"), "new");
      await userEvent.click(testBtn());
      await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
      // A typed password goes with the call; the saved SSH secret is still the profile's (profileId kept for it).
      expect(testCall()).toMatchObject({ profileId: "a", secret: "new", sshSecret: null });
    });

    it("Save without a new password leaves the keyring alone; a changed one is saved; Forget removes it", async () => {
      const { unmount } = render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(saveBtn());
      await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
      expect(saveSpy.mock.calls[0].slice(1)).toEqual([null, ""]);
      unmount();

      const second = render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(screen.getByRole("button", { name: "Change" }));
      await userEvent.type(screen.getByLabelText("Password"), "new");
      await userEvent.click(saveBtn());
      await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(2));
      expect(saveSpy.mock.calls[1].slice(1)).toEqual(["new", ""]);
      second.unmount();

      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(screen.getByRole("button", { name: "Forget" }));
      expect(m.secretDelete).not.toHaveBeenCalled();
      await userEvent.click(saveBtn());
      await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(3));
      expect(saveSpy.mock.calls[2].slice(1)).toEqual(["", ""]);
    });

    it("with nothing in the keyring it asks for the password", async () => {
      m.secretExists.mockResolvedValue(false);
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await waitFor(() => expect(screen.getByLabelText("Password")).toBeTruthy());
      expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    });
  });
});

// ================================================================================================ SQLite / DuckDB

describe.each<[Engine, string, string]>([
  ["sqlite", "SQLite", "SQLite (in-memory)"],
  ["duckDb", "DuckDB", "DuckDB (in-memory)"],
])("sign-in matrix: %s (file grant or in-memory)", (engine, label, memoryName) => {
  it("chosen from the picker: a file location, nothing picked, and no server, sign-in, TLS or SSH fields", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseEngine(label);
    const where = screen.getByRole("radiogroup", { name: "Location" });
    expect(within(where).getAllByRole("radio").map((r) => r.parentElement!.textContent)).toEqual(["Database file", "In-memory database"]);
    expect((within(where).getByRole("radio", { name: "Database file" }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    expect(screen.queryByText("Read-only")).toBeNull();
    expect(screen.queryByRole("button", { name: "Allow writes…" })).toBeNull();
    for (const f of ["Host", "Port", "User name", "Password", "Sign in", "Remember password"]) expect(screen.queryByLabelText(f)).toBeNull();
    expect(screen.queryByRole("button", { name: /^Encryption/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /^SSH tunnel/ })).toBeNull();
    expect(combo().textContent).toContain("File on this computer");
  });

  it("the file is picked through the shell and starts read-only", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(m.pick).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.getByTestId("picked-file").textContent).toBe("sales.db"));
    expect(screen.getByText("Read-only")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Re-pick file…" })).toBeTruthy();
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("sales.db");
    expect(m.allowWrites).not.toHaveBeenCalled();
  });

  it("a cancelled pick changes nothing; a failed pick says why", async () => {
    m.pick.mockResolvedValueOnce(null);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    m.pick.mockRejectedValueOnce(new Error("The shell refused the file."));
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(toasts()).toContain("error:The shell refused the file."));
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
  });

  it("Allow writes… asks the shell for that grant; allowed shows Writes allowed, refused stays Read-only", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    m.allowWrites.mockResolvedValueOnce(false);
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    expect(m.allowWrites).toHaveBeenCalledWith("g1");
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    await waitFor(() => expect(screen.getByText("Writes allowed")).toBeTruthy());
    expect(screen.queryByText("Read-only")).toBeNull();
    // Re-picking makes a new grant, which starts read-only again.
    m.pick.mockResolvedValueOnce({ grantId: "g2", name: "other.db", size: 1 });
    await userEvent.click(screen.getByRole("button", { name: "Re-pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    expect(screen.getByTestId("picked-file").textContent).toBe("other.db");
  });

  it("a failed write request says why and keeps the access", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    m.allowWrites.mockRejectedValueOnce(new Error("Grant unknown."));
    await userEvent.click(screen.getByRole("button", { name: "Allow writes…" }));
    await waitFor(() => expect(toasts()).toContain("error:Grant unknown."));
    expect(screen.getByText("Read-only")).toBeTruthy();
  });

  it("Save without a picked file says so at the file and saves nothing", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Pick the database file first.");
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("button", { name: "Pick file…" })));
  });

  it("the environment is required for a file too", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Choose the environment");
    expect(screen.getByRole("radiogroup", { name: "Environment" }).getAttribute("aria-invalid")).toBe("true");
    await pickEnv("Prod");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(savedCall()[0].environment).toBe("prod");
  });

  it("the file save payload has no host, user or secret: secretArg null, sshArg empty", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    await pickEnv("Test");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret, ssh] = savedCall();
    expect(p).toMatchObject({ engine, location: "file", file: { grantId: "g1", displayName: "sales.db" }, auth: "none", host: null, port: null, username: null, environment: "test", name: "sales.db" });
    expect(p.ssh ?? null).toBeNull();
    expect(secret).toBeNull();
    expect(ssh).toBe("");
    expect(m.secretSet).not.toHaveBeenCalled();
  });

  it("Test of a file profile sends its grant id and no password", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? { ...ok, serverVersion: "3.46" } : undefined));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Pick the database file first.");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await waitFor(() => expect(screen.getByText("Read-only")).toBeTruthy());
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 3.46"));
    const t = testCall();
    expect(t.profile).toMatchObject({ engine, location: "file", file: { grantId: "g1" }, auth: "none", host: null, username: null });
    expect(t.secret || null).toBeNull();
  });

  it("in-memory: says what it is, needs no file, and saves no file, host, user or secret", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("radio", { name: "In-memory database" }));
    expect(screen.getByText(/Each tab gets its own empty database/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Pick file…" })).toBeNull();
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe(memoryName);
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled(); // the environment is still required
    await pickEnv("Local");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret, ssh] = savedCall();
    expect(p).toMatchObject({ engine, location: "memory", file: null, auth: "none", host: null, username: null, environment: "local", name: memoryName });
    expect(secret).toBeNull();
    expect(ssh).toBe("");
  });

  it("an existing file profile re-opens with its file, and saves with no password to keep", async () => {
    useProfiles.setState({ profiles: [fileProfile(engine)] });
    render(<ProfileEditor profileId="f" onClose={() => {}} />);
    expect(screen.getByTestId("picked-file").textContent).toBe("sales.db");
    expect(screen.getByRole("button", { name: "Allow writes…" })).toBeTruthy();
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret, ssh] = savedCall();
    expect(p).toMatchObject({ id: "f", location: "file", file: { grantId: "g1" }, host: null, username: null });
    // An existing profile whose sign-in needs no secret clears any keyring entry: "" (not null).
    expect(secret).toBe("");
    expect(ssh).toBe("");
  });
});

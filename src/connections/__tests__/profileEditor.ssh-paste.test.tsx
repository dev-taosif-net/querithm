// @vitest-environment jsdom
// SSH-tunnel sign-in and connection-string paste in the profile editor: a verification matrix over every server engine
// (SQL Server, PostgreSQL, MySQL / MariaDB). SQLite and DuckDB have neither (checked at the end).
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile, SshSettings } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  secretDelete: vi.fn(async (..._a: unknown[]) => {}),
  secretSet: vi.fn(async (..._a: unknown[]) => {}),
  secretExists: vi.fn(async (..._a: unknown[]) => true),
  openDialog: vi.fn(async (..._a: unknown[]): Promise<unknown> => "/keys/id_rsa"),
  tauri: false,
  engines: null as EngineInfo[] | null,
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, shell: { secretDelete: m.secretDelete, secretSet: m.secretSet, secretExists: m.secretExists }, RpcError: class extends Error {},
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.tauri; } }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: vi.fn(async () => true) }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: m.openDialog }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => m.engines }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: vi.fn(async () => null), requestWriteAccess: vi.fn(async () => true), needsRepick: () => false }));

import { ProfileEditor } from "../ProfileEditor";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

type ServerEngine = "mssql" | "pg" | "mySql";
const SERVER: ServerEngine[] = ["mssql", "pg", "mySql"];
const LABEL: Record<ServerEngine, string> = { mssql: "Microsoft SQL Server", pg: "PostgreSQL", mySql: "MySQL / MariaDB" };
const AUTH: Record<ServerEngine, Profile["auth"]> = { mssql: "sql", pg: "password", mySql: "password" };
const info = (engine: Engine, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed: true, defaultAuth: "none", authKinds: [], locations,
});

const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "db.local", port: null, auth: AUTH[engine as ServerEngine] ?? "none", username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0, ...over,
});
const tunnel = (over: Partial<SshSettings> = {}): SshSettings => ({ host: "jump.example.test", port: 22, username: "ops", auth: "privateKey", keyPath: "/home/me/.ssh/id_ed25519", ...over });

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const saved = (i = 0) => saveSpy.mock.calls[i] as unknown as [Profile, string | null, string | null];
const testCalls = () => m.rpc.mock.calls.filter((c) => c[0] === "profile.test").map((c) => c[1] as Record<string, unknown>);
const combo = () => screen.getByRole("combobox", { name: "Server type" });
const hostBox = () => screen.getByLabelText("Host") as HTMLInputElement;
const testBtn = () => screen.getByRole("button", { name: "Test connection" });
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const status = () => screen.getByRole("status");
const pickEnv = () => userEvent.click(screen.getByRole("button", { name: "Dev" }));
const sshRow = () => screen.getByRole("button", { name: /^SSH tunnel/ });
const enableSsh = async () => {
  if (sshRow().getAttribute("aria-expanded") !== "true") await userEvent.click(sshRow());
  await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
};
/** Test and Save need a bastion host and an SSH user. */
const fillTunnel = async (host = "jump.example.test", user = "ops") => {
  await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), host);
  await userEvent.type(screen.getByPlaceholderText("deploy"), user);
};
const sshAuth = () => screen.getByRole("combobox", { name: /Sign in with/ }) as HTMLSelectElement;
/** The SSH password / passphrase box (its Row label names it). */
const sshSecretBox = () => {
  // The tunnel's box comes after the database password, so it is the last "Password".
  return (screen.queryByLabelText("Passphrase") ?? screen.getAllByLabelText("Password").at(-1)!) as HTMLInputElement;
};
const paste = (el: HTMLElement, text: string) => fireEvent.paste(el, { clipboardData: { getData: () => text } });
const lastDialog = () => useUi.getState().dialogs.at(-1) as unknown as { kind: string; hostKey: unknown; resolve: (v: boolean) => void };

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  m.secretExists.mockReset();
  m.secretExists.mockResolvedValue(true);
  m.secretDelete.mockClear();
  m.secretSet.mockClear();
  m.openDialog.mockClear();
  m.tauri = false;
  m.engines = [info("mssql"), info("pg"), info("mySql"), info("sqlite", ["file", "memory"]), info("duckDb", ["file", "memory"])];
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

// ───────────────────────────── SSH tunnel ─────────────────────────────

describe.each(SERVER)("SSH tunnel sign-in (%s)", (engine) => {
  it("offers only Key file and Password for a new profile; the agent is never offered", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    expect([...sshAuth().options].map((o) => o.value)).toEqual(["privateKey", "password"]);
    expect(screen.queryByText(/SSH agent/)).toBeNull();
  });

  it("key file without a passphrase: saves the path, no SSH secret, and Test sends none", async () => {
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await enableSsh();
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "jump.example.test");
    await userEvent.type(screen.getByPlaceholderText("deploy"), "ops");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k/id");
    expect(screen.getByText("Only if the key is protected.")).toBeTruthy();
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("through the SSH tunnel"));
    expect(testCalls()[0]).toMatchObject({ profileId: null, sshSecret: null, profile: expect.objectContaining({ ssh: { host: "jump.example.test", port: 22, username: "ops", auth: "privateKey", keyPath: "/k/id" } }) });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0].ssh).toEqual({ host: "jump.example.test", port: 22, username: "ops", auth: "privateKey", keyPath: "/k/id" });
    expect(saved()[2]).toBeNull();
  });

  it("key file with a passphrase: the passphrase goes to Test as sshSecret and to Save's SSH keyring slot, never into the profile", async () => {
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await userEvent.type(screen.getByLabelText("Password"), "db-pw");
    await enableSsh();
    await fillTunnel("b");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k/id");
    const phrase = sshSecretBox();
    expect(phrase.type).toBe("password");
    await userEvent.type(phrase, "s3cret-phrase");
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCalls()).toHaveLength(1));
    expect(testCalls()[0]).toMatchObject({ secret: "db-pw", sshSecret: "s3cret-phrase" });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret, ssh] = saved();
    expect(secret).toBe("db-pw");
    expect(ssh).toBe("s3cret-phrase");
    expect(JSON.stringify(p)).not.toContain("s3cret-phrase");
    expect(JSON.stringify(p)).not.toContain("db-pw");
  });

  it("password sign-in: hides the key file, sends the password as sshSecret, saves it in the SSH slot", async () => {
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await enableSsh();
    await fillTunnel("b");
    await userEvent.selectOptions(sshAuth(), "password");
    expect(screen.queryByPlaceholderText("/home/me/.ssh/id_ed25519")).toBeNull();
    await userEvent.type(sshSecretBox(), "ssh-pw");
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCalls()).toHaveLength(1));
    expect(testCalls()[0]).toMatchObject({ sshSecret: "ssh-pw", profile: expect.objectContaining({ ssh: expect.objectContaining({ auth: "password" }) }) });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBe("ssh-pw");
  });

  it("Test and Save refuse a tunnel without a bastion host or an SSH user, and Test does not call profile.test", async () => {
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await enableSsh();
    // Neither: Test names the host first.
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Enter the SSH bastion host."));
    expect(testCalls()).toHaveLength(0);
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Enter the SSH bastion host.");
    expect(screen.getByRole("alert").textContent).toContain("Enter the SSH user name.");
    // Host only: still refused for the user.
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "jump.example.test");
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Enter the SSH user name."));
    expect(testCalls()).toHaveLength(0);
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    // A blank user (spaces) counts as none.
    await userEvent.type(screen.getByPlaceholderText("deploy"), "  ");
    await userEvent.click(saveBtn());
    expect(saveSpy).not.toHaveBeenCalled();
    await userEvent.clear(screen.getByPlaceholderText("deploy"));
    await userEvent.type(screen.getByPlaceholderText("deploy"), "ops");
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCalls()).toHaveLength(1));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saved()[0].ssh).toMatchObject({ host: "jump.example.test", username: "ops" });
  });

  it("the Host field is explained as the address the bastion sees", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    expect(screen.getByText(/Host above is the address as the bastion sees it/)).toBeTruthy();
    // The editor never rewrites Host for the tunnel: an empty Host stays empty (localhost: the bastion's own loopback).
    expect(hostBox().value).toBe("");
    expect(hostBox().placeholder).toBe("localhost");
  });

  it("a profile that uses the agent keeps it listed with a warning, asks for no secret, and clears the SSH slot on Save", async () => {
    useProfiles.setState({ profiles: [base("a", engine, { ssh: tunnel({ auth: "agent", keyPath: null }) })], save: saveSpy as never });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect([...sshAuth().options].map((o) => o.value)).toEqual(["privateKey", "password", "agent"]);
    expect(sshAuth().value).toBe("agent");
    expect(screen.getByText(/SSH agent sign-in is not supported yet/).className).toContain("text-warn");
    expect(screen.queryByText("Passphrase", { selector: "label" })).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBe("");
    expect(saved()[0].ssh?.auth).toBe("agent");
  });

  it("moving an agent profile to a key file drops the agent option and its warning", async () => {
    useProfiles.setState({ profiles: [base("a", engine, { ssh: tunnel({ auth: "agent", keyPath: null }) })], save: saveSpy as never });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(sshAuth(), "privateKey");
    expect([...sshAuth().options].map((o) => o.value)).toEqual(["privateKey", "password"]);
    expect(screen.queryByText(/SSH agent sign-in is not supported yet/)).toBeNull();
  });
});

describe.each(SERVER)("SSH saved secret (%s)", (engine) => {
  const existing = (over: Partial<SshSettings> = {}) =>
    useProfiles.setState({ profiles: [base("a", engine, { ssh: tunnel({ auth: "password", keyPath: null, ...over }) })], save: saveSpy as never });

  it("keep: an untouched box keeps the saved secret (null), and Test lets the shell inject it by profileId", async () => {
    existing();
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByText("Leave empty to keep the one saved in the OS keyring.")).toBeTruthy();
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCalls()).toHaveLength(1));
    expect(testCalls()[0]).toMatchObject({ profileId: "a", sshSecret: null });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBeNull();
  });

  it("retype: a typed SSH secret replaces the saved one and Test sends it", async () => {
    existing();
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.type(sshSecretBox(), "new-ssh-pw");
    expect(screen.queryByText("Leave empty to keep the one saved in the OS keyring.")).toBeNull();
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCalls()).toHaveLength(1));
    // The database secret was not retyped: the shell injects it, so the profile id goes along with the new SSH secret.
    expect(testCalls()[0]).toMatchObject({ profileId: "a", secret: null, sshSecret: "new-ssh-pw" });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBe("new-ssh-pw");
  });

  it("Forget saved is deferred to Save, can be undone, and typing a new one cancels it", async () => {
    existing();
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Forget saved" }));
    expect(screen.getByText("The saved one is forgotten when you save.")).toBeTruthy();
    expect(m.secretDelete).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Undo forget" }));
    expect(screen.queryByText("The saved one is forgotten when you save.")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Forget saved" }));
    await userEvent.type(sshSecretBox(), "x");
    expect(screen.queryByText("The saved one is forgotten when you save.")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBe("x");
  });

  it("Forget saved then Save sends an empty SSH secret (delete); Cancel would have kept it", async () => {
    existing();
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Forget saved" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBe("");
    expect(m.secretDelete).not.toHaveBeenCalled(); // the store's save does it, not the editor
  });

  it("turning the tunnel off on a saved profile clears its SSH keyring slot on Save", async () => {
    existing();
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0].ssh).toBeNull();
    expect(saved()[2]).toBe("");
  });

  it("no SSH secret in the keyring: no keep hint and no Forget saved button", async () => {
    existing();
    m.secretExists.mockImplementation(async (account: unknown) => account !== "a:ssh");
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await waitFor(() => expect(m.secretExists).toHaveBeenCalledWith("a:ssh"));
    await waitFor(() => expect(screen.queryByText("Leave empty to keep the one saved in the OS keyring.")).toBeNull());
    expect(screen.queryByRole("button", { name: "Forget saved" })).toBeNull();
  });

  it("an SSH secret typed and then cleared keeps the saved one (null)", async () => {
    existing();
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.type(sshSecretBox(), "z");
    await userEvent.clear(sshSecretBox());
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[2]).toBeNull();
  });

  it.each([["password", "privateKey"], ["privateKey", "password"]] as const)("switching a saved tunnel from %s to %s does not reuse the saved SSH secret", async (from, to) => {
    existing(from === "privateKey" ? { auth: "privateKey", keyPath: "/k/id" } : {});
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(sshAuth(), to);
    if (to === "privateKey") await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k/id");
    expect(screen.queryByText("Leave empty to keep the one saved in the OS keyring.")).toBeNull();
    expect(screen.queryByRole("button", { name: "Forget saved" })).toBeNull();
    // (Test is not pinned here: with an empty box it sends sshSecret null with the profile id, so the shell would inject
    // the saved secret of the other kind; reported separately.)
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0].ssh?.auth).toBe(to);
    expect(saved()[2]).toBe("");
  });

  it("a changed SSH secret marks an earlier test result out of date", async () => {
    existing();
    m.rpc.mockResolvedValue({ ok: true, serverVersion: "v", elapsedMs: 1, notes: [] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected"));
    await userEvent.type(sshSecretBox(), "y");
    expect(status().textContent).toContain("Settings changed since this test");
  });
});

describe.each(SERVER)("SSH host-key trust (%s)", (engine) => {
  const hostKey = { host: "jump.example.test", port: 22, algorithm: "ssh-ed25519", fingerprint: "AAAAfp" };

  it("accept: trusts exactly the presented key through ssh.trustHostKey, then tests again once", async () => {
    let n = 0;
    m.rpc.mockImplementation(async (method: unknown) => {
      if (method !== "profile.test") return undefined;
      return ++n === 1 ? { ok: false, error: "not trusted", elapsedMs: 0, notes: [], untrustedHostKey: hostKey } : { ok: true, serverVersion: "v", elapsedMs: 1, notes: [] };
    });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(lastDialog()).toMatchObject({ kind: "hostKey", hostKey }));
    expect(m.rpc).not.toHaveBeenCalledWith("ssh.trustHostKey", expect.anything());
    lastDialog().resolve(true);
    await waitFor(() => expect(status().textContent).toContain("through the SSH tunnel"));
    expect(m.rpc.mock.calls.filter((c) => c[0] === "ssh.trustHostKey")).toEqual([["ssh.trustHostKey", hostKey]]);
    expect(testCalls()).toHaveLength(2);
  });

  it("decline: nothing is trusted and the failure is shown", async () => {
    m.rpc.mockResolvedValue({ ok: false, error: "The SSH server is not trusted yet.", elapsedMs: 0, notes: [], untrustedHostKey: hostKey });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(lastDialog()).toMatchObject({ kind: "hostKey" }));
    lastDialog().resolve(false);
    await waitFor(() => expect(status().textContent).toContain("not trusted yet"));
    expect(m.rpc).not.toHaveBeenCalledWith("ssh.trustHostKey", expect.anything());
    expect(testCalls()).toHaveLength(1);
  });

  it("a key that is still untrusted after accepting is asked about once only (no loop)", async () => {
    m.rpc.mockImplementation(async (method: unknown) =>
      method === "profile.test" ? { ok: false, error: "still not trusted", elapsedMs: 0, notes: [], untrustedHostKey: hostKey } : undefined);
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(lastDialog()).toMatchObject({ kind: "hostKey" }));
    lastDialog().resolve(true);
    await waitFor(() => expect(status().textContent).toContain("still not trusted"));
    expect(testCalls()).toHaveLength(2);
    expect(m.rpc.mock.calls.filter((c) => c[0] === "ssh.trustHostKey")).toHaveLength(1);
    // The answered prompt is gone and no second one was opened.
    expect(useUi.getState().dialogs.filter((d) => d.kind === "hostKey")).toHaveLength(0);
  });

  it("a changed key (previous fingerprint) is passed to the prompt as is", async () => {
    const changed = { ...hostKey, previousFingerprint: "AAAAold" };
    m.rpc.mockResolvedValue({ ok: false, error: "presented a different key", elapsedMs: 0, notes: [], untrustedHostKey: changed });
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await enableSsh();
    await fillTunnel();
    await userEvent.click(testBtn());
    await waitFor(() => expect(lastDialog()).toMatchObject({ kind: "hostKey", hostKey: changed }));
    lastDialog().resolve(false);
    await waitFor(() => expect(status().textContent).toContain("presented a different key"));
  });
});

// ───────────────────────────── connection-string paste ─────────────────────────────

const STRINGS: Record<ServerEngine, string> = {
  mssql: "Server=sql.example.test,1433;Database=Sales;User ID=app;Password=Zq1-cs-secret;Encrypt=True",
  pg: "postgresql://svc:Zq1-cs-secret@pg.example.test:5432/app?sslmode=verify-full",
  mySql: "Server=my.example.test;Port=3307;User ID=app;Password=Zq1-cs-secret;Database=shop;SslMode=VerifyCA",
};

describe.each(SERVER)("connection-string paste (%s)", (engine) => {
  const cs = STRINGS[engine];
  const result = (over: object = {}) => ({
    profile: base("", engine, { host: "parsed.example.test", databaseDefault: "parsed-db", name: "", username: "app" }),
    secret: "Zq1-cs-secret", unknown: ["Frob"], notApplied: ["Pooling"], notes: ["SslMode=VerifyCA: the host name is not checked."], ...over,
  });
  const box = () => screen.getByLabelText("Connection string") as HTMLInputElement;
  const openBox = () => userEvent.click(screen.getByRole("button", { name: "Paste a connection string…" }));
  const parseCalls = () => m.rpc.mock.calls.filter((c) => c[0] === "profile.parseConnectionString");

  // The box is uncontrolled (SecretInput): its text lives in the input's value property only, never in a value="…"
  // attribute, and is gone from the DOM once the box closes.
  it("the box is masked, never autocompletes or spellchecks, and Cancel removes the text from the DOM", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    expect(box().type).toBe("password");
    expect(box().getAttribute("autocomplete")).toBe("off");
    expect(box().getAttribute("spellcheck")).toBe("false");
    fireEvent.change(box(), { target: { value: cs } });
    expect(box().value).toBe(cs);
    expect(box().getAttribute("value")).toBeNull();
    expect(document.body.innerHTML).not.toContain("Zq1-cs-secret");
    await userEvent.click(within(box().parentElement!).getByRole("button", { name: "Cancel" }));
    expect(document.body.innerHTML).not.toContain("Zq1-cs-secret");
  });

  it("a paste parses at once, sets the type, fills the fields and clears the box", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(parseCalls()).toHaveLength(1));
    expect(parseCalls()[0][1]).toMatchObject({ connectionString: cs });
    await waitFor(() => expect(combo().textContent).toContain(LABEL[engine]));
    expect(screen.queryByLabelText("Connection string")).toBeNull();
    expect(hostBox().value).toBe("parsed.example.test");
    expect(screen.getByText(/Read as/).textContent).toContain(LABEL[engine]);
  });

  it("the parsed secret goes to the masked password field, the string leaves the DOM, and Save hands the secret to the keyring only", async () => {
    m.rpc.mockResolvedValue(result());
    let unmount = () => {};
    const onClose = vi.fn(() => unmount());
    ({ unmount } = render(<ProfileEditor preset={engine} onClose={onClose} />));
    await pickEnv();
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(hostBox().value).toBe("parsed.example.test"));
    const pw = screen.getByLabelText("Password") as HTMLInputElement;
    expect(pw.type).toBe("password");
    expect(pw.value).toBe("Zq1-cs-secret");
    expect(document.body.innerHTML).not.toContain(cs);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(onClose).toHaveBeenCalled();
    expect(document.body.innerHTML).not.toContain("Zq1-cs-secret");
    const [p, secret, ssh] = saved();
    expect(secret).toBe("Zq1-cs-secret");
    expect(ssh).toBe("");
    expect(JSON.stringify(p)).not.toContain("Zq1-cs-secret");
    expect(JSON.stringify(p)).not.toContain(cs);
  });

  it("Enter in the box parses the typed text; an IME composition Enter does not", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    fireEvent.keyDown(box(), { key: "Enter", isComposing: true });
    expect(parseCalls()).toHaveLength(0);
    fireEvent.keyDown(box(), { key: "Enter" });
    await waitFor(() => expect(parseCalls()).toHaveLength(1));
    expect(parseCalls()[0][1]).toMatchObject({ connectionString: cs });
    // Enter in the box parses; it never saves the dialog.
    expect(saveSpy).not.toHaveBeenCalled();
  });

  it("Fill fields is disabled while the box is empty and parses the typed text", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    const fill = screen.getByRole("button", { name: "Fill fields" }) as HTMLButtonElement;
    expect(fill.disabled).toBe(true);
    fireEvent.change(box(), { target: { value: cs } });
    expect(fill.disabled).toBe(false);
    await userEvent.click(fill);
    await waitFor(() => expect(hostBox().value).toBe("parsed.example.test"));
    expect(screen.queryByLabelText("Connection string")).toBeNull();
  });

  it("a parse error keeps the text in the box, shows the error, and leaves the fields as they were", async () => {
    m.rpc.mockRejectedValue(new Error("Not a valid connection string: Format of the initialization string does not conform"));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    paste(box(), "Server=h;Password='unterminated");
    await waitFor(() => expect(status().textContent).toContain("Not a valid connection string"));
    expect(box().value).toBe("Server=h;Password='unterminated");
    expect(hostBox().value).toBe("");
    expect(screen.queryByText(/Read as/)).toBeNull();
  });

  it("a failed parse from Save stops the save and keeps the text", async () => {
    m.rpc.mockRejectedValue(new Error("Not a valid connection string"));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await openBox();
    fireEvent.change(box(), { target: { value: "junk" } });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(status().textContent).toContain("Not a valid connection string"));
    expect(saveSpy).not.toHaveBeenCalled();
    expect(box().value).toBe("junk");
  });

  it("Test reads text left in the box, and sends the parsed secret and profile", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? { ok: true, serverVersion: "v9", elapsedMs: 1, notes: [] } : result()));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: v9"));
    expect(testCalls()[0]).toMatchObject({ profileId: null, secret: "Zq1-cs-secret", profile: expect.objectContaining({ engine, host: "parsed.example.test" }) });
    expect(combo().textContent).toContain(LABEL[engine]);
  });

  it("Save reads the box, takes its type, and saves", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await openBox();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ engine, host: "parsed.example.test", name: "parsed.example.test / parsed-db" });
    expect(saved()[1]).toBe("Zq1-cs-secret");
  });

  it("a string pasted into Host is held in the masked box, not applied, until Fill fields", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    paste(hostBox(), cs);
    expect(hostBox().value).toBe("");
    expect(box().type).toBe("password");
    expect(box().value).toBe(cs);
    expect(screen.getByText(/so it went here instead of into Host/)).toBeTruthy();
    expect(parseCalls()).toHaveLength(0);
    expect(hostBox().getAttribute("value")).toBe("");
    await userEvent.click(screen.getByRole("button", { name: "Fill fields" }));
    await waitFor(() => expect(hostBox().value).toBe("parsed.example.test"));
    expect(screen.queryByText(/so it went here instead of into Host/)).toBeNull();
    expect(document.body.innerHTML).not.toContain(cs);
  });

  it("a plain host name pasted into Host is a normal paste", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    const ev = paste(hostBox(), "db01.example.test");
    expect(ev).toBe(true); // not prevented
    expect(screen.queryByLabelText("Connection string")).toBeNull();
  });

  it("the parse report (not recognised, not applied, M3 notes) stays visible through edits and a Test", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? { ok: false, error: "refused", elapsedMs: 1, notes: [] } : result()));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(screen.getByText("Not recognised: Frob")).toBeTruthy());
    expect(screen.getByText(/Not applied .*: Pooling/)).toBeTruthy();
    const note = screen.getByText("SslMode=VerifyCA: the host name is not checked.");
    expect(note.className).toContain("text-warn");
    await userEvent.type(hostBox(), "x");
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("refused"));
    expect(screen.getByText("Not recognised: Frob")).toBeTruthy();
    expect(screen.getByText("SslMode=VerifyCA: the host name is not checked.")).toBeTruthy();
    // The report has no dismiss button of its own.
    expect(within(screen.getByText(/Read as/).closest("[aria-live]")! as HTMLElement).queryByRole("button")).toBeNull();
  });

  // PT3-1: a string that points the profile at another server never carries the saved password there: it is not
  // offered as saved, and Save removes it (the next connect asks).
  it("an existing profile's saved password is not kept for the other server a string names", async () => {
    useProfiles.setState({ profiles: [base("a", engine)], save: saveSpy as never });
    m.rpc.mockResolvedValue(result({ secret: null, profile: base("a", engine, { host: "parsed.example.test" }) }));
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await waitFor(() => expect(screen.getByText("Saved in the OS keyring")).toBeTruthy());
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(hostBox().value).toBe("parsed.example.test"));
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    expect(screen.getByText(/The connection settings changed, so the saved password is not sent to them/)).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[1]).toBe("");
  });

  it("an existing profile's saved password is replaced by the string's", async () => {
    useProfiles.setState({ profiles: [base("a", engine)], save: saveSpy as never });
    m.rpc.mockResolvedValue(result({ profile: base("a", engine, { host: "parsed.example.test" }) }));
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("Zq1-cs-secret"));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[1]).toBe("Zq1-cs-secret");
  });

  it("the editor's current profile goes to the parser as the basis (identity fields kept)", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await pickEnv();
    await openBox();
    paste(box(), cs);
    await waitFor(() => expect(parseCalls()).toHaveLength(1));
    expect((parseCalls()[0][1] as { profile: Profile }).profile).toMatchObject({ engine, environment: "dev" });
  });
});

describe("no tunnel and no connection string for file engines", () => {
  it.each<[Engine, string]>([["sqlite", "SQLite"], ["duckDb", "DuckDB"]])("%s", async (engine) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(screen.queryByRole("button", { name: /^SSH tunnel/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Paste a connection string…" })).toBeNull();
  });
});


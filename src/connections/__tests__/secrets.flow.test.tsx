// @vitest-environment jsdom
// §13 rule 1, the webview's side: the profile editor and useProfiles.save hand secrets to the shell (OS keyring) only,
// under the account "<id>" / "<id>:ssh", after profile.save has returned the id; the profile itself never carries one.
// Unlike ProfileEditor.test.tsx (which spies on save), these run the real store against a mocked rpc and shell.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => {
  const log: string[] = [];
  class RpcError extends Error {
    constructor(message: string, readonly code = -1, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  }
  let next = 0;
  const rpc = vi.fn(async (method: string, params?: unknown): Promise<unknown> => {
    log.push(`rpc:${method}`);
    if (method === "profile.save") {
      const p = params as Profile;
      return { ...p, id: p.id || `new${++next}` };
    }
    return undefined;
  });
  return {
    log, RpcError, rpc,
    secretSet: vi.fn(async (id: string, _s: string) => void log.push(`set:${id}`)),
    secretDelete: vi.fn(async (id: string) => void log.push(`delete:${id}`)),
    secretExists: vi.fn(async (_id: string) => true),
    secretEndConnection: vi.fn(async (_id: string) => {}),
    ask: vi.fn(),
    resetIds: () => (next = 0),
  };
});
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, RpcError: m.RpcError,
  shell: { secretSet: m.secretSet, secretDelete: m.secretDelete, secretExists: m.secretExists, secretEndConnection: m.secretEndConnection },
}));
vi.mock("../../ipc/transport", () => ({ isTauri: false }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: vi.fn(async () => true) }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn(async () => null) }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => null }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: vi.fn(), requestWriteAccess: vi.fn(), needsRepick: () => false }));

import { ProfileEditor } from "../ProfileEditor";
import { sshAccount, useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useUi } from "../../store/ui";

const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "db.local", port: null, auth: engine === "mssql" ? "sql" : "password", username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
  passwordRetention: "forever", passwordRetentionMinutes: null, createdAt: 0, updatedAt: 0, ...over,
});
const realSave = useProfiles.getState().save;
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const pickEnv = (name = "Dev") => userEvent.click(screen.getByRole("button", { name }));
const savedProfiles = () => m.rpc.mock.calls.filter((c) => c[0] === "profile.save").map((c) => c[1] as Profile);
const SECRET_KEY = /pass(word|phrase)?$|secret|pwd|token/i;
/** Every key path in an object whose name looks like it holds a secret (passwordRetention* is a policy, not a secret). */
function secretKeys(o: unknown, path = ""): string[] {
  if (!o || typeof o !== "object") return [];
  return Object.entries(o).flatMap(([k, v]) => [...(SECRET_KEY.test(k) && !k.startsWith("passwordRetention") ? [`${path}${k}`] : []), ...secretKeys(v, `${path}${k}.`)]);
}

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  m.log.length = 0;
  m.resetIds();
  for (const f of [m.rpc, m.secretSet, m.secretDelete, m.secretExists, m.secretEndConnection]) f.mockClear();
  m.secretExists.mockResolvedValue(true);
  useProfiles.setState({ profiles: [], groups: [], save: realSave });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

describe("useProfiles.save: null keeps, empty removes, a value stores (§13 rule 1)", () => {
  it.each<[string, string | null | undefined, string[]]>([
    ["a value stores it under <id>", "pw", ["rpc:profile.save", "set:p1"]],
    ["null keeps it", null, ["rpc:profile.save"]],
    ["undefined keeps it", undefined, ["rpc:profile.save"]],
    ["empty removes it", "", ["rpc:profile.save", "delete:p1"]],
  ])("database secret: %s", async (_name, secret, expected) => {
    const ssh = { host: "b", port: 22, username: "j", auth: "password" as const, keyPath: null };
    await useProfiles.getState().save(base("p1", "mssql", { ssh }), secret, null);
    expect(m.log).toEqual(expected);
    if (secret) expect(m.secretSet).toHaveBeenCalledWith("p1", secret);
  });

  it.each<[string, string | null, string[]]>([
    ["a value stores it under <id>:ssh", "sshpw", ["rpc:profile.save", "set:p1:ssh"]],
    ["null keeps it", null, ["rpc:profile.save"]],
    ["empty removes it", "", ["rpc:profile.save", "delete:p1:ssh"]],
  ])("SSH secret with a tunnel: %s", async (_name, sshSecret, expected) => {
    const ssh = { host: "b", port: 22, username: "j", auth: "privateKey" as const, keyPath: "/k" };
    await useProfiles.getState().save(base("p1", "pg", { ssh }), null, sshSecret);
    expect(m.log).toEqual(expected);
  });

  it("a profile without a tunnel reports a failed SSH credential clear", async () => {
    m.secretDelete.mockRejectedValueOnce(new Error("keyring locked"));
    await expect(useProfiles.getState().save(base("p1", "mySql"), null, null)).rejects.toMatchObject({ message: "keyring locked", savedProfile: expect.objectContaining({ id: "p1" }) });
    expect(m.secretDelete).toHaveBeenCalledWith(sshAccount("p1"));
    expect(sshAccount("p1")).toBe("p1:ssh");
  });

  it("a new profile's secrets wait for profile.save and go under the id it returned", async () => {
    let release!: (p: Profile) => void;
    m.rpc.mockImplementationOnce((method: string, params?: unknown) => {
      m.log.push(`rpc:${method}`);
      return new Promise((r) => (release = () => r({ ...(params as Profile), id: "01NEWID" })));
    });
    const ssh = { host: "b", port: 22, username: "j", auth: "password" as const, keyPath: null };
    const saving = useProfiles.getState().save(base("", "pg", { ssh }), "db-pw", "ssh-pw");
    await Promise.resolve();
    expect(m.secretSet).not.toHaveBeenCalled();
    release(undefined as never);
    await saving;
    expect(m.log).toEqual(["rpc:profile.save", "set:01NEWID", "set:01NEWID:ssh"]);
    expect(m.secretSet).toHaveBeenCalledWith("01NEWID", "db-pw");
    expect(m.secretSet).toHaveBeenCalledWith("01NEWID:ssh", "ssh-pw");
  });

  it("a failed profile.save writes no secret", async () => {
    m.rpc.mockRejectedValueOnce(new m.RpcError("invalid", -1, "invalidProfile"));
    await expect(useProfiles.getState().save(base("p1", "mssql"), "pw", "ssh")).rejects.toThrow("invalid");
    expect(m.secretSet).not.toHaveBeenCalled();
    expect(m.secretDelete).not.toHaveBeenCalled();
  });

  it("the profile sent to profile.save carries no secret, whatever secrets are passed", async () => {
    const ssh = { host: "b", port: 22, username: "j", auth: "password" as const, keyPath: null };
    await useProfiles.getState().save(base("p1", "mssql", { ssh }), "db-s3cret", "ssh-s3cret");
    const sent = savedProfiles()[0];
    expect(secretKeys(sent)).toEqual([]);
    expect(JSON.stringify(sent)).not.toMatch(/db-s3cret|ssh-s3cret/);
  });
});

describe("the editor: what reaches the keyring for each engine, sign-in and retention policy", () => {
  // Every engine with a secret sign-in. SQLite and DuckDB have none (no password field: covered at the end).
  const engines: Engine[] = ["mssql", "pg", "mySql"];

  it.each(engines)("%s new profile: the typed password is stored after profile.save, under the new id; the profile carries none", async (engine) => {
    const onClose = vi.fn();
    render(<ProfileEditor preset={engine} onClose={onClose} />);
    await pickEnv();
    await userEvent.type(screen.getByLabelText("Password"), "typed-s3cret");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(m.log).toEqual(["rpc:profile.save", "set:new1", "delete:new1:ssh"]);
    expect(m.secretSet).toHaveBeenCalledWith("new1", "typed-s3cret");
    const sent = savedProfiles()[0];
    expect(sent.id).toBe("");
    expect(sent.passwordRetention).toBe("once"); // a new profile's default
    expect(secretKeys(sent)).toEqual([]);
    expect(JSON.stringify(sent)).not.toContain("typed-s3cret");
  });

  it.each<[string, string, number | null]>([
    ["once", "once", null], ["session", "session", null], ["forever", "forever", null],
  ])("retention %s is sent with the profile (the shell configures it on profile.save's reply, before the secret)", async (_n, policy) => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), policy);
    await userEvent.type(screen.getByLabelText("Password"), "pw1");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(m.secretSet).toHaveBeenCalledWith("new1", "pw1"));
    expect(savedProfiles()[0]).toMatchObject({ passwordRetention: policy });
    expect(m.log.indexOf("rpc:profile.save")).toBeLessThan(m.log.indexOf("set:new1"));
  });

  it.each<[string, number]>([["15 minutes", 15], ["1 hour", 60], ["8 hours", 480], ["1 day", 1440], ["7 days", 10080], ["30 days", 43200]])(
    "duration preset %s sends its minutes", async (_label, minutes) => {
      render(<ProfileEditor preset="mySql" onClose={() => {}} />);
      await pickEnv();
      await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
      await userEvent.selectOptions(screen.getByLabelText("Duration"), String(minutes));
      await userEvent.type(screen.getByLabelText("Password"), "pw2");
      await userEvent.click(saveBtn());
      await waitFor(() => expect(m.secretSet).toHaveBeenCalledWith("new1", "pw2"));
      expect(savedProfiles()[0]).toMatchObject({ passwordRetention: "duration", passwordRetentionMinutes: minutes });
    },
  );

  it.each<[string, string, number]>([["minutes", "90", 90], ["hours", "3", 180], ["days", "2", 2880]])(
    "a custom duration in %s is sent in minutes", async (unit, length, minutes) => {
      render(<ProfileEditor preset="mssql" onClose={() => {}} />);
      await pickEnv();
      await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
      await userEvent.selectOptions(screen.getByLabelText("Duration"), "custom");
      await userEvent.selectOptions(screen.getByLabelText("Unit"), unit);
      await userEvent.clear(screen.getByLabelText("Length"));
      await userEvent.type(screen.getByLabelText("Length"), length);
      await userEvent.type(screen.getByLabelText("Password"), "pw3");
      await userEvent.click(saveBtn());
      await waitFor(() => expect(m.secretSet).toHaveBeenCalledWith("new1", "pw3"));
      expect(savedProfiles()[0]).toMatchObject({ passwordRetention: "duration", passwordRetentionMinutes: minutes });
    },
  );

  it.each<[string, string]>([["0", "zero"], ["525601", "over a year"]])("a custom duration of %s minutes (%s) is refused before anything is saved", async (length) => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
    await userEvent.selectOptions(screen.getByLabelText("Duration"), "custom");
    await userEvent.selectOptions(screen.getByLabelText("Unit"), "minutes");
    await userEvent.clear(screen.getByLabelText("Length"));
    await userEvent.type(screen.getByLabelText("Length"), length);
    await userEvent.type(screen.getByLabelText("Password"), "pw4");
    await userEvent.click(saveBtn());
    expect(useUi.getState().toasts.map((t) => t.text)).toContain("Choose a duration from 1 minute to 365 days.");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.save", expect.anything());
    expect(m.secretSet).not.toHaveBeenCalled();
  });

  it.each(engines)("%s saved profile: Forget waits for Save — Cancel ▸ Discard keeps the secret", async (engine) => {
    useProfiles.setState({ profiles: [base("a", engine)] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(await screen.findByRole("button", { name: "Forget" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(onClose).toHaveBeenCalled();
    expect(m.rpc).not.toHaveBeenCalledWith("profile.save", expect.anything());
    expect(m.secretDelete).not.toHaveBeenCalled();
  });

  it.each(engines)("%s saved profile: Forget then Save deletes <id> after profile.save", async (engine) => {
    useProfiles.setState({ profiles: [base("a", engine)] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(await screen.findByRole("button", { name: "Forget" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(m.log).toEqual(["rpc:profile.save", "delete:a", "delete:a:ssh"]);
    expect(m.secretSet).not.toHaveBeenCalled();
  });

  it("Change then typing a new password stores the new one; nothing is deleted", async () => {
    useProfiles.setState({ profiles: [base("a", "pg")] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(await screen.findByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText("Password"), "replacement");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(m.secretSet).toHaveBeenCalledWith("a", "replacement"));
    expect(m.secretDelete).not.toHaveBeenCalledWith("a");
  });

  it.each<[Engine, string]>([["mssql", "windows"], ["mssql", "kerberos"], ["mssql", "entraInteractive"], ["mssql", "entraDeviceCode"], ["pg", "kerberos"]])(
    "%s: switching to %s (no secret) deletes <id> on Save, not before", async (engine, auth) => {
      useProfiles.setState({ profiles: [base("a", engine)] });
      const onClose = vi.fn();
      render(<ProfileEditor profileId="a" onClose={onClose} />);
      await screen.findByText("Saved in the OS keyring");
      await userEvent.selectOptions(screen.getByLabelText("Sign in"), auth);
      expect(m.secretDelete).not.toHaveBeenCalled();
      await userEvent.click(saveBtn());
      await waitFor(() => expect(onClose).toHaveBeenCalled());
      expect(savedProfiles()[0].auth).toBe(auth);
      expect(m.log).toEqual(["delete:a", "delete:a:ssh", "rpc:profile.save"]);
    },
  );

  // A saved password is not a token: switching to entraToken does not reuse it.
  it.each<[Engine, string]>([["mssql", "entraToken"], ["pg", "entraToken"]])("%s: switching to %s (another kind of secret) with the box empty deletes the saved one on Save", async (engine, auth) => {
    useProfiles.setState({ profiles: [base("a", engine)] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await screen.findByText("Saved in the OS keyring");
    await userEvent.selectOptions(screen.getByLabelText("Sign in"), auth);
    expect(m.secretDelete).not.toHaveBeenCalled();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(savedProfiles()[0].auth).toBe(auth);
    expect(m.log).toEqual(["delete:a", "delete:a:ssh", "rpc:profile.save"]);
    expect(m.secretSet).not.toHaveBeenCalled();
  });

  it.each<[Engine, string]>([["mssql", "entraToken"], ["pg", "entraToken"]])("%s: switching to %s and typing a token stores the typed one under <id>", async (engine, auth) => {
    useProfiles.setState({ profiles: [base("a", engine)] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await screen.findByText("Saved in the OS keyring");
    await userEvent.selectOptions(screen.getByLabelText("Sign in"), auth);
    await userEvent.type(screen.getByLabelText("Access token"), "tok-fake");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(m.secretSet).toHaveBeenCalledWith("a", "tok-fake");
    expect(m.log.indexOf("delete:a")).toBeLessThan(m.log.indexOf("set:a"));
  });

  it("an SSH tunnel's typed passphrase goes under <id>:ssh, the database password under <id>", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await pickEnv();
    await userEvent.type(screen.getByLabelText("Password"), "db-pw");
    await userEvent.click(screen.getByRole("button", { name: /^SSH tunnel/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "bastion");
    await userEvent.type(screen.getByPlaceholderText("deploy"), "deploy");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k");
    const phrase = (screen.getByLabelText("Passphrase") as HTMLInputElement);
    await userEvent.type(phrase, "ssh-pw");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(m.secretSet).toHaveBeenCalledTimes(2));
    expect(m.log).toEqual(["rpc:profile.save", "set:new1", "set:new1:ssh"]);
    expect(m.secretSet).toHaveBeenCalledWith("new1", "db-pw");
    expect(m.secretSet).toHaveBeenCalledWith("new1:ssh", "ssh-pw");
    expect(JSON.stringify(savedProfiles()[0])).not.toMatch(/db-pw|ssh-pw/);
  });

  it("turning a saved tunnel off deletes <id>:ssh on Save", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { ssh: { host: "b", port: 22, username: "u", auth: "password", keyPath: null } })] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("checkbox", { name: /Connect through an SSH bastion/ }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    // Without the tunnel, Host is reached directly (it named the server as the bastion saw it): another endpoint, so the
    // saved database password goes too (PT3-1), and the next connect asks.
    expect(m.log).toEqual(["delete:a", "delete:a:ssh", "rpc:profile.save"]);
  });

  it.each<Engine>(["sqlite", "duckDb"])("%s in memory: no password field and nothing written to the keyring", async (engine) => {
    useProfiles.setState({ profiles: [base("f", engine, { location: "memory", host: null, auth: "none", username: null })] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="f" onClose={onClose} />);
    expect(screen.queryByLabelText("Password")).toBeNull();
    expect(screen.queryByLabelText("Remember password")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(m.secretSet).not.toHaveBeenCalled();
    expect(secretKeys(savedProfiles()[0])).toEqual([]);
  });

  // BUG (Med): "Change" shows "Leave it empty to keep the saved one", but typing and then clearing the box marks the
  // secret as touched with "" and Save DELETES the saved password (ProfileEditor.tsx:490 `d.touched ? d.secret : null`).
  // Flip `it.fails` to `it` once fixed.
  it("Change, type, then clear the box keeps the saved secret, as the hint says", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(await screen.findByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText("Password"), "x");
    await userEvent.clear(screen.getByLabelText("Password"));
    expect(screen.getByText(/Leave it empty to keep the saved one/)).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(m.secretDelete).not.toHaveBeenCalledWith("a");
  });

  // BUG (Med): a new profile whose keyring write fails (locked keyring) was already created by profile.save; the editor
  // stays open with the error, and Save again sends id "" once more, creating a second profile. Flip once fixed.
  it("a keyring failure on a new profile's first Save does not make a duplicate profile on the second Save", async () => {
    m.secretSet.mockRejectedValueOnce(new Error("the keyring is locked"));
    const onClose = vi.fn();
    render(<ProfileEditor preset="pg" onClose={onClose} />);
    await pickEnv();
    await userEvent.type(screen.getByLabelText("Password"), "pw");
    await userEvent.click(saveBtn());
    await waitFor(() => expect(useUi.getState().toasts.map((t) => t.text)).toContain("the keyring is locked"));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    // Expected: the second save updates the profile the first one created.
    expect(savedProfiles().map((p) => p.id)).toEqual(["", "new1"]);
  });
});


describe("pending connection strings use the final endpoint and transport", () => {
  function pending(p: Profile, patch: Partial<Profile>) {
    useProfiles.setState({ profiles: [p] });
    m.rpc.mockImplementation(async (method: string, params?: unknown) => {
      if (method === "profile.parseConnectionString") return { profile: { ...p, ...patch }, secret: null, unknown: [], notApplied: [], notes: [] };
      if (method === "profile.save") return params;
      if (method === "profile.test") return { ok: false, error: "not connected", notes: [], elapsedMs: 0 };
      return undefined;
    });
    render(<ProfileEditor profileId={p.id} onClose={() => {}} />);
  }
  const pasteHost = () => fireEvent.paste(screen.getByLabelText("Host"), { clipboardData: { getData: () => "Host=new.example.test;Username=u" } });

  it("Save removes both old credentials when a pending string changes the host", async () => {
    const ssh = { host: "bastion.invalid", port: 22, username: "jump", auth: "password" as const, keyPath: null };
    pending(base("a", "pg", { ssh }), { host: "new.example.test" });
    pasteHost();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(savedProfiles()).toHaveLength(1));
    expect(savedProfiles()[0].host).toBe("new.example.test");
    expect(m.secretDelete).toHaveBeenCalledWith("a");
    expect(m.secretDelete).toHaveBeenCalledWith("a:ssh");
  });

  it("Test never requests saved credentials for a pending string's new host", async () => {
    pending(base("a", "pg"), { host: "new.example.test" });
    pasteHost();
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ profileId: null, secret: "", profile: expect.objectContaining({ host: "new.example.test" }) })));
  });

  it("a pending Production TLS downgrade waits for acknowledgement before saving", async () => {
    pending(base("a", "pg", { environment: "prod", tlsMode: "verifyFull" }), { tlsMode: "disable" });
    pasteHost();
    await userEvent.click(saveBtn());
    expect(savedProfiles()).toHaveLength(0);
    const checkbox = await screen.findByRole("checkbox", { name: /I accept/ });
    await userEvent.click(checkbox);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(savedProfiles()).toHaveLength(1));
    expect(savedProfiles()[0].tlsMode).toBe("disable");
  });
});

describe("SSH credential deletion failures", () => {
  it("reports a failed deletion with the saved profile so retry updates the same row", async () => {
    const p = base("a", "pg", { ssh: { host: "new-bastion.invalid", port: 22, username: "u", auth: "password", keyPath: null } });
    m.secretDelete.mockRejectedValueOnce(new Error("keyring locked"));
    await expect(realSave(p, null, "")).rejects.toMatchObject({ message: "keyring locked", savedProfile: expect.objectContaining({ id: "a" }) });
    expect(useProfiles.getState().byId("a")).toMatchObject({ ssh: p.ssh });
    await expect(realSave(p, null, "")).resolves.toMatchObject({ id: "a" });
    expect(m.secretDelete).toHaveBeenCalledWith("a:ssh");
  });
});

it("a failed SSH deletion blocks an endpoint edit before the profile is saved", async () => {
  const old = base("a", "pg", { ssh: { host: "old-bastion.invalid", port: 22, username: "u", auth: "password", keyPath: null } });
  useProfiles.setState({ profiles: [old] });
  m.secretDelete.mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error("keyring locked"));
  await expect(realSave({ ...old, ssh: { ...old.ssh!, host: "new-bastion.invalid" } }, null, "")).rejects.toThrow("keyring locked");
  expect(savedProfiles()).toEqual([]);
  expect(useProfiles.getState().byId("a")).toEqual(old);
});

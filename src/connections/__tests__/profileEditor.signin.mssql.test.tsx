// @vitest-environment jsdom
// SQL Server sign-in matrix (connection-page verification): each of the six AuthKinds through the profile editor —
// which fields show, what profile.test and useProfiles.save receive, switching method on a saved profile, and the
// Change / Keep saved / Forget flow. Mocking copied from ProfileEditor.test.tsx.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AuthKind, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  secretDelete: vi.fn(async (..._a: unknown[]) => {}),
  secretSet: vi.fn(async (..._a: unknown[]) => {}),
  secretExists: vi.fn(async (..._a: unknown[]) => true),
  confirmDeleteProfile: vi.fn(async (..._a: unknown[]) => true),
  openDialog: vi.fn(async (..._a: unknown[]): Promise<unknown> => null),
  pick: vi.fn(async () => null),
  allowWrites: vi.fn(async (..._a: unknown[]) => true),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, shell: { secretDelete: m.secretDelete, secretSet: m.secretSet, secretExists: m.secretExists }, RpcError: class extends Error {},
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return false; } }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: m.confirmDeleteProfile }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: m.openDialog }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => null }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: m.pick, requestWriteAccess: m.allowWrites, needsRepick: () => false }));

import { ProfileEditor } from "../ProfileEditor";
import { needsSecretFor } from "../auth";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const SECRET = "s3cret-fake";
const KINDS: AuthKind[] = ["sql", "windows", "kerberos", "entraInteractive", "entraDeviceCode", "entraToken"];
const NEEDS: Record<string, boolean> = { sql: true, windows: false, kerberos: false, entraInteractive: false, entraDeviceCode: false, entraToken: true };
// The user field: its label, or null when the method hides it (and the hint shown in its place).
const USER: Record<string, string | null> = { sql: "User name", windows: null, kerberos: "User name", entraInteractive: "User (optional hint)", entraDeviceCode: null, entraToken: "User name" };
const HINT: Record<string, string | undefined> = { windows: "Signs in as your Windows account.", entraDeviceCode: "Signs in with a code shown when connecting." };
const secretLabel = (a: AuthKind) => (a === "entraToken" ? "Access token" : "Password");

const saved = (auth: AuthKind, over: Partial<Profile> = {}): Profile => ({
  id: "a", name: "Saved-a", engine: "mssql", host: "db.local", port: null, auth, username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
  passwordRetention: "forever", createdAt: 0, updatedAt: 0, ...over,
});

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const ok = { ok: true, serverVersion: "16.0", elapsedMs: 3, notes: [] };
const saveBtn = () => screen.getByRole("button", { name: "Save" });
const testBtn = () => screen.getByRole("button", { name: "Test connection" });
const signIn = () => screen.getByLabelText("Sign in") as HTMLSelectElement;
const chooseAuth = (a: AuthKind) => userEvent.selectOptions(signIn(), a);
const testCall = () => m.rpc.mock.calls.find((c) => c[0] === "profile.test")?.[1] as { profileId: string | null; profile: Profile; secret: string | null; sshSecret: string | null };
const lastSave = () => saveSpy.mock.calls.at(-1)! as unknown as [Profile, string | null | undefined, string | null | undefined];

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  Object.values(m).forEach((f) => (f as { mockClear?: () => void }).mockClear?.());
  m.rpc.mockReset();
  m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? ok : undefined));
  m.secretExists.mockResolvedValue(true);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

describe("SQL Server sign-in: the two sides agree on which methods need a secret", () => {
  it.each(KINDS)("%s", (a) => expect(needsSecretFor(a)).toBe(NEEDS[a]));

  it("offers exactly the six SQL Server methods, SQL login first", () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    expect([...signIn().options].map((o) => o.value)).toEqual(KINDS);
    expect(signIn().value).toBe("sql");
  });
});

describe.each(KINDS)("SQL Server sign-in %s: a new profile", (auth) => {
  it("shows the fields this method uses and nothing else", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await chooseAuth(auth);
    if (USER[auth]) expect(screen.getByLabelText(USER[auth]!)).toBeTruthy();
    else {
      expect(screen.queryByLabelText("User name")).toBeNull();
      expect(screen.queryByLabelText("User (optional hint)")).toBeNull();
      expect(screen.getByText(HINT[auth]!)).toBeTruthy();
    }
    if (NEEDS[auth]) {
      expect((screen.getByLabelText(secretLabel(auth)) as HTMLInputElement).type).toBe("password");
      expect(screen.getByLabelText("Remember password")).toBeTruthy();
      if (auth === "entraToken") expect(screen.queryByLabelText("Password")).toBeNull();
    } else {
      expect(screen.queryByLabelText("Password")).toBeNull();
      expect(screen.queryByLabelText("Access token")).toBeNull();
      expect(screen.queryByLabelText("Remember password")).toBeNull();
    }
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
  });

  it("Test sends the typed secret beside the profile, never inside it", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await chooseAuth(auth);
    if (NEEDS[auth]) await userEvent.type(screen.getByLabelText(secretLabel(auth)), SECRET);
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toBeTruthy());
    const t = testCall();
    expect(t.profileId).toBeNull();
    expect(t.profile).toMatchObject({ engine: "mssql", auth });
    expect(t.secret).toBe(NEEDS[auth] ? SECRET : "");
    expect(t.sshSecret).toBeNull();
    expect(JSON.stringify(t.profile)).not.toContain(SECRET);
  });

  it("Save hands the secret to save() as its own argument (null when the method needs none)", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="mssql" onClose={onClose} />);
    await chooseAuth(auth);
    if (NEEDS[auth]) await userEvent.type(screen.getByLabelText(secretLabel(auth)), SECRET);
    await userEvent.click(screen.getByRole("button", { name: "Dev" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const [p, secret, ssh] = lastSave();
    expect(p).toMatchObject({ engine: "mssql", auth });
    expect(secret).toBe(NEEDS[auth] ? SECRET : null);
    expect(ssh).toBe("");
    expect(JSON.stringify(p)).not.toContain(SECRET);
  });

  if (NEEDS[auth]) {
    it("a secret typed and then cleared is not stored", async () => {
      const onClose = vi.fn();
      render(<ProfileEditor preset="mssql" onClose={onClose} />);
      await chooseAuth(auth);
      await userEvent.type(screen.getByLabelText(secretLabel(auth)), "x");
      await userEvent.clear(screen.getByLabelText(secretLabel(auth)));
      await userEvent.click(screen.getByRole("button", { name: "Dev" }));
      await userEvent.click(saveBtn());
      await waitFor(() => expect(onClose).toHaveBeenCalled());
      // A new profile has nothing in the keyring: an emptied box stores nothing (null), not a delete ("").
      expect(lastSave()[1]).toBeNull();
    });
  }
});

describe("SQL Server sign-in: a secret typed for one method and then switched to one that needs none", () => {
  it.each(KINDS.filter((k) => !NEEDS[k]))("sql → %s: Save stores nothing (new profile)", async (to) => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="mssql" onClose={onClose} />);
    await userEvent.type(screen.getByLabelText("Password"), SECRET);
    await chooseAuth(to);
    await userEvent.click(screen.getByRole("button", { name: "Dev" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBeNull();
    expect(m.secretSet).not.toHaveBeenCalled();
  });
});

describe.each(KINDS)("SQL Server sign-in %s: an existing profile", (auth) => {
  beforeEach(() => {
    m.secretExists.mockResolvedValue(NEEDS[auth]);
    useProfiles.setState({ profiles: [saved(auth)] });
  });

  it("shows a saved secret as saved (never its value), or no secret row at all", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(signIn().value).toBe(auth);
    await waitFor(() => expect(m.secretExists).toHaveBeenCalledWith("a"));
    if (NEEDS[auth]) {
      expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
      expect(screen.getByText(secretLabel(auth))).toBeTruthy();
      expect(screen.getByRole("button", { name: "Change" })).toBeTruthy();
      expect(screen.getByRole("button", { name: "Forget" })).toBeTruthy();
      expect(screen.queryByLabelText(secretLabel(auth))).toBeNull();
    } else {
      expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
      expect(screen.queryByLabelText("Password")).toBeNull();
      expect(screen.queryByLabelText("Access token")).toBeNull();
    }
  });

  it("Test uses the saved secret (profileId, secret null)", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toBeTruthy());
    expect(testCall()).toMatchObject({ profileId: "a", secret: null, sshSecret: null, profile: { auth } });
  });

  it("Save without changes keeps a needed secret (null) and clears any stray one otherwise (\"\")", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe(NEEDS[auth] ? null : "");
  });
});

describe.each<AuthKind>(["sql", "entraToken"])("SQL Server sign-in %s: Change / Keep saved / Forget", (auth) => {
  const label = secretLabel(auth);
  beforeEach(() => useProfiles.setState({ profiles: [saved(auth)] }));

  it("Change with nothing typed keeps the saved secret; Test still uses it", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    expect((screen.getByLabelText(label) as HTMLInputElement).value).toBe("");
    expect(screen.getByText(/Leave it empty to keep the saved one/)).toBeTruthy();
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toMatchObject({ profileId: "a", secret: null }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBeNull();
  });

  it("Change + typed value replaces it, and Test sends the typed value (not the saved one)", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText(label), SECRET);
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toMatchObject({ secret: SECRET }));
    // typed DB secret, saved SSH secret untouched: the profile is sent whole, the secret beside it
    expect(JSON.stringify(testCall().profile)).not.toContain(SECRET);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe(SECRET);
    expect(JSON.stringify(lastSave()[0])).not.toContain(SECRET);
  });

  it("Keep saved drops what was typed", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.type(screen.getByLabelText(label), SECRET);
    await userEvent.click(screen.getByRole("button", { name: "Keep saved" }));
    expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBeNull();
  });

  it("Forget deletes only on Save; Undo cancels it", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    expect(screen.getByText("Forgotten when you save.")).toBeTruthy();
    expect(m.secretDelete).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    await userEvent.click(screen.getByRole("button", { name: "Forget" }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe("");
  });

  it("the label says what the secret is", () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByText(label)).toBeTruthy();
    if (auth === "entraToken") expect(screen.queryByText("Password")).toBeNull();
  });
});

describe("SQL Server sign-in: switching method on a saved profile", () => {
  it.each(KINDS.filter((k) => !NEEDS[k]))("sql → %s warns and removes the saved password on Save", async (to) => {
    useProfiles.setState({ profiles: [saved("sql")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await chooseAuth(to);
    expect(screen.getByText(/The saved password is removed when you save/)).toBeTruthy();
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[0].auth).toBe(to);
    expect(lastSave()[1]).toBe("");
  });

  it("switching back before Save keeps the saved password", async () => {
    useProfiles.setState({ profiles: [saved("sql")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await chooseAuth("windows");
    await chooseAuth("sql");
    expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBeNull();
  });

  it("windows (no saved secret) → sql asks for a password and stores the typed one", async () => {
    m.secretExists.mockResolvedValue(false);
    useProfiles.setState({ profiles: [saved("windows")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await waitFor(() => expect(m.secretExists).toHaveBeenCalled());
    await chooseAuth("sql");
    await waitFor(() => expect(screen.getByLabelText("Password")).toBeTruthy());
    await userEvent.type(screen.getByLabelText("Password"), SECRET);
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[0].auth).toBe("sql");
    expect(lastSave()[1]).toBe(SECRET);
  });

  // A saved SQL password is never reused as the bearer token after switching sql → entraToken (or the reverse).
  it("sql → entraToken does not reuse the saved password as the access token", async () => {
    useProfiles.setState({ profiles: [saved("sql")] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await chooseAuth("entraToken");
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    expect(screen.getByLabelText("Access token")).toBeTruthy();
  });

  it("sql → entraToken with the box empty: a warn hint, Test sends \"\" (not null) and Save deletes the saved password", async () => {
    useProfiles.setState({ profiles: [saved("sql")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await chooseAuth("entraToken");
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    const hint = screen.getByText(/The saved password is not used as an access token:/);
    expect(hint.className).toContain("text-warn");
    expect((screen.getByLabelText("Access token") as HTMLInputElement).value).toBe("");
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toMatchObject({ secret: "", profile: { auth: "entraToken" } }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe("");
  });

  it.each<[AuthKind, AuthKind, RegExp]>([
    ["sql", "entraToken", /The saved password is not used as an access token:/],
    ["entraToken", "sql", /The saved token is not used as a password:/],
  ])("%s → %s with a typed secret: Test and Save send the typed one", async (from, to, hint) => {
    useProfiles.setState({ profiles: [saved(from)] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await chooseAuth(to);
    expect(screen.getByText(hint)).toBeTruthy();
    await userEvent.type(screen.getByLabelText(secretLabel(to)), SECRET);
    await userEvent.click(testBtn());
    await waitFor(() => expect(testCall()).toMatchObject({ secret: SECRET, profile: { auth: to } }));
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe(SECRET);
  });

  it("entraToken → windows says the saved token (not password) is removed on Save", async () => {
    useProfiles.setState({ profiles: [saved("entraToken")] });
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await chooseAuth("windows");
    expect(screen.getByText(/The saved token is removed when you save/)).toBeTruthy();
    await userEvent.click(saveBtn());
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(lastSave()[1]).toBe("");
  });
});

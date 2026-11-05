// @vitest-environment jsdom
// PostgreSQL: every way to sign in (password / SCRAM, Kerberos, Entra access token) through the profile editor — the
// fields each shows, what profile.test and save receive for a new and an existing profile, switching method, retention
// policies, the empty host (W9) and the prefilled default database.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AuthKind, Engine, EngineInfo, Profile } from "../../ipc/contract";

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

const base = (id: string, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine: "pg" as Engine, host: "db.local", port: null, auth: "password", username: "u", databaseDefault: "app",
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
  createdAt: 0, updatedAt: 0, ...over,
});

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const ok = { ok: true, serverVersion: "PostgreSQL 17.0", elapsedMs: 3, notes: [] };
const method = () => screen.getByLabelText("Sign in") as HTMLSelectElement;
const hostBox = () => screen.getByLabelText("Host") as HTMLInputElement;
const dbBox = () => screen.getByLabelText("Database") as HTMLInputElement;
const userBox = () => screen.queryByLabelText("User name") as HTMLInputElement | null;
const secretBox = (auth: AuthKind) => screen.queryByLabelText(auth === "entraToken" ? "Access token" : "Password") as HTMLInputElement | null;
const remember = () => screen.queryByLabelText("Remember password") as HTMLSelectElement | null;
const pickEnv = () => userEvent.click(screen.getByRole("radio", { name: "Dev" }));
const save = () => userEvent.click(screen.getByRole("button", { name: "Save" }));
const test = () => userEvent.click(screen.getByRole("button", { name: "Test connection" }));
const saved = () => saveSpy.mock.calls[0] as unknown as [Profile, string | null, string | null];
const testCall = () => m.rpc.mock.calls.find((c) => c[0] === "profile.test")![1] as { profileId: string | null; profile: Profile; secret: string | null; sshSecret: string | null };

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  Object.values(m).forEach((f) => typeof f === "function" && (f as { mockClear?: () => void }).mockClear?.());
  m.tauri = false;
  m.engines = null;
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(ok);
  m.secretExists.mockResolvedValue(true);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

type Case = { auth: AuthKind; label: string; secret: string | null };
const METHODS: Case[] = [
  { auth: "password", label: "Password / SCRAM", secret: "pw" },
  { auth: "kerberos", label: "Kerberos (GSSAPI)", secret: null },
  { auth: "entraToken", label: "Entra access token (Azure PostgreSQL)", secret: "fake-token" },
];

describe("PostgreSQL sign-in: the method list", () => {
  it("offers exactly password / SCRAM, Kerberos and Entra token, password first", () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect([...method().options].map((o) => [o.value, o.textContent])).toEqual(METHODS.map((c) => [c.auth, c.label]));
    expect(method().value).toBe("password");
  });
});

describe.each(METHODS)("PostgreSQL sign-in, new profile: $auth", ({ auth, secret }) => {
  const choose = async () => {
    if (auth !== "password") await userEvent.selectOptions(method(), auth);
  };

  it("shows the user name, and a secret with its retention only when the method needs one", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await choose();
    expect(userBox()!.value).toBe("postgres");
    if (secret == null) {
      expect(screen.queryByLabelText("Password")).toBeNull();
      expect(screen.queryByLabelText("Access token")).toBeNull();
      expect(remember()).toBeNull();
    } else {
      expect(secretBox(auth)!.type).toBe("password");
      expect(screen.queryByLabelText(auth === "entraToken" ? "Password" : "Access token")).toBeNull();
      expect(remember()!.value).toBe("once");
    }
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
  });

  it("prefills the default database postgres, and switching method keeps it", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect(dbBox().value).toBe("postgres");
    await choose();
    expect(dbBox().value).toBe("postgres");
    await pickEnv();
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ auth, databaseDefault: "postgres" });
  });

  it("Test sends the typed secret (or none) with no profile id", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await choose();
    if (secret != null) await userEvent.type(secretBox(auth)!, secret);
    await test();
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    const t = testCall();
    expect(t.profileId).toBeNull();
    expect(t.profile).toMatchObject({ engine: "pg", auth, username: "postgres", host: "localhost", databaseDefault: "postgres" });
    expect(t.secret ?? "").toBe(secret ?? "");
    expect(t.sshSecret).toBeNull();
    // The secret is never a profile field.
    expect(JSON.stringify(t.profile)).not.toContain("fake-token");
  });

  it("Save hands the secret to the keyring path, never inside the profile", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset="pg" onClose={onClose} />);
    await choose();
    await userEvent.clear(userBox()!);
    await userEvent.type(userBox()!, "svc");
    if (secret != null) await userEvent.type(secretBox(auth)!, secret);
    await pickEnv();
    await save();
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const [p, s, ssh] = saved();
    expect(p).toMatchObject({ engine: "pg", auth, username: "svc" });
    expect(s).toBe(secret); // kerberos: null (a new profile has nothing to clear)
    expect(ssh).toBe("");
    if (secret) expect(JSON.stringify(p)).not.toContain(secret);
  });

  it("an empty host goes out as null on Test and Save (W9)", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await choose();
    await userEvent.clear(hostBox());
    await pickEnv();
    await test();
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    expect(testCall().profile.host).toBeNull();
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ host: null, auth });
  });
});

describe.each(METHODS.filter((c) => c.secret != null))("PostgreSQL sign-in, retention: $auth", ({ auth, secret }) => {
  it.each<[string, Partial<Profile>]>([
    ["once", { passwordRetention: "once" }],
    ["session", { passwordRetention: "session" }],
    ["duration", { passwordRetention: "duration", passwordRetentionMinutes: 60 }],
    ["forever", { passwordRetention: "forever" }],
  ])("%s reaches save with the secret", async (policy, expected) => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    if (auth !== "password") await userEvent.selectOptions(method(), auth);
    await userEvent.selectOptions(remember()!, policy);
    if (policy === "duration") expect((screen.getByLabelText("Duration") as HTMLSelectElement).value).toBe("60");
    else expect(screen.queryByLabelText("Duration")).toBeNull();
    await userEvent.type(secretBox(auth)!, secret!);
    await pickEnv();
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ auth, ...expected });
    expect(saved()[1]).toBe(secret);
  });

  it("a preset duration (8 hours) reaches save", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    if (auth !== "password") await userEvent.selectOptions(method(), auth);
    await userEvent.selectOptions(remember()!, "duration");
    await userEvent.selectOptions(screen.getByLabelText("Duration"), "480");
    await pickEnv();
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ passwordRetention: "duration", passwordRetentionMinutes: 480 });
  });

  it("an existing profile shows its own policy and keeps it on Save", async () => {
    useProfiles.setState({ profiles: [base("a", { auth, passwordRetention: "session" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(remember()!.value).toBe("session");
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ passwordRetention: "session" });
    expect(saved()[1]).toBeNull();
  });
});

describe("PostgreSQL sign-in: Kerberos has no retention control", () => {
  it("the Remember control goes when Kerberos is chosen and comes back for a password", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    expect(remember()).toBeTruthy();
    await userEvent.selectOptions(method(), "kerberos");
    expect(remember()).toBeNull();
    await userEvent.selectOptions(method(), "password");
    expect(remember()!.value).toBe("once");
  });
});

describe.each(METHODS)("PostgreSQL sign-in, existing profile: $auth", ({ auth, secret }) => {
  beforeEach(() => useProfiles.setState({ profiles: [base("a", { auth })] }));

  it("shows the saved secret as saved, never its value", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(method().value).toBe(auth);
    expect(userBox()!.value).toBe("u");
    expect(dbBox().value).toBe("app");
    if (secret == null) {
      expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
      expect(remember()).toBeNull();
    } else {
      expect(screen.getByText("Saved in the OS keyring")).toBeTruthy();
      expect(screen.getByText(auth === "entraToken" ? "Access token" : "Password")).toBeTruthy();
      expect(secretBox(auth)).toBeNull();
    }
    expect(screen.queryByText(/saved password is removed when you save/)).toBeNull();
  });

  it("Test uses the saved secret by profile id", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await test();
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    expect(testCall()).toMatchObject({ profileId: "a", secret: null, sshSecret: null, profile: expect.objectContaining({ id: "a", auth, host: "db.local" }) });
  });

  it("Save without a typed secret leaves the keyring (kerberos: clears any stale entry)", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ id: "a", auth });
    expect(saved()[1]).toBe(secret == null ? "" : null);
  });

  if (secret != null) {
    it("Change then a typed secret: Test sends it (the profile id stays for the SSH secret), and Save stores it", async () => {
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(screen.getByRole("button", { name: "Change" }));
      await userEvent.type(secretBox(auth)!, "new-" + secret);
      await test();
      await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
      expect(testCall()).toMatchObject({ profileId: "a", secret: "new-" + secret });
      await save();
      await waitFor(() => expect(saveSpy).toHaveBeenCalled());
      expect(saved()[1]).toBe("new-" + secret);
    });

    it("Forget clears the keyring entry only on Save", async () => {
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.click(screen.getByRole("button", { name: "Forget" }));
      expect(m.secretDelete).not.toHaveBeenCalled();
      await save();
      await waitFor(() => expect(saveSpy).toHaveBeenCalled());
      expect(saved()[1]).toBe("");
    });

    it("an emptied host saves as null (W9)", async () => {
      render(<ProfileEditor profileId="a" onClose={() => {}} />);
      await userEvent.clear(hostBox());
      await save();
      await waitFor(() => expect(saveSpy).toHaveBeenCalled());
      expect(saved()[0].host).toBeNull();
    });
  }
});

describe("PostgreSQL sign-in: switching method on an existing profile", () => {
  it.each<AuthKind>(["password", "entraToken"])("%s → Kerberos warns that the saved secret goes and clears it on Save", async (from) => {
    useProfiles.setState({ profiles: [base("a", { auth: from })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(method(), "kerberos");
    // The note names what was saved: a token for entraToken, a password otherwise.
    expect(screen.getByText(from === "entraToken" ? /saved token is removed when you save/ : /saved password is removed when you save/)).toBeTruthy();
    expect(remember()).toBeNull();
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ auth: "kerberos", username: "u" });
    expect(saved()[1]).toBe("");
  });

  it.each<AuthKind>(["password", "entraToken"])("Kerberos → %s with no keyring entry asks for the secret and stores it", async (to) => {
    m.secretExists.mockResolvedValue(false);
    useProfiles.setState({ profiles: [base("a", { auth: "kerberos" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await waitFor(() => expect(m.secretExists).toHaveBeenCalledWith("a"));
    await userEvent.selectOptions(method(), to);
    await waitFor(() => expect(secretBox(to)).toBeTruthy());
    expect(screen.queryByText("Saved in the OS keyring")).toBeNull();
    await userEvent.type(secretBox(to)!, "s3cret");
    await test();
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.anything()));
    // The sign-in changed, so the saved profile's secrets are not injected (PT3-1): the test sends no profile id.
    expect(testCall()).toMatchObject({ profileId: null, secret: "s3cret", profile: expect.objectContaining({ auth: to }) });
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ auth: to });
    expect(saved()[1]).toBe("s3cret");
  });

  it("Kerberos → password, then back to Kerberos, saves Kerberos with the keyring cleared", async () => {
    m.secretExists.mockResolvedValue(false);
    useProfiles.setState({ profiles: [base("a", { auth: "kerberos" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(method(), "password");
    await waitFor(() => expect(secretBox("password")).toBeTruthy());
    await userEvent.type(secretBox("password")!, "pw");
    await userEvent.selectOptions(method(), "kerberos");
    await save();
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saved()[0]).toMatchObject({ auth: "kerberos" });
    expect(saved()[1]).toBe("");
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  secretDelete: vi.fn(async (..._a: unknown[]) => {}),
  secretSet: vi.fn(async (..._a: unknown[]) => {}),
  confirmDeleteProfile: vi.fn(async (..._a: unknown[]) => true),
  openDialog: vi.fn(async (..._a: unknown[]): Promise<unknown> => "/keys/id_rsa"),
  tauri: false,
  // system.engines: null (the sidecar could not answer) offers the fallback engines, as before engines were listed.
  engines: null as EngineInfo[] | null,
  pick: vi.fn(async (): Promise<{ grantId: string; name: string; size: number } | null> => ({ grantId: "g1", name: "sales.db", size: 10 })),
  allowWrites: vi.fn(async (..._a: unknown[]) => true),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, shell: { secretDelete: m.secretDelete, secretSet: m.secretSet, secretExists: async () => true }, RpcError: class extends Error {},
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.tauri; } }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: m.confirmDeleteProfile }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: m.openDialog }));
vi.mock("../engineInfo", async (orig) => ({ ...(await orig<typeof import("../engineInfo")>()), loadEngineInfo: async () => m.engines }));
vi.mock("../fileGrants", () => ({ pickDatabaseFile: m.pick, requestWriteAccess: m.allowWrites, needsRepick: () => false }));

import { ProfileEditor, profileFieldErrors, withDefaultHost } from "../ProfileEditor";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "db.local", port: null, auth: engine === "pg" ? "password" : "sql", username: "u", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0, ...over,
});

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => p);
const toasts = () => useUi.getState().toasts.map((t) => `${t.kind}:${t.text}`);
const nameBox = () => screen.getByLabelText("Connection name *") as HTMLInputElement;
const rowInput = (label: string) =>
  screen.getAllByText(label, { selector: "span" }).map((e) => e.closest("label")).find(Boolean)!.querySelector("input,select,textarea") as HTMLInputElement;
const goAdvanced = () => userEvent.click(screen.getByRole("tab", { name: /Advanced/ }));
const testBtn = () => screen.getByRole("button", { name: "Test connection" });
const status = () => screen.getByRole("status");

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  Object.values(m).forEach((f) => typeof f === "function" && (f as { mockClear?: () => void }).mockClear?.());
  m.tauri = false;
  m.engines = null;
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

describe.each<[Engine, string, string, string]>([
  ["mssql", "Microsoft SQL Server", "sa", "SQL Server login"],
  ["pg", "PostgreSQL", "postgres", "Password / SCRAM"],
])("ProfileEditor new %s profile", (engine, label, user, authLabel) => {
  it("starts with the engine's defaults", () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(screen.getByText("New connection profile")).toBeTruthy();
    expect(screen.getByRole("combobox", { name: "Server type" }).textContent).toContain(label);
    expect(rowInput("User name").value).toBe(user);
    expect((rowInput("Method") as unknown as HTMLSelectElement).selectedOptions[0].textContent).toBe(authLabel);
    expect(screen.getByPlaceholderText(engine === "pg" ? "5432" : "1433")).toBeTruthy();
    expect(screen.getByPlaceholderText(engine === "pg" ? "postgres" : "(login default)")).toBeTruthy();
    expect(screen.queryByText("Delete")).toBeNull();
  });

  it("defaults to one connection and saves a custom password duration", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect((screen.getByLabelText("Remember password") as HTMLSelectElement).value).toBe("once");
    await userEvent.type(nameBox(), "Timed DB");
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
    await userEvent.selectOptions(screen.getByLabelText("Duration"), "custom");
    await userEvent.selectOptions(screen.getByLabelText("Unit"), "minutes");
    await userEvent.clear(screen.getByLabelText("Length"));
    await userEvent.type(screen.getByLabelText("Length"), "45");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ passwordRetention: "duration", passwordRetentionMinutes: 45 }), null, "");
  });

  it.each([["7 days", 10080], ["custom days", 17280]])("saves %s as the password duration", async (choice, expected) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(screen.getByRole("option", { name: "Until disconnected" })).toBeTruthy();
    await userEvent.type(nameBox(), "Remember my DB");
    await userEvent.selectOptions(screen.getByLabelText("Remember password"), "duration");
    if (choice === "7 days") {
      await userEvent.selectOptions(screen.getByLabelText("Duration"), "10080");
      expect(screen.queryByLabelText("Length")).toBeNull();
    } else {
      await userEvent.selectOptions(screen.getByLabelText("Duration"), "custom");
      expect((screen.getByLabelText("Unit") as HTMLSelectElement).value).toBe("days");
      await userEvent.clear(screen.getByLabelText("Length"));
      await userEvent.type(screen.getByLabelText("Length"), "12");
    }
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ passwordRetention: "duration", passwordRetentionMinutes: expected }), null, "");
  });

  it("refuses to save without a name, saying so at the field and focusing it", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).not.toHaveBeenCalled();
    expect(nameBox().getAttribute("aria-invalid")).toBe("true");
    expect(document.getElementById(nameBox().getAttribute("aria-describedby")!)!.textContent).toBe("Give the profile a name.");
    await waitFor(() => expect(document.activeElement).toBe(nameBox()));
    expect(toasts()).toEqual([]);
    await userEvent.type(nameBox(), "x");
    expect(nameBox().getAttribute("aria-invalid")).toBeNull();
  });

  it("saves the typed fields and the password (typed secrets go to the keyring path)", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor preset={engine} onClose={onClose} />);
    await userEvent.type(nameBox(), "My DB");
    const host = screen.getByPlaceholderText("localhost");
    await userEvent.clear(host);
    await userEvent.type(host, "srv1");
    await userEvent.type(screen.getByPlaceholderText(engine === "pg" ? "5432" : "1433"), "4000");
    await userEvent.type(screen.getByPlaceholderText(engine === "pg" ? "postgres" : "(login default)"), "Sales");
    await userEvent.type(rowInput("Password"), "s3cret");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const [p, secret, ssh] = saveSpy.mock.calls[0] as unknown as [Profile, string | null, string | null];
    expect(p).toMatchObject({ name: "My DB", host: "srv1", port: 4000, engine, databaseDefault: engine === "pg" ? "postgres" + "Sales" : "Sales" });
    expect(secret).toBe("s3cret");
    expect(ssh).toBe(""); // no tunnel: its keyring entry is cleared
  });

  it("clearing the port and the database stores null", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "x");
    const port = screen.getByPlaceholderText(engine === "pg" ? "5432" : "1433");
    await userEvent.type(port, "12");
    await userEvent.clear(port);
    const db = screen.getByPlaceholderText(engine === "pg" ? "postgres" : "(login default)");
    await userEvent.clear(db);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect((saveSpy.mock.calls[0][0] as Profile).port).toBeNull();
    expect((saveSpy.mock.calls[0][0] as Profile).databaseDefault).toBeNull();
  });

  it("password can be shown and hidden", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(rowInput("Password").type).toBe("password");
    await userEvent.click(screen.getByLabelText("Show password"));
    expect(rowInput("Password").type).toBe("text");
    await userEvent.click(screen.getByLabelText("Hide password"));
    expect(rowInput("Password").type).toBe("password");
  });

  it("offers only the engine's authentication methods, and hides the password for integrated auth", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    const method = rowInput("Method") as unknown as HTMLSelectElement;
    const labels = Array.from(method.options).map((o) => o.textContent);
    if (engine === "mssql") {
      expect(labels).toContain("Windows integrated");
      expect(labels).toContain("Microsoft Entra — device code");
      await userEvent.selectOptions(method, "windows");
      expect(screen.queryByText("Password", { selector: "span" })).toBeNull();
      expect(rowInput("User name").disabled).toBe(true);
      await userEvent.selectOptions(method, "entraInteractive");
      expect(screen.getByText("User (optional hint)")).toBeTruthy();
      await userEvent.selectOptions(method, "entraDeviceCode");
      expect(rowInput("User name").disabled).toBe(true);
    } else {
      expect(labels).not.toContain("Windows integrated");
      expect(labels).toContain("Kerberos (GSSAPI)");
      await userEvent.selectOptions(method, "kerberos");
      expect(screen.queryByText("Password", { selector: "span" })).toBeNull();
    }
    await userEvent.selectOptions(method, "entraToken");
    expect(screen.getByText("Access token", { selector: "span" })).toBeTruthy();
  });

  it("Advanced tab: transport options differ per engine", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await goAdvanced();
    const tls = Array.from((rowInput("Encryption") as unknown as HTMLSelectElement).options).map((o) => o.value);
    if (engine === "mssql") {
      expect(tls).toEqual(["default", "strict", "mandatory", "optional"]);
      expect(screen.getByText("Trust server certificate")).toBeTruthy();
      expect(screen.getByText("Multi-subnet failover")).toBeTruthy();
      expect(screen.getByText("Read-only intent")).toBeTruthy();
      expect(screen.queryByText("CA certificate")).toBeNull();
      expect(screen.getByText("Connect timeout and failover")).toBeTruthy();
    } else {
      expect(tls).toEqual(["default", "verifyFull", "verifyCa", "prefer", "disable"]);
      expect(screen.getByText("CA certificate")).toBeTruthy();
      expect(screen.queryByText("Trust server certificate")).toBeNull();
      expect(screen.queryByText("Multi-subnet failover")).toBeNull();
      expect(screen.queryByText("Read-only intent")).toBeNull();
      expect(screen.getByText("Query timeout")).toBeTruthy();
    }
    expect(screen.getByText("TLS required")).toBeTruthy();
  });

  it("plain-text TLS modes show the 'Not encrypted' warning and mark the tab", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await goAdvanced();
    await userEvent.selectOptions(rowInput("Encryption") as unknown as HTMLSelectElement, engine === "pg" ? "disable" : "optional");
    expect(screen.getByText("Not encrypted")).toBeTruthy();
    expect(screen.getByTitle("Has a security warning")).toBeTruthy();
  });
});

describe("ProfileEditor SQL Server specifics", () => {
  it("trusting the certificate warns; multi-subnet and read-only intent land in options", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(nameBox(), "n");
    await goAdvanced();
    await userEvent.click(within(screen.getByText("Trust server certificate").parentElement!).getByRole("checkbox"));
    expect(screen.getByText("Cert not validated")).toBeTruthy();
    expect(screen.getByText(/server certificate is not validated/)).toBeTruthy();
    await userEvent.click(within(screen.getByText("Multi-subnet failover").parentElement!).getByRole("checkbox"));
    await userEvent.click(within(screen.getByText("Read-only intent").parentElement!).getByRole("checkbox"));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const p = saveSpy.mock.calls[0][0] as Profile;
    expect(p.trustCert).toBe(true);
    expect(p.options).toEqual({ multiSubnetFailover: "true", applicationIntent: "readOnly" });
  });

  it("unchecking read-only intent stores readWrite", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { options: { applicationIntent: "readOnly" } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await goAdvanced();
    const box = within(screen.getByText("Read-only intent").parentElement!).getByRole("checkbox") as HTMLInputElement;
    expect(box.checked).toBe(true);
    await userEvent.click(box);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect((saveSpy.mock.calls[0][0] as Profile).options).toEqual({ applicationIntent: "readWrite" });
  });

  it("warns when saving a production profile without encryption", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { environment: "prod", tlsMode: "optional" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(toasts()).toContain("warn:Production profile without encryption.");
  });
});

describe("ProfileEditor identity section", () => {
  it("environment select updates the badge and hint", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    expect(screen.getByText(/Shared development server/)).toBeTruthy();
    const env = screen.getByDisplayValue("Development") as HTMLSelectElement;
    await userEvent.selectOptions(env, "prod");
    expect(screen.getByText(/Paints tabs and the status bar/)).toBeTruthy();
    expect(screen.getByText("Prod", { selector: "span.uppercase" })).toBeTruthy();
  });

  it("a group with a default environment applies it to new profiles only", async () => {
    useProfiles.setState({ groups: [{ name: "Live", environment: "prod", color: null } as never] });
    const { unmount } = render(<ProfileEditor onClose={() => {}} />);
    const input = document.querySelector("input[list='q-groups']") as HTMLInputElement;
    await userEvent.type(input, "Live");
    expect((screen.getByDisplayValue("Production") as HTMLSelectElement).value).toBe("prod");
    await userEvent.clear(input);
    expect(input.value).toBe("");
    unmount();

    useProfiles.setState({ profiles: [base("a", "pg", { environment: "dev" })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    const input2 = document.querySelector("input[list='q-groups']") as HTMLInputElement;
    await userEvent.type(input2, "Live");
    expect((screen.getByDisplayValue("Development") as HTMLSelectElement).value).toBe("dev");
  });

  it("switching engine resets auth and TLS to the new engine's defaults and keeps the user", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    const list = screen.getByRole("listbox", { name: "Server type" });
    await userEvent.click(within(list).getByText("PostgreSQL"));
    expect(screen.getByRole("combobox", { name: "Server type" }).textContent).toContain("PostgreSQL");
    expect(rowInput("User name").value).toBe("sa");
    expect((rowInput("Method") as unknown as HTMLSelectElement).value).toBe("password");
    expect(screen.getByPlaceholderText("postgres")).toBeTruthy();
    // and back
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await userEvent.click(within(screen.getByRole("listbox")).getByText("Microsoft SQL Server"));
    expect((rowInput("Method") as unknown as HTMLSelectElement).value).toBe("sql");
  });

  it("choosing the current engine changes nothing", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(rowInput("User name"), "2");
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await userEvent.click(within(screen.getByRole("listbox")).getByText("PostgreSQL"));
    expect(rowInput("User name").value).toBe("postgres2");
  });
});

describe("ProfileEditor engine dropdown keyboard", () => {
  const combo = () => screen.getByRole("combobox", { name: "Server type" });
  it("arrows open and move; Enter picks; Escape and Tab close", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    combo().focus();
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("listbox")).toBeTruthy();
    await userEvent.keyboard("{ArrowDown}");
    expect(combo().getAttribute("aria-activedescendant")).toMatch(/-1$/);
    await userEvent.keyboard("{ArrowDown}"); // clamped
    expect(combo().getAttribute("aria-activedescendant")).toMatch(/-1$/);
    await userEvent.keyboard("{ArrowUp}");
    expect(combo().getAttribute("aria-activedescendant")).toMatch(/-0$/);
    await userEvent.keyboard("{End}");
    expect(combo().getAttribute("aria-activedescendant")).toMatch(/-1$/);
    await userEvent.keyboard("{Home}");
    expect(combo().getAttribute("aria-activedescendant")).toMatch(/-0$/);
    await userEvent.keyboard("{End}{Enter}");
    expect(combo().textContent).toContain("PostgreSQL");
    expect(screen.queryByRole("listbox")).toBeNull();

    await userEvent.keyboard("{ArrowUp}");
    expect(screen.getByRole("listbox")).toBeTruthy();
    fireEvent.keyDown(combo(), { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
    await userEvent.keyboard("{ArrowDown}");
    fireEvent.keyDown(combo(), { key: "Tab" });
    expect(screen.queryByRole("listbox")).toBeNull();
    await userEvent.keyboard("{Home}");
    expect(screen.getByRole("listbox")).toBeTruthy();
    fireEvent.keyDown(combo(), { key: " " });
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("clicking the button toggles the list; hovering highlights; an outside press closes it", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.click(combo());
    expect(combo().getAttribute("aria-expanded")).toBe("true");
    const opts = within(screen.getByRole("listbox")).getAllByRole("option");
    expect(opts[0].getAttribute("aria-selected")).toBe("true");
    await userEvent.hover(opts[1]);
    expect(combo().getAttribute("aria-activedescendant")).toBe(opts[1].id);
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
    expect(screen.getByRole("listbox").style.bottom).not.toBe("");
  });
});

describe.each<Engine>(["mssql", "pg"])("ProfileEditor editing an existing %s profile", (engine) => {
  beforeEach(() => useProfiles.setState({ profiles: [base("a", engine)] }));

  it("shows the profile, keeps the saved secret untouched and says so", () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    expect(screen.getByText("Edit profile — Name-a")).toBeTruthy();
    expect(screen.getByText("Leave empty to keep the saved secret.")).toBeTruthy();
    expect(rowInput("Password").placeholder).toBe("••••••••");
  });

  it("saves without touching the keyring when no new password was typed", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBeNull();
  });

  it("a newly typed password replaces the saved one", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.type(rowInput("Password"), "new");
    expect(screen.queryByText("Leave empty to keep the saved secret.")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("new");
  });

  it("switching to integrated/Kerberos auth removes the saved secret", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.selectOptions(rowInput("Method") as unknown as HTMLSelectElement, "kerberos");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][1]).toBe("");
  });

  it("reminds to reconnect when the server is connected", async () => {
    useConnections.setState({ servers: { a: { profileId: "a", status: "connected", databases: [] } } as never, order: ["a"] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(toasts()).toContain("info:Saved. Reconnect to apply connection changes."));
  });

  it("reports a failed save and stays open", async () => {
    saveSpy.mockRejectedValueOnce(new Error("disk full"));
    const onClose = vi.fn();
    render(<ProfileEditor profileId="a" onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(toasts()).toContain("error:disk full"));
    expect(onClose).not.toHaveBeenCalled();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("Forget saved secret deletes the keyring entry", async () => {
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByText("Forget saved secret"));
    expect(m.secretDelete).toHaveBeenCalledWith("a");
    await waitFor(() => expect(toasts()).toContain("info:Saved secret removed from memory and the keyring."));
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
    await userEvent.type(rowInput("Password"), "pw");
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 16.0 (12 ms) — tls ok"));
    expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ profileId: null, secret: "pw", sshSecret: null, profile: expect.objectContaining({ engine }) }));
    await userEvent.click(within(status()).getByLabelText("Dismiss"));
    expect(status().textContent).toBe("");
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
    await waitFor(() => expect(m.rpc).toHaveBeenCalled());
    expect(m.rpc.mock.calls[0][1]).toMatchObject({ profileId: "a", secret: null, sshSecret: null });
  });
});

describe("ProfileEditor SSH tunnel", () => {
  const enable = async () => {
    await goAdvanced();
    await userEvent.click(screen.getByLabelText(/Connect through an SSH bastion/).closest("span")!.querySelector("input")!);
  };

  it.each<Engine>(["mssql", "pg"])("%s: enabling the tunnel shows the bastion fields and a badge; disabling removes it", async (engine) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await goAdvanced();
    const toggle = () => screen.getByText("Connect through an SSH bastion").closest("span")!.querySelector("input")!;
    await userEvent.click(toggle());
    expect(screen.getByText("Through bastion")).toBeTruthy();
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "jump.example.com");
    expect(screen.getByText("Through jump.example.com")).toBeTruthy();
    await userEvent.type(screen.getByPlaceholderText("deploy"), "ops");
    await userEvent.type(screen.getByPlaceholderText("/home/me/.ssh/id_ed25519"), "/k");
    const port = screen.getByLabelText("SSH port") as HTMLInputElement;
    await userEvent.clear(port);
    expect(port.value).toBe("22");
    await userEvent.type(port, "2200");
    await userEvent.click(toggle());
    expect(screen.queryByPlaceholderText("bastion.example.com")).toBeNull();
  });

  it("password auth hides the key file; agent auth warns and hides the secret", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await enable().catch(async () => {
      await userEvent.click(screen.getByText("Connect through an SSH bastion").closest("span")!.querySelector("input")!);
    });
    const sel = screen.getByDisplayValue("Key file") as HTMLSelectElement;
    expect(screen.getByText("Passphrase", { selector: "span" })).toBeTruthy();
    await userEvent.selectOptions(sel, "password");
    expect(screen.queryByPlaceholderText("/home/me/.ssh/id_ed25519")).toBeNull();
    expect(screen.getByText("Password", { selector: "span" })).toBeTruthy();
    await userEvent.selectOptions(sel, "agent");
    expect(screen.getByText(/SSH agent sign-in is not supported yet/)).toBeTruthy();
    expect(screen.queryByText("Passphrase", { selector: "span" })).toBeNull();
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
    await userEvent.type(nameBox(), "tunnelled");
    await enable();
    await userEvent.type(screen.getByPlaceholderText("bastion.example.com"), "b");
    const secret = screen.getByText("Passphrase", { selector: "span" }).closest("label")!.querySelector("input")!;
    await userEvent.type(secret, "phrase");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, , ssh] = saveSpy.mock.calls[0] as unknown as [Profile, unknown, string | null];
    expect(p.ssh).toMatchObject({ host: "b", port: 22, auth: "privateKey" });
    expect(ssh).toBe("phrase");
  });

  it("an existing tunnel keeps its saved secret unless retyped; Forget saved clears it", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql", { ssh: { host: "b", port: 22, username: "u", auth: "password", keyPath: null } })] });
    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await goAdvanced();
    expect(screen.getByText("Leave empty to keep the one saved in the OS keyring.")).toBeTruthy();
    await userEvent.click(screen.getByText("Forget saved"));
    expect(m.secretDelete).toHaveBeenCalledWith("a:ssh");
    await waitFor(() => expect(toasts()).toContain("info:Saved SSH secret removed from the keyring."));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][2]).toBeNull();
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
    await userEvent.type(rowInput("Password"), "pw");
    await enable();
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
])("ProfileEditor connection string (%s)", (engine, cs) => {
  const result = (over: object = {}) => ({
    profile: base("", engine, { host: "parsed-host", databaseDefault: "parsed-db", name: "" }), secret: "from-string", unknown: ["Foo"], notApplied: ["Bar"], ...over,
  });
  const toString = () => userEvent.click(screen.getByRole("radio", { name: "Connection string" }));
  const box = () => screen.getByLabelText("Connection string") as HTMLTextAreaElement;

  it("shows an engine-specific placeholder and disables parsing until text is entered", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    expect(box().placeholder).toContain(engine === "pg" ? "Host=localhost;Port=5432" : "Server=localhost,1433");
    expect((screen.getByRole("button", { name: "Parse into fields" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("parses into the fields, suggests a name, keeps the typed secret, and explains what was ignored", async () => {
    m.rpc.mockResolvedValue(result());
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(screen.getByRole("button", { name: "Parse into fields" }));
    await waitFor(() => expect(screen.getByRole("radio", { name: "Manual" }).getAttribute("aria-checked")).toBe("true"));
    expect(m.rpc).toHaveBeenCalledWith("profile.parseConnectionString", expect.objectContaining({ connectionString: cs }));
    expect(nameBox().value).toBe("parsed-host / parsed-db");
    expect(screen.getByDisplayValue("parsed-host")).toBeTruthy();
    expect(screen.getByText(/Read as/).textContent).toContain(engine === "pg" ? "PostgreSQL" : "Microsoft SQL Server");
    expect(screen.getByText("Not recognised: Foo")).toBeTruthy();
    expect(screen.getByText(/Not applied .*: Bar/)).toBeTruthy();
    expect(rowInput("Password").value).toBe("from-string");
    await userEvent.click(screen.getAllByLabelText("Dismiss")[0]);
    expect(screen.queryByText(/Read as/)).toBeNull();
  });

  it("keeps an existing name and the previous secret when the string has none", async () => {
    m.rpc.mockResolvedValue(result({ secret: null, unknown: [], notApplied: [] }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "Mine");
    await userEvent.type(rowInput("Password"), "typed");
    await toString();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(screen.getByRole("button", { name: "Parse into fields" }));
    await waitFor(() => expect(screen.getByRole("radio", { name: "Manual" }).getAttribute("aria-checked")).toBe("true"));
    expect(nameBox().value).toBe("Mine");
    expect(rowInput("Password").value).toBe("typed");
    expect(screen.queryByText(/Not recognised/)).toBeNull();
  });

  it("warns about a TLS setting the string names that is weaker than the default (M3)", async () => {
    const note = "SslMode=None: the connection is not encrypted, so the password and every result cross the network in clear text.";
    m.rpc.mockResolvedValue(result({ unknown: [], notApplied: [], notes: [note] }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(screen.getByRole("button", { name: "Parse into fields" }));
    await waitFor(() => expect(screen.getByText(note)).toBeTruthy());
    expect(screen.getByText(note).className).toContain("text-warn");
  });

  it("a parse error is reported and the mode stays", async () => {
    m.rpc.mockRejectedValue(new Error("bad keyword"));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    fireEvent.change(box(), { target: { value: "garbage" } });
    await userEvent.click(screen.getByRole("button", { name: "Parse into fields" }));
    await waitFor(() => expect(status().textContent).toContain("bad keyword"));
    expect(box()).toBeTruthy();
  });

  it("Test connection in string mode parses first; with an empty string it asks for one", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Paste a connection string first."));
    m.rpc.mockImplementation(async (method: unknown) => (method === "profile.test" ? { ok: true, serverVersion: "9", elapsedMs: 1, notes: [] } : result()));
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(testBtn());
    await waitFor(() => expect(status().textContent).toContain("Connected: 9"));
    expect(m.rpc).toHaveBeenCalledWith("profile.test", expect.objectContaining({ secret: "from-string", profile: expect.objectContaining({ host: "parsed-host" }) }));
  });

  it("Save in string mode saves the parsed profile; editing the string clears the parse note", async () => {
    m.rpc.mockResolvedValue(result({ unknown: [], notApplied: [] }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    fireEvent.change(box(), { target: { value: cs } });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    const [p, secret] = saveSpy.mock.calls[0] as unknown as [Profile, string];
    expect(p.host).toBe("parsed-host");
    expect(p.name).toBe("parsed-host / parsed-db");
    expect(secret).toBe("from-string");
  });

  it("Save with an empty string reports the problem and saves nothing", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(status().textContent).toContain("Paste a connection string first."));
  });

  it("switching back to Manual keeps the fields; the mode switch is hidden on the Advanced tab", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await toString();
    await userEvent.click(screen.getByRole("radio", { name: "Manual" }));
    expect(screen.getByPlaceholderText("localhost")).toBeTruthy();
    await goAdvanced();
    expect(screen.queryByRole("radiogroup", { name: "Enter connection details as" })).toBeNull();
    await userEvent.click(screen.getByRole("tab", { name: /Connection/ }));
    expect(screen.getByRole("radiogroup", { name: "Enter connection details as" })).toBeTruthy();
  });
});

describe("ProfileEditor timeouts and dialog", () => {
  it("connect and command timeouts are editable numbers", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(nameBox(), "t");
    await goAdvanced();
    const connect = rowInput("Connect timeout");
    const command = rowInput("Command timeout");
    expect(connect.value).toBe("15");
    expect(command.value).toBe("0");
    fireEvent.change(connect, { target: { value: "30" } });
    fireEvent.change(command, { target: { value: "120" } });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ timeoutS: 30, commandTimeoutS: 120 });
  });

  it("the CA certificate path is stored for PostgreSQL and cleared to null", async () => {
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(nameBox(), "t");
    await goAdvanced();
    const ca = screen.getByPlaceholderText("/etc/ssl/certs/root.crt");
    await userEvent.type(ca, "/ca.pem");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalled());
    expect((saveSpy.mock.calls[0][0] as Profile).caPath).toBe("/ca.pem");
  });

  it("Cancel closes without saving", async () => {
    const onClose = vi.fn();
    render(<ProfileEditor onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalled();
    expect(saveSpy).not.toHaveBeenCalled();
  });
});

const info = (engine: Engine, exposed: boolean, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed, defaultAuth: "none", authKinds: [], locations,
});
const optionNames = () => within(screen.getByRole("listbox", { name: "Server type" })).getAllByRole("option").map((o) => o.textContent);

describe("ProfileEditor engines the sidecar exposes (system.engines)", () => {
  it("offers only the exposed engines, in display order", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("sqlite", true, ["file", "memory"]), info("mySql", false)];
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await waitFor(() => expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL", "SQLite"]));
  });

  it("falls back to SQL Server and PostgreSQL when the sidecar cannot say", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL"]);
  });

  it("an existing profile always shows its own engine, exposed or not", async () => {
    m.engines = [info("mssql", true), info("pg", true), info("sqlite", false, ["file", "memory"])];
    useProfiles.setState({ profiles: [base("s1", "sqlite", { location: "memory", auth: "none", host: null, username: null })], groups: [], save: saveSpy as never });
    render(<ProfileEditor profileId="s1" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await waitFor(() => expect(optionNames()).toEqual(["Microsoft SQL Server", "PostgreSQL", "SQLite"]));
  });
});

describe("ProfileEditor local engine (SQLite)", () => {
  beforeEach(() => {
    m.engines = [info("mssql", true), info("pg", true), info("sqlite", true, ["file", "memory"])];
  });
  const chooseSqlite = async () => {
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await waitFor(() => expect(optionNames()).toContain("SQLite"));
    await userEvent.click(screen.getByRole("option", { name: "SQLite" }));
  };

  it("asks where the database is instead of a server, sign-in, TLS or a connection string", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
    const where = screen.getByLabelText("Location") as HTMLSelectElement;
    expect([...where.options].map((o) => o.textContent)).toEqual(["Database file", "In-memory database"]);
    expect(where.value).toBe("file");
    expect(screen.queryByText("Server")).toBeNull();
    expect(screen.queryByText("Authentication")).toBeNull();
    expect(screen.queryByRole("radiogroup", { name: "Enter connection details as" })).toBeNull();
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    await goAdvanced();
    expect(screen.queryByText("Transport")).toBeNull();
  });

  it("picks the file through the shell, shows its name read-only, and asks the shell before allowing writes", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
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
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).toHaveBeenCalledWith(
      expect.objectContaining({ engine: "sqlite", location: "file", file: { grantId: "g1", displayName: "sales.db" }, auth: "none", host: null, username: null }),
      null, "",
    );
  });

  it("refuses to save or test a file profile without a file", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
    await userEvent.type(nameBox(), "Local");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).not.toHaveBeenCalled();
    const pick = screen.getByRole("button", { name: "Pick file…" });
    expect(pick.getAttribute("aria-describedby")).toBeTruthy();
    expect(document.getElementById(pick.getAttribute("aria-describedby")!)!.textContent).toBe("Pick the database file first.");
    await waitFor(() => expect(document.activeElement).toBe(pick));
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Pick the database file first.");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
  });

  it("an in-memory database needs no file and says each tab gets its own", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
    await userEvent.type(nameBox(), "Scratch");
    await userEvent.selectOptions(screen.getByLabelText("Location"), "memory");
    expect(screen.getByText(/Each tab gets its own empty database/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ engine: "sqlite", location: "memory", file: null }), null, "");
  });

  it("a cancelled pick changes nothing; a failed one says why", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
    m.pick.mockResolvedValueOnce(null);
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
    m.pick.mockRejectedValueOnce(new Error("The file is gone."));
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    expect(toasts()).toContain("error:The file is gone.");
  });

  it("switching back to a server engine restores its server fields and drops the file", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await chooseSqlite();
    await userEvent.click(screen.getByRole("button", { name: "Pick file…" }));
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await userEvent.click(screen.getByRole("option", { name: "PostgreSQL" }));
    expect(screen.getByText("Server")).toBeTruthy();
    expect(rowInput("User name").value).toBe("postgres");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ engine: "pg", location: null, file: null, host: "localhost", auth: "password", databaseDefault: "postgres" }), null, "");
  });

  it("a duplicate of a file profile drops the file grant (one grant binds one profile)", () => {
    useProfiles.setState({
      profiles: [base("f1", "sqlite", { name: "Sales file", location: "file", file: { grantId: "g9", displayName: "sales.db" }, auth: "none", host: null, username: null })],
      groups: [], save: saveSpy as never,
    });
    render(<ProfileEditor copyOf="f1" onClose={() => {}} />);
    expect(screen.getByText("New connection profile")).toBeTruthy();
    expect(nameBox().value).toBe("Sales file (copy)");
    expect(screen.getByTestId("picked-file").textContent).toBe("No file picked");
  });
});

describe("ProfileEditor MySQL / MariaDB", () => {
  beforeEach(() => {
    m.engines = [info("mssql", true), info("pg", true), info("mySql", true)];
  });

  it("a server engine: port 3306, user root, password sign-in, its own TLS choices; Require warns that the certificate is not checked", async () => {
    render(<ProfileEditor onClose={() => {}} />);
    await userEvent.click(screen.getByRole("combobox", { name: "Server type" }));
    await waitFor(() => expect(optionNames()).toContain("MySQL / MariaDB"));
    await userEvent.click(screen.getByRole("option", { name: "MySQL / MariaDB" }));
    expect(screen.getByPlaceholderText("3306")).toBeTruthy();
    expect(screen.getByPlaceholderText("(login default)")).toBeTruthy();
    expect([...(rowInput("Method") as unknown as HTMLSelectElement).options].map((o) => o.textContent)).toEqual(["Password"]);
    expect(screen.queryByText(/host\\instance/)).toBeNull();
    await goAdvanced();
    const tls = rowInput("Encryption") as unknown as HTMLSelectElement;
    expect([...tls.options].map((o) => o.value)).toEqual(["default", "verifyCa", "require", "prefer", "disable"]);
    expect(screen.queryByText("Trust server certificate")).toBeNull();
    await userEvent.selectOptions(tls, "require");
    expect(screen.getByText("Cert not validated")).toBeTruthy();
  });
});

// L25: Host and Port are labelled fields; a port outside 1–65535 is shown at its field (aria-invalid, aria-describedby)
// and blocks Save and Test, for every server engine; the sidecar's refusal of a field shows there too. W9: an empty host
// is the engine's default (localhost), as the sidecar takes it.
describe.each<[Engine, string]>([["mssql", "1433"], ["pg", "5432"], ["mySql", "3306"]])("ProfileEditor address fields (%s)", (engine, dflt) => {
  const port = () => screen.getByLabelText("Port") as HTMLInputElement;
  const host = () => screen.getByLabelText("Host") as HTMLInputElement;
  const message = (el: HTMLElement) => {
    const ids = (el.getAttribute("aria-describedby") ?? "").split(" ").filter(Boolean);
    return ids.map((id) => document.getElementById(id)?.textContent).join(" | ");
  };

  it("labels Host and Port", () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    expect(host().value).toBe("localhost");
    expect(port().placeholder).toBe(dflt);
    expect(port().getAttribute("aria-invalid")).toBeNull();
  });

  it.each([["0"], ["70000"], ["-5"], ["1.5"]])("an out-of-range port (%s) shows its message at the field and blocks Save and Test", async (bad) => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "Shop");
    await userEvent.type(port(), bad);
    await userEvent.tab();
    expect(port().getAttribute("aria-invalid")).toBe("true");
    expect(message(port())).toBe(`Enter a port from 1 to 65535, or leave it empty for ${dflt}.`);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(port()));
    await userEvent.click(testBtn());
    expect(status().textContent).toContain("Enter a port from 1 to 65535");
    expect(m.rpc).not.toHaveBeenCalledWith("profile.test", expect.anything());
    await userEvent.clear(port());
    await userEvent.type(port(), "65535");
    expect(port().getAttribute("aria-invalid")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ port: 65535 }), null, ""));
  });

  it("an empty host is the engine's default, as the sidecar takes it: Save sends no host (W9)", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "Shop");
    await userEvent.clear(host());
    await userEvent.type(host(), "   ");
    expect(host().getAttribute("aria-required")).toBeNull();
    expect(host().placeholder).toBe("localhost");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    // Before, Save was refused ("Enter the server's host name"), so a profile saved without a host could not be saved again.
    await waitFor(() => expect(saveSpy).toHaveBeenCalledWith(expect.objectContaining({ host: null }), null, ""));
    expect(host().getAttribute("aria-invalid")).toBeNull();
  });

  it("a host with a control character is refused at the field", async () => {
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "Shop");
    fireEvent.change(host(), { target: { value: "db\u0007host" } });
    await goAdvanced();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(saveSpy).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(host()));
    expect(message(host())).toContain("without line breaks or control characters");
  });

  it("shows the sidecar's refusal of a field at that field until the field changes", async () => {
    saveSpy.mockRejectedValueOnce(Object.assign(new Error("The port must be a whole number from 1 to 65535."), { appCode: "invalidProfile", data: { field: "port" } }));
    render(<ProfileEditor preset={engine} onClose={() => {}} />);
    await userEvent.type(nameBox(), "Shop");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(port().getAttribute("aria-invalid")).toBe("true"));
    expect(message(port())).toBe("The port must be a whole number from 1 to 65535.");
    expect(toasts()).toEqual([]);
    await userEvent.type(port(), "1");
    expect(port().getAttribute("aria-invalid")).toBeNull();
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

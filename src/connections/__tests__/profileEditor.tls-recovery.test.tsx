// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, EngineInfo, Profile, TlsCertError } from "../../ipc/contract";

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

import { ProfileEditor, looksLikeSelfSignedMssqlHost } from "../ProfileEditor";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const info = (engine: Engine, locations: EngineInfo["locations"] = ["server"]): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed: true, defaultAuth: "none", authKinds: [], locations,
});
const base = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({
  id, name: `Name-${id}`, engine, host: "198.51.100.10", port: 1433, auth: engine === "mssql" ? "sql" : "password", username: "sa", databaseDefault: null,
  environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
  createdAt: 0, updatedAt: 0, ...over,
});

const mssqlCertErr: TlsCertError = {
  engine: "mssql", host: "198.51.100.10:1433", reason: "untrustedRoot",
  canTrustCert: true, strictMode: false, canSwitchToRequire: false, supportsCaPath: false,
  message: "Connected to SQL Server at 198.51.100.10:1433, but its TLS certificate is not trusted.",
  detail: "UntrustedRoot",
};

const saveSpy = vi.fn(async (p: Profile, ..._r: unknown[]) => ({ ...p, id: p.id || "new-id" }));

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  m.engines = [info("mssql"), info("pg"), info("mySql"), info("oracle")];
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  saveSpy.mockClear();
  useProfiles.setState({ profiles: [], groups: [], save: saveSpy as never });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

describe("looksLikeSelfSignedMssqlHost", () => {
  it.each(["198.51.100.10", "198.51.100.10,1433", "tcp:10.0.0.5", "localhost", ".", "(local)", ".\\SQLEXPRESS", "SRV01\\INST", "sql-dev", "::1"])(
    "flags %s", (h) => expect(looksLikeSelfSignedMssqlHost(h)).toBe(true),
  );
  it.each(["", "   ", "myserver.database.windows.net", "sql.example.com", "db.local", "host.example.com:1433"])(
    "ignores %s", (h) => expect(looksLikeSelfSignedMssqlHost(h)).toBe(false),
  );
  it("defers single-label hostnames when includeSingleLabel is false to avoid mid-typing flicker", () => {
    expect(looksLikeSelfSignedMssqlHost("sql-dev", { includeSingleLabel: false })).toBe(false);
    expect(looksLikeSelfSignedMssqlHost("198.51.100.10", { includeSingleLabel: false })).toBe(true);
    expect(looksLikeSelfSignedMssqlHost("localhost", { includeSingleLabel: false })).toBe(true);
  });
});

describe("ProfileEditor: self-hosted SQL Server hint", () => {
  it("shows a one-click Trust server certificate offer for IP hosts and opens Encryption when accepted", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    const host = screen.getByLabelText("Host");
    await userEvent.type(host, "198.51.100.10");
    expect(screen.getByText(/likely uses a self-signed certificate/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Trust server certificate" }));
    expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("true");
    expect((screen.getByRole("checkbox", { name: /Trust server certificate/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.queryByText(/likely uses a self-signed certificate/)).toBeNull();
  });

  it("does not show the hint for domain FQDNs or PostgreSQL", async () => {
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Host"), "sql.example.com");
    expect(screen.queryByText(/likely uses a self-signed certificate/)).toBeNull();
    cleanup();
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Host"), "198.51.100.10");
    expect(screen.queryByText(/likely uses a self-signed certificate/)).toBeNull();
  });
});

describe("ProfileEditor: TLS certificate recovery on Test and Save & Connect", () => {
  it("Test connection: untrusted SQL Server cert shows recovery banner and one-click re-test succeeds", async () => {
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.test") return undefined;
      const { profile } = params as { profile: Profile };
      if (!profile.trustCert) return { ok: false, error: mssqlCertErr.message, elapsedMs: 12, notes: [], untrustedCertificate: mssqlCertErr };
      return { ok: true, serverVersion: "SQL Server 15.0.4430", elapsedMs: 18, notes: [] };
    });

    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Host"), "198.51.100.10");
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));

    const banner = await screen.findByTestId("tls-cert-offer");
    expect(banner.textContent).toContain("198.51.100.10:1433");
    // Shown once in the full-width feedback dock, never duplicated as a second status in the button row.
    expect(screen.getAllByRole("status")).toEqual([banner]);
    expect(screen.getAllByText(mssqlCertErr.message)).toHaveLength(1);
    expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("true");

    await userEvent.click(within(banner).getByRole("button", { name: "Trust server certificate & test again" }));
    await waitFor(() => expect(screen.getByRole("status").textContent).toContain("Connected: SQL Server 15.0.4430"));
    expect((screen.getByRole("checkbox", { name: /Trust server certificate/ }) as HTMLInputElement).checked).toBe(true);
    expect(screen.queryByTestId("tls-cert-offer")).toBeNull();
  });

  it("Save & Connect: catches untrusted cert before closing, trusts & saves with password intact, then calls onConnect", async () => {
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.test") return undefined;
      const { profile } = params as { profile: Profile };
      if (!profile.trustCert) return { ok: false, error: mssqlCertErr.message, elapsedMs: 10, notes: [], untrustedCertificate: mssqlCertErr };
      return { ok: true, serverVersion: "SQL Server 15.0", elapsedMs: 14, notes: [] };
    });
    const onClose = vi.fn();
    const onConnect = vi.fn();

    render(<ProfileEditor preset="mssql" onClose={onClose} onConnect={onConnect} />);
    await userEvent.type(screen.getByLabelText("Host"), "198.51.100.10");
    await userEvent.type(screen.getByLabelText("Password"), "demo-pass");
    await userEvent.click(screen.getByRole("button", { name: "Dev" }));
    await userEvent.click(screen.getByRole("button", { name: "Save & Connect" }));

    const banner = await screen.findByTestId("tls-cert-offer");
    expect(saveSpy).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    await userEvent.click(within(banner).getByRole("button", { name: "Trust certificate & connect" }));
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ engine: "mssql", host: "198.51.100.10", trustCert: true });
    expect(saveSpy.mock.calls[0][1]).toBe("demo-pass");
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith(expect.objectContaining({ id: "new-id", trustCert: true }));
  });

  it("Save & Connect on Production labels the Production risk directly and connects in one step without a second checkbox stop", async () => {
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.test") return undefined;
      const { profile } = params as { profile: Profile };
      if (!profile.trustCert) return { ok: false, error: mssqlCertErr.message, elapsedMs: 10, notes: [], untrustedCertificate: mssqlCertErr };
      return { ok: true, serverVersion: "SQL Server 15.0", elapsedMs: 12, notes: [] };
    });
    const onClose = vi.fn();
    const onConnect = vi.fn();

    render(<ProfileEditor preset="mssql" onClose={onClose} onConnect={onConnect} />);
    await userEvent.type(screen.getByLabelText("Host"), "198.51.100.10");
    await userEvent.click(screen.getByRole("button", { name: "Prod (Production)" }));
    await userEvent.click(screen.getByRole("button", { name: "Save & Connect" }));

    const banner = await screen.findByTestId("tls-cert-offer");
    expect(banner.textContent).toContain("Production server certificate is not trusted");
    await userEvent.click(within(banner).getByRole("button", { name: "Trust on Production & connect" }));

    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(saveSpy.mock.calls[0][0]).toMatchObject({ environment: "prod", trustCert: true });
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onConnect).toHaveBeenCalledWith(expect.objectContaining({ id: "new-id", environment: "prod", trustCert: true }));
  });

  it("Save & Connect non-certificate failure offers Save without connecting", async () => {
    m.rpc.mockResolvedValue({ ok: false, error: "Could not reach SQL Server", elapsedMs: 5, notes: [] });
    const onClose = vi.fn();

    render(<ProfileEditor preset="mssql" onClose={onClose} />);
    await userEvent.type(screen.getByLabelText("Host"), "sql.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Dev" }));
    await userEvent.click(screen.getByRole("button", { name: "Save & Connect" }));

    const saveAnyway = await screen.findByRole("button", { name: "Save without connecting" });
    expect(saveSpy).not.toHaveBeenCalled();
    await userEvent.click(saveAnyway);
    await waitFor(() => expect(saveSpy).toHaveBeenCalledTimes(1));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("SQL Server Strict mode offers Switch to Mandatory + trust", async () => {
    const strictErr: TlsCertError = { ...mssqlCertErr, canTrustCert: false, strictMode: true, message: "Strict encryption rejected certificate." };
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.test") return undefined;
      const { profile } = params as { profile: Profile };
      if (profile.tlsMode === "strict") return { ok: false, error: strictErr.message, elapsedMs: 10, notes: [], untrustedCertificate: strictErr };
      return { ok: true, serverVersion: "SQL Server 15.0", elapsedMs: 10, notes: [] };
    });

    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /^Encryption/ }));
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), "strict");
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));

    const banner = await screen.findByTestId("tls-cert-offer");
    await userEvent.click(within(banner).getByRole("button", { name: "Switch to Mandatory + trust" }));
    await waitFor(() => expect(screen.getByRole("status").textContent).toContain("Connected: SQL Server 15.0"));
    expect((screen.getByLabelText("Encryption") as HTMLSelectElement).value).toBe("mandatory");
  });

  it("MySQL VerifyFull offers Set CA certificate and Switch to Require & test again", async () => {
    const myErr: TlsCertError = {
      engine: "mySql", host: "db.example.com:3306", reason: "untrustedRoot",
      canTrustCert: false, strictMode: false, canSwitchToRequire: true, supportsCaPath: true,
      message: "MySQL cert not verified.",
    };
    m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
      if (method !== "profile.test") return undefined;
      const { profile } = params as { profile: Profile };
      if (profile.tlsMode !== "require") return { ok: false, error: myErr.message, elapsedMs: 10, notes: [], untrustedCertificate: myErr };
      return { ok: true, serverVersion: "MySQL 8.4", elapsedMs: 10, notes: [] };
    });

    render(<ProfileEditor preset="mySql" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));
    const banner = await screen.findByTestId("tls-cert-offer");
    expect(within(banner).getByRole("button", { name: "Set CA certificate" })).toBeTruthy();
    await userEvent.click(within(banner).getByRole("button", { name: "Switch to Require & test again" }));
    await waitFor(() => expect(screen.getByRole("status").textContent).toContain("Connected: MySQL 8.4"));
    expect((screen.getByLabelText("Encryption") as HTMLSelectElement).value).toBe("require");
  });

  it("existing profile with keyring-only password updates setting cleanly instead of hitting EndpointRefusal", async () => {
    useProfiles.setState({ profiles: [base("a", "mssql")] });
    m.rpc.mockResolvedValue({ ok: false, error: mssqlCertErr.message, elapsedMs: 10, notes: [], untrustedCertificate: mssqlCertErr });

    render(<ProfileEditor profileId="a" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));
    const banner = await screen.findByTestId("tls-cert-offer");
    m.rpc.mockClear();
    await userEvent.click(within(banner).getByRole("button", { name: "Trust server certificate & test again" }));
    expect(m.rpc).not.toHaveBeenCalled();
    expect(screen.getByRole("status").textContent).toContain("Re-enter the password to test, or click Save.");
    expect((screen.getByRole("checkbox", { name: /Trust server certificate/ }) as HTMLInputElement).checked).toBe(true);
  });

  it("openEncryption prop opens the Encryption disclosure immediately", () => {
    useProfiles.setState({ profiles: [base("a", "mssql")] });
    render(<ProfileEditor profileId="a" openEncryption onClose={() => {}} />);
    expect(screen.getByRole("button", { name: /^Encryption/ }).getAttribute("aria-expanded")).toBe("true");
  });

  it("Test connection clears a prior Save validation summary so TLS recovery banner is never masked", async () => {
    m.rpc.mockResolvedValue({ ok: false, error: mssqlCertErr.message, elapsedMs: 10, notes: [], untrustedCertificate: mssqlCertErr });
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(screen.getByRole("alert").textContent).toContain("Choose the environment");
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));
    expect(await screen.findByTestId("tls-cert-offer")).toBeTruthy();
  });

  it("Save & Connect shows feedback dock with Save without connecting even if probe throws an RPC error", async () => {
    m.rpc.mockRejectedValue(new Error("sidecar disconnected"));
    render(<ProfileEditor preset="mssql" onClose={() => {}} />);
    await userEvent.type(screen.getByLabelText("Host"), "sql.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Dev" }));
    await userEvent.click(screen.getByRole("button", { name: "Save & Connect" }));
    expect((await screen.findByRole("status")).textContent).toContain("sidecar disconnected");
    expect(screen.getByRole("button", { name: "Save without connecting" })).toBeTruthy();
  });

  it("clicking Set CA certificate clears the error dock without flashing a red error and focuses CA input", async () => {
    const pgErr: TlsCertError = {
      engine: "pg", host: "db.example.com:5432", reason: "untrustedRoot",
      canTrustCert: false, strictMode: false, canSwitchToRequire: true, supportsCaPath: true,
      message: "PG cert not verified.",
    };
    m.rpc.mockResolvedValue({ ok: false, error: pgErr.message, elapsedMs: 10, notes: [], untrustedCertificate: pgErr });
    render(<ProfileEditor preset="pg" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /^Encryption/ }));
    await userEvent.selectOptions(screen.getByLabelText("Encryption"), "verifyFull");
    await userEvent.click(screen.getByRole("button", { name: "Test connection" }));
    const banner = await screen.findByTestId("tls-cert-offer");
    await userEvent.click(within(banner).getByRole("button", { name: "Set CA certificate" }));
    expect(screen.queryByTestId("tls-cert-offer")).toBeNull();
    expect(screen.queryByTestId("profile-feedback-dock")).toBeNull();
    expect(document.activeElement).toBe(screen.getByPlaceholderText("/etc/ssl/certs/root.crt"));
  });
});

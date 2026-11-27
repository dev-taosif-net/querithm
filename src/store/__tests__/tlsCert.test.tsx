// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Profile, TlsCertError } from "../../ipc/contract";

const { rpc, shell, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  }
  return {
    rpc: vi.fn(),
    shell: { secretExists: vi.fn(async () => true), secretSet: vi.fn(async () => {}), secretDelete: vi.fn(async () => {}), secretEndConnection: vi.fn(async () => {}) },
    RpcError,
  };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell, RpcError }));
vi.mock("../../editor/monaco", () => ({ initMonaco: vi.fn(), monaco: { editor: {} } }));
vi.mock("../../editor/textDrop", () => ({ installTextDrop: () => () => {} }));

import { useConnections } from "../connections";
import { useProfiles } from "../profiles";
import { useUi } from "../ui";
import { Dialogs } from "../../app/Dialogs";

const mssqlProfile = (over: Partial<Profile> = {}): Profile => ({
  id: "p1", name: "SQL Dev", engine: "mssql", host: "198.51.100.10", port: 1433, auth: "windows",
  username: null, databaseDefault: null, environment: "dev", color: null, groupName: null,
  tlsMode: "default", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0,
  options: null, createdAt: 0, updatedAt: 0, ...over,
});

const certErr: TlsCertError = {
  engine: "mssql", host: "198.51.100.10:1433", reason: "untrustedRoot",
  canTrustCert: true, strictMode: false, canSwitchToRequire: false, supportsCaPath: false,
  message: "Connected to SQL Server at 198.51.100.10:1433, but its TLS certificate is not trusted.",
  detail: "UntrustedRoot",
};

function setupServer(...failures: Error[]) {
  rpc.mockImplementation(async (method: string, params?: unknown) => {
    if (method === "profile.save") return params;
    if (method === "group.list") return [];
    if (method === "session.open") return { sessionId: `s${rpc.mock.calls.filter((x) => x[0] === "session.open").length}`, database: null };
    if (method === "session.state") {
      const f = failures.shift();
      if (f) throw f;
      return { serverVersion: "SQL Server 15.0" };
    }
    if (method === "meta.databases") return [];
    return undefined;
  });
}

beforeEach(() => {
  rpc.mockReset();
  shell.secretExists.mockResolvedValue(true);
  shell.secretDelete.mockClear();
  shell.secretSet.mockClear();
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useUi.setState({ dialogs: [], toasts: [] });
  useProfiles.setState({ profiles: [mssqlProfile()], groups: [] });
});
afterEach(cleanup);

describe("TLS certificate recovery on Connect (TlsCertDialog + connections store)", () => {
  it("interactive connect prompts TlsCertDialog; clicking Trust certificate & connect saves trustCert=true and connects", async () => {
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    const trustBtn = await screen.findByRole("button", { name: "Trust certificate & connect" });
    expect(screen.getByText(/Self-signed or untrusted certificate authority/)).toBeTruthy();
    await userEvent.click(trustBtn);

    expect(await connecting).toBe(true);
    expect(useProfiles.getState().byId("p1")?.trustCert).toBe(true);
    expect(useConnections.getState().servers.p1.status).toBe("connected");
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s1", force: true });
  });

  it("cancelling TlsCertDialog leaves profile unchanged and server in error", async () => {
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    const cancelBtn = await screen.findByRole("button", { name: "Cancel" });
    await userEvent.click(cancelBtn);

    expect(await connecting).toBe(false);
    expect(useProfiles.getState().byId("p1")?.trustCert).toBe(false);
    expect(useConnections.getState().servers.p1).toMatchObject({ status: "error", error: certErr.message });
  });

  it("Edit profile… opens ProfileEditor with openEncryption=true", async () => {
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    await userEvent.click(await screen.findByRole("button", { name: "Edit profile…" }));

    expect(await connecting).toBe(false);
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile", profileId: "p1", openEncryption: true });
  });

  it("Production profile shows explicit Production warning and connects on one deliberate click without a redundant checkbox", async () => {
    useProfiles.setState({ profiles: [mssqlProfile({ environment: "prod" })], groups: [] });
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    const trustBtn = (await screen.findByRole("button", { name: "Trust on Production & connect" })) as HTMLButtonElement;
    expect(screen.getByText(/This is a Production connection/)).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Cancel" }));
    await userEvent.click(trustBtn);

    expect(await connecting).toBe(true);
    expect(useProfiles.getState().byId("p1")?.trustCert).toBe(true);
  });

  it("quiet background reconnect never opens TlsCertDialog", async () => {
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    expect(await useConnections.getState().connect("p1", { quiet: true })).toBe(false);
    expect(useUi.getState().dialogs).toHaveLength(0);
    expect(useConnections.getState().servers.p1).toMatchObject({ status: "error", error: expect.stringContaining("Reconnect to continue") });
  });

  it("never persists an opt-out one-time SSH secret to the OS keyring during TLS trust recovery (§13.1)", async () => {
    useProfiles.setState({
      profiles: [mssqlProfile({ ssh: { host: "bastion", port: 22, username: "deploy", auth: "password", keyPath: null } })],
      groups: [],
    });
    setupServer(
      new RpcError("SSH password needed", -32001, "sshNeedsSecret"),
      new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr),
    );
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    // Uncheck "Save in the OS keyring" and enter a one-time SSH password.
    const rememberBox = await screen.findByRole("checkbox", { name: /Save in the OS keyring/ });
    await userEvent.click(rememberBox);
    await userEvent.type(screen.getByLabelText("SSH password"), "one-time-ssh-pw");
    await userEvent.click(screen.getByRole("button", { name: "Connect" }));

    await userEvent.click(await screen.findByRole("button", { name: "Trust certificate & connect" }));
    expect(await connecting).toBe(true);
    expect(shell.secretSet).not.toHaveBeenCalled();
    const sshSecrets = rpc.mock.calls.filter((x) => x[0] === "session.open").map((x) => (x[1] as { sshSecret: string | null }).sshSecret);
    expect(sshSecrets.at(-1)).toBe("one-time-ssh-pw");
  });

  it("save failure during TLS recovery sets status to error instead of leaving the server stuck connecting", async () => {
    rpc.mockImplementation(async (method: string) => {
      if (method === "profile.save") throw new Error("keyring locked");
      if (method === "session.open") return { sessionId: "s1", database: null };
      if (method === "session.state") throw new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr);
      return undefined;
    });
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    await userEvent.click(await screen.findByRole("button", { name: "Trust certificate & connect" }));
    expect(await connecting).toBe(false);
    expect(useConnections.getState().servers.p1).toMatchObject({ status: "error", error: "keyring locked" });
  });

  it("disconnecting while TlsCertDialog is open aborts without saving or resurrecting the server", async () => {
    setupServer(new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr));
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    const trustBtn = await screen.findByRole("button", { name: "Trust certificate & connect" });
    await useConnections.getState().disconnect("p1");
    await userEvent.click(trustBtn);

    expect(await connecting).toBe(false);
    expect(useConnections.getState().servers.p1).toBeUndefined();
    expect(useProfiles.getState().byId("p1")?.trustCert).toBe(false);
  });

  it("TLS recovery on the 4th loop iteration still retries and connects cleanly", async () => {
    useProfiles.setState({
      profiles: [mssqlProfile({ ssh: { host: "bastion", port: 22, username: "deploy", auth: "password", keyPath: null } })],
      groups: [],
    });
    const hostKey = { host: "bastion", port: 22, algorithm: "ssh-ed25519", fingerprint: "AbC", previousFingerprint: null };
    setupServer(
      new RpcError("host key", -32001, "sshHostKeyUnknown", hostKey),
      new RpcError("ssh secret", -32001, "sshNeedsSecret"),
      new RpcError("ssh secret retry", -32001, "sshNeedsSecret"),
      new RpcError(certErr.message, -32001, "tlsCertificateUntrusted", certErr),
    );
    render(<Dialogs />);

    const connecting = useConnections.getState().connect("p1");
    await userEvent.click(await screen.findByRole("button", { name: "Trust and connect" }));
    for (let i = 0; i < 2; i++) {
      // PresenceList briefly keeps the exiting dialog mounted (with data-q-exiting); act on the active top modal.
      const checkboxes = await screen.findAllByRole("checkbox", { name: /Save in the OS keyring/ });
      const activeCheckbox = checkboxes.find((el) => !el.closest("[data-q-exiting]"))!;
      await userEvent.click(activeCheckbox);
      const inputs = screen.getAllByLabelText("SSH password");
      const activeInput = inputs.find((el) => !el.closest("[data-q-exiting]"))!;
      await userEvent.type(activeInput, "pw");
      const btns = screen.getAllByRole("button", { name: "Connect" });
      await userEvent.click(btns.find((el) => !el.closest("[data-q-exiting]"))!);
    }
    await userEvent.click(await screen.findByRole("button", { name: "Trust certificate & connect" }));

    expect(await connecting).toBe(true);
    expect(useConnections.getState().servers.p1.status).toBe("connected");
  });
});

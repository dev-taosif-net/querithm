// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  rpc: vi.fn(),
  shell: { secretSet: vi.fn(async () => {}), updateCheck: vi.fn() },
  a: { newQueryTab: vi.fn(() => "newtab"), openDefinition: vi.fn(), runQuery: vi.fn() },
  explorerAction: vi.fn(),
  refresh: vi.fn(),
  diff: { setModel: vi.fn(), updateOptions: vi.fn(), dispose: vi.fn() },
  ed: { getValue: vi.fn(() => "edited"), getModel: vi.fn(() => ({ dispose: vi.fn() })), dispose: vi.fn() },
  create: vi.fn(),
  createDiff: vi.fn(),
  createModel: vi.fn((v: string) => ({ v, dispose: vi.fn() })),
  sessionFor: vi.fn(async () => "sess"),
}));

vi.mock("../../ipc/client", () => ({ rpc: (...a: any[]) => h.rpc(...a), shell: h.shell }));
vi.mock("../actions", () => h.a);
vi.mock("../commands", () => ({
  commands: [
    { id: "c1", title: "Run query", keys: ["F5"] },
    { id: "c2", title: "Unbound command" },
  ],
  keysFor: (c: any) => (useSettings.getState().keybindings[c.id] ? [useSettings.getState().keybindings[c.id]] : (c.keys ?? [])),
  displayKeys: (k: string) => k,
}));
vi.mock("../../explorer/explorerWindow", () => ({ explorerAction: (...a: any[]) => h.explorerAction(...a) }));
vi.mock("../../editor/editors", () => ({
  refreshEditorOptions: () => h.refresh(), languageFor: () => "tsql", editorForTab: () => undefined,
  borrowEditor: () => ({ editor: {}, getValue: () => "", release: () => {} }),
}));
vi.mock("../../editor/monaco", () => ({
  initMonaco: vi.fn(),
  monaco: { editor: { createDiffEditor: (...a: any[]) => { h.createDiff(...a); return h.diff; }, createModel: (...a: any[]) => h.createModel(...(a as [string])), create: (...a: any[]) => { h.create(...a); return h.ed; } } },
}));
vi.mock("../../connections/ConnectDialog", () => ({ ConnectDialog: ({ onClose }: any) => <button onClick={onClose}>connect-dialog</button> }));
vi.mock("../../connections/ProfileEditor", () => ({ ProfileEditor: ({ profileId, preset, onClose }: any) => <button onClick={onClose}>profile-editor {profileId ?? "new"} {preset ?? ""}</button> }));
vi.mock("../../results/GridDialogs", () => ({
  GridPreviewDialog: () => <div>grid-preview</div>, GridConflictsDialog: () => <div>grid-conflicts</div>, GridPendingDialog: () => <div>grid-pending</div>,
}));
vi.mock("../../import/ImportWizardDialog", () => ({ ImportWizardDialog: () => <div>import-wizard</div> }));
vi.mock("../../refactor/RenameDialog", () => ({ RenameDialog: () => <div>rename-dialog</div> }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor: h.sessionFor }) } }));

import { Dialogs } from "../Dialogs";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "prod", auth: "sql", username: "sa", host: "srv", ssh: { host: "bast", username: "jump", auth: "privateKey" } };
const pg: any = { id: "pg1", name: "Analytics", engine: "pg", environment: "dev", auth: "entraToken", username: "pgu", host: "pgh" };
beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ dialogs: [], toasts: [] });
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
});
afterEach(() => cleanup());

const show = (d: any) => act(() => useUi.getState().openDialog(d));
describe("password prompt: an empty box", () => {
  // Intended: an empty password is a valid answer (PostgreSQL trust sign-in, a MySQL root without a password), so it is
  // remembered like any other; a server that refuses it fails the login and the prompt asks again with its reason.
  it("Connect with nothing typed remembers the empty password as the answer", async () => {
    render(<Dialogs />);
    const resolve = vi.fn();
    show({ kind: "password", profileId: "ms1", resolve });
    await userEvent.setup().click(screen.getByText("Connect"));
    expect(h.shell.secretSet).toHaveBeenCalledWith("ms1", "");
  });
});

describe("password prompt: show password", () => {
  it("hidden at first; the eye shows and hides it, with one name and a pressed state, and the caret stays in the box", async () => {
    render(<Dialogs />);
    show({ kind: "password", profileId: "ms1", resolve: vi.fn() });
    const user = userEvent.setup();
    const box = screen.getByLabelText("Password") as HTMLInputElement;
    expect(box.type).toBe("password");
    await user.type(box, "s3cret!");
    const eye = screen.getByRole("button", { name: "Show password" });
    expect(eye.getAttribute("aria-pressed")).toBe("false");
    await user.click(eye);
    expect(box.type).toBe("text");
    expect(box.value).toBe("s3cret!");
    expect(eye.getAttribute("aria-pressed")).toBe("true");
    expect(document.activeElement).toBe(box);
    // Shown or not, the typed value never becomes the input's value attribute (the page's HTML).
    expect(box.getAttribute("value")).toBeNull();
    expect(document.body.innerHTML).not.toContain("s3cret!");
    await user.click(eye);
    expect(box.type).toBe("password");
    expect(eye.getAttribute("aria-pressed")).toBe("false");
  });

  it("the eye never submits: nothing is saved or answered until Connect", async () => {
    render(<Dialogs />);
    const resolve = vi.fn();
    show({ kind: "password", profileId: "ms1", resolve });
    await userEvent.setup().click(screen.getByRole("button", { name: "Show password" }));
    expect(h.shell.secretSet).not.toHaveBeenCalled();
    expect(resolve).not.toHaveBeenCalled();
    expect(screen.getByText("Connect")).toBeInTheDocument();
  });

  it.each([
    [{ kind: "password", profileId: "pg1" }, "Show token"],
    [{ kind: "password", profileId: "ms1", ssh: true }, "Show passphrase"],
  ])("names what it shows (%o → %s)", (d, name) => {
    render(<Dialogs />);
    show({ ...d, resolve: vi.fn() });
    expect(screen.getByRole("button", { name })).toBeInTheDocument();
  });
});

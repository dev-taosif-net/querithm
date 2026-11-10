// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useSnapshots } from "../../store/snapshots";
import { useSettings } from "../../store/settings";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "prod", auth: "sql", username: "sa", host: "srv", ssh: { host: "bast", username: "jump", auth: "privateKey" } };
const pg: any = { id: "pg1", name: "Analytics", engine: "pg", environment: "dev", auth: "entraToken", username: "pgu", host: "pgh" };

const show = (d: any) => act(() => useUi.getState().openDialog(d));

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ dialogs: [], toasts: [] });
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
  useSnapshots.setState({ entries: {} });
  useTabs.setState({ tabs: { t1: { id: "t1", title: "one.sql" } as any }, order: ["t1"], activeId: "t1" });
  useSettings.setState({ keybindings: {} } as any);
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe("Dialogs host", () => {
  it("renders nothing without dialogs and one view per open dialog", () => {
    const { container } = render(<Dialogs />);
    expect(container).toBeEmptyDOMElement();
    show({ kind: "connect" });
    show({ kind: "profile", profileId: "ms1", preset: "pg" });
    expect(screen.getByText("connect-dialog")).toBeInTheDocument();
    expect(screen.getByText(/profile-editor ms1 pg/)).toBeInTheDocument();
  });
  it("closing a dialog removes only that one", async () => {
    render(<Dialogs />);
    show({ kind: "connect" });
    show({ kind: "profile" });
    await userEvent.setup().click(screen.getByText("connect-dialog"));
    expect(screen.getByText("connect-dialog").closest("[inert][data-q-exiting]")).not.toBeNull();
    await waitFor(() => expect(screen.queryByText("connect-dialog")).toBeNull());
    expect(screen.getByText(/profile-editor/)).toBeInTheDocument();
  });
  it("routes grid dialogs and lazily loaded wizards", async () => {
    render(<Dialogs />);
    show({ kind: "gridPreview" });
    show({ kind: "gridConflicts" });
    show({ kind: "gridPending" });
    show({ kind: "import", profileId: "ms1", database: "d" });
    show({ kind: "rename" });
    show({ kind: "snippets" });
    show({ kind: "snippetEdit", snippet: { id: "", title: "", prefix: "pfx", body: "", tags: [], updatedAt: 0, builtIn: false } });
    expect(screen.getByText("grid-preview")).toBeInTheDocument();
    expect(screen.getByText("grid-conflicts")).toBeInTheDocument();
    expect(screen.getByText("grid-pending")).toBeInTheDocument();
    expect(await screen.findByText("import-wizard")).toBeInTheDocument();
    expect(await screen.findByText("rename-dialog")).toBeInTheDocument();
    expect(await screen.findByText("Snippets")).toBeInTheDocument();
    expect(await screen.findByText("New snippet")).toBeInTheDocument();
  });
});

describe("confirm", () => {
  it("OK / Cancel / Enter / Escape resolve true / false", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "confirm", title: "Sure?", body: "Really do it", ok: "Do it", resolve });
    expect(screen.getByText("Really do it")).toBeInTheDocument();
    expect(screen.getByRole("dialog", { name: "Sure?" })).toHaveFocus();
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Enter" });
    expect(resolve).toHaveBeenLastCalledWith(true);
    const user = userEvent.setup();
    await user.click(screen.getByText("Do it"));
    expect(resolve).toHaveBeenLastCalledWith(true);
    await user.click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenLastCalledWith(false);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(resolve).toHaveBeenLastCalledWith(false);
  });
  // M19: Enter confirmed whatever had focus, and a danger confirm opened on itself (disconnect and roll back, delete
  // profile, close tabs with unsaved SQL, EXPLAIN ANALYZE, quit with unsaved work).
  it("Enter on a focused Cancel cancels", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "confirm", title: "Sure?", body: "b", ok: "Do it", resolve });
    screen.getByText("Cancel").focus();
    await userEvent.setup().keyboard("{Enter}");
    expect(resolve).toHaveBeenCalledTimes(1);
    expect(resolve).toHaveBeenLastCalledWith(false);
  });
  it("a danger confirm is an alertdialog that opens on Cancel: Enter right after it opens cancels", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "confirm", title: "Disconnect and roll back?", body: "b", ok: "Disconnect", danger: true, resolve });
    expect(screen.getByRole("alertdialog", { name: "Disconnect and roll back?" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
    await userEvent.setup().keyboard("{Enter}");
    expect(resolve).toHaveBeenCalledTimes(1);
    expect(resolve).toHaveBeenLastCalledWith(false);
    fireEvent.keyDown(screen.getByRole("alertdialog"), { key: "Enter" });
    expect(resolve).not.toHaveBeenCalledWith(true);
  });
  it("defaults the button label to OK", () => {
    render(<Dialogs />);
    show({ kind: "confirm", title: "T", body: "B", resolve: vi.fn() });
    expect(screen.getByText("OK")).toBeInTheDocument();
  });
  it("backdrop click cancels; only the topmost modal reacts to Escape", async () => {
    const lower = vi.fn();
    const upper = vi.fn();
    const { container } = render(<Dialogs />);
    show({ kind: "confirm", title: "A", body: "lower", resolve: lower });
    show({ kind: "confirm", title: "B", body: "upper", resolve: upper });
    fireEvent.keyDown(window, { key: "Escape" });
    expect(upper).toHaveBeenCalledWith(false);
    expect(lower).not.toHaveBeenCalled();
    fireEvent.mouseDown(container.querySelector(".q-modal-backdrop")!);
    expect(lower).toHaveBeenCalledWith(false);
  });
});

describe("password", () => {
  const open = (extra: any = {}) => {
    const resolve = vi.fn();
    show({ kind: "password", profileId: "ms1", resolve, ...extra });
    return resolve;
  };
  it("saves to the keyring and resolves undefined (Rust injects it)", async () => {
    render(<Dialogs />);
    const resolve = open();
    const user = userEvent.setup();
    expect(screen.getByText("Sign in — Sales")).toBeInTheDocument();
    expect(screen.getByText("sa@srv")).toBeInTheDocument();
    await user.type(document.querySelector("input[type=password]")!, "pw");
    await user.click(screen.getByText("Connect"));
    expect(h.shell.secretSet).toHaveBeenCalledWith("ms1", "pw");
    expect(resolve).toHaveBeenCalledWith(undefined);
  });
  it("one connection saves its policy and caches the password", async () => {
    render(<Dialogs />);
    const resolve = open();
    const user = userEvent.setup();
    const save = vi.spyOn(useProfiles.getState(), "save").mockResolvedValue({ ...ms, passwordRetention: "once" });
    await user.selectOptions(screen.getByLabelText("Remember password"), "once");
    await user.type(document.querySelector("input[type=password]")!, "pw{Enter}");
    await waitFor(() => expect(resolve).toHaveBeenCalledWith(undefined));
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ passwordRetention: "once" }));
    expect(h.shell.secretSet).toHaveBeenCalledWith("ms1", "pw");
    save.mockRestore();
  });
  it("keeps the prompt open and warns when remembering fails", async () => {
    h.shell.secretSet.mockRejectedValueOnce(new Error("no keyring"));
    render(<Dialogs />);
    const resolve = open();
    const user = userEvent.setup();
    await user.type(document.querySelector("input[type=password]")!, "pw");
    await user.click(screen.getByText("Connect"));
    expect(resolve).not.toHaveBeenCalled();
    expect(useUi.getState().toasts[0].text).toMatch(/Could not remember password: no keyring/);
  });
  it("cancel resolves null; an error banner and token label are shown", async () => {
    render(<Dialogs />);
    const resolve = open({ profileId: "pg1", error: "Login failed" });
    expect(screen.getByText("Login failed")).toBeInTheDocument();
    expect(screen.getByText("Access token")).toBeInTheDocument();
    await userEvent.setup().click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenCalledWith(null);
  });
  it("SSH secret uses its own keyring account and key-passphrase label", async () => {
    render(<Dialogs />);
    const resolve = open({ ssh: true });
    expect(screen.getByText("SSH tunnel — Sales")).toBeInTheDocument();
    expect(screen.getByText("jump@bast")).toBeInTheDocument();
    expect(screen.getByText("Key passphrase")).toBeInTheDocument();
    const user = userEvent.setup();
    await user.type(document.querySelector("input[type=password]")!, "pp");
    await user.click(screen.getByText("Connect"));
    expect(h.shell.secretSet).toHaveBeenCalledWith("ms1:ssh", "pp");
    expect(resolve).toHaveBeenCalled();
  });
  it("SSH password label for password-auth tunnels", () => {
    useProfiles.setState({ profiles: [{ ...ms, ssh: { ...ms.ssh, auth: "password" } }, pg] });
    render(<Dialogs />);
    open({ ssh: true });
    expect(screen.getByText("SSH password")).toBeInTheDocument();
  });
});

describe("hostKey", () => {
  const key = { host: "bast", port: 22, algorithm: "ssh-ed25519", fingerprint: "AAA" };
  it("a new key asks for trust and shows the fingerprint", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "hostKey", hostKey: key, resolve });
    expect(screen.getByText("Trust this SSH server?")).toBeInTheDocument();
    expect(screen.getByText("SHA256:AAA")).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(screen.getByText("Trust and connect"));
    expect(resolve).toHaveBeenCalledWith(true);
    await user.click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenLastCalledWith(false);
  });
  it("a changed key is a danger warning that shows both fingerprints", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "hostKey", hostKey: { ...key, previousFingerprint: "OLD" }, resolve });
    expect(screen.getByText("⚠ SSH HOST KEY CHANGED")).toBeInTheDocument();
    expect(screen.getByText(/SHA256:OLD/)).toBeInTheDocument();
    await userEvent.setup().click(screen.getByText("Trust the new key"));
    expect(resolve).toHaveBeenCalledWith(true);
  });
});

describe("guard", () => {
  const block = (reason: string) => ({
    reason, profileName: "Sales", server: "srv", database: null,
    findings: [{ description: "DELETE without WHERE", line: 3, statement: "DELETE FROM t" }, { description: "second", line: 1 }],
  });
  it.each([
    ["destructive", "⚠ DESTRUCTIVE STATEMENT DETECTED ON PRODUCTION", /AST analysis/],
    ["prodWrite", "⚠ DATA CHANGE ON PRODUCTION", /about to write data/],
    ["prodFirstRun", "⚠ FIRST EXECUTION ON PRODUCTION", /first execution on this/],
  ])("%s block", async (reason, title, body) => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "guard", block: block(reason), resolve });
    expect(screen.getByText(title)).toBeInTheDocument();
    expect(screen.getByText(body)).toBeInTheDocument();
    expect(screen.getByText("(default)")).toBeInTheDocument();
    expect(screen.getByText("DELETE FROM t")).toBeInTheDocument();
    expect(screen.getByText("(line 3)")).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(screen.getByText("Confirm & execute on PROD"));
    expect(resolve).toHaveBeenCalledWith(true);
    await user.click(screen.getByText(/Cancel execution/));
    expect(resolve).toHaveBeenLastCalledWith(false);
  });
});

describe("transaction dialogs", () => {
  it("txClose offers cancel / roll back / commit, with Cancel as the default and no way to keep the transaction (D21)", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "txClose", tabId: "t1", resolve });
    expect(screen.getByText("one")).toBeInTheDocument();
    expect(screen.queryByText(/Keep open/)).toBeNull();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
    const user = userEvent.setup();
    for (const [label, v] of [["Cancel", "cancel"], ["Roll back and close", "rollback"], ["Commit and close", "commit"]]) {
      await user.click(screen.getByRole("button", { name: label }));
      expect(resolve).toHaveBeenLastCalledWith(v);
    }
    expect(resolve).toHaveBeenCalledTimes(3);
  });
  it("txClose names the server and how long the transaction has been open", () => {
    useTabs.getState().update("t1", { profileId: "ms1", database: "Billing", txState: "open", txStartedAt: Date.now() - 12 * 60_000 - 5_000, spid: 53 });
    render(<Dialogs />);
    show({ kind: "txClose", tabId: "t1", resolve: vi.fn() });
    expect(screen.getByText(/open for 12 min \(SPID 53\)/)).toBeInTheDocument();
    expect(screen.getByTestId("server-chip")).toBeInTheDocument();
  });
  it("txClose for an unknown state says the transaction may be open (M9)", () => {
    useTabs.getState().update("t1", { txState: "unknown" });
    render(<Dialogs />);
    show({ kind: "txClose", tabId: "t1", resolve: vi.fn() });
    expect(screen.getByText("Close a tab that may have an open transaction?")).toBeInTheDocument();
    expect(screen.getByText(/could not read whether this tab's transaction is still open/)).toBeInTheDocument();
  });
  it("txClose and busyClose asked while quitting say quit, and Cancel keeps Querithm open (H4)", async () => {
    useTabs.getState().update("t1", { txState: "open" });
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "txClose", tabId: "t1", quit: true, resolve });
    expect(screen.getByText(/Quit Querithm\? A tab has an open transaction/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Roll back and quit" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
    await userEvent.setup().click(screen.getByRole("button", { name: "Commit and quit" }));
    expect(resolve).toHaveBeenLastCalledWith("commit");
    act(() => useUi.setState({ dialogs: [] }));
    show({ kind: "busyClose", tabId: "t1", quit: true, resolve });
    expect(screen.getByRole("button", { name: "Keep Querithm open" })).toHaveFocus();
    expect(screen.getByRole("button", { name: "Cancel query and quit" })).toBeInTheDocument();
  });
  it("busyClose: Keep tab is the default; the other button cancels the query and closes (D20)", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "busyClose", tabId: "t1", resolve });
    expect(screen.getByText("Cancel the running query?")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Keep tab" })).toHaveFocus();
    const user = userEvent.setup();
    await user.keyboard("{Enter}");
    expect(resolve).toHaveBeenLastCalledWith(false);
    await user.click(screen.getByRole("button", { name: "Cancel query and close" }));
    expect(resolve).toHaveBeenLastCalledWith(true);
  });
  it("txSwitch: Stay or discard", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "txSwitch", tabId: "t1", resolve });
    const user = userEvent.setup();
    expect(screen.getByRole("button", { name: "Stay" })).toHaveFocus();
    await user.click(screen.getByText("Stay"));
    expect(resolve).toHaveBeenLastCalledWith(false);
    await user.click(screen.getByText("Switch and discard the transaction"));
    expect(resolve).toHaveBeenLastCalledWith(true);
  });
});

describe("params", () => {
  it("collects values, types and NULL flags; type is undefined when blank", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "params", names: ["@a", "@b"], resolve });
    const inputs = screen.getAllByRole("textbox");
    const user = userEvent.setup();
    await user.type(inputs[0], "5");
    await user.type(inputs[1], "int");
    const nulls = screen.getAllByLabelText("NULL");
    await user.click(nulls[1]);
    expect(inputs[2]).toBeDisabled();
    await user.click(screen.getByText("Execute"));
    expect(resolve).toHaveBeenCalledWith([
      { name: "@a", value: "5", isNull: false, type: "int" },
      { name: "@b", value: "", isNull: true, type: undefined },
    ]);
  });
  it("Cancel and Escape resolve null; Enter executes", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "params", names: ["@a"], resolve });
    await userEvent.setup().click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenLastCalledWith(null);
    fireEvent.keyDown(window, { key: "Enter" });
    expect(resolve).toHaveBeenLastCalledWith([{ name: "@a", value: "", isNull: false, type: undefined }]);
  });
  it("Oracle (D4 S06): asks for :name values only, says they bind as text, and never sends a type", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "params", names: [":dept", ":1"], dialect: "oracle", resolve });
    expect(screen.getByText(/bound as text \(VARCHAR2\)/)).toBeTruthy();
    expect(screen.queryByPlaceholderText("type (optional)")).toBeNull();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText("Value of :dept"), "10");
    await user.click(screen.getAllByLabelText("NULL")[1]);
    await user.click(screen.getByText("Execute"));
    expect(resolve).toHaveBeenCalledWith([
      { name: ":dept", value: "10", isNull: false, type: undefined },
      { name: ":1", value: "", isNull: true, type: undefined },
    ]);
  });
});

describe("diff", () => {
  const diff = (extra: any = {}) => ({ kind: "diff", title: "Compare", left: "a", right: "b", leftTitle: "SERVER", rightTitle: "LOCAL", language: "sql", ...extra });
  it("creates a Monaco diff editor with both texts and disposes it on close", () => {
    const { unmount } = render(<Dialogs />);
    show(diff());
    expect(h.createDiff).toHaveBeenCalled();
    expect(h.createModel).toHaveBeenCalledWith("a", "sql");
    expect(h.createModel).toHaveBeenCalledWith("b", "sql");
    expect(h.diff.setModel).toHaveBeenCalled();
    expect(screen.getByText("SERVER")).toBeInTheDocument();
    unmount();
    expect(h.diff.dispose).toHaveBeenCalled();
  });
  it("informational diff: Close dismisses the dialog itself", async () => {
    render(<Dialogs />);
    show(diff());
    expect(screen.queryByText("Overwrite")).toBeNull();
    await userEvent.setup().click(screen.getByText("Close"));
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
  it("confirming diff resolves with the choice; whitespace toggle updates the editor", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show(diff({ resolve, okLabel: "Overwrite Server Version" }));
    const user = userEvent.setup();
    await user.click(screen.getByLabelText("Ignore whitespace"));
    expect(h.diff.updateOptions).toHaveBeenLastCalledWith({ ignoreTrimWhitespace: false });
    await user.click(screen.getByText("Overwrite Server Version"));
    expect(resolve).toHaveBeenLastCalledWith(true);
    await user.click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenLastCalledWith(false);
  });
});

describe("execProc (both engines)", () => {
  const params = [
    { name: "@id", type: "int", ordinal: 1, output: false, hasDefault: false },
    { name: "@name", type: "nvarchar(50)", ordinal: 2, output: false, hasDefault: true },
    { name: "@out", type: "int", ordinal: 3, output: true, hasDefault: false },
  ];
  const seed = (profileId: string, database: string, kind: string, ps: any[]) =>
    useSnapshots.setState({
      entries: { [`${profileId}|${database}`]: { key: `${profileId}|${database}`, profileId, database, status: "ready", index: { byId: new Map([[9, { id: 9, schema: "dbo", name: "p", kind, params: ps }]]) } as any, version: 1 } as any },
    });
  const open = () => show({ kind: "execProc", profileId: "ms1", database: "db", objectId: 9 });

  it("shows a message while the object is not cached", () => {
    render(<Dialogs />);
    open();
    expect(screen.getByText(/not in the metadata cache/)).toBeInTheDocument();
  });
  it("shows 'No parameters.' for parameterless objects", () => {
    seed("ms1", "db", "proc", []);
    render(<Dialogs />);
    open();
    expect(screen.getByText("No parameters.")).toBeInTheDocument();
  });
  it("M19: without parameters it opens on Cancel, so Enter does not run the procedure; with them Enter in a field runs it", async () => {
    seed("ms1", "db", "proc", []);
    render(<Dialogs />);
    open();
    expect(screen.getByRole("alertdialog", { name: "Execute dbo.p" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
    const user = userEvent.setup();
    await user.keyboard("{Enter}");
    expect(useUi.getState().dialogs).toHaveLength(0);
    expect(h.a.newQueryTab).not.toHaveBeenCalled();
    seed("ms1", "db", "proc", params);
    open();
    expect(screen.getAllByRole("textbox")[0]).toHaveFocus();
    await user.keyboard("1{Enter}");
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toContain("@id = 1");
  });
  it("says which environment, server and database Execute runs on", () => {
    seed("ms1", "db", "proc", []);
    render(<Dialogs />);
    open();
    const chip = screen.getByTestId("server-chip");
    expect(chip.title).toBe("PROD Sales › db");
    expect(chip.querySelector("[data-env]")?.textContent).toBe("PROD");
  });
  it("SQL Server proc: DECLAREs OUTPUT params, EXECs with typed literals, selects outputs, runs after a delay", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    seed("ms1", "db", "proc", params);
    render(<Dialogs />);
    open();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const boxes = screen.getAllByRole("textbox");
    await user.type(boxes[0], "42");
    expect(boxes[1]).toBeDisabled(); // @name has a default: starts as "use default"
    await user.click(screen.getByLabelText("default"));
    await user.type(boxes[1], "O'Brien");
    await user.click(screen.getByText("Execute"));
    const call = (h.a.newQueryTab.mock.calls[0] as any[])[0] as any;
    expect(call.title).toBe("EXEC p.sql");
    expect(call.text).toBe("DECLARE @out int;\nEXEC [dbo].[p]\n    @id = 42,\n    @name = N'O''Brien',\n    @out = @out OUTPUT;\nSELECT @out AS [out];\n");
    expect(useUi.getState().dialogs).toHaveLength(0);
    await act(async () => { vi.advanceTimersByTime(50); });
    expect(h.a.runQuery).toHaveBeenCalledWith("newtab");
  });
  it("SQL Server proc: a parameter with a default left as default is omitted; NULL is passed otherwise", async () => {
    seed("ms1", "db", "proc", params);
    render(<Dialogs />);
    open();
    const user = userEvent.setup();
    const nulls = screen.getAllByRole("checkbox");
    await user.click(nulls[0]); // @id NULL
    await user.click(screen.getByText("Script to new tab"));
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toContain("@id = NULL");
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).not.toContain("@name");
    expect(h.a.runQuery).not.toHaveBeenCalled();
  });
  it.each([
    ["func", "SELECT [dbo].[p](5, NULL) AS result;"],
    ["tvf", "SELECT * FROM [dbo].[p](5, NULL);"],
  ])("SQL Server %s", async (kind, expected) => {
    seed("ms1", "db", kind, [{ name: "@a", type: "int", ordinal: 1, output: false, hasDefault: false }, { name: "@b", type: "int", ordinal: 2, output: false, hasDefault: false }]);
    render(<Dialogs />);
    open();
    const user = userEvent.setup();
    await user.type(screen.getAllByRole("textbox")[0], "5");
    await user.click(screen.getAllByRole("checkbox")[1]);
    await user.click(screen.getByText("Script to new tab"));
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toContain(expected);
  });
  it("SQL Server: a ] inside the schema or the name is doubled inside the brackets (the dialect quoting table)", async () => {
    useSnapshots.setState({
      entries: { "ms1|db": { key: "ms1|db", profileId: "ms1", database: "db", status: "ready", index: { byId: new Map([[9, { id: 9, schema: "my]s", name: "odd]p", kind: "proc", params: [{ name: "@out", type: "int", ordinal: 1, output: true, hasDefault: false }] }]]) } as any, version: 1 } as any },
    });
    render(<Dialogs />);
    open();
    await userEvent.setup().click(screen.getByText("Script to new tab"));
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toBe("DECLARE @out int;\nEXEC [my]]s].[odd]]p]\n    @out = @out OUTPUT;\nSELECT @out AS [out];\n");
  });
  it("MySQL procedure: CALL `db`.`p` with one positional argument per line; OUT parameters through user variables", async () => {
    useProfiles.setState({ profiles: [ms, pg, { id: "my1", name: "Shop", engine: "mySql", environment: "dev", auth: "password", username: "root", host: "h" }] });
    useSnapshots.setState({
      entries: { "my1|shop": { key: "my1|shop", profileId: "my1", database: "shop", status: "ready", index: { byId: new Map([[9, { id: 9, schema: "shop", name: "add`order", kind: "proc", params: [
        { name: "p_id", type: "int", ordinal: 1, output: false, hasDefault: false },
        { name: "p_note", type: "varchar(50)", ordinal: 2, output: false, hasDefault: false },
        { name: "p_total", type: "decimal(10,2)", ordinal: 3, output: true, hasDefault: false },
      ] }]]) } as any, version: 1 } as any },
    });
    render(<Dialogs />);
    show({ kind: "execProc", profileId: "my1", database: "shop", objectId: 9 });
    const user = userEvent.setup();
    const boxes = screen.getAllByRole("textbox");
    await user.type(boxes[0], "7");
    await user.type(boxes[1], "it's C:\\x");
    await user.click(screen.getByText("Script to new tab"));
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toBe(
      "CALL `shop`.`add``order`(\n    7,\n    'it''s C:\\\\x',\n    @p_total\n);\nSELECT @p_total AS `p_total`;\n",
    );
  });
  it("SQLite has no stored procedures: the dialog says so and offers nothing to run", () => {
    useProfiles.setState({ profiles: [ms, pg, { id: "sl1", name: "File", engine: "sqlite", environment: "dev", auth: "none" }] });
    useSnapshots.setState({
      entries: { "sl1|main": { key: "sl1|main", profileId: "sl1", database: "main", status: "ready", index: { byId: new Map([[9, { id: 9, schema: "main", name: "p", kind: "proc", params: [] }]]) } as any, version: 1 } as any },
    });
    render(<Dialogs />);
    show({ kind: "execProc", profileId: "sl1", database: "main", objectId: 9 });
    expect(screen.getByText("SQLite has no stored procedures.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Execute" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Script to new tab" })).toBeNull();
  });
  it("PostgreSQL procedure uses CALL with named arguments and plain string literals", async () => {
    seed("pg1", "app", "proc", [{ name: "p_id", type: "integer", ordinal: 1, output: false, hasDefault: false }, { name: "p_name", type: "text", ordinal: 2, output: false, hasDefault: false }]);
    render(<Dialogs />);
    show({ kind: "execProc", profileId: "pg1", database: "app", objectId: 9 });
    const user = userEvent.setup();
    const boxes = screen.getAllByRole("textbox");
    await user.type(boxes[0], "7");
    await user.type(boxes[1], "it's");
    await user.click(screen.getByText("Script to new tab"));
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toBe("CALL dbo.p(p_id => 7, p_name => 'it''s');\n");
  });
  it("PostgreSQL function uses SELECT * FROM, positional args and NULL", async () => {
    seed("pg1", "app", "func", [{ name: "", type: "integer", ordinal: 1, output: false, hasDefault: false }, { name: "", type: "integer", ordinal: 2, output: false, hasDefault: false }]);
    render(<Dialogs />);
    show({ kind: "execProc", profileId: "pg1", database: "app", objectId: 9 });
    expect(screen.getByText("$1")).toBeInTheDocument();
    await userEvent.setup().click(screen.getByText("Script to new tab"));
    // keyed by empty name: both parameters share one value slot; blank values render as NULL
    expect(((h.a.newQueryTab.mock.calls[0] as any[])[0] as any).text).toBe("SELECT * FROM dbo.p(NULL, NULL);\n");
  });
  it("Cancel closes without creating a tab", async () => {
    seed("ms1", "db", "proc", []);
    render(<Dialogs />);
    open();
    await userEvent.setup().click(screen.getByText("Cancel"));
    expect(h.a.newQueryTab).not.toHaveBeenCalled();
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
});

describe("cellViewer", () => {
  it("read-only viewer pretty-prints JSON, copies, opens in a tab and closes", async () => {
    render(<Dialogs />);
    show({ kind: "cellViewer", title: "Cell", value: '{"a":1}', language: "json" });
    expect(h.create.mock.calls[0][1]).toMatchObject({ value: '{\n  "a": 2'.replace("2", "1") + "\n}", language: "json", readOnly: true });
    expect(screen.getByText("7 characters")).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(screen.getByText("Copy"));
    expect(await navigator.clipboard.readText()).toBe('{"a":1}');
    await waitFor(() => expect(useUi.getState().toasts.some((t) => t.text === "Copied.")).toBe(true));
    await user.click(screen.getByText("Open in tab"));
    expect(h.a.newQueryTab).toHaveBeenCalledWith({ text: '{"a":1}', title: "value.txt" });
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
  it("invalid JSON and plain text are shown raw", () => {
    render(<Dialogs />);
    show({ kind: "cellViewer", title: "Cell", value: "{nope" , language: "json" });
    expect(h.create.mock.calls[0][1].value).toBe("{nope");
    show({ kind: "cellViewer", title: "Cell2", value: "text" });
    expect(h.create.mock.calls[1][1]).toMatchObject({ language: "plaintext", readOnly: true });
  });
  it("editable viewer stages the edited value", async () => {
    const onSave = vi.fn();
    render(<Dialogs />);
    show({ kind: "cellViewer", title: "Edit", value: "orig", onSave });
    expect(h.create.mock.calls[0][1]).toMatchObject({ value: "orig", readOnly: false });
    await userEvent.setup().click(screen.getByText("Stage value"));
    expect(onSave).toHaveBeenCalledWith("edited");
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
  it("editable viewer Cancel does not save", async () => {
    const onSave = vi.fn();
    render(<Dialogs />);
    show({ kind: "cellViewer", title: "Edit", value: "orig", onSave });
    await userEvent.setup().click(screen.getByText("Cancel"));
    expect(onSave).not.toHaveBeenCalled();
  });
  it("read-only Close button closes", async () => {
    render(<Dialogs />);
    show({ kind: "cellViewer", title: "Cell", value: "x" });
    await userEvent.setup().click(screen.getByRole("button", { name: "Close" }));
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
});

describe("settings", () => {
  // The dialog itself is tested in src/settings/__tests__/SettingsDialog.test.tsx.
  it("loads Settings on first use, opens it at a requested page, and Done closes it", async () => {
    h.rpc.mockResolvedValue(undefined);
    render(<Dialogs />);
    show({ kind: "settings", page: "about" });
    expect(await screen.findByRole("dialog", { name: "Settings" })).toBeInTheDocument();
    expect(await screen.findByRole("tab", { name: /About/ })).toHaveAttribute("aria-selected", "true");
    await userEvent.setup().click(screen.getByRole("button", { name: "Done" }));
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
});

describe("history", () => {
  const rows = [
    { id: 1, profileId: "ms1", database: "Sales", sql: "select 1", executedAt: 0, status: "done", elapsedMs: 5, rowsAffected: 3 },
    { id: 2, profileId: "pg1", database: "app", sql: "select 2", executedAt: 0, status: "failed", elapsedMs: 9, rowsAffected: null },
    { id: 3, profileId: null, database: null, sql: "select 3", executedAt: 0, status: "cancelled", elapsedMs: 1, rowsAffected: null },
  ];
  it("lists recent history across both engines and opens an entry in a new tab", async () => {
    h.rpc.mockResolvedValue(rows);
    render(<Dialogs />);
    show({ kind: "history" });
    await waitFor(() => expect(screen.getByText("select 1")).toBeInTheDocument(), { timeout: 2000 });
    expect(h.rpc).toHaveBeenCalledWith("history.list", { limit: 300, profileId: null, database: null, beforeId: null });
    expect(screen.getByText(/\[MS\] Prod - Sales · Sales/)).toBeInTheDocument();
    expect(screen.getByText(/\[PG\] Dev - Analytics · app/)).toBeInTheDocument();
    expect(screen.getByText(/\(deleted profile\)/)).toBeInTheDocument();
    expect(screen.getByText("done · 5 ms · 3 rows")).toBeInTheDocument();
    expect(screen.getByText("failed · 9 ms").className).toMatch(/text-err/);
    expect(screen.getByText("cancelled · 1 ms").className).toMatch(/text-warn/);
    await userEvent.setup().click(screen.getByText("select 2"));
    expect(h.a.newQueryTab).toHaveBeenCalledWith({ profileId: "pg1", database: "app", text: "select 2", title: "history.sql" });
  });
  it("badges Phase D engines' entries by engine and reopens one on its own profile (its dialect comes with it)", async () => {
    useProfiles.setState({
      profiles: [
        { id: "sl1", name: "Local", engine: "sqlite", environment: "dev", auth: "none", location: "memory" },
        { id: "my1", name: "Shop", engine: "mySql", environment: "prod", auth: "password", host: "h" },
        { id: "dk1", name: "Lake", engine: "duckDb", environment: "test", auth: "none", location: "file" },
      ] as never,
      groups: [],
    });
    h.rpc.mockResolvedValue([
      { id: 4, profileId: "sl1", database: "main", sql: "select 4", executedAt: 0, status: "done", elapsedMs: 1, rowsAffected: null },
      { id: 5, profileId: "my1", database: "shop", sql: "select `5`", executedAt: 0, status: "done", elapsedMs: 1, rowsAffected: null },
      { id: 6, profileId: "dk1", database: "lake", sql: "FROM t6", executedAt: 0, status: "done", elapsedMs: 1, rowsAffected: null },
    ]);
    render(<Dialogs />);
    show({ kind: "history" });
    await waitFor(() => expect(screen.getByText("select 4")).toBeInTheDocument(), { timeout: 2000 });
    expect(screen.getByText(/\[SL\] Dev - Local · main/)).toBeInTheDocument();
    expect(screen.getByText(/\[MY\] Prod - Shop · shop/)).toBeInTheDocument();
    expect(screen.getByText(/\[DK\] Test - Lake · lake/)).toBeInTheDocument();
    await userEvent.setup().click(screen.getByText("FROM t6"));
    expect(h.a.newQueryTab).toHaveBeenCalledWith({ profileId: "dk1", database: "lake", text: "FROM t6", title: "history.sql" });
  });
  it("searches full text when typing and shows 'Nothing found.' on failure", async () => {
    // the list call is debounced and cancelled by typing, so key the mock on the method rather than call order
    h.rpc.mockImplementation(async (m: string) => (m === "history.list" ? rows : []));
    render(<Dialogs />);
    show({ kind: "history" });
    await userEvent.setup().type(screen.getByPlaceholderText("Search history…"), "foo");
    await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("history.search", { text: "foo", profileId: null, limit: 300 }), { timeout: 2000 });
    await waitFor(() => expect(screen.getByText("Nothing found.")).toBeInTheDocument(), { timeout: 2000 });
    h.rpc.mockRejectedValue(new Error("fts"));
    await userEvent.setup().type(screen.getByPlaceholderText("Search history…"), "x");
    await waitFor(() => expect(screen.getByText("Nothing found.")).toBeInTheDocument(), { timeout: 2000 });
  });
});

describe("history previews and retention (L17)", () => {
  const preview = "select " + "x, ".repeat(164);
  const long = { id: 7, profileId: null, database: null, sql: preview, sqlLength: 20_000, sqlTruncated: true, executedAt: 0, status: "done", elapsedMs: 1, rowsAffected: null };
  const route = (over: Record<string, (p: any) => unknown> = {}) =>
    h.rpc.mockImplementation(async (m: string, p: any) =>
      over[m] ? over[m](p) : m === "history.list" ? [long] : m === "history.retention" ? { days: 90, maxEntries: 50_000 } : undefined);

  it("shows a long entry's preview and opens it with its whole text from history.get", async () => {
    route({ "history.get": (p) => ({ ...long, sql: `whole text of ${p.id}`, sqlTruncated: false }) });
    render(<Dialogs />);
    show({ kind: "history" });
    await waitFor(() => expect(screen.getByText("… 20,000 characters in all")).toBeInTheDocument(), { timeout: 2000 });
    await userEvent.setup().click(screen.getByText("… 20,000 characters in all"));
    await waitFor(() => expect(h.a.newQueryTab).toHaveBeenCalledWith({ profileId: undefined, database: undefined, text: "whole text of 7", title: "history.sql" }));
    expect(h.rpc).toHaveBeenCalledWith("history.get", { id: 7 });
  });

  it("says so when the entry was pruned meanwhile and opens nothing", async () => {
    route({ "history.get": () => { throw new Error("This history entry is no longer kept"); } });
    render(<Dialogs />);
    show({ kind: "history" });
    await waitFor(() => expect(screen.getByText("… 20,000 characters in all")).toBeInTheDocument(), { timeout: 2000 });
    await userEvent.setup().click(screen.getByText("… 20,000 characters in all"));
    await waitFor(() => expect(useUi.getState().toasts.some((t) => t.kind === "error" && t.text === "This history entry is no longer kept")).toBe(true));
    expect(h.a.newQueryTab).not.toHaveBeenCalled();
  });

  it("edits the retention; the sidecar's refusal is shown", async () => {
    route({ "history.setRetention": (p) => (p.days > 3650 ? Promise.reject(new Error("Keep history between 1 and 3,650 days.")) : p) });
    render(<Dialogs />);
    show({ kind: "history" });
    const days = await screen.findByLabelText("Days of history to keep", undefined, { timeout: 2000 });
    expect((days as HTMLInputElement).value).toBe("90");
    expect((screen.getByLabelText("History entries to keep") as HTMLInputElement).value).toBe("50000");
    const save = screen.getByRole("button", { name: "Save" });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    const user = userEvent.setup();
    await user.clear(days);
    await user.type(days, "30");
    await user.click(save);
    await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("history.setRetention", { days: 30, maxEntries: 50_000 }));
    await waitFor(() => expect(useUi.getState().toasts.some((t) => t.kind === "info" && t.text === "History keeps 30 days, at most 50,000 entries.")).toBe(true));
    await user.clear(days);
    await user.type(days, "9999");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(useUi.getState().toasts.some((t) => t.kind === "error" && t.text === "Keep history between 1 and 3,650 days.")).toBe(true));
  });
});

describe("history ordering", () => {
  it("ignores a slow list response that lands after a newer search", async () => {
    let releaseList!: (v: unknown) => void;
    h.rpc.mockImplementation((m: string) => (m === "history.list" ? new Promise((r) => (releaseList = r)) : Promise.resolve([])));
    render(<Dialogs />);
    show({ kind: "history" });
    await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("history.list", expect.anything()), { timeout: 2000 });
    await userEvent.setup().type(screen.getByPlaceholderText("Search history…"), "foo");
    await waitFor(() => expect(screen.getByText("Nothing found.")).toBeInTheDocument(), { timeout: 2000 });
    releaseList([{ id: 1, profileId: null, database: null, sql: "stale row", executedAt: 0, status: "done", elapsedMs: 1, rowsAffected: null }]);
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText("stale row")).not.toBeInTheDocument();
  });
});

describe("dependencies", () => {
  const d = { kind: "dependencies", profileId: "ms1", database: "Sales", schema: "dbo", name: "p", objectId: 5, kind2: "proc" };
  const deps = [
    { direction: "uses", object: { schema: "dbo", name: "t", kind: "table", database: null } },
    { direction: "uses", object: { schema: "dbo", name: "v", kind: "view", database: "Other" } },
    { direction: "usedBy", object: { schema: "dbo", name: "q", kind: "proc" } },
  ];
  it("loads and groups dependencies into Uses and Used by; tables are not navigable", async () => {
    h.rpc.mockResolvedValue(deps);
    render(<Dialogs />);
    show(d);
    expect(screen.getByRole("status")).toHaveTextContent("Loading dependencies…");
    await screen.findByText("Uses (2)");
    expect(screen.getByText("Used by (1)")).toBeInTheDocument();
    expect(h.rpc).toHaveBeenCalledWith("meta.dependencies", { sessionId: "sess", object: { schema: "dbo", name: "p", objectId: 5, kind: "proc", database: "Sales", profileId: "ms1" } });
    const user = userEvent.setup();
    await user.click(screen.getByText(/dbo.t$/));
    expect(h.a.openDefinition).not.toHaveBeenCalled();
    await user.click(screen.getByText(/Other.dbo.v/));
    expect(h.a.openDefinition).toHaveBeenCalledWith("ms1", "Other", expect.objectContaining({ name: "v" }), false);
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
  it("shows None for empty groups and the error when loading fails", async () => {
    h.rpc.mockResolvedValueOnce([]);
    render(<Dialogs />);
    show(d);
    await screen.findByText("Uses (0)");
    expect(screen.getAllByText("None")).toHaveLength(2);
    cleanup();
    useUi.setState({ dialogs: [] });
    h.rpc.mockRejectedValueOnce(new Error("no access"));
    render(<Dialogs />);
    show(d);
    expect(await screen.findByText("no access")).toBeInTheDocument();
  });
  it("uses the dialog's own database for PostgreSQL objects without one", async () => {
    h.rpc.mockResolvedValue([{ direction: "usedBy", object: { schema: "public", name: "f", kind: "func" } }]);
    render(<Dialogs />);
    show({ ...d, profileId: "pg1", database: "app", kind2: "func" });
    await userEvent.setup().click(await screen.findByText(/public.f/));
    expect(h.a.openDefinition).toHaveBeenCalledWith("pg1", "app", expect.anything(), false);
  });
});

describe("authPrompt", () => {
  it("shows the device-code, copies it, and shows the URL", async () => {
    render(<Dialogs />);
    show({ kind: "authPrompt", message: "Sign in at the page", url: "https://login.example/device", code: "ABCD-1234" });
    expect(screen.getByText("ABCD-1234")).toBeInTheDocument();
    expect(screen.getByText("https://login.example/device")).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(screen.getByText("Copy code"));
    expect(await navigator.clipboard.readText()).toBe("ABCD-1234");
    await user.click(screen.getByText("Done"));
    expect(useUi.getState().dialogs).toHaveLength(0);
  });
  it("omits code and url when absent", () => {
    render(<Dialogs />);
    show({ kind: "authPrompt", message: "Waiting" });
    expect(screen.queryByText("Copy code")).toBeNull();
  });
});

describe("debugCall", () => {
  it("lets the user edit the call and start, or cancel", async () => {
    const resolve = vi.fn();
    render(<Dialogs />);
    show({ kind: "debugCall", title: "Debug f", sql: "select f(1)", resolve });
    const user = userEvent.setup();
    const ta = screen.getByRole("textbox");
    expect(ta).toHaveValue("select f(1)");
    await user.clear(ta);
    await user.type(ta, "select f(2)");
    await user.click(screen.getByText("Start debugging"));
    expect(resolve).toHaveBeenCalledWith("select f(2)");
    await user.click(screen.getByText("Cancel"));
    expect(resolve).toHaveBeenLastCalledWith(null);
  });
});


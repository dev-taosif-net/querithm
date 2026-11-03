// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  a: { cancelQuery: vi.fn(), newQueryTab: vi.fn(), runQuery: vi.fn(), showPlan: vi.fn(), txAction: vi.fn() },
  installUpdate: vi.fn(async () => {}),
  saveWorkspaceNow: vi.fn(async () => {}),
}));
vi.mock("../actions", () => h.a);
vi.mock("../commands", () => ({ displayKeys: (k: string) => k, commands: [], keysFor: () => [] }));
vi.mock("../navigation", async () => {
  const { create } = await import("zustand");
  return { goBack: vi.fn(), goForward: vi.fn(), useNav: create(() => ({ canBack: false, canForward: false })) };
});
vi.mock("../persistence", () => ({ saveWorkspaceNow: h.saveWorkspaceNow }));
vi.mock("../../editor/runScope", () => ({ showRunScope: vi.fn(), clearRunScope: vi.fn() }));
vi.mock("../../store/updates", async (orig) => ({ ...(await orig<typeof import("../../store/updates")>()), installUpdate: h.installUpdate }));

import { TopBar } from "../TopBar";
import { UpdateButton } from "../UpdateButton";
import { UpdateDialog } from "../UpdateDialog";
import { DOWNLOAD_PAGE, useUpdates } from "../../store/updates";
import { useUi } from "../../store/ui";
import { useTabs } from "../../store/tabs";
import { useExecutions } from "../../store/executions";

const info = { version: "0.2.0", currentVersion: "0.1.1", notes: "Faster grid\n<b>bold?</b>", date: null };
const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
const tab = (id: string, extra: any = {}) => ({ ...base, id, title: `${id}.sql`, ...extra });
const setUpdate = (s: any) => act(() => useUpdates.setState({ downloaded: 0, total: null, error: null, ...s }));
const close = vi.fn();

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ dialogs: [], palette: null, toasts: [] });
  useTabs.setState({ tabs: { t1: tab("t1") as any }, order: ["t1"], activeId: "t1", mru: ["t1"] });
  useExecutions.setState({ views: {} });
  useUpdates.setState({ phase: "idle", info: null, downloaded: 0, total: null, error: null });
});
afterEach(cleanup);

describe("Update button", () => {
  it("is hidden until a newer release is known", () => {
    render(<UpdateButton />);
    expect(screen.queryByRole("button")).toBeNull();
    setUpdate({ phase: "checking", info: null });
    expect(screen.queryByRole("button")).toBeNull();
    setUpdate({ phase: "available", info });
    expect(screen.getByRole("button", { name: "Update to Querithm 0.2.0" })).toHaveTextContent("Update");
    expect(screen.getByRole("button")).toHaveAttribute("title", "Querithm 0.2.0 is available");
  });

  it("sits right of the Settings button in the top bar", () => {
    useUpdates.setState({ phase: "available", info });
    render(<TopBar />);
    const settings = screen.getByRole("button", { name: "Settings" });
    expect(settings.nextElementSibling).toBe(screen.getByRole("button", { name: "Update to Querithm 0.2.0" }));
  });

  it("is not in the top bar without an update", () => {
    render(<TopBar />);
    expect(screen.getByRole("button", { name: "Settings" }).nextElementSibling).toBeNull();
  });

  it("shows download progress, then Restarting", () => {
    setUpdate({ phase: "downloading", info, downloaded: 0, total: null });
    render(<UpdateButton />);
    expect(screen.getByRole("button")).toHaveTextContent("Updating…");
    setUpdate({ downloaded: 42, total: 100 });
    expect(screen.getByRole("button")).toHaveTextContent("Updating 42%");
    setUpdate({ downloaded: 150, total: 100 });
    expect(screen.getByRole("button")).toHaveTextContent("Updating 100%");
    setUpdate({ phase: "installing" });
    expect(screen.getByRole("button")).toHaveTextContent("Restarting…");
  });

  it("stays visible after a failure and points to the details", () => {
    setUpdate({ phase: "failed", info, error: { kind: "network", message: "offline" } });
    render(<UpdateButton />);
    expect(screen.getByRole("button")).toHaveAttribute("title", "The update did not finish. Click for details.");
    expect(screen.getByRole("button").className).toContain("text-warn");
  });

  it("a click opens the update dialog once", async () => {
    useUpdates.setState({ phase: "available", info });
    render(<UpdateButton />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button"));
    await user.click(screen.getByRole("button"));
    expect(useUi.getState().dialogs).toEqual([{ kind: "update" }]);
  });
});

describe("Update dialog", () => {
  beforeEach(() => useUpdates.setState({ phase: "available", info }));

  it("names both versions and shows the release notes as plain text", () => {
    const { container } = render(<UpdateDialog close={close} />);
    expect(screen.getByText("Querithm 0.2.0 is available")).toBeInTheDocument();
    expect(screen.getByText("You have version 0.1.1")).toBeInTheDocument();
    expect(screen.getByText(/Faster grid/)).toHaveTextContent("<b>bold?</b>");
    expect(container.ownerDocument.querySelector("b")).toBeNull();
    expect(screen.getByText(/Your open tabs are saved/)).toBeInTheDocument();
  });

  it("without notes, shows no What's new section", () => {
    useUpdates.setState({ info: { ...info, notes: null } });
    render(<UpdateDialog close={close} />);
    expect(screen.queryByText("What's new")).toBeNull();
  });

  it("Update & Restart installs and saves the tabs first; Enter does the same", async () => {
    render(<UpdateDialog close={close} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Update & Restart" }));
    expect(h.installUpdate).toHaveBeenCalledWith(h.saveWorkspaceNow);
    await user.keyboard("{Enter}");
    expect(h.installUpdate).toHaveBeenCalledTimes(2);
  });

  // M19: the dialog opened on itself and Enter installed, rolling back open transactions.
  it("opens as an alertdialog on Later, so Enter right after it opens does not install", async () => {
    render(<UpdateDialog close={close} />);
    expect(screen.getByRole("alertdialog", { name: "Querithm 0.2.0 is available" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Later" })).toHaveFocus();
    await userEvent.setup().keyboard("{Enter}");
    expect(close).toHaveBeenCalledTimes(1);
    expect(h.installUpdate).not.toHaveBeenCalled();
  });

  it("Later closes without installing", async () => {
    render(<UpdateDialog close={close} />);
    await userEvent.setup().click(screen.getByRole("button", { name: "Later" }));
    expect(close).toHaveBeenCalled();
    expect(h.installUpdate).not.toHaveBeenCalled();
  });

  it("warns about tabs that may have an open transaction (unknown state, M9)", () => {
    useTabs.setState({ tabs: { t1: tab("t1", { txState: "unknown" }) as any }, order: ["t1"] });
    useUpdates.setState({ phase: "available", info } as any);
    render(<UpdateDialog close={() => {}} />);
    expect(screen.getByText(/1 tab may have an open transaction; it will be rolled back/)).toBeInTheDocument();
  });

  it("warns about open transactions, running queries and unsaved files", () => {
    useTabs.setState({
      tabs: {
        t1: tab("t1", { txState: "open" }) as any,
        t2: tab("t2", { txState: "open", filePath: "/x/a.sql", dirty: true }) as any,
        t3: tab("t3", { filePath: "/x/b.sql", dirty: false }) as any,
      },
      order: ["t1", "t2", "t3"],
    });
    useExecutions.setState({
      views: {
        e1: { id: "e1", state: "running" } as any,
        e2: { id: "e2", state: "streaming" } as any,
        e3: { id: "e3", state: "completed" } as any,
      },
    });
    render(<UpdateDialog close={close} />);
    expect(screen.getByText(/2 open transactions will be rolled back/)).toBeInTheDocument();
    expect(screen.getByText(/2 running queries will be cancelled/)).toBeInTheDocument();
    expect(screen.getByText(/1 file has unsaved changes/)).toBeInTheDocument();
  });

  it("uses the singular for one transaction or query, and shows no warnings when nothing is at risk", () => {
    useTabs.setState({ tabs: { t1: tab("t1", { txState: "open" }) as any }, order: ["t1"] });
    useExecutions.setState({ views: { e1: { id: "e1", state: "queued" } as any } });
    const { unmount } = render(<UpdateDialog close={close} />);
    expect(screen.getByText(/1 open transaction will be rolled back/)).toBeInTheDocument();
    expect(screen.getByText(/1 running query will be cancelled/)).toBeInTheDocument();
    unmount();
    useTabs.setState({ tabs: { t1: tab("t1") as any }, order: ["t1"] });
    useExecutions.setState({ views: {} });
    render(<UpdateDialog close={close} />);
    expect(screen.queryByText(/⚠/)).toBeNull();
  });

  it("while downloading: a progress bar, sizes, and Hide instead of Update", async () => {
    useUpdates.setState({ phase: "downloading", downloaded: 3 * 1_048_576, total: 12 * 1_048_576 });
    render(<UpdateDialog close={close} />);
    const bar = screen.getByRole("progressbar", { name: "Download progress" });
    expect(bar).toHaveAttribute("aria-valuenow", "25");
    expect(screen.getByText(/Downloading 25%/)).toHaveTextContent("(3.0 MB of 12.0 MB)");
    expect(screen.queryByRole("button", { name: "Update & Restart" })).toBeNull();
    const user = userEvent.setup();
    await user.keyboard("{Enter}");
    expect(h.installUpdate).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Hide" }));
    expect(close).toHaveBeenCalled();
  });

  it("an unknown download size shows an indeterminate bar", () => {
    useUpdates.setState({ phase: "downloading", downloaded: 0, total: null });
    render(<UpdateDialog close={close} />);
    expect(screen.getByRole("progressbar")).not.toHaveAttribute("aria-valuenow");
    expect(screen.getByText("Downloading…")).toBeInTheDocument();
  });

  it("while installing: says it restarts and cannot be hidden", () => {
    useUpdates.setState({ phase: "installing" });
    render(<UpdateDialog close={close} />);
    expect(screen.getByText(/Querithm will restart in a moment/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Hide" })).toBeDisabled();
  });

  it("after a failure: the reason and Try again", async () => {
    useUpdates.setState({ phase: "failed", error: { kind: "network", message: "Could not reach the update server." } });
    render(<UpdateDialog close={close} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Could not reach the update server.");
    expect(screen.queryByText(DOWNLOAD_PAGE)).toBeNull();
    await userEvent.setup().click(screen.getByRole("button", { name: "Try again" }));
    expect(h.installUpdate).toHaveBeenCalledWith(h.saveWorkspaceNow);
  });

  it.each(["signature", "unsupported", "permission"])("a %s failure points to the download page", (kind) => {
    useUpdates.setState({ phase: "failed", error: { kind, message: "Download it from the Querithm website." } });
    render(<UpdateDialog close={close} />);
    expect(screen.getByText(DOWNLOAD_PAGE)).toBeInTheDocument();
  });

  it("with no release known (withdrawn while open), says up to date", async () => {
    useUpdates.setState({ phase: "idle", info: null });
    render(<UpdateDialog close={close} />);
    expect(screen.getByText("Querithm is up to date.")).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "Close" }));
    expect(close).toHaveBeenCalled();
  });
});

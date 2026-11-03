// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";

const { monacoMock, created } = vi.hoisted(() => {
  const created: { opts: Record<string, unknown>; disposed: boolean; modelDisposed: boolean }[] = [];
  return {
    created,
    monacoMock: {
      editor: {
        create: (_host: unknown, opts: Record<string, unknown>) => {
          const e = { opts, disposed: false, modelDisposed: false };
          created.push(e);
          return { getModel: () => ({ dispose: () => (e.modelDisposed = true) }), dispose: () => (e.disposed = true) };
        },
      },
    },
  };
});
vi.mock("../../editor/monaco", () => ({ initMonaco: vi.fn(), monaco: monacoMock }));

import { useTabs } from "../../store/tabs";
import { GridConflictsDialog, GridPendingDialog, GridPreviewDialog } from "../GridDialogs";
import { GridStaging } from "../staging";
import { editInfo, ordersSet } from "./fixtures";

afterEach(() => {
  cleanup();
  created.length = 0;
});

describe("GridPreviewDialog", () => {
  const dlg = (engine: "mssql" | "pg", notes: string[] = []) => ({ kind: "gridPreview" as const, title: "Changes to dbo.orders", sql: "UPDATE x", notes, engine, resolve: vi.fn() });

  for (const [engine, language] of [["mssql", "tsql"], ["pg", "pgsql"]] as const) {
    it(`opens a read-only editor in the ${language} language (${engine})`, () => {
      const d = dlg(engine);
      const { unmount } = render(<GridPreviewDialog d={d} />);
      expect(created[0].opts).toMatchObject({ value: "UPDATE x", language, readOnly: true });
      unmount();
      expect(created[0].disposed && created[0].modelDisposed).toBe(true);
    });
  }

  it("lists notes and resolves apply, cancel and close", () => {
    const d = dlg("mssql", ["Wrapped in a transaction", "1 row"]);
    render(<GridPreviewDialog d={d} />);
    expect(screen.getByText("Wrapped in a transaction")).toBeTruthy();
    fireEvent.click(screen.getByText("Apply changes"));
    expect(d.resolve).toHaveBeenLastCalledWith(true);
    fireEvent.click(screen.getByText("Cancel"));
    expect(d.resolve).toHaveBeenLastCalledWith(false);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(d.resolve).toHaveBeenCalledTimes(3);
  });

  it("omits the notes list when there are none", () => {
    render(<GridPreviewDialog d={dlg("pg")} />);
    expect(document.querySelector("ul")).toBeNull();
  });
});

describe("GridConflictsDialog", () => {
  function setup(conflicts: unknown[]) {
    const staging = new GridStaging(editInfo(), ordersSet() as never);
    staging.setCell({ kind: "existing", row: 0 }, 1, { value: "uno" });
    staging.setCell({ kind: "existing", row: 0 }, 2, { value: null });
    const d = { kind: "gridConflicts" as const, conflicts: conflicts as never, staging, resolve: vi.fn() };
    render(<GridConflictsDialog d={d} />);
    return d;
  }

  it("shows what you read, the server's value and yours for each staged column", () => {
    const d = setup([
      { rowIndex: 0, insert: false, reason: "changedOnServer", current: { name: { isNull: false, useDefault: false, text: "ONE!" }, qty: { isNull: true, useDefault: false, text: null } } },
      { rowIndex: 2, insert: false, reason: "deletedOnServer" },
    ]);
    expect(screen.getByText(/1 changed, 1 deleted since the query ran/)).toBeTruthy();
    expect(screen.getByText(/key 1/)).toBeTruthy();
    expect(screen.getByText("changed on the server")).toBeTruthy();
    expect(screen.getByText("deleted on the server")).toBeTruthy();
    expect(screen.getByText("ONE!")).toBeTruthy(); // server now
    expect(screen.getByText("uno")).toBeTruthy(); // yours
    expect(screen.getByText("one")).toBeTruthy(); // you read
    expect(screen.getAllByText("NULL").length).toBeGreaterThanOrEqual(2);
    fireEvent.click(screen.getByText("Keep editing"));
    fireEvent.click(screen.getByText("Drop my edits of these rows"));
    fireEvent.click(screen.getByText("Overwrite with my values"));
    expect(d.resolve.mock.calls.map((c) => c[0])).toEqual(["cancel", "discard", "overwrite"]);
  });

  it("shows ? when the server value is unknown and disables overwrite when every row was deleted", () => {
    setup([{ rowIndex: 0, insert: false, reason: "deletedOnServer", current: {} }]);
    expect((screen.getByText("Overwrite with my values") as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getAllByText("?").length).toBe(2);
  });

  it("truncates long conflict lists to 50 and says how many more", () => {
    setup(Array.from({ length: 55 }, (_, i) => ({ rowIndex: i, insert: false, reason: "changedOnServer" })));
    expect(screen.getByText("…and 5 more.")).toBeTruthy();
  });

  it("Escape cancels", () => {
    const d = setup([{ rowIndex: 0, insert: false, reason: "changedOnServer" }]);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(d.resolve).toHaveBeenCalledWith("cancel");
  });
});

describe("GridPendingDialog", () => {
  it("offers cancel, discard and apply for the tab's staged changes", () => {
    useTabs.setState({ tabs: { t1: { id: "t1", title: "Orders.sql" } as never } });
    const d = { kind: "gridPending" as const, tabId: "t1", summary: "2 updated", resolve: vi.fn() };
    render(<GridPendingDialog d={d} />);
    expect(screen.getByText("Orders.sql")).toBeTruthy();
    expect(screen.getByText(/2 updated/)).toBeTruthy();
    fireEvent.click(screen.getByText("Cancel"));
    fireEvent.click(screen.getByText("Discard changes"));
    fireEvent.click(screen.getByText("Apply changes"));
    expect(d.resolve.mock.calls.map((c) => c[0])).toEqual(["cancel", "discard", "apply"]);
  });
});

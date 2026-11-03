import { describe, expect, it, vi, beforeEach } from "vitest";
import { useTabs } from "../../store/tabs";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";

vi.mock("../monaco", () => ({ monaco: { languages: { CompletionItemKind: {} } } }));
vi.mock("../editors", () => ({
  tabIdOfModel: (m: { key?: string }) => m.key ?? "t1",
  // A notebook cell's model "t1::c1" belongs to the notebook tab t1 (Phase C3).
  modelOwner: (k?: string) => (k?.includes("::") ? k.split("::")[0] : k),
  onEditorCreated: () => {},
  onModelContent: () => {},
}));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

const { docContext } = await import("../providers");
const model = { getLanguageId: () => "tsql" } as never;
const index = { objects: [] } as never;

describe("editor document context", () => {
  beforeEach(() => {
    useTabs.setState({ tabs: { t1: { id: "t1", title: "q", profileId: "p1", database: "Sales" } as never } });
    useSnapshots.setState({ entries: { [snapKey("p1", "Sales")]: { key: snapKey("p1", "Sales"), profileId: "p1", database: "Sales", status: "fresh", index, version: 1 } } });
    useConnections.setState({ disconnected: {} });
  });

  it("a connected tab gets its profile, database and cached schema", () => {
    expect(docContext(model)).toMatchObject({ tabId: "t1", profileId: "p1", database: "Sales", schema: index });
  });
  it("a tab on a disconnected server gets no schema and no profile (no tier-1 or tier-2 object suggestions)", () => {
    useConnections.setState({ disconnected: { p1: true } });
    expect(docContext(model)).toEqual({ tabId: "t1", dialect: "tsql", schema: null });
  });
  it("a SQLite tab reads its own session's view and names the session for tier 2 and signature help (Phase D0d)", () => {
    const own = { objects: [{ id: 9 }] } as never;
    useProfiles.setState({ profiles: [{ id: "lite", name: "App", engine: "sqlite", location: "file", file: { grantId: "g" }, environment: "dev" } as never, { id: "p1", name: "S", engine: "mssql", environment: "dev" } as never] });
    useTabs.setState({ tabs: { t1: { id: "t1", title: "q", profileId: "lite", database: "main", sessionId: "s1" } as never } });
    useSnapshots.setState({ entries: {
      [snapKey("lite", "main")]: { key: snapKey("lite", "main"), profileId: "lite", database: "main", status: "fresh", index, version: 1 },
      [snapKey("lite", "main", "s1")]: { key: snapKey("lite", "main", "s1"), profileId: "lite", database: "main", sessionId: "s1", status: "fresh", index: own, version: 1 },
    } });
    expect(docContext(model)).toMatchObject({ profileId: "lite", database: "main", sessionId: "s1", schema: own });
    // A server tab never names its session.
    useTabs.setState({ tabs: { t1: { id: "t1", title: "q", profileId: "p1", database: "Sales", sessionId: "s2" } as never } });
    useSnapshots.setState({ entries: { [snapKey("p1", "Sales")]: { key: snapKey("p1", "Sales"), profileId: "p1", database: "Sales", status: "fresh", index, version: 1 } } });
    expect(docContext(model)).not.toHaveProperty("sessionId");
    expect(docContext(model)).toMatchObject({ schema: index });
  });
  it("a notebook cell reads its notebook's connection, and keeps its own document id for the sidecar", () => {
    const cell = { getLanguageId: () => "tsql", key: "t1::c1" } as never;
    expect(docContext(cell)).toMatchObject({ tabId: "t1", docId: "t1::c1", profileId: "p1", database: "Sales", schema: index });
    expect(docContext(model)).not.toHaveProperty("docId");
  });
});

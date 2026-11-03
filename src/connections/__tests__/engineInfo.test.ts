import { describe, expect, it, vi } from "vitest";
import type { EngineInfo } from "../../ipc/contract";

const rpc = vi.hoisted(() => vi.fn());
vi.mock("../../ipc/client", () => ({ rpc }));

import { loadEngineInfo, offeredEngines } from "../engineInfo";

const info = (engine: EngineInfo["engine"], exposed: boolean): EngineInfo => ({
  engine, dialect: engine === "mssql" ? "tsql" : engine, name: engine, exposed, defaultAuth: "none", authKinds: [], locations: ["server"],
});

describe("engines the sidecar exposes (system.engines)", () => {
  it("offers only exposed engines, in the app's display order, whatever order the sidecar lists them in", () => {
    expect(offeredEngines([info("mySql", true), info("sqlite", false), info("pg", true), info("mssql", true)])).toEqual(["mssql", "pg", "mySql"]);
  });
  it("falls back to SQL Server and PostgreSQL when the list is missing, empty or names nothing this build knows", () => {
    expect(offeredEngines(null)).toEqual(["mssql", "pg"]);
    expect(offeredEngines([])).toEqual(["mssql", "pg"]);
    expect(offeredEngines([{ ...info("pg", true), engine: "duckdb" as never }])).toEqual(["mssql", "pg"]);
  });
  it("keeps the edited profile's own engine even when it is not exposed", () => {
    expect(offeredEngines([info("mssql", true), info("pg", true), info("sqlite", false)], "sqlite")).toEqual(["mssql", "pg", "sqlite"]);
    expect(offeredEngines(null, "pg")).toEqual(["mssql", "pg"]);
  });
  it("loadEngineInfo: the list, or null when the call fails or answers something else", async () => {
    rpc.mockResolvedValueOnce([info("mssql", true)]);
    expect(await loadEngineInfo()).toEqual([info("mssql", true)]);
    expect(rpc).toHaveBeenLastCalledWith("system.engines");
    rpc.mockRejectedValueOnce(new Error("unknown method"));
    expect(await loadEngineInfo()).toBeNull();
    rpc.mockResolvedValueOnce(undefined);
    expect(await loadEngineInfo()).toBeNull();
  });
});

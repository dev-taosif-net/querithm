// "Open as plan" (Phase C2): a showplan XML or EXPLAIN (FORMAT JSON) value from a result cell is parsed by the sidecar
// (plan.parse, nothing runs on a server) and shown in the tab's Plan panel. The plan is added to the execution the cell
// belongs to, newest first, so the results stay where they are and Results brings them back.
import type { Engine } from "../../ipc/contract";
import type { JsonPlanFormat } from "./jsonView";
import { rpc } from "../../ipc/client";
import { useExecutions } from "../../store/executions";
import { useTabs } from "../../store/tabs";
import { toast } from "../../store/ui";

/** Plans opened from cells keep this batch index, so they never mix with the execution's own (≥ 0). */
export const OPENED_PLAN_BATCH = -1;

/** Plans opened from cells kept per execution (oldest dropped). */
export const MAX_OPENED_PLANS = 5;

export type PlanFormat = "showplan" | JsonPlanFormat;

/**
 * Which engine's parser reads a plan value, and how the button names it: showplan XML is SQL Server's, EXPLAIN JSON
 * PostgreSQL's; the SQLite, MySQL, DuckDB and Oracle forms are what the sidecar returns as "sqlite-eqp", "mysql-json",
 * "duckdb-json" and "oracle-json".
 */
export const PLAN_FORMATS: Record<PlanFormat, { engine: Engine; title: string }> = {
  showplan: { engine: "mssql", title: "Show this SQL Server showplan in the Plan panel" },
  explainJson: { engine: "pg", title: "Show this PostgreSQL EXPLAIN plan in the Plan panel" },
  sqliteEqp: { engine: "sqlite", title: "Show this SQLite query plan in the Plan panel" },
  mysqlJson: { engine: "mySql", title: "Show this MySQL EXPLAIN plan in the Plan panel" },
  duckdbJson: { engine: "duckDb", title: "Show this DuckDB plan in the Plan panel" },
  oracleJson: { engine: "oracle", title: "Show this Oracle plan in the Plan panel" },
};

export function planEngine(format: PlanFormat): Engine {
  return PLAN_FORMATS[format].engine;
}

/** Parses `raw` and shows it in the tab's Plan panel. False (with a toast saying why) when it could not. */
export async function openAsPlan(o: { tabId: string; executionId: string; engine: Engine; raw: string }): Promise<boolean> {
  if (!useExecutions.getState().views[o.executionId] || !useTabs.getState().tabs[o.tabId]) {
    toast("error", "The results this value came from are gone. Run the query again to open the plan.");
    return false;
  }
  let plan;
  try {
    plan = (await rpc("plan.parse", { engine: o.engine, raw: o.raw })).plan;
  } catch (e) {
    toast("error", `Could not open the plan: ${e instanceof Error ? e.message : String(e)}`);
    return false;
  }
  if (!useExecutions.getState().views[o.executionId]) return false;
  useExecutions.getState().patch(o.executionId, (v) => {
    const opened = v.plans.filter((p) => p.batchIndex === OPENED_PLAN_BATCH).slice(0, MAX_OPENED_PLANS - 1);
    const own = v.plans.filter((p) => p.batchIndex !== OPENED_PLAN_BATCH);
    return { plans: [{ batchIndex: OPENED_PLAN_BATCH, plan, raw: o.raw }, ...opened, ...own] };
  });
  // Results stay on this execution; only the bottom panel moves to the plan.
  if (useTabs.getState().tabs[o.tabId]?.activeExecutionId === o.executionId) useTabs.getState().update(o.tabId, { bottomTab: "plan", bottomOpen: true });
  else toast("info", "The plan was added to the execution it came from; switch to it to see the plan.");
  return true;
}

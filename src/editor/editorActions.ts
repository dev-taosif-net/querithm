// Editor commands that need the schema: go to definition (F12), and the Trace helper that inserts a PRINT for a
// variable (§11: variable values are not available in Trace; this is the supported way to inspect state).
import { editorForTab, getModel } from "./editors";
import { identifierAt, resolveObjectAt } from "./completion";
import { docContext } from "./providers";
import { openDefinition } from "../app/actions";
import { useTabs } from "../store/tabs";
import { toast } from "../store/ui";
import { expandWildcardAtOffset } from "./expandWildcard";
import { COMMENT_ACTIONS } from "./commentMenu";
import type { Dialect } from "../ipc/contract";
import { dialectForLanguage } from "../engines";

/**
 * The statement that prints a variable's value to Messages; null where the dialect has no PRINT for `@variables`
 * (PostgreSQL and SQLite have none; MySQL's user variables are read with SELECT, which would add a result set).
 */
const PRINT_VARIABLE: Record<Dialect, ((name: string) => string) | null> = {
  tsql: (name) => `PRINT CONCAT(N'${name} = ', CONVERT(nvarchar(max), ${name}));`,
  pg: null,
  sqlite: null,
  mySql: null,
  duckDb: null,
  // DBMS_OUTPUT needs SET SERVEROUTPUT (D4 owner decision O5, S04); nothing is offered until then.
  oracle: null,
};

export async function goToDefinition(tabId: string) {
  const ed = editorForTab(tabId);
  const model = getModel(tabId);
  const pos = ed?.getPosition();
  if (!ed || !model || !pos) return;
  const ctx = docContext(model);
  if (!ctx?.profileId || !ctx.database) return toast("info", "Connect this tab to a database first.");
  const r = resolveObjectAt(model.getValue(), model.getOffsetAt(pos), ctx.dialect, ctx.schema);
  const obj = r?.object ?? r?.column?.table;
  if (!obj) return toast("info", "No database object under the cursor.");
  if (obj.kind === "table") {
    // Tables have no module definition: script them instead.
    const { scriptObject } = await import("../app/actions");
    return scriptObject(ctx.profileId, ctx.database, { schema: obj.schema, name: obj.name, kind: obj.kind, objectId: obj.id }, "create");
  }
  return openDefinition(ctx.profileId, ctx.database, { schema: obj.schema, name: obj.name, kind: obj.kind, objectId: obj.id }, false);
}

export function insertPrintForVariable(tabId: string) {
  const ed = editorForTab(tabId);
  const model = getModel(tabId);
  const pos = ed?.getPosition();
  if (!ed || !model || !pos) return;
  const tab = useTabs.getState().tabs[tabId];
  const dialect = dialectForLanguage(model.getLanguageId());
  const ident = identifierAt(model.getValue(), model.getOffsetAt(pos), dialect);
  const name = ident?.parts.at(-1);
  const print = PRINT_VARIABLE[dialect];
  if (!print || !name || !name.startsWith("@")) return toast("info", "Place the cursor on a @variable.");
  const line = pos.lineNumber;
  const indent = model.getLineContent(line).match(/^\s*/)?.[0] ?? "";
  const text = `${indent}${print(name)} -- querithm:trace\n`;
  ed.executeEdits("insertPrint", [{ range: { startLineNumber: line + 1, startColumn: 1, endLineNumber: line + 1, endColumn: 1 }, text }]);
  if (tab?.boundObject) toast("info", "Inserted. Apply the procedure to see the value in Messages; remove the line afterwards (search for querithm:trace).");
}

/** Comment/uncomment the selected lines (or the cursor line). Refocuses the editor: the palette takes focus. */
export function commentLines(tabId: string, mode: keyof typeof COMMENT_ACTIONS) {
  const ed = editorForTab(tabId);
  if (!ed) return;
  ed.focus();
  ed.trigger("querithm", COMMENT_ACTIONS[mode], null);
}

export function expandWildcardAtCursor(tabId: string) {
  const ed = editorForTab(tabId);
  const model = getModel(tabId);
  const pos = ed?.getPosition();
  if (!ed || !model || !pos) return;
  const ctx = docContext(model);
  if (!ctx) return;
  const exp = expandWildcardAtOffset(model.getValue(), model.getOffsetAt(pos), ctx.dialect, ctx.schema);
  if (!exp) return toast("info", "No wildcard '*' to expand at cursor.");
  const start = model.getPositionAt(exp.start);
  const end = model.getPositionAt(exp.end);
  ed.pushUndoStop();
  ed.executeEdits("expandWildcard", [
    {
      range: {
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: end.lineNumber,
        endColumn: end.column,
      },
      text: exp.text,
    },
  ]);
  ed.pushUndoStop();
}


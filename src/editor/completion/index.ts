// Public API of the tier-1 autocomplete engine (§9).
export type {
  Clause, CompletionRequest, CompletionResult, CursorContext, SchemaIndex, TableInScope, Tier1Item,
} from "./types";
export { tokenize, scan } from "./scanner";
export type { Token, TokenKind } from "./scanner";
export { buildSchemaIndex, findObject, resolveTable } from "./schemaIndex";
export { complete } from "./complete";
export { matchRank, humpMatch, sortText } from "./rank";
export {
  statementBoundsAt, detectUse, undeclaredParameters, unsetUserVariables, identifierAt, resolveObjectAt,
} from "./sqlText";
export type { IdentifierAt, ResolvedAt } from "./sqlText";
export { keywordCaseEdit, applyKeywordCase } from "./keywordCase";
export type { CaseEdit } from "./keywordCase";
export { firstStatementLine, statementTitle } from "./statementTitle";

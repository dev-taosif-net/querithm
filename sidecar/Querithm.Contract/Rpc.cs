namespace Querithm.Contract;

/// <summary>Binds an interface method to a JSON-RPC method name. Params are always one DTO (or none).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcMethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Binds a notification name to its payload DTO.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcNotificationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Every control-plane method (§5). Querithm.Host implements this; ContractGen emits the
/// TypeScript RpcMap from it. Add a method here first, then regenerate contract.ts.
/// </summary>
public interface IQuerithmRpc
{
    [RpcMethod("system.ping")] Task<PingResult> Ping();
    [RpcMethod("system.shutdown")] Task Shutdown();
    /// <summary>Phase D0g: the engines this build knows, exposed ones first; hidden ones only behind the dev setting.</summary>
    [RpcMethod("system.engines")] Task<EngineInfo[]> Engines();
    /// <summary>M22: what the sidecar met at start that the user must hear about (a damaged store set aside); each is answered once.</summary>
    [RpcMethod("system.notices")] Task<SystemNotice[]> Notices();
    /// <summary>M17: a webview render error, written to the sidecar log (capped, scrubbed).</summary>
    [RpcMethod("system.reportError")] Task ReportError(ErrorReport p);
    /// <summary>M21: the newest lines of Querithm's own log files (sidecar and shell), scrubbed; reads no other file.</summary>
    [RpcMethod("system.logTail")] Task<LogTailResult> LogTail(LogTailParams p);

    [RpcMethod("profile.list")] Task<Profile[]> ProfileList();
    [RpcMethod("profile.save")] Task<Profile> ProfileSave(Profile profile);
    [RpcMethod("profile.delete")] Task ProfileDelete(ProfileRef p);
    [RpcMethod("profile.test")] Task<ProfileTestResult> ProfileTest(ProfileTestParams p);
    [RpcMethod("profile.parseConnectionString")] Task<ParseConnectionStringResult> ProfileParseConnectionString(ParseConnectionStringParams p);
    [RpcMethod("ssh.trustHostKey")] Task SshTrustHostKey(SshHostKey p);
    [RpcMethod("group.list")] Task<ProfileGroup[]> GroupList();
    [RpcMethod("group.save")] Task<ProfileGroup[]> GroupSave(ProfileGroup g);
    [RpcMethod("group.delete")] Task<ProfileGroup[]> GroupDelete(ProfileGroup g);

    [RpcMethod("session.open")] Task<SessionOpenResult> SessionOpen(SessionOpenParams p);
    /// <summary>Phase D0c: closes the profile's sessions and forgets its file grant; refused while one has an open or unknown transaction or a running query. The shell then removes the grant.</summary>
    [RpcMethod("grant.revoke")] Task<GrantRevokeResult> GrantRevoke(GrantRevokeParams p);
    [RpcMethod("session.close")] Task SessionClose(SessionCloseParams p);
    [RpcMethod("session.changeDatabase")] Task<ChangeDatabaseResult> SessionChangeDatabase(ChangeDatabaseParams p);
    [RpcMethod("session.state")] Task<SessionState> SessionStateGet(SessionRef p);
    [RpcMethod("session.capabilities")] Task<Capabilities> SessionCapabilities(SessionRef p);

    [RpcMethod("query.execute")] Task<QueryExecuteResult> QueryExecute(QueryExecuteParams p);
    [RpcMethod("query.cancel")] Task<CancelResult> QueryCancel(ExecutionRef p);
    [RpcMethod("query.cell")] Task<CellResult> QueryCell(CellParams p);
    [RpcMethod("query.close")] Task QueryClose(ExecutionRef p);

    [RpcMethod("data.rowTokens")] Task<RowTokensResult> DataRowTokens(RowTokensParams p);
    [RpcMethod("data.previewPatch")] Task<GridPreviewResult> DataPreviewPatch(GridPatchParams p);
    [RpcMethod("data.applyPatch")] Task<GridApplyResult> DataApplyPatch(GridPatchParams p);

    [RpcMethod("import.inspect")] Task<ImportInspectResult> ImportInspect(ImportInspectParams p);
    [RpcMethod("import.checkTarget")] Task<ImportTargetResult> ImportCheckTarget(ImportTargetParams p);
    [RpcMethod("import.run")] Task<ImportRunResult> ImportRun(ImportRunParams p);
    [RpcMethod("import.cancel")] Task<ImportCancelResult> ImportCancel(ImportJobRef p);

    [RpcMethod("schemaCompare.run")] Task<SchemaCompareResult> SchemaCompareRun(SchemaCompareParams p);
    [RpcMethod("schemaCompare.objectSql")] Task<CompareObjectSql> SchemaCompareObjectSql(CompareObjectParams p);
    [RpcMethod("schemaCompare.migrationSql")] Task<SchemaMigrationResult> SchemaCompareMigrationSql(SchemaMigrationParams p);
    [RpcMethod("schemaCompare.exportModel")] Task<SchemaModelFile> SchemaCompareExportModel(SchemaExportParams p);

    [RpcMethod("dataCompare.start")] Task<DataCompareStart> DataCompareStart(DataCompareParams p);
    [RpcMethod("dataCompare.cancel")] Task<CancelResult> DataCompareCancel(DataCompareRef p);
    [RpcMethod("dataCompare.syncSql")] Task<DataSyncSqlResult> DataCompareSyncSql(DataSyncSqlParams p);

    [RpcMethod("refactor.symbolAt")] Task<SymbolAtResult> RefactorSymbolAt(SymbolAtParams p);
    [RpcMethod("refactor.findUsages")] Task<FindUsagesResult> RefactorFindUsages(FindUsagesParams p);
    [RpcMethod("refactor.previewRename")] Task<RenamePreviewResult> RefactorPreviewRename(RenameParams p);

    [RpcMethod("tx.begin")] Task<SessionState> TxBegin(SessionRef p);
    [RpcMethod("tx.commit")] Task<SessionState> TxCommit(SessionRef p);
    [RpcMethod("tx.rollback")] Task<SessionState> TxRollback(SessionRef p);

    [RpcMethod("meta.databases")] Task<DatabaseInfo[]> MetaDatabases(SessionRef p);
    [RpcMethod("meta.snapshot")] Task<SchemaSnapshot> MetaSnapshot(SnapshotParams p);
    [RpcMethod("meta.objects")] Task<SnapObject[]> MetaObjects(ObjectsParams p);
    [RpcMethod("meta.refresh")] Task<JobRef> MetaRefresh(RefreshParams p);
    [RpcMethod("meta.objectDetails")] Task<ObjectDetails> MetaObjectDetails(ObjectDetailsParams p);
    [RpcMethod("meta.definition")] Task<DefinitionResult> MetaDefinition(DefinitionParams p);
    [RpcMethod("meta.dependencies")] Task<Dependency[]> MetaDependencies(DefinitionParams p);
    [RpcMethod("meta.search")] Task<ObjectRef[]> MetaSearch(SearchParams p);
    [RpcMethod("meta.script")] Task<SqlText> MetaScript(ScriptParams p);
    [RpcMethod("meta.rowCounts")] Task<RowCount[]> MetaRowCounts(RowCountsParams p);

    [RpcMethod("lang.complete")] Task<CompletionItem[]> LangComplete(CompleteParams p);
    [RpcMethod("lang.diagnostics")] Task<Diagnostic[]> LangDiagnostics(DiagnosticsParams p);
    [RpcMethod("lang.format")] Task<FormatResult> LangFormat(FormatParams p);
    [RpcMethod("lang.signature")] Task<SignatureHelp> LangSignature(SignatureParams p);
    [RpcMethod("lang.folding")] Task<FoldingRange[]> LangFolding(FoldingParams p);
    [RpcMethod("lang.classify")] Task<Classification> LangClassify(ClassifyParams p);
    [RpcMethod("lang.forget")] Task LangForget(LangForgetParams p);

    [RpcMethod("plan.get")] Task<QueryExecuteResult> PlanGet(PlanParams p);
    [RpcMethod("plan.parse")] Task<PlanParseResult> PlanParse(PlanParseParams p);

    [RpcMethod("trace.start")] Task<TraceRef> TraceStart(TraceStartParams p);
    [RpcMethod("trace.stop")] Task TraceStop(TraceRef p);

    [RpcMethod("debug.attach")] Task<DebugAttachResult> DebugAttach(DebugAttachParams p);
    [RpcMethod("debug.setBreakpoint")] Task DebugSetBreakpoint(DebugBreakpointParams p);
    [RpcMethod("debug.dropBreakpoint")] Task DebugDropBreakpoint(DebugBreakpointParams p);
    [RpcMethod("debug.step")] Task DebugStep(DebugStepParams p);
    [RpcMethod("debug.source")] Task<DebugSource> DebugSourceGet(DebugSourceParams p);
    [RpcMethod("debug.detach")] Task DebugDetach(DebugRef p);

    [RpcMethod("history.list")] Task<HistoryEntry[]> HistoryList(HistoryListParams p);
    [RpcMethod("history.search")] Task<HistoryEntry[]> HistorySearch(HistorySearchParams p);
    /// <summary>L17: one entry with its whole text (list and search send previews); <c>historyGone</c> once it was pruned.</summary>
    [RpcMethod("history.get")] Task<HistoryEntry> HistoryGet(HistoryGetParams p);
    [RpcMethod("history.retention")] Task<HistoryRetention> HistoryRetentionGet();
    /// <summary>Validated (<c>invalid</c> outside <see cref="HistoryLimits"/>), stored, then applied by a background pass.</summary>
    [RpcMethod("history.setRetention")] Task<HistoryRetention> HistorySetRetention(HistoryRetention p);

    [RpcMethod("settings.get")] Task<SettingEntry[]> SettingsGet(SettingsGetParams p);
    [RpcMethod("settings.set")] Task SettingsSet(SettingEntry p);

    [RpcMethod("snippet.list")] Task<Snippet[]> SnippetList();
    [RpcMethod("snippet.save")] Task<Snippet> SnippetSave(SnippetSaveParams p);
    [RpcMethod("snippet.delete")] Task SnippetDelete(SnippetRef p);

    [RpcMethod("monitor.start")] Task<MonitorStartResult> MonitorStart(MonitorStartParams p);
    [RpcMethod("monitor.stop")] Task MonitorStop(MonitorRef p);
    [RpcMethod("monitor.setPanes")] Task MonitorSetPanes(MonitorSetPanesParams p);
    [RpcMethod("monitor.setInterval")] Task MonitorSetInterval(MonitorSetIntervalParams p);
    [RpcMethod("monitor.setTopQueries")] Task MonitorSetTopQueries(MonitorTopQueriesParams p);
    [RpcMethod("monitor.pause")] Task MonitorPause(MonitorRef p);
    [RpcMethod("monitor.resume")] Task MonitorResume(MonitorRef p);
    [RpcMethod("monitor.texts")] Task<MonitorTextsResult> MonitorTexts(MonitorTextsParams p);
    [RpcMethod("monitor.kill")] Task<KillResult> MonitorKill(MonitorKillParams p);
    [RpcMethod("monitor.killPreview")] Task<KillPreview> MonitorKillPreview(MonitorSessionParams p);
    [RpcMethod("monitor.diagnose")] Task<Diagnosis> MonitorDiagnose(MonitorSessionParams p);
    [RpcMethod("monitor.waitInfo")] Task<WaitInfo> MonitorWaitInfo(WaitInfoParams p);
    [RpcMethod("monitor.deadlocks")] Task<DeadlocksResult> MonitorDeadlocks(DeadlocksParams p);
    [RpcMethod("monitor.deadlockXml")] Task<MonitorFileResult> MonitorDeadlockXml(DeadlockXmlParams p);
    [RpcMethod("monitor.requestDetail")] Task<RequestDetail> MonitorRequestDetail(MonitorSessionParams p);
    [RpcMethod("monitor.history")] Task<HistoryResult> MonitorHistory(HistoryParams p);
    [RpcMethod("monitor.historySettings")] Task MonitorHistorySettings(HistorySettingsParams p);
    [RpcMethod("monitor.export")] Task<MonitorFileResult> MonitorExport(MonitorExportParams p);

    /// <summary>Milestone-1 synthetic result stream used by the bench harness (metrics 11–13).</summary>
    [RpcMethod("bench.synthetic")] Task<QueryExecuteResult> BenchSynthetic(SyntheticParams p);
}

/// <summary>Every sidecar → webview notification (§5).</summary>
public interface IQuerithmNotifications
{
    [RpcNotification("query.queued")] void QueryQueued(QueryQueuedEvent e);
    [RpcNotification("query.started")] void QueryStarted(QueryStartedEvent e);
    /// <summary>Single-message form, kept for the transition (M13): the sidecar now sends <c>query.messages</c>; the webview reads both.</summary>
    [RpcNotification("query.message")] void QueryMessage(ServerMessage e);
    [RpcNotification("query.messages")] void QueryMessages(QueryMessagesEvent e);
    [RpcNotification("query.resultSet")] void QueryResultSet(QueryResultSetEvent e);
    [RpcNotification("query.plan")] void QueryPlan(QueryPlanEvent e);
    [RpcNotification("query.done")] void QueryDone(QueryDoneEvent e);
    [RpcNotification("session.stateChanged")] void SessionStateChanged(SessionState e);
    [RpcNotification("meta.updated")] void MetaUpdated(MetaUpdatedEvent e);
    [RpcNotification("trace.event")] void TraceEvents(TraceEventsEvent e);
    [RpcNotification("debug.event")] void DebugEvent(DebugEvent e);
    [RpcNotification("auth.prompt")] void AuthPrompt(AuthPromptEvent e);
    [RpcNotification("import.progress")] void ImportProgress(ImportProgressEvent e);
    [RpcNotification("dataCompare.progress")] void DataCompareProgress(DataCompareProgressEvent e);
    [RpcNotification("monitor.snapshot")] void MonitorSnapshot(MonitorSnapshot e);
    [RpcNotification("monitor.stopped")] void MonitorStopped(MonitorStoppedEvent e);
    [RpcNotification("session.blocking")] void SessionBlocking(SessionBlockingEvent e);
    [RpcNotification("dataPlane.state")] void DataPlaneState(DataPlaneStateEvent e);
    /// <summary>A notice was added after start (history retention deleting entries for the first time): the webview takes it with system.notices.</summary>
    [RpcNotification("system.noticeAdded")] void SystemNoticeAdded(SystemNotice e);
}

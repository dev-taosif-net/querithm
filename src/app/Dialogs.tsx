import { useEffect, useMemo, useRef, useState } from "react";
import type { Dependency, HistoryEntry, HistoryRetention, SnapObject } from "../ipc/contract";
import { rpc, shell } from "../ipc/client";
import { useUi, type Dialog, toast } from "../store/ui";
import { useTabs } from "../store/tabs";
import { engineBadge, envLabel, sshAccount, useProfiles } from "../store/profiles";
import { useSnapshots } from "../store/snapshots";
import { Field, Modal } from "./Modal";
import { LoadingSkeleton } from "./LoadingStatus";
import { lazyDialog } from "./LazyDialog";
import { PresenceList } from "./Presence";
import { SecretInput } from "./SecretInput";
import { escapeForDisplay } from "../monitor/format";
import { ServerChip } from "./EnvTag";
import { txAgeLong } from "./txAge";
import { tabTitleText } from "./tabTitle";
import { ConnectDialog } from "../connections/ConnectDialog";
import { PasswordRetention } from "../connections/PasswordRetention";
import { ProfileEditor } from "../connections/ProfileEditor";
import { initMonaco, monaco } from "../editor/monaco";
import { newQueryTab, openDefinition, runQuery } from "./actions";
import { GridConflictsDialog, GridPendingDialog, GridPreviewDialog } from "../results/GridDialogs";
import { UpdateDialog } from "./UpdateDialog";
import { assertNever, dialectOf, DIALECTS, engineTraits } from "../engines";

// The wizard (and its grid preview) loads when first opened.
const ImportWizardDialog = lazyDialog(async () => { const m = await import("../import/ImportWizardDialog"); return { default: m.ImportWizardDialog }; });
const RenameDialog = lazyDialog(async () => { const m = await import("../refactor/RenameDialog"); return { default: m.RenameDialog }; });
const SnippetsPanel = lazyDialog(async () => { const m = await import("../snippets/SnippetsPanel"); return { default: m.SnippetsPanel }; });
const SnippetEditor = lazyDialog(async () => { const m = await import("../snippets/SnippetsPanel"); return { default: m.SnippetEditor }; });
const SettingsDialog = lazyDialog(async () => { const m = await import("../settings/SettingsDialog"); return { default: m.SettingsDialog }; });

export function Dialogs() {
  const dialogs = useUi((s) => s.dialogs);
  return (
    <PresenceList items={dialogs}>{(d) => <DialogView d={d} />}</PresenceList>
  );
}

function DialogView({ d }: { d: Dialog }) {
  const close = () => useUi.getState().closeDialog(d);
  switch (d.kind) {
    case "connect":
      return <ConnectDialog onClose={close} />;
    case "profile":
      return <ProfileEditor profileId={d.profileId} preset={d.preset} copyOf={d.copyOf} onClose={close} />;
    case "password":
      return <PasswordDialog d={d} />;
    case "hostKey":
      return <HostKeyDialog d={d} />;
    case "guard":
      return <GuardDialog d={d} />;
    case "txClose":
      return <TxCloseDialog d={d} />;
    case "busyClose":
      return <BusyCloseDialog d={d} />;
    case "txSwitch":
      return <TxSwitchDialog d={d} />;
    case "params":
      return <ParamsDialog d={d} />;
    case "confirm":
      return (
        <Modal
          title={d.title}
          danger={d.danger}
          onClose={() => d.resolve(false)}
          onEnter={() => d.resolve(true)}
          footer={
            <>
              <button className="q-btn" data-modal-cancel onClick={() => d.resolve(false)}>{d.cancel ?? "Cancel"}</button>
              <button className={`q-btn ${d.danger ? "q-btn-danger" : "q-btn-primary"}`} onClick={() => d.resolve(true)}>{d.ok ?? "OK"}</button>
            </>
          }
        >
          <p className="leading-relaxed text-fg">{d.body}</p>
        </Modal>
      );
    case "diff":
      return <DiffDialog d={d} close={close} />;
    case "execProc":
      return <ExecProcDialog d={d} close={close} />;
    case "cellViewer":
      return <CellViewer d={d} close={close} />;
    case "settings":
      return (
        <SettingsDialog loadingTitle="Settings" onLoadClose={close} close={close} page={d.page} focus={d.focus} />
      );
    case "update":
      return <UpdateDialog close={close} />;
    case "history":
      return <HistoryDialog close={close} />;
    case "dependencies":
      return <DependenciesDialog d={d} close={close} />;
    case "authPrompt":
      return (
        <Modal title="Microsoft Entra sign-in" onClose={close} footer={<button className="q-btn q-btn-primary" onClick={close}>Done</button>}>
          <p className="text-fg leading-relaxed whitespace-pre-wrap">{d.message}</p>
          {d.code && (
            <div className="mt-3 flex items-center gap-2">
              <span className="q-mono text-lg font-bold tracking-widest text-accent">{d.code}</span>
              <button className="q-btn" onClick={() => void navigator.clipboard.writeText(d.code!)}>Copy code</button>
            </div>
          )}
          {d.url && <div className="mt-2 q-mono text-ident select-text">{d.url}</div>}
        </Modal>
      );
    case "debugCall":
      return <DebugCallDialog d={d} />;
    case "gridPreview":
      return <GridPreviewDialog d={d} />;
    case "gridConflicts":
      return <GridConflictsDialog d={d} />;
    case "gridPending":
      return <GridPendingDialog d={d} />;
    case "import":
      return (
        <ImportWizardDialog loadingTitle="Import data" onLoadClose={close} d={d} />
      );
    case "rename":
      return (
        <RenameDialog loadingTitle="Rename" onLoadClose={close} d={d} />
      );
    case "snippets":
      return (
        <SnippetsPanel loadingTitle="Snippets" onLoadClose={close} close={close} />
      );
    case "snippetEdit":
      return (
        <SnippetEditor loadingTitle="Edit snippet" onLoadClose={close} snippet={d.snippet} close={close} />
      );
  }
}

function PasswordDialog({ d }: { d: Extract<Dialog, { kind: "password" }> }) {
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === d.profileId));
  const [value, setValue] = useState("");
  const [remember, setRemember] = useState(true);
  const [retention, setRetention] = useState({ passwordRetention: profile?.passwordRetention ?? "forever", passwordRetentionMinutes: profile?.passwordRetentionMinutes ?? 60 });
  const [saving, setSaving] = useState(false);
  const submit = async () => {
    if (saving) return;
    if (d.ssh && !remember) return d.resolve(value);
    if (!d.ssh && retention.passwordRetention === "duration" && (!Number.isInteger(retention.passwordRetentionMinutes) || retention.passwordRetentionMinutes < 1 || retention.passwordRetentionMinutes > 525600)) {
      toast("warn", "Choose a duration from 1 minute to 365 days.");
      return;
    }
    setSaving(true);
    try {
      if (!d.ssh && profile && (retention.passwordRetention !== (profile.passwordRetention ?? "forever") || retention.passwordRetentionMinutes !== (profile.passwordRetentionMinutes ?? 60))) {
        await useProfiles.getState().save({ ...profile, ...retention });
      }
      await shell.secretSet(d.ssh ? sshAccount(d.profileId) : d.profileId, value);
      d.resolve(undefined as unknown as string);
    } catch (e) {
      toast("warn", `Could not remember password: ${(e as Error).message}`);
    } finally { setSaving(false); }
  };
  return (
    <Modal
      title={d.ssh ? `SSH tunnel — ${profile?.name ?? ""}` : `Sign in — ${profile?.name ?? ""}`}
      subtitle={escapeForDisplay(d.ssh ? `${profile?.ssh?.username ?? ""}@${profile?.ssh?.host ?? ""}` : `${profile?.username ?? ""}@${profile?.host ?? ""}`)}
      onClose={() => d.resolve(null)}
      onEnter={() => void submit()}
      width={420}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve(null)}>Cancel</button>
          <button className="q-btn q-btn-primary" disabled={saving} onClick={() => void submit()}>Connect</button>
        </>
      }
    >
      {d.error && <div className="mb-3 p-2 rounded bg-err/10 border border-err/40 text-err text-[11px]">{d.error}</div>}
      <Field label={d.ssh ? (profile?.ssh?.auth === "privateKey" ? "Key passphrase" : "SSH password") : profile?.auth === "entraToken" ? "Access token" : "Password"}>
        <SecretInput className="q-input w-full" type="password" autoFocus value={value} onValue={setValue} />
      </Field>
      {d.ssh ? <label className="flex items-center gap-2 mt-3 text-fg">
        <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} /> Save in the OS keyring
      </label> : profile && <div className="mt-3"><PasswordRetention profile={{ ...profile, ...retention }} onChange={(patch) => setRetention((r) => ({ ...r, ...patch }) as typeof r)} /></div>}
    </Modal>
  );
}

/**
 * An SSH bastion's host key before it is trusted: never accepted automatically. A changed key is shown as a
 * possible interception, with both fingerprints.
 */
function HostKeyDialog({ d }: { d: Extract<Dialog, { kind: "hostKey" }> }) {
  const k = d.hostKey;
  const changed = !!k.previousFingerprint;
  return (
    <Modal
      danger={changed}
      title={changed ? "⚠ SSH HOST KEY CHANGED" : "Trust this SSH server?"}
      subtitle={`${escapeForDisplay(k.host)}:${k.port}`}
      onClose={() => d.resolve(false)}
      width={560}
      footer={
        <>
          <button className="q-btn" data-modal-cancel onClick={() => d.resolve(false)} autoFocus>Cancel</button>
          <button className={`q-btn ${changed ? "q-btn-danger" : "q-btn-primary"}`} onClick={() => d.resolve(true)}>{changed ? "Trust the new key" : "Trust and connect"}</button>
        </>
      }
    >
      <div className="space-y-3 text-fg leading-relaxed">
        {changed ? (
          <p>The server presents a <b>different key</b> than the one you trusted. It may have been reinstalled — or someone may be intercepting the connection. Trust the new key only after confirming it with the server's administrator.</p>
        ) : (
          <p>Querithm has not connected to this SSH server before. Compare the fingerprint with the one the server's administrator gives you (<span className="q-mono">ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub</span> on the server).</p>
        )}
        <div className="p-3 bg-bg border border-line rounded-lg q-mono text-[11px] space-y-1 select-text">
          <div><span className="text-muted">Key type:</span> {k.algorithm}</div>
          <div><span className="text-muted">{changed ? "New fingerprint:" : "Fingerprint:"}</span> <b className={changed ? "text-err" : "text-fg"}>SHA256:{k.fingerprint}</b></div>
          {changed && <div><span className="text-muted">Trusted before:</span> SHA256:{k.previousFingerprint}</div>}
        </div>
      </div>
    </Modal>
  );
}

/** Mockup screen 4, modal B: production guards enforced by the sidecar (§6, §13 rule 7). */
function GuardDialog({ d }: { d: Extract<Dialog, { kind: "guard" }> }) {
  const b = d.block;
  const destructive = b.reason === "destructive";
  const write = b.reason === "prodWrite";
  return (
    <Modal
      danger
      title={destructive ? "⚠ DESTRUCTIVE STATEMENT DETECTED ON PRODUCTION" : write ? "⚠ DATA CHANGE ON PRODUCTION" : "⚠ FIRST EXECUTION ON PRODUCTION"}
      onClose={() => d.resolve(false)}
      width={560}
      footer={
        <>
          <button className="q-btn" data-modal-cancel onClick={() => d.resolve(false)} autoFocus>Cancel execution (Esc)</button>
          <button className="q-btn q-btn-danger" onClick={() => d.resolve(true)}>Confirm & execute on PROD</button>
        </>
      }
    >
      <div className="space-y-3">
        <p className="text-fg leading-relaxed">
          {destructive ? (
            <>
              <b>AST analysis</b> blocked this execution because it modifies, drops or alters data on a <b>PRODUCTION</b> connection:
            </>
          ) : write ? (
            <>Querithm is about to write data on a <b>PRODUCTION</b> connection. Every such write is confirmed, not only the first one.</>
          ) : (
            <>This is the first execution on this <b>PRODUCTION</b> connection since Querithm started. Check the server and database.</>
          )}
        </p>
        <div className="p-3 bg-bg border border-err/40 rounded-lg space-y-1.5 q-mono text-[11px]">
          <div><span className="text-muted">Connection:</span> <b className="text-err">{escapeForDisplay(b.profileName ?? "")}</b> <span className="text-muted">({escapeForDisplay(b.server ?? "")})</span></div>
          <div><span className="text-muted">Database:</span> <b className="text-fg">{b.database != null ? escapeForDisplay(b.database) : "(default)"}</b></div>
          {b.findings.map((f, i) => (
            <div key={i} className="pt-1 border-t border-line/60">
              <div><span className="text-muted">Rule:</span> <b className="text-warn">{f.description}</b> <span className="text-muted">(line {f.line})</span></div>
              {f.statement && <div className="pt-1 text-err break-all">{f.statement}</div>}
            </div>
          ))}
        </div>
      </div>
    </Modal>
  );
}

/**
 * Closing a tab that holds an open transaction (§6 rule 5, D21): commit or roll back, or cancel and keep the tab.
 * Cancel is the default (focused, so Enter keeps everything as it is); the server and the transaction's age are shown
 * so the choice is made knowing where the work is.
 */
function TxCloseDialog({ d }: { d: Extract<Dialog, { kind: "txClose" }> }) {
  const tab = useTabs((s) => s.tabs[d.tabId]);
  const age = tab?.txStartedAt ? txAgeLong(Date.now() - tab.txStartedAt) : undefined;
  // An unknown state is asked about like an open one (M9): the connection may hold uncommitted work.
  const maybe = tab?.txState === "unknown";
  const what = maybe ? "that may have an open transaction" : "with an open transaction";
  const close = d.quit ? "quit" : "close";
  return (
    <Modal
      title={d.quit ? `Quit Querithm? A tab ${maybe ? "may have" : "has"} an open transaction` : `Close a tab ${what}?`}
      subtitle={tab ? tabTitleText(tab.title) : undefined}
      onClose={() => d.resolve("cancel")}
      footer={
        <>
          <button className="q-btn q-btn-primary" autoFocus onClick={() => d.resolve("cancel")}>Cancel</button>
          <button className="q-btn text-err" onClick={() => d.resolve("rollback")}>Roll back and {close}</button>
          <button className="q-btn" onClick={() => d.resolve("commit")}>Commit and {close}</button>
        </>
      }
    >
      <div className="space-y-2.5">
        <ServerChip profileId={tab?.profileId} database={tab?.database} />
        <p className="text-fg leading-relaxed">
          {maybe
            ? "Querithm could not read whether this tab's transaction is still open, so it treats it as open. "
            : age ? `The transaction has been open for ${age}${tab?.spid ? ` (SPID ${tab.spid})` : ""}. ` : ""}
          {d.quit
            ? "Quitting never rolls back silently: commit or roll back the uncommitted work, or cancel to keep Querithm open."
            : "Closing the tab never rolls back silently: commit or roll back the uncommitted work, or cancel to keep the tab."}
        </p>
      </div>
    </Modal>
  );
}

/** Closing a tab that is still running a query (D20): Keep tab is the default; the other choice cancels the query. */
function BusyCloseDialog({ d }: { d: Extract<Dialog, { kind: "busyClose" }> }) {
  const tab = useTabs((s) => s.tabs[d.tabId]);
  return (
    <Modal
      title="Cancel the running query?"
      subtitle={tab ? tabTitleText(tab.title) : undefined}
      onClose={() => d.resolve(false)}
      footer={
        <>
          <button className="q-btn q-btn-primary" autoFocus onClick={() => d.resolve(false)}>{d.quit ? "Keep Querithm open" : "Keep tab"}</button>
          <button className="q-btn q-btn-danger" onClick={() => d.resolve(true)}>{d.quit ? "Cancel query and quit" : "Cancel query and close"}</button>
        </>
      }
    >
      <div className="space-y-2.5">
        <ServerChip profileId={tab?.profileId} database={tab?.database} />
        <p className="text-fg leading-relaxed">
          {d.quit
            ? "This tab is still running a query. Quitting cancels it on the server first; if it has not stopped within 10 s, Querithm quits anyway and the server rolls back what it had not committed."
            : "This tab is still running a query. Closing it cancels the query on the server first."}
        </p>
      </div>
    </Modal>
  );
}

function TxSwitchDialog({ d }: { d: Extract<Dialog, { kind: "txSwitch" }> }) {
  const maybe = useTabs((s) => s.tabs[d.tabId]?.txState === "unknown");
  return (
    <Modal
      title={maybe ? "Leave a session that may have an open transaction?" : "Leave a session with an open transaction?"}
      danger
      onClose={() => d.resolve(false)}
      footer={
        <>
          <button className="q-btn" data-modal-cancel onClick={() => d.resolve(false)}>Stay</button>
          <button className="q-btn q-btn-danger" onClick={() => d.resolve(true)}>Switch and discard the transaction</button>
        </>
      }
    >
      <p className="text-fg">Switching this tab's connection closes its session; the server rolls the open transaction back.</p>
    </Modal>
  );
}

function ParamsDialog({ d }: { d: Extract<Dialog, { kind: "params" }> }) {
  const [values, setValues] = useState(() => d.names.map((name) => ({ name, value: "", isNull: false, type: "" })));
  const submit = () => d.resolve(values.map((v) => ({ ...v, type: v.type || undefined })));
  return (
    <Modal
      title="Query parameters"
      subtitle="Values are sent as driver parameters, never substituted into the text (§7)."
      onClose={() => d.resolve(null)}
      onEnter={submit}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve(null)}>Cancel</button>
          <button className="q-btn q-btn-primary" onClick={submit}>Execute</button>
        </>
      }
    >
      <div className="space-y-2">
        {values.map((v, i) => (
          <div key={v.name} className="grid grid-cols-[140px_1fr_120px_60px] gap-2 items-center">
            <span className="q-mono text-ident">{v.name}</span>
            <input className="q-input" autoFocus={i === 0} disabled={v.isNull} value={v.value} onChange={(e) => setValues((s) => s.map((x, k) => (k === i ? { ...x, value: e.target.value } : x)))} />
            <input className="q-input" placeholder="type (optional)" value={v.type} onChange={(e) => setValues((s) => s.map((x, k) => (k === i ? { ...x, type: e.target.value } : x)))} />
            <label className="flex items-center gap-1 text-muted">
              <input type="checkbox" checked={v.isNull} onChange={(e) => setValues((s) => s.map((x, k) => (k === i ? { ...x, isNull: e.target.checked } : x)))} /> NULL
            </label>
          </div>
        ))}
      </div>
    </Modal>
  );
}

function DiffDialog({ d, close }: { d: Extract<Dialog, { kind: "diff" }>; close: () => void }) {
  const host = useRef<HTMLDivElement>(null);
  const [ignoreWs, setIgnoreWs] = useState(true);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  useEffect(() => {
    initMonaco();
    const ed = monaco.editor.createDiffEditor(host.current!, {
      readOnly: true, automaticLayout: true, ignoreTrimWhitespace: true, renderSideBySide: true, minimap: { enabled: false }, fontSize: 12,
      fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", monospace', originalEditable: false, scrollBeyondLastLine: false,
      // A read-only viewer: no current-line fill under the diff tints (their contrast is checked on --q-bg).
      renderLineHighlight: "none",
    });
    const original = monaco.editor.createModel(d.left, d.language);
    const modified = monaco.editor.createModel(d.right, d.language);
    ed.setModel({ original, modified });
    editor.current = ed;
    return () => {
      ed.dispose();
      original.dispose();
      modified.dispose();
    };
  }, [d]);
  useEffect(() => editor.current?.updateOptions({ ignoreTrimWhitespace: ignoreWs }), [ignoreWs]);
  const done = (ok: boolean) => {
    if (d.resolve) d.resolve(ok);
    else close();
  };
  return (
    <Modal
      title={d.title}
      onClose={() => done(false)}
      width={1100}
      danger={!!d.okLabel}
      footer={
        <>
          <label className="flex items-center gap-1.5 mr-auto text-muted">
            <input type="checkbox" checked={ignoreWs} onChange={(e) => setIgnoreWs(e.target.checked)} /> Ignore whitespace
          </label>
          <button className="q-btn" data-modal-cancel onClick={() => done(false)}>{d.okLabel ? "Cancel" : "Close"}</button>
          {d.okLabel && <button className="q-btn q-btn-danger" onClick={() => done(true)}>{d.okLabel}</button>}
        </>
      }
    >
      <div className="grid grid-cols-2 text-[11px] font-bold mb-1">
        <span className="text-muted">{d.leftTitle}</span>
        <span className="text-accent">{d.rightTitle}</span>
      </div>
      <div ref={host} className="h-[60vh] border border-line rounded" />
    </Modal>
  );
}

/** Execute a procedure (§10): a parameter form from the cached param rows generates DECLARE/EXEC/SELECT. */
function ExecProcDialog({ d, close }: { d: Extract<Dialog, { kind: "execProc" }>; close: () => void }) {
  const entry = useSnapshots((s) => s.get(d.profileId, d.database));
  const obj: SnapObject | undefined = entry?.index?.byId.get(d.objectId);
  const engine = useProfiles((s) => s.byId(d.profileId)?.engine);
  const params = obj?.params ?? [];
  const [values, setValues] = useState<Record<string, { v: string; isNull: boolean }>>(() => Object.fromEntries(params.map((p) => [p.name, { v: "", isNull: p.hasDefault }])));
  if (!obj) return <Modal title="Execute" onClose={close}><div className="text-muted">The object is not in the metadata cache yet.</div></Modal>;
  const traits = engineTraits(engine);
  if (!traits.procedures) return <Modal title="Execute" onClose={close}><div className="text-muted">{traits.shortLabel} has no stored procedures.</div></Modal>;
  const lit = (type: string, v: string) => (/char|text|date|time|uniqueidentifier|xml|uuid|json/i.test(type) ? `N'${v.replace(/'/g, "''")}'` : v || "NULL");
  // Null where the engine has no procedures to call (SQLite; the dialog says so above).
  const build = (): string | null => {
    const dialect = dialectOf(engine);
    switch (dialect) {
      case "pg": {
        const args = params.filter((p) => !p.output || p.name).map((p) => {
          const x = values[p.name];
          const val = x?.isNull ? "NULL" : lit(p.type, x?.v ?? "").replace(/^N'/, "'");
          return p.name ? `${p.name} => ${val}` : val;
        });
        return obj.kind === "proc" ? `CALL ${obj.schema}.${obj.name}(${args.join(", ")});` : `SELECT * FROM ${obj.schema}.${obj.name}(${args.join(", ")});`;
      }
      case "tsql": {
        const q = DIALECTS.tsql.quote;
        const outs = params.filter((p) => p.output);
        const lines = outs.map((p) => `DECLARE ${p.name} ${p.type};`);
        if (obj.kind === "func") {
          lines.push(`SELECT ${q(obj.schema)}.${q(obj.name)}(${params.map((p) => (values[p.name]?.isNull ? "NULL" : lit(p.type, values[p.name]?.v ?? ""))).join(", ")}) AS result;`);
        } else if (obj.kind === "tvf") {
          lines.push(`SELECT * FROM ${q(obj.schema)}.${q(obj.name)}(${params.map((p) => (values[p.name]?.isNull ? "NULL" : lit(p.type, values[p.name]?.v ?? ""))).join(", ")});`);
        } else {
          const args = params
            .filter((p) => p.output || !(values[p.name]?.isNull && p.hasDefault))
            .map((p) => (p.output ? `${p.name} = ${p.name} OUTPUT` : `${p.name} = ${values[p.name]?.isNull ? "NULL" : lit(p.type, values[p.name]?.v ?? "")}`));
          lines.push(`EXEC ${q(obj.schema)}.${q(obj.name)}${args.length ? "\n    " + args.join(",\n    ") : ""};`);
          if (outs.length) lines.push(`SELECT ${outs.map((p) => `${p.name} AS ${q(p.name.slice(1))}`).join(", ")};`);
        }
        return lines.join("\n");
      }
      case "mySql": {
        // Positional arguments, one per line (MySQL has no named arguments); OUT parameters go through user variables.
        const q = DIALECTS.mySql.quote;
        const target = `${q(obj.schema || d.database)}.${q(obj.name)}`;
        const quoted = (type: string) => /char|text|date|time|json|enum|set|binary|blob/i.test(type);
        const arg = (p: (typeof params)[number]) => {
          if (p.output) return `@${p.name}`;
          const x = values[p.name];
          if (x?.isNull) return "NULL";
          const v = x?.v ?? "";
          return quoted(p.type) ? DIALECTS.mySql.stringLiteral(v) : v || "NULL";
        };
        const list = params.length ? `(\n    ${params.map(arg).join(",\n    ")}\n)` : "()";
        if (obj.kind !== "proc") return `SELECT ${target}${list} AS result;`;
        const outs = params.filter((p) => p.output);
        const lines = [`CALL ${target}${list};`];
        if (outs.length) lines.push(`SELECT ${outs.map((p) => `@${p.name} AS ${q(p.name)}`).join(", ")};`);
        return lines.join("\n");
      }
      case "sqlite":
      case "duckDb":
        return null;
      default:
        return assertNever(dialect);
    }
  };
  const go = (run: boolean) => {
    const sql = build();
    if (sql === null) return;
    const id = newQueryTab({ profileId: d.profileId, database: d.database, text: sql + "\n", title: `EXEC ${obj.name}.sql` });
    close();
    if (run) setTimeout(() => void runQuery(id), 30);
  };
  return (
    <Modal
      title={`Execute ${obj.schema}.${obj.name}`}
      subtitle={<ServerChip profileId={d.profileId} database={d.database} />}
      onClose={close}
      // An alert: without parameters it opens on Cancel, so Enter alone never runs the procedure (M19); Enter in a
      // parameter field still runs it.
      alert
      onEnter={() => go(true)}
      width={600}
      footer={
        <>
          <button className="q-btn" data-modal-cancel onClick={close}>Cancel</button>
          <button className="q-btn" onClick={() => go(false)}>Script to new tab</button>
          <button className="q-btn q-btn-primary" onClick={() => go(true)}>Execute</button>
        </>
      }
    >
      {!params.length && <div className="text-muted">No parameters.</div>}
      <div className="space-y-2">
        {params.map((p, i) => (
          <div key={p.name || i} className="grid grid-cols-[170px_1fr_70px] gap-2 items-center">
            <span className="q-mono text-ident truncate" title={`${p.name} ${p.type}`}>
              {p.name || `$${p.ordinal}`} <span className="text-muted">{p.type}{p.output ? " OUT" : ""}</span>
            </span>
            <input className="q-input" autoFocus={i === 0} disabled={p.output || values[p.name]?.isNull} value={values[p.name]?.v ?? ""} onChange={(e) => setValues((s) => ({ ...s, [p.name]: { v: e.target.value, isNull: false } }))} />
            {!p.output && (
              <label className="flex items-center gap-1 text-muted" title={p.hasDefault ? "Use the parameter's default" : "Pass NULL"}>
                <input type="checkbox" checked={values[p.name]?.isNull ?? false} onChange={(e) => setValues((s) => ({ ...s, [p.name]: { v: s[p.name]?.v ?? "", isNull: e.target.checked } }))} />
                {p.hasDefault ? "default" : "NULL"}
              </label>
            )}
          </div>
        ))}
      </div>
    </Modal>
  );
}

function CellViewer({ d, close }: { d: Extract<Dialog, { kind: "cellViewer" }>; close: () => void }) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<ReturnType<typeof monaco.editor.create> | null>(null);
  useEffect(() => {
    initMonaco();
    let text = d.value;
    if (d.language === "json") {
      try {
        text = JSON.stringify(JSON.parse(d.value), null, 2);
      } catch {
        /* keep raw */
      }
    }
    const ed = monaco.editor.create(host.current!, {
      value: d.onSave ? d.value : text, language: d.language ?? "plaintext", readOnly: !d.onSave, automaticLayout: true, minimap: { enabled: false }, wordWrap: "on", fontSize: 12,
      fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", monospace', scrollBeyondLastLine: false,
    });
    editor.current = ed;
    return () => {
      editor.current = null;
      ed.getModel()?.dispose();
      ed.dispose();
    };
  }, [d]);
  return (
    <Modal
      title={d.title}
      subtitle={`${d.value.length.toLocaleString()} characters`}
      onClose={close}
      width={960}
      footer={
        <>
          <button className="q-btn" onClick={() => void navigator.clipboard.writeText(d.value).then(() => toast("success", "Copied."))}>Copy</button>
          <button className="q-btn" onClick={() => { newQueryTab({ text: d.value, title: "value.txt" }); close(); }}>Open in tab</button>
          {d.onSave ? (
            <>
              <button className="q-btn" onClick={close}>Cancel</button>
              <button className="q-btn q-btn-primary" onClick={() => { d.onSave!(editor.current?.getValue() ?? d.value); close(); }}>Stage value</button>
            </>
          ) : (
            <button className="q-btn q-btn-primary" onClick={close}>Close</button>
          )}
        </>
      }
    >
      <div ref={host} className="h-[60vh] border border-line rounded" />
    </Modal>
  );
}

function HistoryDialog({ close }: { close: () => void }) {
  const [text, setText] = useState("");
  const [rows, setRows] = useState<HistoryEntry[]>([]);
  const profiles = useProfiles((s) => s.profiles);
  // L17: what the store keeps (validated in the sidecar); the list holds previews, an entry's whole text comes on open.
  const [retention, setRetention] = useState<HistoryRetention | null>(null);
  const [keep, setKeep] = useState({ days: "", maxEntries: "" });
  useEffect(() => {
    rpc("history.retention")
      .then((r) => {
        if (typeof r?.days !== "number") return;
        setRetention(r);
        setKeep({ days: String(r.days), maxEntries: String(r.maxEntries) });
      })
      .catch(() => {});
  }, []);
  const saveKeep = async () => {
    try {
      const r = await rpc("history.setRetention", { days: Number(keep.days), maxEntries: Number(keep.maxEntries) });
      setRetention(r);
      toast("info", `History keeps ${r.days.toLocaleString()} days, at most ${r.maxEntries.toLocaleString()} entries.`);
    } catch (e) {
      toast("error", (e as Error).message);
    }
  };
  const openEntry = async (h: HistoryEntry) => {
    let sql = h.sql;
    if (h.sqlTruncated) {
      try {
        sql = (await rpc("history.get", { id: h.id })).sql;
      } catch (e) {
        toast("error", (e as Error).message);
        return;
      }
    }
    newQueryTab({ profileId: h.profileId ?? undefined, database: h.database ?? undefined, text: sql, title: "history.sql" });
    close();
  };
  const keepChanged = retention != null && (keep.days !== String(retention.days) || keep.maxEntries !== String(retention.maxEntries));
  useEffect(() => {
    let stale = false;
    const t = setTimeout(() => {
      (text.trim() ? rpc("history.search", { text, profileId: null, limit: 300 }) : rpc("history.list", { limit: 300, profileId: null, database: null, beforeId: null }))
        .then((r) => !stale && setRows(r))
        .catch(() => !stale && setRows([]));
    }, 150);
    return () => {
      stale = true;
      clearTimeout(t);
    };
  }, [text]);
  return (
    <Modal
      title="Query history"
      subtitle="Full-text search over everything executed (FTS5)"
      onClose={close}
      width={900}
      footer={
        <>
          {retention && (
            <div className="flex items-center gap-1 text-[11px] text-muted">
              <label className="flex items-center gap-1">
                Keep
                <input className="q-input w-[64px] h-[22px]" type="number" min={1} max={3650} aria-label="Days of history to keep" value={keep.days}
                  onChange={(e) => setKeep({ ...keep, days: e.target.value })} />
                days,
              </label>
              <label className="flex items-center gap-1">
                at most
                <input className="q-input w-[90px] h-[22px]" type="number" min={1000} max={1000000} step={1000} aria-label="History entries to keep" value={keep.maxEntries}
                  onChange={(e) => setKeep({ ...keep, maxEntries: e.target.value })} />
                entries
              </label>
              <button className="q-btn h-[22px]" disabled={!keepChanged} onClick={() => void saveKeep()}>Save</button>
            </div>
          )}
          <div className="flex-1" />
          <button className="q-btn" onClick={close}>Close</button>
        </>
      }
    >
      <input className="q-input w-full mb-2" autoFocus placeholder="Search history…" value={text} onChange={(e) => setText(e.target.value)} />
      <div className="max-h-[55vh] overflow-y-auto space-y-1">
        {rows.map((h) => {
          const p = profiles.find((x) => x.id === h.profileId);
          return (
            <button
              key={h.id}
              className="w-full text-left p-2 rounded border border-line hover:bg-raised"
              onClick={() => void openEntry(h)}
            >
              <div className="flex justify-between text-[10.5px] text-muted mb-1">
                <span>
                  {p ? `[${engineBadge(p)}] ${envLabel[p.environment]} - ${p.name}` : "(deleted profile)"} · {h.database} · {new Date(h.executedAt).toLocaleString()}
                </span>
                <span className={h.status === "failed" ? "text-err" : h.status === "cancelled" ? "text-warn" : "text-ok"}>
                  {h.status} · {h.elapsedMs} ms{h.rowsAffected != null ? ` · ${h.rowsAffected} rows` : ""}
                </span>
              </div>
              <div className="q-mono text-[11.5px] text-fg whitespace-pre-wrap line-clamp-3">{h.sql}</div>
              {h.sqlTruncated && <div className="text-[10.5px] text-muted mt-0.5">… {h.sqlLength.toLocaleString()} characters in all</div>}
            </button>
          );
        })}
        {!rows.length && <div className="text-muted p-4 text-center">Nothing found.</div>}
      </div>
    </Modal>
  );
}

function DependenciesDialog({ d, close }: { d: Extract<Dialog, { kind: "dependencies" }>; close: () => void }) {
  const [deps, setDeps] = useState<Dependency[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true;
    setError(null);
    void (async () => {
      try {
        const { useConnections } = await import("../store/connections");
        const sessionId = await useConnections.getState().sessionFor(d.profileId);
        const result = await rpc("meta.dependencies", { sessionId, object: { schema: d.schema, name: d.name, objectId: d.objectId, kind: d.kind2 as SnapObject["kind"], database: d.database, profileId: d.profileId } });
        if (active) setDeps(result);
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : String(e));
      }
    })();
    return () => { active = false; };
  }, [d, attempt]);
  const groups = useMemo(() => ({ uses: deps?.filter((x) => x.direction === "uses") ?? [], usedBy: deps?.filter((x) => x.direction === "usedBy") ?? [] }), [deps]);
  const open = (x: Dependency) => {
    if (x.object.kind === "table") return;
    void openDefinition(d.profileId, x.object.database ?? d.database, x.object, false);
    close();
  };
  return (
    <Modal title={`Dependencies — ${d.schema}.${d.name}`} onClose={close} width={720} footer={<button className="q-btn" onClick={close}>Close</button>}>
      {error && <div role="alert" className="text-err flex items-center gap-2">{error}<button className="q-btn" onClick={() => setAttempt((n) => n + 1)}>Retry</button></div>}
      {!deps && !error && <LoadingSkeleton label="Loading dependencies…" />}
      {deps && (
        <div className="grid grid-cols-2 gap-4">
          {(["uses", "usedBy"] as const).map((k) => (
            <div key={k}>
              <div className="text-[10px] font-bold uppercase text-muted mb-1">{k === "uses" ? "Uses" : "Used by"} ({groups[k].length})</div>
              <div className="space-y-0.5">
                {groups[k].map((x, i) => (
                  <button key={i} className="w-full text-left px-2 py-1 rounded hover:bg-raised q-mono text-[11.5px]" onClick={() => open(x)}>
                    <span className="text-muted">{x.object.kind}</span> {x.object.database ? `${x.object.database}.` : ""}{x.object.schema}.{x.object.name}
                  </button>
                ))}
                {!groups[k].length && <div className="text-muted px-2">None</div>}
              </div>
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}

function DebugCallDialog({ d }: { d: Extract<Dialog, { kind: "debugCall" }> }) {
  const [sql, setSql] = useState(d.sql);
  return (
    <Modal
      title={d.title}
      subtitle="pldebugger runs this call on a separate target connection and stops at the first line of the function."
      onClose={() => d.resolve(null)}
      width={620}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve(null)}>Cancel</button>
          <button className="q-btn q-btn-primary" onClick={() => d.resolve(sql)}>Start debugging</button>
        </>
      }
    >
      <textarea className="q-input w-full h-32 q-mono" value={sql} onChange={(e) => setSql(e.target.value)} />
    </Modal>
  );
}

// Connection profile editor (§6, §13): engine, auth, TLS, environment, group. The password goes to the
// shell under the chosen retention policy; it is never part of the profile. A pasted connection string is parsed by the
// sidecar's drivers into the same fields and is itself never stored.
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import type { AuthKind, Engine, EngineInfo, EnvironmentKind, LocationKind, Profile, SshAuthKind, SshSettings, TlsMode } from "../ipc/contract";
import { rpc, shell } from "../ipc/client";
import { Field, Modal } from "../app/Modal";
import { readableOnTint, useThemeKey } from "../app/tabStyle";
import { envLabel, sshAccount, useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { ask, toast } from "../store/ui";
import { isTauri } from "../ipc/transport";
import { confirmDeleteProfile } from "./deleteProfile";
import { PasswordRetention } from "./PasswordRetention";
import { duplicateProfile, isLocal, locationOf, needsSecretFor } from "./auth";
import { pickDatabaseFile, requestWriteAccess } from "./fileGrants";
import { loadEngineInfo, offeredEngines } from "./engineInfo";
import { EngineLogo } from "./EngineLogo";
import mssqlLogo from "./logos/mssql.svg";
import pgLogo from "./logos/postgresql.svg";
import { DEFAULT_ENGINE, ENGINE_LIST, ENGINES as ENGINE_TRAITS, engineTraits, knownEngine } from "../engines";

/** Profile fields only some engines' drivers take. */
interface EngineFields {
  connectionStringExample: string;
  /** Host also takes `host\instance` and `tcp:host`. */
  instanceNames: boolean;
  /** "Trust server certificate"; otherwise a CA certificate file for the verify modes. */
  trustServerCertificate: boolean;
  multiSubnetFailover: boolean;
  /** Read-only application intent. */
  applicationIntent: boolean;
  /** TLS modes that encrypt without checking the server certificate: warned like "Trust server certificate". */
  certNotChecked: TlsMode[];
}

const FIELDS: Record<Engine, EngineFields> = {
  mssql: {
    connectionStringExample: "Server=localhost,1433;Database=master;User ID=sa;Password=…;Encrypt=True",
    instanceNames: true, trustServerCertificate: true, multiSubnetFailover: true, applicationIntent: true, certNotChecked: [],
  },
  pg: {
    connectionStringExample: "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=…",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
  },
  // A file is only ever picked through the shell's dialog (§13 rule 6): no connection string names one.
  sqlite: {
    connectionStringExample: "",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
  },
  mySql: {
    connectionStringExample: "Server=localhost;Port=3306;User ID=root;Password=…;Database=…",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: ["require"],
  },
  duckDb: {
    connectionStringExample: "",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
  },
};


/** Where an engine's databases live: the sidecar's answer when it gave one, else the engine table's. */
function locationsOf(info: readonly EngineInfo[] | null, engine: Engine): LocationKind[] {
  const own = info?.find((i) => i.engine === engine)?.locations;
  return own?.length ? own : ENGINE_TRAITS[engine].locations;
}

const LOCATION_LABEL: Record<LocationKind, string> = { server: "Server", file: "Database file", memory: "In-memory database" };

const AUTH: Record<Engine, { id: AuthKind; label: string }[]> = {
  mssql: [
    { id: "sql", label: "SQL Server login" },
    { id: "windows", label: "Windows integrated" },
    { id: "kerberos", label: "Kerberos (ticket)" },
    { id: "entraInteractive", label: "Microsoft Entra — interactive (browser)" },
    { id: "entraDeviceCode", label: "Microsoft Entra — device code" },
    { id: "entraToken", label: "Microsoft Entra — access token" },
  ],
  pg: [
    { id: "password", label: "Password / SCRAM" },
    { id: "kerberos", label: "Kerberos (GSSAPI)" },
    { id: "entraToken", label: "Entra access token (Azure PostgreSQL)" },
  ],
  sqlite: [{ id: "none", label: "None (a database on this computer)" }],
  mySql: [{ id: "password", label: "Password" }],
  duckDb: [{ id: "none", label: "None (a database on this computer)" }],
};

const TLS: Record<Engine, { id: TlsMode; label: string }[]> = {
  mssql: [
    { id: "default", label: "Default — Strict (TDS 8) if supported, else Mandatory" },
    { id: "strict", label: "Strict (SQL Server 2022+)" },
    { id: "mandatory", label: "Mandatory" },
    { id: "optional", label: "Optional (not encrypted) — not recommended" },
  ],
  pg: [
    { id: "default", label: "Require (default)" },
    { id: "verifyFull", label: "Verify full (CA + host name)" },
    { id: "verifyCa", label: "Verify CA" },
    { id: "prefer", label: "Prefer (falls back to plain text)" },
    { id: "disable", label: "Disable — not recommended" },
  ],
  sqlite: [],
  duckDb: [],
  mySql: [
    { id: "default", label: "Default — Verify full (certificate and host name)" },
    { id: "verifyCa", label: "Verify CA" },
    { id: "require", label: "Require (encrypted, certificate not checked) — not recommended" },
    { id: "prefer", label: "Prefer (falls back to plain text)" },
    { id: "disable", label: "Disable — not recommended" },
  ],
};

const empty = (engine: Engine): Profile => {
  const server = ENGINE_TRAITS[engine].locations.includes("server");
  return {
    id: "", name: "", engine, host: server ? "localhost" : null, port: null, auth: ENGINE_TRAITS[engine].defaultAuth, username: ENGINE_TRAITS[engine].defaultUser,
    databaseDefault: ENGINE_TRAITS[engine].defaultDatabase, environment: "dev", color: null, groupName: null, tlsMode: "default", trustCert: false, caPath: null,
    timeoutS: 15, commandTimeoutS: 0, options: null, passwordRetention: "once", createdAt: 0, updatedAt: 0,
    ...(server ? {} : { location: ENGINE_TRAITS[engine].locations[0], file: null }),
  };
};


/** The fields the editor checks itself and shows a message at (L25); the sidecar names the same ones when it refuses. */
export type FieldKey = "name" | "host" | "port" | "file";
const FIELD_ORDER: FieldKey[] = ["name", "host", "port", "file"];
/** What Test checks (a name is needed only to save). */
const CONNECT_FIELDS: FieldKey[] = ["host", "port", "file"];
/** As the sidecar's ProfileRules.MaxHostLength. */
const MAX_HOST = 1024;

/**
 * What is wrong with each field, as the sidecar's ProfileRules would refuse it: a name, a host of at most 1024
 * characters without control characters, and a port of 1–65535 for a server, a picked file for a database file. An
 * empty host or port is the engine's default (W9: the sidecar takes no host as localhost for SQL Server, PostgreSQL
 * and MySQL / MariaDB), so neither is required; `withDefaultHost` sends an empty host as none.
 */
export function profileFieldErrors(p: Profile): Partial<Record<FieldKey, string>> {
  const e: Partial<Record<FieldKey, string>> = {};
  if (!p.name.trim()) e.name = "Give the profile a name.";
  if (isLocal(p)) {
    if (locationOf(p) === "file" && !p.file) e.file = "Pick the database file first.";
    return e;
  }
  const host = p.host ?? "";
  // eslint-disable-next-line no-control-regex
  if (host.length > MAX_HOST || /[\u0000-\u001f\u007f-\u009f]/.test(host)) e.host = `The host must be at most ${MAX_HOST} characters, without line breaks or control characters.`;
  if (p.port != null && !(Number.isInteger(p.port) && p.port >= 1 && p.port <= 65535)) {
    const dflt = engineTraits(p.engine).defaultPort;
    e.port = `Enter a port from 1 to 65535${dflt == null ? "" : `, or leave it empty for ${dflt}`}.`;
  }
  return e;
}

/**
 * A server profile whose host field is blank goes out with no host: the sidecar then uses the engine's default
 * (localhost), where an empty text would reach the driver as an empty server name. File engines have no host.
 */
export function withDefaultHost(p: Profile): Profile {
  return !isLocal(p) && p.host != null && !p.host.trim() ? { ...p, host: null } : p;
}

/** What Test and Save act on: the fields, or the fields after merging the connection string. */
type Draft = { p: Profile; secret: string; touched: boolean };

export function ProfileEditor({ profileId, preset, copyOf, onClose }: { profileId?: string; preset?: Engine; copyOf?: string; onClose: () => void }) {
  const existing = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const groups = useProfiles((s) => s.groups);
  const [p, setP] = useState<Profile>(() => {
    if (existing) return existing;
    const source = copyOf ? useProfiles.getState().byId(copyOf) : undefined;
    return source ? duplicateProfile(source) : empty(preset ?? DEFAULT_ENGINE);
  });
  // The engines this sidecar exposes; null until it answers (or when it cannot), which offers the fallback engines.
  const [engineInfo, setEngineInfo] = useState<EngineInfo[] | null>(null);
  useEffect(() => {
    let live = true;
    void loadEngineInfo().then((list) => live && list && setEngineInfo(list));
    return () => {
      live = false;
    };
  }, []);
  // A picked file's access as the shell last reported it (a new grant starts read-only).
  const [access, setAccess] = useState<"readOnly" | "readWrite" | null>(null);
  const [secret, setSecret] = useState("");
  const [secretTouched, setSecretTouched] = useState(false);
  // SSH tunnel: its password or key passphrase is a keyring entry of its own.
  const [sshSecret, setSshSecret] = useState("");
  const [sshSecretTouched, setSshSecretTouched] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState<"connection" | "advanced">("connection");
  const [showSecret, setShowSecret] = useState(false);
  const [mode, setMode] = useState<"fields" | "string">("fields");
  const [connStr, setConnStr] = useState("");
  const [parsed, setParsed] = useState<{ engine: Engine; unknown: string[]; notApplied: string[]; notes: string[] } | null>(null);
  // Field messages (L25): shown once Save or Test was tried (the port also on leaving it); the sidecar's refusal of a
  // field shows there until the profile changes.
  const [shown, setShown] = useState<ReadonlySet<FieldKey>>(new Set());
  const [refused, setRefused] = useState<{ field: FieldKey; message: string } | null>(null);
  const [focusField, setFocusField] = useState<FieldKey | null>(null);
  const fid = useId();
  const nameRef = useRef<HTMLInputElement>(null);
  const hostRef = useRef<HTMLInputElement>(null);
  const portRef = useRef<HTMLInputElement>(null);
  const fileRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!focusField) return;
    ({ name: nameRef, host: hostRef, port: portRef, file: fileRef })[focusField].current?.focus();
    setFocusField(null);
  }, [focusField]);
  const set = (patch: Partial<Profile>) => {
    setP((x) => ({ ...x, ...patch }));
    setRefused(null);
  };
  const needsSecret = needsSecretFor(p.auth);
  // A file or memory database: no server, sign-in, TLS or SSH fields (Phase D0a); environment and Prod guards stay.
  const local = isLocal(p);
  const traits = engineTraits(p.engine);
  const fields = FIELDS[knownEngine(p.engine)];
  const defaultPort = traits.defaultPort;
  const errors = profileFieldErrors(p);
  const fieldError = (f: FieldKey) => (shown.has(f) ? errors[f] : undefined) ?? (refused?.field === f ? refused.message : undefined);
  const errId = (f: FieldKey) => `${fid}-${f}-error`;
  /** The field's error (aria-invalid and aria-describedby point at it), plus any hint ids it also has. */
  const invalidProps = (f: FieldKey, ...hints: (string | undefined)[]) => {
    const e = fieldError(f);
    const describedBy = [...hints, e ? errId(f) : undefined].filter(Boolean).join(" ");
    return { "aria-invalid": e ? true : undefined, "aria-describedby": describedBy || undefined };
  };
  const errorLine = (f: FieldKey, className = "") => {
    const e = fieldError(f);
    return e ? <span id={errId(f)} className={`block mt-1 text-[10.5px] text-err ${className}`}>{e}</span> : null;
  };
  /** Shows these fields' messages, brings their tab and the fields view up, and focuses the first one that has a message. */
  const reveal = (fields: FieldKey[], bad: FieldKey[]) => {
    setShown((cur) => new Set([...cur, ...fields]));
    const first = FIELD_ORDER.find((f) => bad.includes(f));
    if (!first) return;
    if (first !== "name") setTab("connection");
    if (first === "host" || first === "port") setMode("fields");
    setFocusField(first);
  };

  // Settings the string names replace the fields; the rest keep their values. Name is suggested when empty.
  const parse = async (): Promise<Draft | null> => {
    if (!connStr.trim()) {
      setTest({ ok: false, text: "Paste a connection string first." });
      return null;
    }
    try {
      const r = await rpc("profile.parseConnectionString", { profile: p, connectionString: connStr });
      const name = p.name.trim() ? p.name : [r.profile.host, r.profile.databaseDefault].filter(Boolean).join(" / ");
      const d: Draft = { p: { ...r.profile, name }, secret: r.secret ?? secret, touched: r.secret != null || secretTouched };
      setP(d.p);
      setSecret(d.secret);
      setSecretTouched(d.touched);
      setParsed({ engine: r.profile.engine, unknown: r.unknown, notApplied: r.notApplied, notes: r.notes ?? [] });
      return d;
    } catch (e) {
      setParsed(null);
      setTest({ ok: false, text: (e as Error).message || "Failed" });
      return null;
    }
  };

  const draft = async (): Promise<Draft | null> => {
    const d = mode === "string" ? await parse() : { p, secret, touched: secretTouched };
    return d && { ...d, p: withDefaultHost(d.p) };
  };

  const parseIntoFields = async () => {
    setBusy(true);
    setTest(null);
    try {
      if (await parse()) setMode("fields");
    } finally {
      setBusy(false);
    }
  };

  const runTest = async () => {
    setBusy(true);
    setTest(null);
    try {
      const d = await draft();
      if (!d) return;
      const errs = profileFieldErrors(d.p);
      const bad = CONNECT_FIELDS.filter((f) => errs[f]);
      if (bad.length) {
        reveal(CONNECT_FIELDS, bad);
        return setTest({ ok: false, text: errs[bad[0]]! });
      }
      const typed = d.touched || !existing;
      const sshTyped = sshSecretTouched || !existing;
      for (let round = 0; round < 2; round++) {
        const r = await rpc("profile.test", {
          profileId: typed && sshTyped ? null : existing!.id, profile: d.p, secret: typed ? d.secret : null, sshSecret: sshTyped ? sshSecret || null : null,
        });
        // An untrusted bastion key is shown to the user, never accepted automatically.
        if (!r.ok && r.untrustedHostKey && round === 0) {
          const hostKey = r.untrustedHostKey;
          if (await ask<boolean>((resolve) => ({ kind: "hostKey", hostKey, resolve }))) {
            await rpc("ssh.trustHostKey", hostKey);
            continue;
          }
        }
        const notes = r.notes.length ? ` — ${r.notes.join(" ")}` : "";
        setTest(r.ok ? { ok: true, text: `Connected: ${r.serverVersion} (${Math.round(r.elapsedMs)} ms)${d.p.ssh ? " through the SSH tunnel" : ""}${notes}` } : { ok: false, text: r.error || "Failed" });
        break;
      }
    } catch (e) {
      setTest({ ok: false, text: (e as Error).message || "Failed" });
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    setBusy(true);
    try {
      const d = await draft();
      if (!d) return;
      const { p } = d;
      const errs = profileFieldErrors(p);
      const bad = FIELD_ORDER.filter((f) => errs[f]);
      if (bad.length) return reveal(FIELD_ORDER, bad);
      if (p.passwordRetention === "duration" && (!Number.isInteger(p.passwordRetentionMinutes) || p.passwordRetentionMinutes! < 1 || p.passwordRetentionMinutes! > 525600)) return toast("warn", "Choose a duration from 1 minute to 365 days.");
      if (p.environment === "prod" && p.tlsMode === "optional") toast("warn", "Production profile without encryption.");
      const needs = needsSecretFor(p.auth);
      const sshNeeds = !!p.ssh && p.ssh.auth !== "agent";
      await useProfiles.getState().save(p, needs && d.touched ? d.secret : !needs && existing ? "" : null, sshNeeds ? (sshSecretTouched ? sshSecret : null) : "");
      onClose();
      if (existing && useConnections.getState().servers[existing.id]) toast("info", "Saved. Reconnect to apply connection changes.");
    } catch (e) {
      // A refusal about one field (the sidecar's invalidProfile with data.field) is shown at that field.
      const field = (e as { data?: { field?: unknown } }).data?.field;
      const message = (e as Error).message;
      if (typeof field === "string" && (FIELD_ORDER as string[]).includes(field)) {
        setRefused({ field: field as FieldKey, message });
        reveal([], [field as FieldKey]);
      } else toast("error", message);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!existing) return;
    if (await confirmDeleteProfile(existing)) onClose();
  };

  const plainText = p.tlsMode === "optional" || p.tlsMode === "prefer" || p.tlsMode === "disable";
  const certUnchecked = (fields.trustServerCertificate && p.trustCert) || fields.certNotChecked.includes(p.tlsMode);
  const transportWarn = plainText || certUnchecked;
  const env = ENVS.find((e) => e.id === p.environment) ?? ENVS[1];
  const envVar = `var(--q-env-${p.environment})`;
  // The pill's text in the environment's hue, fitted to 4.5:1 on its own tint over the side panel (light theme included).
  useThemeKey();
  const envText = readableOnTint(envVar, "var(--q-panel-2)", 0.14) ?? envVar;

  // A user name or database already typed is kept; an engine with a default database fills an empty one. An engine on
  // this computer starts at its first location with no file; leaving one returns to a server with its defaults.
  const pickEngine = (e: Engine) => {
    const t = ENGINE_TRAITS[e];
    const where = locationsOf(engineInfo, e);
    if (!where.includes("server")) {
      setAccess(null);
      setMode("fields");
      set({ engine: e, location: where[0], file: null, auth: t.defaultAuth, host: null, port: null, username: null, databaseDefault: t.defaultDatabase, tlsMode: "default", ssh: null });
      return;
    }
    const dflt = t.defaultDatabase;
    set({
      engine: e, auth: AUTH[e][0].id, tlsMode: "default", username: p.username || t.defaultUser, databaseDefault: dflt === null ? p.databaseDefault : (p.databaseDefault ?? dflt),
      ...(local ? { location: null, file: null, host: p.host || "localhost", databaseDefault: dflt } : {}),
    });
  };

  const pickFile = async () => {
    try {
      const picked = await pickDatabaseFile();
      if (!picked) return;
      set({ location: "file", file: { grantId: picked.grantId, displayName: picked.name } });
      setAccess("readOnly");
      if (!p.name.trim()) set({ name: picked.name });
    } catch (e) {
      toast("error", (e as Error).message || "Could not open the file.");
    }
  };

  const allowWrites = async () => {
    if (!p.file) return;
    try {
      setAccess((await requestWriteAccess(p.file.grantId)) ? "readWrite" : "readOnly");
    } catch (e) {
      toast("error", (e as Error).message || "Could not change the file's access.");
    }
  };

  return (
    <Modal
      title={existing ? `Edit profile — ${existing.name}` : "New connection profile"}
      onClose={onClose}
      width={1040}
      bodyClassName="flex min-h-0 h-[600px]"
      footer={
        <>
          {existing && <button className="q-btn text-err mr-auto" onClick={() => void remove()}>Delete</button>}
          <button className="q-btn q-btn-run" onClick={() => void runTest()} disabled={busy}>Test connection</button>
          <button className="q-btn" onClick={onClose}>Cancel</button>
          <button className="q-btn q-btn-primary" onClick={() => void save()} disabled={busy}>Save</button>
        </>
      }
    >
      <aside className="w-[280px] shrink-0 border-r border-line bg-panel-2 p-4 overflow-auto space-y-4">
        <div className="flex items-center gap-2.5 pb-3 border-b border-line">
          <div className="w-8 h-8 rounded-md bg-accent/15 text-accent flex items-center justify-center"><Icon d={ICON.database} /></div>
          <div>
            <div className="text-[12.5px] font-semibold text-fg">Identity</div>
            <div className="text-[11px] text-muted">Name, environment and engine</div>
          </div>
        </div>
        <div>
          <Field label="Connection name *">
            <input ref={nameRef} className={INPUT} autoFocus aria-required value={p.name} onChange={(e) => set({ name: e.target.value })} {...invalidProps("name")} />
          </Field>
          {errorLine("name")}
        </div>
        <Field label="Environment" hint={env.hint}>
          <div className="flex items-center gap-2">
            <select className={`${INPUT} flex-1`} value={p.environment} onChange={(e) => set({ environment: e.target.value as EnvironmentKind })}>
              {ENVS.map((e) => (
                <option key={e.id} value={e.id}>{e.label}</option>
              ))}
            </select>
            <span
              className="shrink-0 inline-flex items-center gap-1.5 h-6 px-2.5 rounded-full text-[10px] font-bold tracking-wider uppercase"
              style={{ color: envText, background: `color-mix(in srgb, ${envVar} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${envVar} 35%, transparent)` }}
            >
              <span className="w-1.5 h-1.5 rounded-full" style={{ background: envVar }} />
              {envLabel[p.environment]}
            </span>
          </div>
        </Field>
        <div className="space-y-1">
          <span className="block text-[11px] font-semibold text-muted">Server type</span>
          <EngineSelect value={p.engine} options={offeredEngines(engineInfo, existing?.engine)} onChange={(e) => e !== p.engine && pickEngine(e)} />
        </div>
        <Field label="Group">
          <input
            className={INPUT}
            list="q-groups"
            value={p.groupName ?? ""}
            onChange={(e) => {
              const g = groups.find((x) => x.name === e.target.value);
              set({ groupName: e.target.value || null, ...(g?.environment && !existing ? { environment: g.environment } : {}) });
            }}
          />
          <datalist id="q-groups">
            {groups.map((g) => (
              <option key={g.name} value={g.name} />
            ))}
          </datalist>
        </Field>
      </aside>

      <div className="flex-1 min-w-0 flex flex-col bg-bg">
        <div className="flex items-center gap-3 px-5 border-b border-line bg-panel shrink-0">
          <div role="tablist" className="flex items-end gap-1 self-end">
            <Tab id="connection" current={tab} onSelect={setTab} icon={ICON.plug} label="Connection" />
            <Tab id="advanced" current={tab} onSelect={setTab} icon={ICON.sliders} label="Advanced" alert={transportWarn} />
          </div>
          {tab === "connection" && !local && (
            <div role="radiogroup" aria-label="Enter connection details as" className="ml-auto flex items-center">
              <div className="inline-flex p-0.5 rounded-md border border-line bg-panel-2">
                <Segment on={mode === "fields"} onSelect={() => setMode("fields")}>Manual</Segment>
                <Segment on={mode === "string"} onSelect={() => setMode("string")}>Connection string</Segment>
              </div>
            </div>
          )}
        </div>

        <div className="flex-1 min-h-0 overflow-auto p-5 space-y-4">
          {tab === "connection" ? (
            <>
              {parsed && (
                <div className="flex items-start gap-2.5 p-2.5 rounded-md border border-line bg-panel text-[11.5px] text-muted leading-snug">
                  <span className="mt-px shrink-0 text-accent"><Icon d={ICON.info} size={15} /></span>
                  <div className="flex-1 min-w-0 space-y-0.5 select-text">
                    <div className="text-fg">Read as {ENGINE_TRAITS[knownEngine(parsed.engine)].label}. Settings the string names replaced the fields; the rest kept their values.</div>
                    {parsed.unknown.length > 0 && <div className="text-warn">Not recognised: {parsed.unknown.join(", ")}</div>}
                    {parsed.notApplied.length > 0 && <div>Not applied (Querithm sets or does not offer these): {parsed.notApplied.join(", ")}</div>}
                    {/* M3: a TLS setting the string names that is weaker than the engine's default */}
                    {parsed.notes.map((n) => <div key={n} className="text-warn">{n}</div>)}
                  </div>
                  <button type="button" className="shrink-0 w-5 h-5 flex items-center justify-center rounded opacity-70 hover:opacity-100 hover:bg-hover" aria-label="Dismiss" title="Dismiss" onClick={() => setParsed(null)}>
                    <Icon d={ICON.x} size={13} />
                  </button>
                </div>
              )}
              {mode === "string" && !local ? (
                <Section icon={ICON.code} title="Connection string" summary="ADO.NET keywords for either engine, or a postgres:// URI">
                  <textarea
                    className="q-input q-mono w-full h-28 py-2 resize-none"
                    aria-label="Connection string"
                    spellCheck={false}
                    autoComplete="off"
                    value={connStr}
                    placeholder={fields.connectionStringExample}
                    onChange={(e) => {
                      setConnStr(e.target.value);
                      setParsed(null);
                    }}
                  />
                  <div className="flex items-start gap-3">
                    <p className="flex-1 text-[10.5px] text-muted leading-snug">
                      Test connection and Save read the string directly. The string itself is not stored: its settings become the profile, and any password follows the selected remember-password setting.
                    </p>
                    <button type="button" className="q-btn shrink-0" onClick={() => void parseIntoFields()} disabled={busy || !connStr.trim()}>Parse into fields</button>
                  </div>
                  <PasswordRetention profile={p} onChange={set} />
                </Section>
              ) : local ? (
                <Section icon={ICON.database} title="Database" summary="A database on this computer: no server, sign-in or encryption">
                  <Row label="Location">
                    <select className={INPUT} aria-label="Location" value={locationOf(p)} onChange={(e) => set({ location: e.target.value as LocationKind, file: e.target.value === "file" ? p.file : null })}>
                      {locationsOf(engineInfo, p.engine).filter((l) => l !== "server").map((l) => (
                        <option key={l} value={l}>{LOCATION_LABEL[l]}</option>
                      ))}
                    </select>
                  </Row>
                  {locationOf(p) === "file" ? (
                    // A div, not Row's <label>: a label would name the buttons after the whole row and click the first one on any press.
                    <div className="grid grid-cols-[110px_1fr] items-center gap-x-3">
                      <span className="text-[11.5px] text-muted">File</span>
                      <div className="flex items-center gap-2 min-w-0">
                        <span className={`flex-1 min-w-0 truncate q-mono text-[11.5px] ${p.file ? "text-fg" : "text-muted"}`} data-testid="picked-file">
                          {p.file ? (p.file.displayName ?? "Picked file") : "No file picked"}
                        </span>
                        {access && <Badge tone={access === "readWrite" ? "warn" : "ok"}>{access === "readWrite" ? "Writes allowed" : "Read-only"}</Badge>}
                        <button type="button" ref={fileRef} className="q-btn shrink-0" onClick={() => void pickFile()} aria-describedby={fieldError("file") ? errId("file") : undefined}>{p.file ? "Re-pick file…" : "Pick file…"}</button>
                        {p.file && <button type="button" className="q-btn shrink-0" onClick={() => void allowWrites()}>Allow writes…</button>}
                      </div>
                      {errorLine("file", "col-start-2")}
                      <span className="col-start-2 mt-1 text-[10.5px] text-muted">Querithm reads only the file you pick here, read-only until you allow writes.</span>
                    </div>
                  ) : (
                    <p className="text-[11px] text-muted leading-snug">Each tab gets its own empty database, and it is gone when the tab closes. Nothing is written to disk.</p>
                  )}
                </Section>
              ) : (
                <>
                  <Section icon={ICON.server} title="Server" summary="Where the database engine is listening">
                    <div className="grid grid-cols-[110px_1fr_auto_88px] items-center gap-x-3">
                      <label htmlFor={`${fid}-host`} className="text-[11.5px] text-muted">Host</label>
                      <input
                        id={`${fid}-host`} ref={hostRef} className={INPUT} value={p.host ?? ""} onChange={(e) => set({ host: e.target.value })} placeholder="localhost"
                        title="Empty: localhost"
                        {...invalidProps("host", fields.instanceNames ? `${fid}-host-hint` : undefined)}
                      />
                      <label htmlFor={`${fid}-port`} className="text-[11.5px] text-muted">Port</label>
                      <input
                        id={`${fid}-port`} ref={portRef} className={INPUT} type="number" min={1} max={65535} step={1} inputMode="numeric"
                        placeholder={defaultPort == null ? "" : String(defaultPort)} value={p.port == null || Number.isNaN(p.port) ? "" : p.port}
                        // A number the field cannot read ("1e" while typing) is kept as invalid, never silently the default.
                        onChange={(e) => set({ port: e.target.value ? Number(e.target.value) : e.target.validity?.badInput ? NaN : null })}
                        onBlur={() => p.port != null && setShown((cur) => new Set([...cur, "port"]))}
                        {...invalidProps("port")}
                      />
                      {fields.instanceNames && <span id={`${fid}-host-hint`} className="col-start-2 col-span-3 mt-1 text-[10.5px] text-muted">Server name, host\instance, or tcp:host</span>}
                      {errorLine("host", "col-start-2 col-span-3")}
                      {errorLine("port", "col-start-2 col-span-3")}
                    </div>
                    <Row label="Database">
                      <input className={INPUT} value={p.databaseDefault ?? ""} onChange={(e) => set({ databaseDefault: e.target.value || null })} placeholder={traits.defaultDatabase ?? "(login default)"} />
                    </Row>
                  </Section>

                  <Section icon={ICON.key} title="Authentication" summary="How Querithm signs in">
                    <Row label="Method">
                      <select className={INPUT} value={p.auth} onChange={(e) => set({ auth: e.target.value as AuthKind })}>
                        {AUTH[p.engine].map((a) => (
                          <option key={a.id} value={a.id}>{a.label}</option>
                        ))}
                      </select>
                    </Row>
                    <Row label={p.auth === "entraInteractive" ? "User (optional hint)" : "User name"}>
                      <input className={INPUT} value={p.username ?? ""} onChange={(e) => set({ username: e.target.value })} disabled={p.auth === "windows" || p.auth === "entraDeviceCode"} />
                    </Row>
                    {needsSecret && (
                      <Row
                        label={p.auth === "entraToken" ? "Access token" : "Password"}
                        hint={existing && !secretTouched ? "Leave empty to keep the saved secret." : "Remembered according to the setting below."}
                      >
                        <div className="relative">
                          <input
                            className={INPUT}
                            style={{ paddingRight: 34 }}
                            type={showSecret ? "text" : "password"}
                            autoComplete="new-password"
                            spellCheck={false}
                            value={secret}
                            placeholder={existing && !secretTouched ? "••••••••" : ""}
                            onChange={(e) => {
                              setSecret(e.target.value);
                              setSecretTouched(true);
                            }}
                          />
                          <button
                            type="button"
                            className="absolute right-1 top-1/2 -translate-y-1/2 w-7 h-6 flex items-center justify-center rounded text-muted hover:text-fg hover:bg-hover"
                            title={showSecret ? "Hide" : "Show"}
                            aria-label={showSecret ? "Hide password" : "Show password"}
                            aria-pressed={showSecret}
                            onClick={() => setShowSecret((v) => !v)}
                          >
                            <Icon d={showSecret ? ICON.eyeOff : ICON.eye} size={15} />
                          </button>
                        </div>
                      </Row>
                    )}
                    {needsSecret && <div className="pl-[122px]"><PasswordRetention profile={p} onChange={set} /></div>}
                    {existing && needsSecret && (
                      <div className="pl-[122px] text-[10.5px] text-muted">
                        <button className="hover:underline" onClick={() => void shell.secretDelete(existing.id).then(() => toast("info", "Saved secret removed from memory and the keyring.")).catch((e) => toast("error", `Could not forget password: ${String(e)}`))}>Forget saved secret</button>
                      </div>
                    )}
                  </Section>
                </>
              )}
            </>
          ) : (
            <>
              {!local && (
              <>
              <Section
                icon={ICON.shield}
                title="Transport"
                summary="Encryption and certificate checking"
                badge={plainText ? <Badge tone="warn">Not encrypted</Badge> : certUnchecked ? <Badge tone="warn">Cert not validated</Badge> : <Badge tone="ok">TLS required</Badge>}
              >
                <Row label="Encryption">
                  <select className={INPUT} value={p.tlsMode} onChange={(e) => set({ tlsMode: e.target.value as TlsMode })}>
                    {TLS[p.engine].map((t) => (
                      <option key={t.id} value={t.id}>{t.label}</option>
                    ))}
                  </select>
                </Row>
                {fields.trustServerCertificate ? (
                  <Row label="Certificate" hint={p.trustCert ? <span className="text-warn">The server certificate is not validated. Use only for development servers.</span> : "Certificates are validated by default."}>
                    <Check checked={p.trustCert} onChange={(v) => set({ trustCert: v })}>Trust server certificate</Check>
                  </Row>
                ) : (
                  <Row label="CA certificate" hint="Used by Verify CA / Verify full.">
                    <input className={INPUT} value={p.caPath ?? ""} onChange={(e) => set({ caPath: e.target.value || null })} placeholder="/etc/ssl/certs/root.crt" />
                  </Row>
                )}
              </Section>
              <SshSection
                ssh={p.ssh ?? null}
                onChange={(ssh) => set({ ssh })}
                secret={sshSecret}
                onSecret={(v) => {
                  setSshSecret(v);
                  setSshSecretTouched(true);
                }}
                saved={!!existing && !sshSecretTouched}
                onForget={existing ? () => void shell.secretDelete(sshAccount(existing.id)).then(() => toast("info", "Saved SSH secret removed from the keyring.")) : undefined}
              />
              <Section icon={ICON.globe} title="Network" summary={fields.multiSubnetFailover ? "Connect timeout and failover" : "Connect timeout"}>
                <Row label="Connect timeout" hint="Seconds">
                  <input className={`${INPUT} max-w-[120px]`} type="number" value={p.timeoutS ?? 15} onChange={(e) => set({ timeoutS: Number(e.target.value) })} />
                </Row>
                {fields.multiSubnetFailover && (
                  <Row label="Failover">
                    <Check checked={p.options?.multiSubnetFailover === "true"} onChange={(v) => set({ options: { ...(p.options ?? {}), multiSubnetFailover: String(v) } })}>Multi-subnet failover</Check>
                  </Row>
                )}
              </Section>
              </>
              )}
              <Section icon={ICON.timer} title="Session" summary={fields.applicationIntent ? "Query timeout and application intent" : "Query timeout"}>
                <Row label="Command timeout" hint="Seconds. 0 = no limit">
                  <input className={`${INPUT} max-w-[120px]`} type="number" value={p.commandTimeoutS ?? 0} onChange={(e) => set({ commandTimeoutS: Number(e.target.value) })} />
                </Row>
                {fields.applicationIntent && (
                  <Row label="Intent">
                    <Check checked={p.options?.applicationIntent === "readOnly"} onChange={(v) => set({ options: { ...(p.options ?? {}), applicationIntent: v ? "readOnly" : "readWrite" } })}>Read-only intent</Check>
                  </Row>
                )}
              </Section>
            </>
          )}

        </div>

        {/* Pinned under the tabs so a test result is visible without scrolling, whichever tab is open. */}
        <div role="status" aria-live="polite" className={test ? "shrink-0 border-t border-line bg-panel px-5 py-3" : undefined}>
          {test && (
            <div className={`flex items-start gap-2.5 p-2.5 rounded-md border text-[11.5px] ${test.ok ? "border-ok/40 bg-ok/10 text-ok" : "border-err/40 bg-err/10 text-err"}`}>
              <span className="mt-px shrink-0"><Icon d={test.ok ? ICON.circleCheck : ICON.circleAlert} size={15} /></span>
              <div className="flex-1 min-w-0 max-h-[60px] overflow-auto select-text leading-snug">{test.text}</div>
              <button type="button" className="shrink-0 w-5 h-5 flex items-center justify-center rounded opacity-70 hover:opacity-100 hover:bg-black/10" aria-label="Dismiss" title="Dismiss" onClick={() => setTest(null)}>
                <Icon d={ICON.x} size={13} />
              </button>
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}

const INPUT = "q-input w-full h-8";

const SSH_AUTH: { id: SshAuthKind; label: string }[] = [
  { id: "privateKey", label: "Key file" },
  { id: "password", label: "Password" },
  { id: "agent", label: "SSH agent (not supported yet)" },
];

/**
 * SSH tunnel settings: the bastion, the user and how to sign in. The server's host and port on the
 * Connection tab are as the bastion sees them. The password / key passphrase goes to the OS keyring.
 */
function SshSection({ ssh, onChange, secret, onSecret, saved, onForget }: {
  ssh: SshSettings | null; onChange: (ssh: SshSettings | null) => void; secret: string; onSecret: (v: string) => void; saved: boolean; onForget?: () => void;
}) {
  const on = !!ssh;
  const set = (patch: Partial<SshSettings>) => onChange({ ...(ssh ?? { host: "", port: 22, username: "", auth: "privateKey", keyPath: null }), ...patch });
  const browse = async () => {
    const { open } = await import("@tauri-apps/plugin-dialog");
    const path = await open({ multiple: false, title: "SSH private key" });
    if (typeof path === "string") set({ keyPath: path });
  };
  return (
    <Section
      icon={ICON.shield}
      title="SSH tunnel"
      summary="Reach the server through a bastion"
      badge={on ? <Badge tone="ok">Through {ssh!.host || "bastion"}</Badge> : undefined}
    >
      <Row label="Tunnel">
        <Check checked={on} onChange={(v) => onChange(v ? { host: "", port: 22, username: "", auth: "privateKey", keyPath: null } : null)}>Connect through an SSH bastion</Check>
      </Row>
      {on && (
        <>
          <Row label="Bastion" hint="The server address on the Connection tab is as the bastion sees it (e.g. db.internal).">
            <div className="flex gap-2">
              <input className={INPUT} value={ssh!.host} placeholder="bastion.example.com" onChange={(e) => set({ host: e.target.value })} />
              <input className={`${INPUT} max-w-[90px]`} type="number" value={ssh!.port ?? 22} onChange={(e) => set({ port: Number(e.target.value) || 22 })} aria-label="SSH port" />
            </div>
          </Row>
          <Row label="User">
            <input className={INPUT} value={ssh!.username} placeholder="deploy" onChange={(e) => set({ username: e.target.value })} />
          </Row>
          <Row label="Sign in with" hint={ssh!.auth === "agent" ? <span className="text-warn">SSH agent sign-in is not supported yet; choose a key file or a password.</span> : undefined}>
            <select className={INPUT} value={ssh!.auth} onChange={(e) => set({ auth: e.target.value as SshAuthKind })}>
              {SSH_AUTH.map((a) => <option key={a.id} value={a.id}>{a.label}</option>)}
            </select>
          </Row>
          {ssh!.auth === "privateKey" && (
            <Row label="Key file" hint="OpenSSH, PEM or PuTTY format. The path is saved; the key is read when connecting.">
              <div className="flex gap-2">
                <input className={INPUT} value={ssh!.keyPath ?? ""} placeholder="/home/me/.ssh/id_ed25519" onChange={(e) => set({ keyPath: e.target.value || null })} />
                {isTauri && <button type="button" className="q-btn" onClick={() => void browse()}>Browse…</button>}
              </div>
            </Row>
          )}
          {ssh!.auth !== "agent" && (
            <Row label={ssh!.auth === "password" ? "Password" : "Passphrase"} hint={saved ? "Leave empty to keep the one saved in the OS keyring." : ssh!.auth === "privateKey" ? "Only if the key is protected." : undefined}>
              <div className="flex gap-2 items-center">
                <input className={INPUT} type="password" value={secret} autoComplete="off" onChange={(e) => onSecret(e.target.value)} />
                {onForget && <button type="button" className="q-btn shrink-0" onClick={onForget}>Forget saved</button>}
              </div>
            </Row>
          )}
        </>
      )}
    </Section>
  );
}

/** Bundled engine logos; null draws Querithm's own engine mark (EngineLogo) instead. */
const LOGOS: Record<Engine, string | null> = { mssql: mssqlLogo, pg: pgLogo, sqlite: null, mySql: null, duckDb: null };
const ALL_ENGINES: { id: Engine; label: string; logo: string | null }[] = ENGINE_LIST.map((id) => ({ id, label: ENGINE_TRAITS[id].label, logo: LOGOS[id] }));

function EngineMark({ engine, logo, size }: { engine: Engine; logo: string | null; size: number }) {
  return logo ? <img src={logo} alt="" style={{ width: size, height: size }} className="shrink-0" draggable={false} /> : <EngineLogo engine={engine} size={size} decorative />;
}

// Ordered along the usual promotion path; only Prod adds guards (§6).
const ENVS: { id: EnvironmentKind; label: string; hint: string }[] = [
  { id: "local", label: "Local", hint: "Your own machine or a container. No extra guards." },
  { id: "dev", label: "Development", hint: "Shared development server. No extra guards." },
  { id: "test", label: "Test / QA", hint: "Test and QA data. No extra guards." },
  { id: "staging", label: "Staging", hint: "Pre-production mirror. No extra guards." },
  { id: "prod", label: "Production", hint: "Paints tabs and the status bar and requires confirmations (§6)." },
];

// Lucide-style outline glyphs, inlined so the app stays offline.
const ICON = {
  database: <><ellipse cx="12" cy="5" rx="9" ry="3" /><path d="M3 5V19A9 3 0 0 0 21 19V5" /><path d="M3 12A9 3 0 0 0 21 12" /></>,
  server: <><rect width="20" height="8" x="2" y="2" rx="2" ry="2" /><rect width="20" height="8" x="2" y="14" rx="2" ry="2" /><line x1="6" x2="6.01" y1="6" y2="6" /><line x1="6" x2="6.01" y1="18" y2="18" /></>,
  plug: <><path d="M12 22v-5" /><path d="M9 8V2" /><path d="M15 8V2" /><path d="M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z" /></>,
  sliders: <><line x1="21" x2="14" y1="4" y2="4" /><line x1="10" x2="3" y1="4" y2="4" /><line x1="21" x2="12" y1="12" y2="12" /><line x1="8" x2="3" y1="12" y2="12" /><line x1="21" x2="16" y1="20" y2="20" /><line x1="12" x2="3" y1="20" y2="20" /><line x1="14" x2="14" y1="2" y2="6" /><line x1="8" x2="8" y1="10" y2="14" /><line x1="16" x2="16" y1="18" y2="22" /></>,
  key: <><path d="M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z" /><circle cx="16.5" cy="7.5" r=".5" fill="currentColor" /></>,
  shield: <path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z" />,
  globe: <><circle cx="12" cy="12" r="10" /><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20" /><path d="M2 12h20" /></>,
  timer: <><line x1="10" x2="14" y1="2" y2="2" /><line x1="12" x2="15" y1="14" y2="11" /><circle cx="12" cy="14" r="8" /></>,
  code: <><polyline points="16 18 22 12 16 6" /><polyline points="8 6 2 12 8 18" /></>,
  info: <><circle cx="12" cy="12" r="10" /><path d="M12 16v-4" /><path d="M12 8h.01" /></>,
  eye: <><path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0" /><circle cx="12" cy="12" r="3" /></>,
  chevronDown: <path d="m6 9 6 6 6-6" />,
  check: <path d="M20 6 9 17l-5-5" />,
  x: <><path d="M18 6 6 18" /><path d="m6 6 12 12" /></>,
  circleCheck: <><circle cx="12" cy="12" r="10" /><path d="m9 12 2 2 4-4" /></>,
  circleAlert: <><circle cx="12" cy="12" r="10" /><line x1="12" x2="12" y1="8" y2="12" /><line x1="12" x2="12.01" y1="16" y2="16" /></>,
  eyeOff: <><path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49" /><path d="M14.084 14.158a3 3 0 0 1-4.242-4.242" /><path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143" /><path d="m2 2 20 20" /></>,
};

function Icon({ d, size = 16 }: { d: ReactNode; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden>
      {d}
    </svg>
  );
}

function Tab<T extends string>({ id, current, onSelect, icon, label, alert }: { id: T; current: T; onSelect: (id: T) => void; icon: ReactNode; label: string; alert?: boolean }) {
  const on = id === current;
  return (
    <button
      type="button"
      role="tab"
      aria-selected={on}
      onClick={() => onSelect(id)}
      className={`relative -mb-px flex items-center gap-2 h-11 px-3 border-b-2 text-[12px] font-semibold ${on ? "border-accent text-fg" : "border-transparent text-muted hover:text-fg"}`}
    >
      <Icon d={icon} size={14} />
      {label}
      {alert && <span className="w-1.5 h-1.5 rounded-full bg-warn" title="Has a security warning" />}
    </button>
  );
}

function Segment({ on, onSelect, children }: { on: boolean; onSelect: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={on}
      onClick={onSelect}
      className={`h-6 px-2.5 rounded text-[11px] font-medium ${on ? "bg-bg text-fg shadow-sm ring-1 ring-line" : "text-muted hover:text-fg"}`}
    >
      {children}
    </button>
  );
}

function Section({ icon, title, summary, badge, children }: { icon: ReactNode; title: string; summary: string; badge?: ReactNode; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-line bg-panel">
      <header className="flex items-center gap-2.5 px-4 h-11 border-b border-line">
        <span className="text-muted"><Icon d={icon} size={14} /></span>
        <span className="text-[12px] font-semibold text-fg">{title}</span>
        <span className="text-[11px] text-muted truncate">{summary}</span>
        {badge && <span className="ml-auto">{badge}</span>}
      </header>
      <div className="p-4 space-y-3">{children}</div>
    </section>
  );
}

function Row({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="grid grid-cols-[110px_1fr] items-center gap-x-3">
      <span className="text-[11.5px] text-muted">{label}</span>
      {children}
      {hint && <span className="col-start-2 mt-1 text-[10.5px] text-muted">{hint}</span>}
    </label>
  );
}

function Check({ checked, onChange, children }: { checked: boolean; onChange: (v: boolean) => void; children: ReactNode }) {
  return (
    <span className="flex items-center gap-2 h-8 text-fg">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} /> {children}
    </span>
  );
}

function Badge({ tone, children }: { tone: "ok" | "warn"; children: ReactNode }) {
  return <span className={`px-2 h-5 inline-flex items-center rounded-full border text-[10px] font-medium ${tone === "ok" ? "border-ok/50 bg-ok/10 text-ok" : "border-warn/50 bg-warn/10 text-warn"}`}>{children}</span>;
}

/** Server-type dropdown with engine logos; a native select cannot show images. Escape is left to it while open (see Modal). */
function EngineSelect({ value, options, onChange }: { value: Engine; options: Engine[]; onChange: (e: Engine) => void }) {
  const ENGINES = ALL_ENGINES.filter((e) => options.includes(e.id));
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [pos, setPos] = useState<{ left: number; width: number; top?: number; bottom?: number }>({ left: 0, width: 0 });
  const btn = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const id = useId();
  const current = ENGINES.find((e) => e.id === value) ?? ENGINES[0] ?? ALL_ENGINES[0];

  const show = () => {
    const r = btn.current!.getBoundingClientRect();
    const h = Math.min(ENGINES.length * 34 + 8, 260);
    const below = window.innerHeight - r.bottom;
    setPos(below < h + 8 && r.top > below ? { left: r.left, width: r.width, bottom: window.innerHeight - r.top + 4 } : { left: r.left, width: r.width, top: r.bottom + 4 });
    setActive(Math.max(0, ENGINES.indexOf(current)));
    setOpen(true);
  };
  const choose = (i: number) => {
    setOpen(false);
    onChange(ENGINES[i].id);
    btn.current?.focus();
  };

  useEffect(() => {
    if (!open) return;
    const away = (e: Event) => {
      if (!btn.current?.contains(e.target as Node) && !list.current?.contains(e.target as Node)) setOpen(false);
    };
    const close = () => setOpen(false);
    window.addEventListener("mousedown", away, true);
    window.addEventListener("scroll", away, true);
    window.addEventListener("resize", close);
    return () => {
      window.removeEventListener("mousedown", away, true);
      window.removeEventListener("scroll", away, true);
      window.removeEventListener("resize", close);
    };
  }, [open]);

  useEffect(() => {
    if (open) list.current?.children[active]?.scrollIntoView({ block: "nearest" });
  }, [open, active]);

  const onKey = (e: KeyboardEvent) => {
    const move = (to: number) => {
      e.preventDefault();
      if (!open) return show();
      setActive(Math.min(ENGINES.length - 1, Math.max(0, to)));
    };
    if (e.key === "ArrowDown") move(active + 1);
    else if (e.key === "ArrowUp") move(active - 1);
    else if (e.key === "Home") move(0);
    else if (e.key === "End") move(ENGINES.length - 1);
    else if (open && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      choose(active);
    } else if (open && e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
    } else if (e.key === "Tab") setOpen(false);
  };

  return (
    <>
      <button
        ref={btn}
        type="button"
        role="combobox"
        aria-label="Server type"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={`${id}-list`}
        aria-activedescendant={open ? `${id}-${active}` : undefined}
        onClick={() => (open ? setOpen(false) : show())}
        onKeyDown={onKey}
        className={`${INPUT} flex items-center gap-2 text-left ${open ? "border-accent" : ""}`}
      >
        <EngineMark engine={current.id} logo={current.logo} size={16} />
        <span className="flex-1 truncate">{current.label}</span>
        <span className="text-muted"><Icon d={ICON.chevronDown} size={14} /></span>
      </button>
      {open && (
        <ul
          ref={list}
          id={`${id}-list`}
          role="listbox"
          aria-label="Server type"
          className="fixed z-50 max-h-[260px] overflow-auto rounded-md border border-line bg-panel py-1 shadow-xl"
          style={pos}
        >
          {ENGINES.map((e, i) => (
            <li
              key={e.id}
              id={`${id}-${i}`}
              role="option"
              aria-selected={e.id === value}
              onMouseEnter={() => setActive(i)}
              onMouseDown={(ev) => ev.preventDefault()}
              onClick={() => choose(i)}
              className={`flex items-center gap-2.5 h-[34px] px-2.5 text-[12px] ${i === active ? "bg-hover text-fg" : "text-fg"}`}
            >
              <EngineMark engine={e.id} logo={e.logo} size={20} />
              <span className="flex-1 truncate">{e.label}</span>
              {e.id === value && <span className="text-accent"><Icon d={ICON.check} size={14} /></span>}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

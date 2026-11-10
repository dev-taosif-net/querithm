// Connection profile editor (§6, §13): engine, auth, TLS, environment, group. The password goes to the
// shell under the chosen retention policy; it is never part of the profile. A pasted connection string is parsed by the
// sidecar's drivers into the same fields and is itself never stored.
//
// One column, read in the same order by the eye, Tab and a screen reader: server type → where → environment →
// sign-in → name → folded extras. A new profile preselects neither the server type nor the environment; the folded rows
// show their state in one line and open by themselves on a warning (transportSummary is their only source).
import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ClipboardEvent, type KeyboardEvent, type ReactNode, type RefObject } from "react";
import { BusyButtonContent } from "../app/LoadingStatus";
import { SecretInput } from "../app/SecretInput";
import { escapeForDisplay } from "../monitor/format";
import type { AuthKind, Engine, EngineInfo, EnvironmentKind, LocationKind, Profile, SshAuthKind, SshSettings, TlsMode } from "../ipc/contract";
import { rpc, shell } from "../ipc/client";
import { Modal } from "../app/Modal";
import { readableOnTint, useThemeKey } from "../app/tabStyle";
import { envLabel, sshAccount, useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { ask, toast } from "../store/ui";
import { isTauri } from "../ipc/transport";
import { confirmDeleteProfile } from "./deleteProfile";
import { PasswordRetention } from "./PasswordRetention";
import { duplicateProfile, endpointKey, isLocal, locationOf, needsSecretFor } from "./auth";
import { pickDatabaseFile, requestWriteAccess } from "./fileGrants";
import { loadEngineInfo, offeredEngines } from "./engineInfo";
import { EngineLogo } from "./EngineLogo";
import mssqlLogo from "./logos/mssql.svg";
import pgLogo from "./logos/postgresql.svg";
import { DEFAULT_ENGINE, ENGINES as ENGINE_TRAITS, engineTraits, knownEngine } from "../engines";

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
  /** Words the server-type search also finds the engine by. */
  aliases: string;
}

const FIELDS: Record<Engine, EngineFields> = {
  mssql: {
    connectionStringExample: "Server=localhost,1433;Database=master;User ID=sa;Password=…;Encrypt=True",
    instanceNames: true, trustServerCertificate: true, multiSubnetFailover: true, applicationIntent: true, certNotChecked: [],
    aliases: "mssql sqlserver sql server azure tsql t-sql",
  },
  pg: {
    connectionStringExample: "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=…  or  postgres://…",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
    aliases: "postgres postgresql pg psql",
  },
  // A file is only ever picked through the shell's dialog (§13 rule 6): no connection string names one.
  sqlite: {
    connectionStringExample: "",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
    aliases: "sqlite sqlite3 file",
  },
  mySql: {
    connectionStringExample: "Server=localhost;Port=3306;User ID=root;Password=…;Database=…",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: ["require"],
    aliases: "mysql mariadb",
  },
  duckDb: {
    connectionStringExample: "",
    instanceNames: false, trustServerCertificate: false, multiSubnetFailover: false, applicationIntent: false, certNotChecked: [],
    aliases: "duckdb duck file parquet",
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

/**
 * The Encryption row's one-line state, and the only place that decides it: the folded row, its warning colour, the
 * auto-open and the Production acknowledgement all read this. `risk` is "plain" when the connection may run
 * unencrypted, "unchecked" when it encrypts without checking the server certificate; PostgreSQL's default Require is
 * named as it is but not flagged, as before (§13.2).
 */
export function transportSummary(p: Pick<Profile, "engine" | "tlsMode" | "trustCert" | "location">): { text: string; tone: "ok" | "warn" | "muted"; risk: "plain" | "unchecked" | null } {
  if (isLocal(p)) return { text: "Not used for a database on this computer", tone: "muted", risk: null };
  const f = FIELDS[knownEngine(p.engine)];
  const m = p.tlsMode;
  if (m === "optional" || m === "disable") return { text: "Not encrypted", tone: "warn", risk: "plain" };
  if (m === "prefer") return { text: "May fall back to plain text", tone: "warn", risk: "plain" };
  // SqlClient checks the certificate under Strict whatever TrustServerCertificate says.
  if ((f.trustServerCertificate && p.trustCert && m !== "strict") || f.certNotChecked.includes(m)) return { text: "Encrypted · certificate not checked", tone: "warn", risk: "unchecked" };
  // PostgreSQL's default (Require) encrypts without checking the certificate, as MySQL's Require does: not a warning on
  // its own (it is the engine's default, §13.2), but a Production profile is asked about it like the others.
  if (knownEngine(p.engine) === "pg" && (m === "default" || m === "require")) return { text: "Encrypted (Require) · certificate not checked", tone: "muted", risk: "unchecked" };
  return { text: "Encrypted · certificate checked", tone: "ok", risk: null };
}

/** The name offered until the user types one: host / database for a server, the file's name for a file. */
export function suggestedName(p: Profile): string {
  if (isLocal(p)) return locationOf(p) === "file" ? (p.file?.displayName ?? "") : `${engineTraits(p.engine).shortLabel} (in-memory)`;
  const host = (p.host ?? "").trim() || "localhost";
  const db = (p.databaseDefault ?? "").trim();
  return db ? `${host} / ${db}` : host;
}

/**
 * A host or database name with a word that usually means Production ("db-prod-01", "billing.prd"). It only ever
 * offers Prod, never chooses an environment: a tunnelled or port-forwarded Prod server shows up as localhost, so no
 * name may ever suggest a lower environment.
 */
export function looksLikeProduction(...names: (string | null | undefined)[]): string | null {
  for (const n of names) {
    const word = (n ?? "").toLowerCase().split(/[^a-z0-9]+/).find((w) => /^(prod|prd|live|production)\d*$/.test(w));
    if (word) return word;
  }
  return null;
}

/** Text pasted into Host that is really a connection string or a URI: offered to the parser instead. */
export function looksLikeConnectionString(text: string): boolean {
  const t = text.trim();
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(t) || (t.includes("=") && t.includes(";"));
}

/** "db01:5433" typed in Host, split into host and port; null for SQL Server (`host,port`, `host\instance`) and IPv6. */
export function splitHostPort(host: string, engine: Engine): { host: string; port: number } | null {
  if (engine === "mssql") return null;
  const m = /^([^:\s]+):(\d{1,5})$/.exec(host.trim());
  return m ? { host: m[1], port: Number(m[2]) } : null;
}

export { endpointKey } from "./auth";

/** A server's version as the success line shows it: its first words only, so a server cannot write its own claims there. */
export function shownVersion(v: string | null | undefined): string {
  const head = (v ?? "").split(/[\r\n(,;]/)[0].trim().slice(0, 80);
  return escapeForDisplay(head) || "unknown version";
}

/** Which kind of secret a sign-in keeps: a password, or an access token. */
function secretKind(a: AuthKind): "token" | "password" {
  return a === "entraToken" ? "token" : "password";
}

/** What a test result belongs to: everything that reaches the server, normalised as Test sends it (not the name or environment). */
function testKey(p: Profile, secret: string, sshSecret: string): string {
  return JSON.stringify([{ ...withDefaultHost(p), name: "", groupName: null, environment: null, color: null, passwordRetention: null, passwordRetentionMinutes: null }, secret, sshSecret]);
}

/** What Test and Save act on: the fields, or the fields after merging the connection string. */
type Draft = { p: Profile; secret: string; touched: boolean };
/** Problems found on Save that live outside profileFieldErrors (UI-only choices). */
type Focusable = FieldKey | "environment" | "ack" | "sshHost" | "sshUser";

export function ProfileEditor({ profileId, preset, copyOf, onClose }: { profileId?: string; preset?: Engine; copyOf?: string; onClose: () => void }) {
  const existing = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const groups = useProfiles((s) => s.groups);
  const allProfiles = useProfiles((s) => s.profiles);
  const [start] = useState(() => {
    if (existing) return { p: existing, known: true };
    const source = copyOf ? useProfiles.getState().byId(copyOf) : undefined;
    return source ? { p: duplicateProfile(source), known: true, fromEnv: source.environment } : { p: empty(preset ?? DEFAULT_ENGINE), known: false };
  });
  const [p, setP] = useState<Profile>(start.p);
  // A new profile names neither its server type nor its environment until the user picks them (a preset names the type).
  const [engineChosen, setEngineChosen] = useState(start.known || !!preset);
  const [animated, setAnimated] = useState(false);
  const [mounted, setMounted] = useState(false);
  useEffect(() => {
    setMounted(true);
  }, []);
  const [envChosen, setEnvChosen] = useState(start.known);
  const [envFromGroup, setEnvFromGroup] = useState<string | null>(null);
  const [nameTouched, setNameTouched] = useState(start.known);
  // The engines this sidecar exposes; null until it answers (or when it cannot), which offers the fallback engines.
  const [engineInfo, setEngineInfo] = useState<EngineInfo[] | null>(null);
  useEffect(() => {
    let live = true;
    void loadEngineInfo().then((list) => live && list && setEngineInfo(list));
    return () => {
      live = false;
    };
  }, []);
  // Whether the keyring holds this profile's secret; null until the shell answers (shown as saved, as before).
  const [savedSecret, setSavedSecret] = useState<boolean | null>(null);
  useEffect(() => {
    if (!existing) return;
    let live = true;
    Promise.resolve(shell.secretExists?.(existing.id)).then((v) => live && typeof v === "boolean" && setSavedSecret(v), () => {});
    return () => {
      live = false;
    };
  }, [existing?.id]);
  // The same for the tunnel's password or passphrase ("<id>:ssh"): a key file without a passphrase has none.
  const [sshSaved, setSshSaved] = useState<boolean | null>(null);
  useEffect(() => {
    if (!existing?.ssh) return;
    let live = true;
    Promise.resolve(shell.secretExists?.(sshAccount(existing.id))).then((v) => live && typeof v === "boolean" && setSshSaved(v), () => {});
    return () => {
      live = false;
    };
  }, [existing?.id]); // eslint-disable-line react-hooks/exhaustive-deps
  // A picked file's access as the shell last reported it (a new grant starts read-only).
  const [access, setAccess] = useState<"readOnly" | "readWrite" | null>(null);
  const [secret, setSecret] = useState("");
  const [secretTouched, setSecretTouched] = useState(false);
  const [changeSecret, setChangeSecret] = useState(false);
  // Forget runs on Save, so Cancel keeps the secret.
  const [forget, setForget] = useState(false);
  // SSH tunnel: its password or key passphrase is a keyring entry of its own.
  const [sshSecret, setSshSecret] = useState("");
  const [sshSecretTouched, setSshSecretTouched] = useState(false);
  const [sshForget, setSshForget] = useState(false);
  const [test, setTest] = useState<{ ok: boolean; text: string; key: string } | null>(null);
  const [busyAction, setBusyAction] = useState<"parse" | "test" | "save" | null>(null);
  const busy = busyAction != null;
  const [showSecret, setShowSecret] = useState(false);
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pastedIntoHost, setPastedIntoHost] = useState(false);
  const [connStr, setConnStr] = useState("");
  const [parsed, setParsed] = useState<{ engine: Engine; unknown: string[]; notApplied: string[]; notes: string[] } | null>(null);
  const [undo, setUndo] = useState<{ before: Profile; text: string } | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [portOffer, setPortOffer] = useState<Engine | null>(null);
  const [prodDismissed, setProdDismissed] = useState<string | null>(null);
  const [lowerProd, setLowerProd] = useState<EnvironmentKind | null>(null);
  const [ack, setAck] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const keepRef = useRef<HTMLButtonElement>(null);
  // The question takes focus, so Enter keeps editing and nothing behind it reacts.
  useEffect(() => {
    if (confirmDiscard) keepRef.current?.focus();
  }, [confirmDiscard]);
  const [showSummary, setShowSummary] = useState(false);
  // Field messages (L25): shown once Save or Test was tried (the port also on leaving it); the sidecar's refusal of a
  // field shows there until the profile changes.
  const [shown, setShown] = useState<ReadonlySet<FieldKey>>(new Set());
  const [refused, setRefused] = useState<{ field: FieldKey; message: string } | null>(null);
  const [focusField, setFocusField] = useState<Focusable | null>(null);
  const [initial] = useState(() => JSON.stringify(start.p));
  const fid = useId();
  const refs = {
    name: useRef<HTMLInputElement>(null), host: useRef<HTMLInputElement>(null), port: useRef<HTMLInputElement>(null), file: useRef<HTMLButtonElement>(null),
    engine: useRef<HTMLButtonElement>(null), environment: useRef<HTMLInputElement>(null), ack: useRef<HTMLInputElement>(null),
    sshHost: useRef<HTMLInputElement>(null), sshUser: useRef<HTMLInputElement>(null),
  };
  const set = (patch: Partial<Profile>) => {
    setP((x) => ({ ...x, ...patch }));
    setRefused(null);
  };
  const needsSecret = needsSecretFor(p.auth);
  // A saved password is not a token (or the reverse): after switching between them the saved secret is not reused.
  const kindChanged = !!existing && needsSecretFor(existing.auth) && needsSecret && secretKind(existing.auth) !== secretKind(p.auth);
  const sshKindChanged = !!existing?.ssh && !!p.ssh && existing.ssh.auth !== p.ssh.auth;
  // A saved secret is sent only to the server it was saved for: after the server, sign-in, encryption or tunnel changes,
  // the shell must not fill it in (it would reach whatever the edited profile names), so it is typed again or asked for
  // when connecting, and Save removes the old one.
  const endpointChanged = !!existing && endpointKey(existing) !== endpointKey(p);
  // A file or memory database: no server, sign-in, TLS or SSH fields (Phase D0a); environment and Prod guards stay.
  const local = isLocal(p);
  const traits = engineTraits(p.engine);
  const fields = FIELDS[knownEngine(p.engine)];
  const defaultPort = traits.defaultPort;
  const offered = offeredEngines(engineInfo, existing?.engine);
  // Until the user types a name the suggestion is the name; an emptied name falls back to it (its placeholder).
  const withName = (x: Profile): Profile => ({ ...x, name: nameTouched && x.name.trim() ? x.name : suggestedName(x) });
  const name = nameTouched ? p.name : suggestedName(p);
  const errors = profileFieldErrors(withName(p));
  // A file profile without its file has no name yet either: that is said once, at the file.
  const fieldError = (f: FieldKey) => (shown.has(f) && !(f === "name" && errors.file) ? errors[f] : undefined) ?? (refused?.field === f ? refused.message : undefined);
  const errId = (f: string) => `${fid}-${f}-error`;
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

  const transport = transportSummary(p);
  // On Production a weak transport needs an explicit tick where it is chosen; a profile that was already saved so is
  // not asked again until its environment or transport changes.
  const needsTransportAck = (draft: Profile) => envChosen && draft.environment === "prod" && !!transportSummary(draft).risk
    && (!existing || existing.environment !== "prod" || existing.engine !== draft.engine || existing.tlsMode !== draft.tlsMode || existing.trustCert !== draft.trustCert);
  const ackNeeded = needsTransportAck(p);
  useEffect(() => setAck(false), [p.engine, p.environment, p.tlsMode, p.trustCert]);
  const [open, setOpen] = useState(() => ({
    enc: !local && (transport.tone === "warn" || p.tlsMode !== "default" || p.trustCert || !!p.caPath),
    ssh: !!p.ssh,
    time: (p.timeoutS ?? 15) !== 15 || (p.commandTimeoutS ?? 0) !== 0 || !!p.options?.multiSubnetFailover || p.options?.applicationIntent === "readOnly",
  }));
  // A row never folds a warning away: it opens when its state becomes risky.
  useEffect(() => {
    if (transport.tone === "warn" || ackNeeded) setOpen((o) => (o.enc ? o : { ...o, enc: true }));
  }, [transport.tone, ackNeeded]);

  // What a test result belongs to: a later change to anything that reaches the server marks it out of date.
  const connKey = testKey(p, secret, sshSecret);
  const testStale = !!test && test.key !== connKey;
  const dirty = JSON.stringify(p) !== initial || !!secret || !!sshSecret || forget || sshForget || !!connStr || (!start.known && (engineChosen !== !!preset || envChosen));

  /** A tunnel needs its bastion and user (the sidecar would refuse an empty one with a driver message). */
  const sshProblems = (d: Profile): { key: Focusable; text: string }[] => {
    if (isLocal(d) || !d.ssh) return [];
    const list: { key: Focusable; text: string }[] = [];
    if (!d.ssh.host.trim()) list.push({ key: "sshHost", text: "Enter the SSH bastion host." });
    if (!d.ssh.username.trim()) list.push({ key: "sshUser", text: "Enter the SSH user name." });
    return list;
  };

  /** Every problem Save would stop at, in reading order. */
  const problems = (d: Profile): { key: Focusable; text: string }[] => {
    const list: { key: Focusable; text: string }[] = [];
    const errs = profileFieldErrors(d);
    for (const f of ["host", "port", "file"] as FieldKey[]) if (errs[f]) list.push({ key: f, text: errs[f]! });
    list.push(...sshProblems(d));
    if (!envChosen) list.push({ key: "environment", text: "Choose the environment: Local, Dev, Test, Staging or Prod." });
    // A missing file is the reason there is no name yet: it is said once, at the file.
    if (errs.name && !errs.file) list.push({ key: "name", text: errs.name });
    // A pending connection string may have changed the transport since this render. An earlier acknowledgement
    // belongs only to the transport the checkbox displayed, never to newly parsed settings.
    const sameTransport = d.engine === p.engine && d.environment === p.environment && d.tlsMode === p.tlsMode && d.trustCert === p.trustCert;
    if (needsTransportAck(d) && !(ack && sameTransport)) list.push({ key: "ack", text: `Confirm that this Production connection is ${transportSummary(d).risk === "plain" ? "not encrypted" : "not checking the certificate"}.` });
    return list;
  };
  const summary = showSummary ? problems(withName(p)) : [];

  useEffect(() => {
    if (!focusField) return;
    refs[focusField].current?.focus();
    setFocusField(null);
  }, [focusField]); // eslint-disable-line react-hooks/exhaustive-deps

  /** Shows these fields' messages and focuses the first one that has a message (opening its row if folded). */
  const reveal = (fieldsToShow: FieldKey[], bad: Focusable[]) => {
    setShown((cur) => new Set([...cur, ...fieldsToShow]));
    const order: Focusable[] = ["host", "port", "file", "sshHost", "sshUser", "environment", "name", "ack"];
    const first = order.find((f) => bad.includes(f));
    if (!first) return;
    if (first === "ack") setOpen((o) => ({ ...o, enc: true }));
    if (first === "sshHost" || first === "sshUser") setOpen((o) => ({ ...o, ssh: true }));
    setFocusField(first);
  };

  // Settings the string names replace the fields; the rest keep their values. The type it names is the server type.
  const parse = async (text = connStr): Promise<Draft | null> => {
    if (!text.trim()) {
      setTest({ ok: false, text: "Paste a connection string first.", key: connKey });
      return null;
    }
    try {
      const r = await rpc("profile.parseConnectionString", { profile: p, connectionString: text });
      const d: Draft = { p: { ...r.profile, name: nameTouched ? p.name : "" }, secret: r.secret ?? secret, touched: r.secret != null || secretTouched };
      setP(d.p);
      setSecret(d.secret);
      setSecretTouched(d.touched);
      if (r.secret != null) setChangeSecret(true);
      setAnimated(true);
      setEngineChosen(true);
      setUndo(null);
      setParsed({ engine: r.profile.engine, unknown: r.unknown, notApplied: r.notApplied, notes: r.notes ?? [] });
      setConnStr("");
      setPasteOpen(false);
      setPastedIntoHost(false);
      return d;
    } catch (e) {
      setParsed(null);
      setTest({ ok: false, text: (e as Error).message || "Failed", key: connKey });
      return null;
    }
  };

  // Text still in the paste box is read first, so Test and Save never ignore it.
  const draft = async (): Promise<Draft | null> => {
    const d = connStr.trim() ? await parse() : { p, secret, touched: secretTouched };
    return d && { ...d, p: withDefaultHost(withName(d.p)) };
  };

  const fillFromString = async (text = connStr) => {
    setBusyAction("parse");
    setTest(null);
    try {
      await parse(text);
    } finally {
      setBusyAction(null);
    }
  };

  const runTest = async () => {
    if (busy) return;
    setBusyAction("test");
    setTest(null);
    try {
      const d = await draft();
      if (!d) return;
      const errs = profileFieldErrors(d.p);
      const bad = CONNECT_FIELDS.filter((f) => errs[f]);
      if (bad.length) {
        reveal(CONNECT_FIELDS, bad);
        return setTest({ ok: false, text: errs[bad[0]]!, key: connKey });
      }
      const tunnel = sshProblems(d.p);
      if (tunnel.length) {
        reveal([], tunnel.map((x) => x.key));
        return setTest({ ok: false, text: tunnel[0].text, key: connKey });
      }
      const endpointChanged = !!existing && endpointKey(existing) !== endpointKey(d.p);
      const kindChanged = !!existing && needsSecretFor(existing.auth) && needsSecretFor(d.p.auth) && secretKind(existing.auth) !== secretKind(d.p.auth);
      const sshKindChanged = !!existing?.ssh && !!d.p.ssh && existing.ssh.auth !== d.p.ssh.auth;
      // A saved secret is used unless one was typed, or the saved one is of another kind.
      const typed = (d.touched && !!d.secret) || !existing || kindChanged || endpointChanged;
      const sshTyped = (sshSecretTouched && !!sshSecret) || !existing || sshKindChanged || endpointChanged;
      const key = testKey(d.p, d.secret, sshSecret);
      for (let round = 0; round < 2; round++) {
        const r = await rpc("profile.test", {
          profileId: typed && sshTyped ? null : existing!.id, profile: d.p, secret: typed ? d.secret : null, sshSecret: sshTyped ? (sshSecret || (sshKindChanged || endpointChanged ? "" : null)) : null,
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
        setTest(r.ok ? { ok: true, text: `Connected: ${shownVersion(r.serverVersion)} (${Math.round(r.elapsedMs)} ms)${d.p.ssh ? " through the SSH tunnel" : ""}${notes}`, key } : { ok: false, text: r.error || "Failed", key });
        break;
      }
    } catch (e) {
      setTest({ ok: false, text: (e as Error).message || "Failed", key: connKey });
    } finally {
      setBusyAction(null);
    }
  };

  const save = async () => {
    if (busy) return;
    setBusyAction("save");
    try {
      const d = await draft();
      if (!d) return;
      const { p } = d;
      const list = problems(p);
      if (list.length) {
        setShowSummary(true);
        return reveal(FIELD_ORDER, list.map((x) => x.key));
      }
      if (p.passwordRetention === "duration" && (!Number.isInteger(p.passwordRetentionMinutes) || p.passwordRetentionMinutes! < 1 || p.passwordRetentionMinutes! > 525600)) return toast("warn", "Choose a duration from 1 minute to 365 days.");
      if (p.environment === "prod" && transportSummary(p).risk === "plain") toast("warn", "Production profile without encryption.");
      const needs = needsSecretFor(p.auth);
      const sshNeeds = !!p.ssh && p.ssh.auth !== "agent";
      const endpointChanged = !!existing && endpointKey(existing) !== endpointKey(p);
      const kindChanged = !!existing && needsSecretFor(existing.auth) && needs && secretKind(existing.auth) !== secretKind(p.auth);
      const sshKindChanged = !!existing?.ssh && !!p.ssh && existing.ssh.auth !== p.ssh.auth;
      // Typed text replaces the secret; an emptied box keeps it ("Leave it empty to keep the saved one") unless Forget
      // was chosen or the saved one is of another kind (a password is not a token).
      const secretArg = needs ? (d.touched && d.secret ? d.secret : forget || kindChanged || endpointChanged ? "" : null) : existing ? "" : null;
      const sshArg = sshNeeds ? (sshSecretTouched && sshSecret ? sshSecret : sshForget || sshKindChanged || endpointChanged ? "" : null) : "";
      await useProfiles.getState().save(p, secretArg, sshArg);
      onClose();
      if (existing && useConnections.getState().servers[existing.id]) toast("info", "Saved. Reconnect to apply connection changes.");
    } catch (e) {
      // A refusal about one field (the sidecar's invalidProfile with data.field) is shown at that field.
      const created = (e as { savedProfile?: Profile }).savedProfile;
      if (created && !p.id) setP((x) => ({ ...x, id: created.id, createdAt: created.createdAt, updatedAt: created.updatedAt }));
      const field = (e as { data?: { field?: unknown } }).data?.field;
      const message = (e as Error).message;
      if (typeof field === "string" && (FIELD_ORDER as string[]).includes(field)) {
        setRefused({ field: field as FieldKey, message });
        reveal([], [field as FieldKey]);
      } else toast("error", message);
    } finally {
      setBusyAction(null);
    }
  };

  // Esc, Cancel, ✕ and a click outside all ask before typed changes are dropped.
  const tryClose = () => {
    if (busy) return;
    if (dirty) setConfirmDiscard(true);
    else onClose();
  };

  const remove = async () => {
    if (!existing) return;
    if (await confirmDeleteProfile(existing)) onClose();
  };

  const env = ENVS.find((e) => e.id === p.environment) ?? ENVS[1];
  // The pill's text in the environment's hue, fitted to 4.5:1 on its own tint over the dialog's header.
  useThemeKey();
  const tintText = (id: EnvironmentKind) => readableOnTint(`var(--q-env-${id})`, "var(--q-panel)", 0.14) ?? `var(--q-env-${id})`;
  const chipText = (id: EnvironmentKind) => readableOnTint(`var(--q-env-${id})`, "var(--q-panel)", 0.16) ?? `var(--q-env-${id})`;

  // A user name or database already typed is kept; an engine with a default database fills an empty one. An engine on
  // this computer starts at its first location with no file; leaving one returns to a server with its defaults. A port
  // that was the old engine's default follows the new one.
  const pickEngine = (e: Engine) => {
    const t = ENGINE_TRAITS[e];
    const where = locationsOf(engineInfo, e);
    if (!where.includes("server")) {
      setAccess(null);
      set({ engine: e, location: where[0], file: null, auth: t.defaultAuth, host: null, port: null, username: null, databaseDefault: t.defaultDatabase, tlsMode: "default", trustCert: false, caPath: null, ssh: null });
      return;
    }
    const dflt = t.defaultDatabase;
    set({
      engine: e, auth: AUTH[e][0].id, tlsMode: "default", trustCert: false, caPath: null, username: p.username || t.defaultUser, databaseDefault: dflt === null ? p.databaseDefault : (p.databaseDefault ?? dflt),
      port: p.port != null && p.port === traits.defaultPort ? null : p.port,
      ...(local ? { location: null, file: null, host: p.host || "localhost", databaseDefault: dflt } : {}),
    });
  };
  const chooseEngine = (e: Engine) => {
    setPortOffer(null);
    if (!engineChosen || e !== p.engine) {
      setAnimated(true);
    }
    if (!engineChosen) {
      // The first choice starts from that engine's own defaults: nothing was typed into fields that were not shown yet
      // (the placeholder engine's user or database must not carry over); the environment, group and name stay.
      const where = locationsOf(engineInfo, e);
      const fresh = empty(e);
      setEngineChosen(true);
      setAccess(null);
      set({
        ...fresh, environment: p.environment, groupName: p.groupName, name: p.name, color: p.color,
        ...(where.includes("server") ? {} : { location: where[0], file: null, host: null }),
      });
      return;
    }
    if (e === p.engine) return;
    const changes = [
      `sign-in is ${AUTH[e][0].label}`,
      locationsOf(engineInfo, e).includes("server") && !local ? "your host, user and database were kept" : null,
    ].filter(Boolean);
    setUndo({ before: p, text: `Switched to ${ENGINE_TRAITS[e].shortLabel}: ${changes.join("; ")}.` });
    pickEngine(e);
  };

  const pickFile = async () => {
    try {
      const picked = await pickDatabaseFile();
      if (!picked) return;
      set({ location: "file", file: { grantId: picked.grantId, displayName: picked.name } });
      setAccess("readOnly");
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

  const pickEnv = (e: EnvironmentKind) => {
    // Lowering a saved Production profile drops its guards: asked once, here.
    const was = existing?.environment ?? (start as { fromEnv?: EnvironmentKind }).fromEnv;
    if (was === "prod" && p.environment === "prod" && e !== "prod") return setLowerProd(e);
    set({ environment: e });
    setEnvChosen(true);
    setEnvFromGroup(null);
  };

  const pickGroup = (value: string) => {
    const g = groups.find((x) => x.name === value);
    set({ groupName: value || null });
    // A group's default environment fills only an environment nobody chose (or one an earlier group filled).
    if (g?.environment && (!envChosen || envFromGroup)) {
      set({ environment: g.environment });
      setEnvChosen(true);
      setEnvFromGroup(g.name);
    }
  };

  const onHostPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const text = e.clipboardData.getData("text");
    if (!looksLikeConnectionString(text)) return;
    // Offered, never applied by itself: the text waits masked in the connection-string box.
    e.preventDefault();
    setConnStr(text);
    setPasteOpen(true);
    setPastedIntoHost(true);
  };
  const onHostBlur = () => {
    const split = p.host ? splitHostPort(p.host, p.engine) : null;
    if (!split) return;
    set({ host: split.host, port: split.port });
    setNote(`Moved ${split.port} from Host into Port.`);
  };
  const onPortBlur = () => {
    if (p.port != null) setShown((cur) => new Set([...cur, "port"]));
    const other = p.port == null || p.port === defaultPort ? undefined : offered.find((x) => x !== p.engine && ENGINE_TRAITS[x].defaultPort === p.port);
    setPortOffer(other ?? null);
  };

  // PostgreSQL and MySQL take a comma-separated failover list in Host (SQL Server's `host,port` is one server).
  const hostList = !local && engineChosen && knownEngine(p.engine) !== "mssql" ? (p.host ?? "").split(",").map((h) => h.trim()).filter(Boolean) : [];
  const prodWord = !local && engineChosen && p.environment !== "prod" ? looksLikeProduction(p.host, p.databaseDefault) : null;
  const prodKey = `${p.host}|${p.databaseDefault}`;
  const recent = useMemo(() => {
    const seen: Engine[] = [];
    for (const x of [...allProfiles].sort((a, b) => b.updatedAt - a.updatedAt)) if (!seen.includes(x.engine) && offered.includes(x.engine)) seen.push(x.engine);
    return seen.slice(0, 3);
  }, [allProfiles, offered.join()]); // eslint-disable-line react-hooks/exhaustive-deps

  const secretSaved = !!existing && savedSecret !== false && !kindChanged && !endpointChanged && !changeSecret && !secretTouched;
  const pasteBox = pasteOpen && (
    <div className="q-profile-reveal space-y-1">
      {pastedIntoHost && <p className="text-[11px] text-muted">That looks like a connection string, so it went here instead of into Host.</p>}
      <div className="flex items-center gap-2">
        <SecretInput
          className={`${INPUT} q-mono flex-1`} type="password" aria-label="Connection string" autoFocus autoComplete="off" spellCheck={false} data-no-enter-save
          value={connStr} placeholder={fields.connectionStringExample}
          onValue={setConnStr}
          onPaste={(e) => {
            const text = e.clipboardData.getData("text");
            if (!text.trim()) return;
            e.preventDefault();
            setConnStr(text);
            void fillFromString(text);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.nativeEvent.isComposing) {
              e.preventDefault();
              void fillFromString();
            }
          }}
        />
        <button type="button" className="q-btn shrink-0" onClick={() => void fillFromString()} disabled={busy || !connStr.trim()}>Fill fields</button>
        <button type="button" className="q-btn shrink-0" onClick={() => { setConnStr(""); setPasteOpen(false); setPastedIntoHost(false); }}>Cancel</button>
      </div>
      <p className="text-[10.5px] text-muted">Read once by the sidecar and cleared: its settings become the fields, the string is never stored.</p>
    </div>
  );
  const pasteLink = !pasteOpen && (
    <button type="button" className="text-[11.5px] text-accent hover:underline" onClick={() => setPasteOpen(true)}>Paste a connection string…</button>
  );

  return (
    <Modal
      title={
        <span className="flex items-center gap-2 min-w-0">
          <EnvPill env={envChosen ? p.environment : null} text={envChosen ? tintText(p.environment) : undefined} />
          <span className="truncate">{existing ? `Edit profile — ${existing.name}` : "New connection profile"}</span>
        </span>
      }
      onClose={tryClose}
      onEscape={() => {
        if (!confirmDiscard) return false;
        setConfirmDiscard(false);
        return true;
      }}
      onEnter={() => {
        if (!engineChosen) return;
        const a = document.activeElement as HTMLElement | null;
        if (a?.closest("[data-no-enter-save]") || (a instanceof HTMLInputElement && a.list) || confirmDiscard) return;
        void save();
      }}
      width={720}
      bodyClassName="flex flex-col min-h-0 flex-1"
      footer={
        confirmDiscard ? (
          <>
            <span className="mr-auto self-center text-[12px] text-fg font-semibold">Discard your changes to this profile?</span>
            <button ref={keepRef} className="q-btn q-btn-primary" onClick={() => setConfirmDiscard(false)}>Keep editing</button>
            <button className="q-btn text-err" onClick={onClose}>Discard</button>
          </>
        ) : engineChosen ? (
          <>
            {existing && <button className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}q-btn text-err mr-auto`} onClick={() => void remove()}>Delete</button>}
            <button className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}q-btn q-btn-run ${existing ? "" : "mr-auto"}`} onClick={() => void runTest()} disabled={busy} aria-busy={busyAction === "test" || undefined}><BusyButtonContent busy={busyAction === "test"} label="Test connection" busyLabel="Testing…" /></button>
            <button className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}q-btn`} onClick={tryClose}>Cancel</button>
            <button className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}q-btn q-btn-primary`} onClick={() => void save()} disabled={busy} aria-busy={busyAction === "save" || undefined}><BusyButtonContent busy={busyAction === "save"} label="Save" busyLabel="Saving…" /></button>
          </>
        ) : undefined
      }
    >
      {envChosen && p.environment === "prod" && <div aria-hidden className="q-profile-reveal h-[3px] shrink-0" style={{ background: "var(--q-env-prod)" }} />}
      <div className="flex-1 min-h-0 overflow-auto px-5 py-3.5 space-y-2.5">
        <SmoothExpand className="space-y-2.5">
          {summary.length > 0 && (
            <div role="alert" className="q-profile-reveal flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 rounded-md border border-err/40 bg-err/10 text-err text-[11.5px]">
              <span className="font-semibold">{summary.length === 1 ? "1 thing needs" : `${summary.length} things need`} attention:</span>
              {summary.map((x) => (
                <button key={x.key} type="button" className="underline hover:no-underline text-left" onClick={() => reveal([], [x.key])}>{x.text}</button>
              ))}
            </div>
          )}
          {undo && (
            <Note onDismiss={() => setUndo(null)} action={<button type="button" className="text-accent hover:underline" onClick={() => { setP(undo.before); setUndo(null); }}>Undo</button>}>
              {undo.text}
            </Note>
          )}
          {parsed && (
            <Note>
              <div className="text-fg">Read as {ENGINE_TRAITS[knownEngine(parsed.engine)].label}. Settings the string names replaced the fields; the rest kept their values.</div>
              {parsed.unknown.length > 0 && <div className="text-warn">Not recognised: {parsed.unknown.map(escapeForDisplay).join(", ")}</div>}
              {parsed.notApplied.length > 0 && <div>Not applied (Querithm sets or does not offer these): {parsed.notApplied.map(escapeForDisplay).join(", ")}</div>}
              {/* M3: a TLS setting the string names that is weaker than the engine's default */}
              {parsed.notes.map((n) => <div key={n} className="text-warn">{escapeForDisplay(n)}</div>)}
            </Note>
          )}

          <Line label="Server type">
            <div className="space-y-1.5 min-w-0">
              <EngineSelect ref={refs.engine} value={engineChosen ? p.engine : null} options={offered} autoFocus={!engineChosen} onChange={chooseEngine} />
              {!engineChosen && recent.length > 0 && (
                <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-muted">
                  Recent:
                  {recent.map((e) => (
                    <button key={e} type="button" className="inline-flex items-center gap-1.5 h-6 px-2 rounded-full border border-line bg-panel-2 text-fg hover:bg-hover" onClick={() => chooseEngine(e)}>
                      <EngineMark engine={e} logo={LOGOS[e]} size={13} />
                      {ENGINE_TRAITS[e].shortLabel}
                    </button>
                  ))}
                </div>
              )}
            </div>
          </Line>
        </SmoothExpand>

        <SmoothExpand className="space-y-2.5">
          {!engineChosen ? (
            <div className="pl-[108px]">
              <p className="text-[11.5px] text-muted">Choose a server type first: its connection fields appear here.</p>
            </div>
          ) : local ? (
            <div key={p.engine} className={`${animated ? "q-profile-reveal " : ""}space-y-2.5`}>
              <Line label="Location">
                <div role="radiogroup" aria-label="Location" className="flex items-center gap-4 h-8">
                  {locationsOf(engineInfo, p.engine).filter((l) => l !== "server").map((l) => (
                    <label key={l} className="inline-flex items-center gap-1.5 text-fg cursor-pointer">
                      <input type="radio" name={`${fid}-loc`} checked={locationOf(p) === l} onChange={() => { setAccess(null); set({ location: l, file: l === "file" ? p.file : null }); }} />
                      {LOCATION_LABEL[l]}
                    </label>
                  ))}
                </div>
              </Line>
              {locationOf(p) === "file" ? (
                // A div, not Row's <label>: a label would name the buttons after the whole row and click the first one on any press.
                <div key="file" className="q-profile-reveal grid grid-cols-[96px_1fr] items-center gap-x-3">
                  <span className="text-[11.5px] text-muted">File</span>
                  <div className="flex items-center gap-2 min-w-0">
                    <span className={`flex-1 min-w-0 truncate q-mono text-[11.5px] ${p.file ? "text-fg" : "text-muted"}`} data-testid="picked-file">
                      {p.file ? (p.file.displayName ?? "Picked file") : "No file picked"}
                    </span>
                    {access && <Badge tone={access === "readWrite" ? "warn" : "ok"}>{access === "readWrite" ? "Writes allowed" : "Read-only"}</Badge>}
                    <button type="button" ref={refs.file} className="q-btn shrink-0" onClick={() => void pickFile()} aria-describedby={fieldError("file") ? errId("file") : undefined}>{p.file ? "Re-pick file…" : "Pick file…"}</button>
                    {p.file && <button type="button" className="q-btn shrink-0" onClick={() => void allowWrites()}>Allow writes…</button>}
                  </div>
                  {errorLine("file", "col-start-2")}
                  <span className="col-start-2 mt-1 text-[10.5px] text-muted">Querithm reads only the file you pick here, read-only until you allow writes.</span>
                </div>
              ) : (
                <p key="mem" className="q-profile-reveal pl-[108px] text-[11px] text-muted leading-snug">Each tab gets its own empty database, and it is gone when the tab closes. Nothing is written to disk.</p>
              )}
            </div>
          ) : (
            <div key={p.engine} className={`${animated ? "q-profile-reveal " : ""}space-y-2.5`}>
              <div className="grid grid-cols-[96px_1fr_auto_84px] items-center gap-x-3">
                <label htmlFor={`${fid}-host`} className="text-[11.5px] text-muted">Host</label>
                <input
                  id={`${fid}-host`} ref={refs.host} className={`${INPUT} q-mono`} value={p.host ?? ""} onChange={(e) => { set({ host: e.target.value }); setNote(null); }} placeholder="localhost"
                  title="Empty: localhost" autoComplete="off" spellCheck={false} onPaste={onHostPaste} onBlur={onHostBlur}
                  {...invalidProps("host", fields.instanceNames ? `${fid}-host-hint` : undefined)}
                />
                <label htmlFor={`${fid}-port`} className="text-[11.5px] text-muted">Port</label>
                <input
                  id={`${fid}-port`} ref={refs.port} className={`${INPUT} q-mono`} type="number" min={1} max={65535} step={1} inputMode="numeric"
                  placeholder={defaultPort == null ? "" : String(defaultPort)} value={p.port == null || Number.isNaN(p.port) ? "" : p.port}
                  // A number the field cannot read ("1e" while typing) is kept as invalid, never silently the default.
                  onChange={(e) => set({ port: e.target.value ? Number(e.target.value) : e.target.validity?.badInput ? NaN : null })}
                  onWheel={(e) => (e.target as HTMLInputElement).blur()}
                  onBlur={onPortBlur}
                  {...invalidProps("port")}
                />
                {hostList.length > 1 && (
                  <span className="col-start-2 col-span-3 mt-1 text-[10.5px] text-warn" aria-live="polite">
                    This profile names {hostList.length} servers; the driver tries them in order and may connect to the last one, {escapeForDisplay(hostList[hostList.length - 1])}.
                  </span>
                )}
                {fields.instanceNames && <span id={`${fid}-host-hint`} className="col-start-2 col-span-3 mt-1 text-[10.5px] text-muted">Server name, host\instance, or tcp:host</span>}
                {note && <span className="col-start-2 col-span-3 mt-1 text-[10.5px] text-muted" aria-live="polite">{note}</span>}
                {errorLine("host", "col-start-2 col-span-3")}
                {errorLine("port", "col-start-2 col-span-3")}
              </div>
              {portOffer && (
                <Offer>
                  <span>{p.port} is the usual {ENGINE_TRAITS[portOffer].shortLabel} port.</span>
                  <button type="button" className="q-btn h-6 px-2" onClick={() => { const port = p.port; chooseEngine(portOffer); set({ port }); }}>Switch to {ENGINE_TRAITS[portOffer].shortLabel}</button>
                  <button type="button" className="text-accent hover:underline" onClick={() => setPortOffer(null)}>Keep {traits.shortLabel}</button>
                </Offer>
              )}
              <Line label="Database" htmlFor={`${fid}-db`}>
                <input id={`${fid}-db`} className={`${INPUT} q-mono`} value={p.databaseDefault ?? ""} onChange={(e) => set({ databaseDefault: e.target.value || null })} placeholder={traits.defaultDatabase ?? "(login default)"} />
              </Line>
              <div className="pl-[108px]">{pasteBox || pasteLink}</div>
            </div>
          )}
        </SmoothExpand>

        {engineChosen && (
          <SmoothExpand animateMount={mounted} className={`${animated ? "q-profile-reveal q-profile-reveal-1 " : ""}space-y-2.5`}>
            <div className="border-t border-line" />

            <EnvironmentPicker
              fid={fid} value={envChosen ? p.environment : null} onChange={pickEnv} firstRef={refs.environment} tint={chipText}
              invalid={summary.some((x) => x.key === "environment")} errorId={errId("environment")}
              after={
                <>
                  {envFromGroup && <span className="q-profile-reveal block text-[10.5px] text-muted" aria-live="polite">Set from group “{envFromGroup}”. Change it if that’s wrong.</span>}
                  {envChosen && p.environment === "prod" && (
                    <div className="q-profile-reveal border-l-[3px] pl-2.5 py-1 text-[11px] text-fg rounded-r" style={{ borderColor: "var(--q-env-prod)", background: "color-mix(in srgb, var(--q-env-prod) 9%, transparent)" }}>
                      {env.hint}
                    </div>
                  )}
                  {prodWord && prodDismissed !== prodKey && (
                    <Offer>
                      <span>The address contains “{prodWord}”. Is this Production?</span>
                      <button type="button" className="q-btn h-6 px-2" onClick={() => pickEnv("prod")}>Mark as Production</button>
                      <button type="button" className="text-accent hover:underline" onClick={() => setProdDismissed(prodKey)}>It isn’t</button>
                    </Offer>
                  )}
                  {lowerProd && (
                    <Offer warn>
                      <span className="text-fg">Lower this profile from Production to {envLabel[lowerProd]}? Its tabs lose the Production guards.</span>
                      <button type="button" className="q-btn h-6 px-2" onClick={() => { set({ environment: lowerProd }); setEnvChosen(true); setEnvFromGroup(null); setLowerProd(null); }}>Lower it</button>
                      <button type="button" className="text-accent hover:underline" onClick={() => setLowerProd(null)}>Keep Production</button>
                    </Offer>
                  )}
                </>
              }
            />
          </SmoothExpand>
        )}

        {engineChosen && !local && (
          <SmoothExpand animateMount={mounted} className={`${animated ? "q-profile-reveal q-profile-reveal-2 " : ""}space-y-2.5`}>
            <div className="border-t border-line" />
            <div className="grid grid-cols-[96px_minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-x-3 gap-y-2.5">
              <label htmlFor={`${fid}-auth`} className="text-[11.5px] text-muted">Sign in</label>
              {AUTH[p.engine].length > 1 ? (
                <select id={`${fid}-auth`} className={INPUT} value={p.auth} onChange={(e) => set({ auth: e.target.value as AuthKind })}>
                  {AUTH[p.engine].map((a) => (
                    <option key={a.id} value={a.id}>{a.label}</option>
                  ))}
                </select>
              ) : (
                <span id={`${fid}-auth`} className="h-8 flex items-center text-fg">{AUTH[p.engine][0].label}</span>
              )}
              {needsSecret ? (
                <>
                  <span className="text-[11.5px] text-muted">Remember</span>
                  <PasswordRetention profile={p} onChange={set} compact />
                </>
              ) : <span className="col-span-2" />}
              {p.auth !== "windows" && p.auth !== "entraDeviceCode" ? (
                <>
                  <label htmlFor={`${fid}-user`} className="text-[11.5px] text-muted">{p.auth === "entraInteractive" ? "User (optional hint)" : "User name"}</label>
                  <input id={`${fid}-user`} className={`${INPUT} q-mono`} value={p.username ?? ""} onChange={(e) => set({ username: e.target.value })} autoComplete="off" spellCheck={false} />
                </>
              ) : (
                <span className="col-start-2 text-[11px] text-muted">{p.auth === "windows" ? "Signs in as your Windows account." : "Signs in with a code shown when connecting."}</span>
              )}
              {needsSecret ? (
                <>
                  {secretSaved
                    ? <span className="text-[11.5px] text-muted">{p.auth === "entraToken" ? "Access token" : "Password"}</span>
                    : <label htmlFor={`${fid}-secret`} className="text-[11.5px] text-muted">{p.auth === "entraToken" ? "Access token" : "Password"}</label>}
                  {secretSaved ? (
                    <div className="h-8 flex items-center gap-2.5 min-w-0 text-[11.5px]">
                      {forget ? (
                        <>
                          <span className="text-warn">Forgotten when you save.</span>
                          <button type="button" className="text-accent hover:underline" onClick={() => setForget(false)}>Undo</button>
                        </>
                      ) : (
                        <>
                          <span className="text-ok truncate">Saved in the OS keyring</span>
                          <button type="button" className="text-accent hover:underline" onClick={() => setChangeSecret(true)}>Change</button>
                          <button type="button" className="text-accent hover:underline" onClick={() => setForget(true)}>Forget</button>
                        </>
                      )}
                    </div>
                  ) : (
                    <div className="relative">
                      <SecretInput
                        id={`${fid}-secret`} className={INPUT} style={{ paddingRight: 34 }} type={showSecret ? "text" : "password"} autoComplete="new-password" spellCheck={false}
                        value={secret} autoFocus={changeSecret && !secretTouched}
                        onValue={(v) => {
                          setSecret(v);
                          setSecretTouched(true);
                          setForget(false);
                        }}
                      />
                      <button
                        type="button"
                        className="absolute right-1 top-1/2 -translate-y-1/2 w-7 h-6 flex items-center justify-center rounded text-muted hover:text-fg hover:bg-hover"
                        title={showSecret ? "Hide" : "Show"}
                        aria-label={`${showSecret ? "Hide" : "Show"} ${p.auth === "entraToken" ? "token" : "password"}`}
                        aria-pressed={showSecret}
                        onClick={() => setShowSecret((v) => !v)}
                      >
                        <Icon d={showSecret ? ICON.eyeOff : ICON.eye} size={15} />
                      </button>
                    </div>
                  )}
                  {endpointChanged && !kindChanged && savedSecret !== false && needsSecretFor(existing!.auth) && (
                    <span className="col-start-4 text-[10.5px] text-warn">
                      The connection settings changed, so the saved {secretKind(existing!.auth) === "token" ? "token" : "password"} is not sent to them: type it again, or leave it empty to be asked when connecting.
                    </span>
                  )}
                  {kindChanged && (
                    <span className="col-start-4 text-[10.5px] text-warn">
                      The saved {secretKind(existing!.auth) === "token" ? "token" : "password"} is not used as {p.auth === "entraToken" ? "an access token" : "a password"}: enter one, or leave it empty to be asked when connecting.
                    </span>
                  )}
                  {existing && savedSecret !== false && changeSecret && !kindChanged && (
                    <span className="col-start-4 text-[10.5px] text-muted">
                      Leave it empty to keep the saved one. <button type="button" className="text-accent hover:underline" onClick={() => { setChangeSecret(false); setSecret(""); setSecretTouched(false); }}>Keep saved</button>
                    </span>
                  )}
                </>
              ) : <span className="col-span-2" />}
            </div>
            {existing && needsSecretFor(existing.auth) && !needsSecret && savedSecret !== false && (
              <p className="pl-[108px] text-[10.5px] text-warn">The saved {secretKind(existing.auth) === "token" ? "token" : "password"} is removed when you save, because this sign-in needs none.</p>
            )}
          </SmoothExpand>
        )}

        {engineChosen && (
          <>
            <SmoothExpand animateMount={mounted} className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}space-y-2.5`}>
              <div className="border-t border-line" />
              <div className="grid grid-cols-[96px_minmax(0,1fr)_auto_minmax(0,200px)] items-center gap-x-3">
                <label htmlFor={`${fid}-name`} className="text-[11.5px] text-muted">Name</label>
                <input
                  id={`${fid}-name`} ref={refs.name} className={`${INPUT} ${nameTouched ? "" : "italic text-muted"}`} aria-required value={name} placeholder={suggestedName(p)}
                  onChange={(e) => { set({ name: e.target.value }); setNameTouched(true); }}
                  aria-description={nameTouched ? undefined : "Suggested from the connection; type to change it."}
                  {...invalidProps("name")}
                />
                <label htmlFor={`${fid}-group`} className="text-[11.5px] text-muted">Group</label>
                <input id={`${fid}-group`} className={INPUT} list={`${fid}-groups`} value={p.groupName ?? ""} placeholder="None" onChange={(e) => pickGroup(e.target.value)} />
                <datalist id={`${fid}-groups`}>
                  {groups.map((g) => (
                    <option key={g.name} value={g.name} />
                  ))}
                </datalist>
                {errorLine("name", "col-start-2 col-span-3")}
              </div>
            </SmoothExpand>

            <div className={`${animated ? "q-profile-reveal q-profile-reveal-3 " : ""}rounded-md border border-line bg-panel overflow-hidden`}>
              {!local && (
                <Disclosure id={`${fid}-enc`} title="Encryption" summary={transport.text} tone={ackNeeded ? "warn" : transport.tone} open={open.enc} onToggle={() => setOpen((o) => ({ ...o, enc: !o.enc }))}>
                  <Row label="Mode">
                    <select className={INPUT} value={p.tlsMode} onChange={(e) => set({ tlsMode: e.target.value as TlsMode })} aria-label="Encryption">
                      {TLS[p.engine].map((t) => (
                        <option key={t.id} value={t.id}>{t.label}</option>
                      ))}
                    </select>
                  </Row>
                  {fields.trustServerCertificate ? (
                    <Row label="Certificate" hint={p.trustCert ? <span className="q-profile-reveal text-warn">The server certificate is not validated: anyone on the network path could pose as this server. Use only for development servers.</span> : undefined}>
                      <Check checked={p.trustCert} onChange={(v) => set({ trustCert: v })}>Trust server certificate</Check>
                    </Row>
                  ) : (p.tlsMode === "verifyCa" || p.tlsMode === "verifyFull" || (knownEngine(p.engine) === "mySql" && p.tlsMode === "default") || !!p.caPath) && (
                    <Row label="CA certificate" hint="Used by Verify CA / Verify full.">
                      <input className={`${INPUT} q-mono`} value={p.caPath ?? ""} onChange={(e) => set({ caPath: e.target.value || null })} placeholder="/etc/ssl/certs/root.crt" />
                    </Row>
                  )}
                  {transport.risk === "plain" && <p className="q-profile-reveal pl-[108px] text-[10.5px] text-warn">The password and every result cross the network as plain text.</p>}
                  {ackNeeded && (
                    <div className="q-profile-reveal pl-[108px]">
                      <label className="inline-flex items-center gap-2 text-[11.5px] font-semibold cursor-pointer" style={{ color: "var(--q-env-prod)" }}>
                        <input ref={refs.ack} type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} />
                        I accept that this Production connection {transport.risk === "plain" ? "is not encrypted" : "does not check the server certificate"}
                      </label>
                    </div>
                  )}
                </Disclosure>
              )}
              {!local && (
                <SshSection
                  id={`${fid}-ssh`} open={open.ssh} onToggle={() => setOpen((o) => ({ ...o, ssh: !o.ssh }))}
                  ssh={p.ssh ?? null}
                  onChange={(ssh) => set({ ssh })}
                  secret={sshSecret}
                  onSecret={(v) => {
                    setSshSecret(v);
                    setSshSecretTouched(true);
                    setSshForget(false);
                  }}
                  saved={!!existing?.ssh && !sshSecretTouched && sshSaved !== false && !sshKindChanged && !endpointChanged}
                  hostRef={refs.sshHost} userRef={refs.sshUser}
                  forget={sshForget}
                  onForget={existing?.ssh && sshSaved !== false ? setSshForget : undefined}
                />
              )}
              <Disclosure
                id={`${fid}-time`} title={local ? "Timeout" : "Timeouts"} open={open.time} onToggle={() => setOpen((o) => ({ ...o, time: !o.time }))}
                summary={[
                  !local ? `Connect ${p.timeoutS ?? 15} s` : null,
                  `queries ${p.commandTimeoutS ? `${p.commandTimeoutS} s` : "no limit"}`,
                  p.options?.multiSubnetFailover === "true" ? "multi-subnet failover" : null,
                  p.options?.applicationIntent === "readOnly" ? "read-only intent" : null,
                ].filter(Boolean).join(" · ")}
              >
                {!local && (
                  <Row label="Connect timeout" hint="Seconds">
                    <input className={`${INPUT} max-w-[120px]`} type="number" min={0} max={600} value={p.timeoutS ?? 15} onChange={(e) => set({ timeoutS: Number(e.target.value) })} />
                  </Row>
                )}
                <Row label="Command timeout" hint="Seconds. 0 = no limit">
                  <input className={`${INPUT} max-w-[120px]`} type="number" min={0} max={86400} value={p.commandTimeoutS ?? 0} onChange={(e) => set({ commandTimeoutS: Number(e.target.value) })} />
                </Row>
                {fields.multiSubnetFailover && !local && (
                  <Row label="Failover">
                    <Check checked={p.options?.multiSubnetFailover === "true"} onChange={(v) => set({ options: { ...(p.options ?? {}), multiSubnetFailover: String(v) } })}>Multi-subnet failover</Check>
                  </Row>
                )}
                {fields.applicationIntent && !local && (
                  <Row label="Intent">
                    <Check checked={p.options?.applicationIntent === "readOnly"} onChange={(v) => set({ options: { ...(p.options ?? {}), applicationIntent: v ? "readOnly" : "readWrite" } })}>Read-only intent</Check>
                  </Row>
                )}
              </Disclosure>
            </div>
          </>
        )}
      </div>

      {/* Pinned under the form so a test result is visible without scrolling. */}
      {engineChosen && (
        <SmoothExpand animateMount={mounted} role="status" aria-live="polite" className={`${animated ? "q-profile-reveal q-profile-reveal-2 " : ""}shrink-0 border-t border-line bg-panel px-5 py-2`}>
          {test ? (
            <div className={`q-profile-reveal flex items-start gap-2.5 px-2.5 py-1.5 rounded-md border text-[11.5px] ${testStale ? "border-line text-muted" : test.ok ? "border-ok/40 bg-ok/10 text-ok" : "border-err/40 bg-err/10 text-err"}`}>
              <span className="mt-px shrink-0"><Icon d={test.ok ? ICON.circleCheck : ICON.circleAlert} size={15} /></span>
              <div className="flex-1 min-w-0 max-h-[60px] overflow-auto select-text leading-snug">
                {test.text}
                {testStale && <span className="block text-[10.5px]">Settings changed since this test. Test again.</span>}
              </div>
              <button type="button" className="shrink-0 w-5 h-5 flex items-center justify-center rounded opacity-70 hover:opacity-100 hover:bg-black/10" aria-label="Dismiss" title="Dismiss" onClick={() => setTest(null)}>
                <Icon d={ICON.x} size={13} />
              </button>
            </div>
          ) : (
            <span className="text-[11px] text-muted">Not tested yet. You can save without testing.</span>
          )}
        </SmoothExpand>
      )}
    </Modal>
  );
}

const INPUT = "q-input w-full h-8";

const SSH_AUTH: { id: SshAuthKind; label: string }[] = [
  { id: "privateKey", label: "Key file" },
  { id: "password", label: "Password" },
];

/**
 * SSH tunnel settings: the bastion, the user and how to sign in. The server's host and port above are as the bastion
 * sees it. The password / key passphrase goes to the OS keyring; Forget waits for Save.
 */
function SshSection({ id, open, onToggle, ssh, onChange, secret, onSecret, saved, forget, onForget, hostRef, userRef }: {
  id: string; open: boolean; onToggle: () => void; hostRef?: RefObject<HTMLInputElement | null>; userRef?: RefObject<HTMLInputElement | null>;
  ssh: SshSettings | null; onChange: (ssh: SshSettings | null) => void; secret: string; onSecret: (v: string) => void; saved: boolean; forget: boolean; onForget?: (v: boolean) => void;
}) {
  const on = !!ssh;
  const set = (patch: Partial<SshSettings>) => onChange({ ...(ssh ?? { host: "", port: 22, username: "", auth: "privateKey", keyPath: null }), ...patch });
  const browse = async () => {
    const { open } = await import("@tauri-apps/plugin-dialog");
    const path = await open({ multiple: false, title: "SSH private key" });
    if (typeof path === "string") set({ keyPath: path });
  };
  // An older profile may hold the agent sign-in, which is not supported yet: it stays listed for it, with the warning.
  const auths = ssh?.auth === "agent" ? [...SSH_AUTH, { id: "agent" as SshAuthKind, label: "SSH agent (not supported yet)" }] : SSH_AUTH;
  return (
    <Disclosure id={id} title="SSH tunnel" open={open} onToggle={onToggle} summary={on ? `Through ${ssh!.host || "bastion"}` : "Off"} tone={on ? "ok" : "muted"}>
      <Row label="Tunnel">
        <Check checked={on} onChange={(v) => onChange(v ? { host: "", port: 22, username: "", auth: "privateKey", keyPath: null } : null)}>Connect through an SSH bastion</Check>
      </Row>
      {on && (
        <div className="q-profile-reveal space-y-2.5">
          <Row label="Bastion" hint="With a tunnel, Host above is the address as the bastion sees it (e.g. db.internal or localhost).">
            <div className="flex gap-2">
              <input ref={hostRef} className={INPUT} value={ssh!.host} placeholder="bastion.example.com" onChange={(e) => set({ host: e.target.value })} aria-required />
              <input className={`${INPUT} max-w-[90px]`} type="number" value={ssh!.port ?? 22} onChange={(e) => set({ port: Number(e.target.value) || 22 })} aria-label="SSH port" />
            </div>
          </Row>
          <Row label="User">
            <input ref={userRef} className={INPUT} value={ssh!.username} placeholder="deploy" onChange={(e) => set({ username: e.target.value })} aria-required />
          </Row>
          <Row label="Sign in with" hint={ssh!.auth === "agent" ? <span className="text-warn">SSH agent sign-in is not supported yet; choose a key file or a password.</span> : undefined}>
            <select className={INPUT} value={ssh!.auth} onChange={(e) => set({ auth: e.target.value as SshAuthKind })}>
              {auths.map((a) => <option key={a.id} value={a.id}>{a.label}</option>)}
            </select>
          </Row>
          {ssh!.auth === "privateKey" && (
            <div className="q-profile-reveal">
              <Row label="Key file" hint="OpenSSH, PEM or PuTTY format. The path is saved; the key is read when connecting.">
                <div className="flex gap-2">
                  <input className={INPUT} value={ssh!.keyPath ?? ""} placeholder="/home/me/.ssh/id_ed25519" onChange={(e) => set({ keyPath: e.target.value || null })} />
                  {isTauri && <button type="button" className="q-btn" onClick={() => void browse()}>Browse…</button>}
                </div>
              </Row>
            </div>
          )}
          {ssh!.auth !== "agent" && (
            <Row
              label={ssh!.auth === "password" ? "Password" : "Passphrase"}
              hint={forget ? <span className="text-warn">The saved one is forgotten when you save.</span> : saved ? "Leave empty to keep the one saved in the OS keyring." : ssh!.auth === "privateKey" ? "Only if the key is protected." : undefined}
            >
              <div className="flex gap-2 items-center">
                <SecretInput className={INPUT} type="password" value={secret} autoComplete="off" onValue={onSecret} />
                {onForget && saved && <button type="button" className="q-btn shrink-0" onClick={() => onForget(!forget)}>{forget ? "Undo forget" : "Forget saved"}</button>}
              </div>
            </Row>
          )}
        </div>
      )}
    </Disclosure>
  );
}

/** Bundled engine logos; null draws Querithm's own engine mark (EngineLogo) instead. */
const LOGOS: Record<Engine, string | null> = { mssql: mssqlLogo, pg: pgLogo, sqlite: null, mySql: null, duckDb: null };

function EngineMark({ engine, logo, size }: { engine: Engine; logo: string | null; size: number }) {
  return logo ? <img src={logo} alt="" style={{ width: size, height: size }} className="shrink-0" draggable={false} /> : <EngineLogo engine={engine} size={size} decorative />;
}

// Ordered along the usual promotion path; only Prod adds guards (§6).
const ENVS: { id: EnvironmentKind; label: string; hint: string }[] = [
  { id: "local", label: "Local", hint: "Your own machine or a container. No extra guards." },
  { id: "dev", label: "Dev", hint: "Shared development server. No extra guards." },
  { id: "test", label: "Test", hint: "Test and QA data. No extra guards." },
  { id: "staging", label: "Staging", hint: "Pre-production mirror. No extra guards." },
  { id: "prod", label: "Prod", hint: "Production: tabs and the status bar are painted, and Querithm asks before the first run and before destructive statements." },
];

/** The header's environment pill; a dashed "No environment" until one is chosen. */
function EnvPill({ env, text }: { env: EnvironmentKind | null; text?: string }) {
  if (!env) return <span className="shrink-0 inline-flex items-center h-5 px-2 rounded-full text-[9.5px] font-bold tracking-wider uppercase border border-dashed border-line-strong text-muted">No environment</span>;
  const v = `var(--q-env-${env})`;
  return (
    <span
      className="shrink-0 inline-flex items-center gap-1.5 h-5 px-2 rounded-full text-[9.5px] font-bold tracking-wider uppercase"
      style={{ color: text, background: `color-mix(in srgb, ${v} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${v} 35%, transparent)` }}
    >
      <span className="w-1.5 h-1.5 rounded-full" style={{ background: v }} />
      {envLabel[env]}
    </span>
  );
}

/**
 * The five environments as one radio group, all visible: nothing is chosen until the user picks (a hidden default was
 * how Production got saved as Dev), and a mouse wheel cannot change it the way it changes a focused select.
 */
function EnvironmentPicker({ fid, value, onChange, firstRef, tint, invalid, errorId, after }: {
  fid: string; value: EnvironmentKind | null; onChange: (e: EnvironmentKind) => void; firstRef: RefObject<HTMLInputElement | null>;
  tint: (e: EnvironmentKind) => string; invalid: boolean; errorId: string; after: ReactNode;
}) {
  return (
    <div className="grid grid-cols-[96px_1fr] items-start gap-x-3">
      <span id={`${fid}-env-label`} className="text-[11.5px] text-muted h-8 flex items-center">Environment</span>
      <div className="space-y-1.5 min-w-0">
        <div
          role="radiogroup" aria-labelledby={`${fid}-env-label`} aria-required="true" aria-invalid={invalid || undefined} aria-describedby={invalid ? errorId : undefined}
          className={`inline-flex flex-wrap rounded-md border overflow-hidden ${invalid ? "border-err" : "border-line"}`}
        >
          {ENVS.map((e, i) => {
            const on = value === e.id;
            const v = `var(--q-env-${e.id})`;
            return (
              <label
                key={e.id}
                className={`relative inline-flex items-center gap-1.5 h-8 px-3 cursor-pointer text-[12px] border-line focus-within:outline focus-within:outline-2 focus-within:outline-accent focus-within:-outline-offset-2 ${i ? "border-l" : ""} ${on ? "font-semibold" : "text-fg hover:bg-hover"}`}
                style={on ? { color: tint(e.id), background: `color-mix(in srgb, ${v} 16%, transparent)`, boxShadow: `inset 0 -2px 0 ${v}` } : undefined}
              >
                <input
                  ref={i === 0 ? firstRef : undefined} type="radio" name={`${fid}-env`} value={e.id} checked={on} onChange={() => onChange(e.id)}
                  className="absolute opacity-0 w-0 h-0" aria-label={e.id === "prod" ? "Prod (Production)" : e.label}
                />
                <span aria-hidden className="w-2 h-2 rounded-full" style={{ background: v }} />
                {e.label}
              </label>
            );
          })}
        </div>
        {invalid && <span id={errorId} className="block text-[10.5px] text-err">Choose which environment this is. Nothing is picked for you.</span>}
        {after}
      </div>
    </div>
  );
}

// Lucide-style outline glyphs, inlined so the app stays offline.
const ICON = {
  check: <path d="M20 6 9 17l-5-5" />,
  chevronDown: <path d="m6 9 6 6 6-6" />,
  chevronRight: <path d="m9 18 6-6-6-6" />,
  search: <><circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" /></>,
  info: <><circle cx="12" cy="12" r="10" /><path d="M12 16v-4" /><path d="M12 8h.01" /></>,
  eye: <><path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0" /><circle cx="12" cy="12" r="3" /></>,
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

/**
 * Smoothly animates height transitions when a section mounts after the dialog is open or when its content expands or
 * collapses (e.g. choosing a server type, opening a disclosure, toggling SSH bastion fields, or showing hints/offers).
 */
function SmoothExpand({
  children, className = "", animateMount = false, initialHeight = 0, asSection = false, role, "aria-live": ariaLive,
}: {
  children: ReactNode; className?: string; animateMount?: boolean; initialHeight?: number; asSection?: boolean;
  role?: string; "aria-live"?: "polite" | "assertive" | "off";
}) {
  const ref = useRef<HTMLElement>(null);
  const prevHeight = useRef<number | null>(null);
  const animRef = useRef<Animation | null>(null);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const canAnimate =
      typeof el.animate === "function" &&
      !window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;

    let fromH = prevHeight.current;
    if (animRef.current) {
      fromH = el.getBoundingClientRect().height;
      animRef.current.cancel();
      animRef.current = null;
    }

    const toH = el.getBoundingClientRect().height;
    prevHeight.current = toH;

    if (!canAnimate) return;

    if (fromH === null) {
      if (!animateMount || Math.abs(toH - initialHeight) <= 2) return;
      fromH = initialHeight;
    } else if (Math.abs(toH - fromH) <= 2) {
      return;
    }

    el.style.overflow = "hidden";
    const anim = el.animate(
      [
        { height: `${fromH}px`, ...(fromH === 0 ? { opacity: 0 } : {}) },
        { height: `${toH}px`, ...(fromH === 0 ? { opacity: 1 } : {}) },
      ],
      { duration: 200, easing: "cubic-bezier(0.16, 1, 0.3, 1)" },
    );
    animRef.current = anim;
    const clear = () => {
      if (animRef.current === anim) {
        el.style.overflow = "";
        animRef.current = null;
        prevHeight.current = el.getBoundingClientRect().height;
      }
    };
    anim.onfinish = clear;
    anim.oncancel = clear;
  });

  if (asSection) {
    return (
      <section ref={ref as RefObject<HTMLElement>} role={role} aria-live={ariaLive} className={className}>
        {children}
      </section>
    );
  }
  return (
    <div ref={ref as RefObject<HTMLDivElement>} role={role} aria-live={ariaLive} className={className}>
      {children}
    </div>
  );
}

/** A folded group of rarely changed settings: one line of state while folded, its warning colour included. */
function Disclosure({ id, title, summary, tone = "muted", open, onToggle, children }: { id: string; title: string; summary: string; tone?: "ok" | "warn" | "muted"; open: boolean; onToggle: () => void; children: ReactNode }) {
  return (
    <SmoothExpand asSection className="border-b border-line last:border-b-0">
      <button
        type="button" aria-expanded={open} aria-controls={`${id}-panel`} onClick={onToggle}
        className="w-full grid grid-cols-[14px_96px_minmax(0,1fr)] items-center gap-2 h-8 px-3 text-left hover:bg-hover"
      >
        <span className={`text-muted transition-transform ${open ? "rotate-90" : ""}`}><Icon d={ICON.chevronRight} size={13} /></span>
        <span className="text-[12px] font-semibold text-fg">{title}</span>
        <span className={`text-[11.5px] truncate ${tone === "warn" ? "text-warn" : tone === "ok" ? "text-ok" : "text-muted"}`}>{tone === "warn" ? "⚠ " : ""}{summary}</span>
      </button>
      {open && <div id={`${id}-panel`} className="q-profile-reveal px-3 pb-3 pt-1 space-y-2.5">{children}</div>}
    </SmoothExpand>
  );
}

function Note({ children, action, onDismiss }: { children: ReactNode; action?: ReactNode; onDismiss?: () => void }) {
  return (
    <div className="q-profile-reveal flex items-start gap-2.5 px-2.5 py-2 rounded-md border border-line bg-panel text-[11.5px] text-muted leading-snug" aria-live="polite">
      <span className="mt-px shrink-0 text-accent"><Icon d={ICON.info} size={15} /></span>
      <div className="flex-1 min-w-0 space-y-0.5 select-text">{children}</div>
      {action}
      {onDismiss && (
        <button type="button" className="shrink-0 w-5 h-5 flex items-center justify-center rounded opacity-70 hover:opacity-100 hover:bg-hover" aria-label="Dismiss" title="Dismiss" onClick={onDismiss}>
          <Icon d={ICON.x} size={13} />
        </button>
      )}
    </div>
  );
}

function Offer({ children, warn = false }: { children: ReactNode; warn?: boolean }) {
  return (
    <div className={`q-profile-reveal flex flex-wrap items-center gap-x-2.5 gap-y-1 px-2.5 py-1.5 rounded-md border border-dashed text-[11.5px] text-muted ${warn ? "border-warn" : "border-line-strong"}`} aria-live="polite">
      {children}
    </div>
  );
}

/** A row whose control is not one labelable element (a group of buttons or radios): a div, with an optional label for one field. */
function Line({ label, htmlFor, children }: { label: string; htmlFor?: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[96px_1fr] items-center gap-x-3">
      {htmlFor ? <label htmlFor={htmlFor} className="text-[11.5px] text-muted">{label}</label> : <span className="text-[11.5px] text-muted">{label}</span>}
      {children}
    </div>
  );
}

function Row({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <label className="grid grid-cols-[96px_1fr] items-center gap-x-3">
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

/** Server types grouped by where their databases live, filtered by name or alias. */
function engineMatches(options: Engine[], query: string): Engine[] {
  const q = query.trim().toLowerCase();
  const hit = options.filter((e) => !q || `${ENGINE_TRAITS[e].label} ${ENGINE_TRAITS[e].shortLabel} ${e} ${FIELDS[e].aliases}`.toLowerCase().includes(q));
  const server = (e: Engine) => ENGINE_TRAITS[e].locations.includes("server");
  return [...hit.filter(server), ...hit.filter((e) => !server(e))];
}

/**
 * Server-type picker: a button showing the chosen type (or "Choose a server type…"), opening a searchable list grouped
 * into server and file engines, so it stays usable as engines are added. Escape is left to it while open (see Modal).
 */
const EngineSelect = ({ ref, value, options, onChange, autoFocus }: {
  ref: RefObject<HTMLButtonElement | null>; value: Engine | null; autoFocus?: boolean; options: Engine[]; onChange: (e: Engine) => void;
}) => {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const [pos, setPos] = useState<{ left: number; width: number; top?: number; bottom?: number }>({ left: 0, width: 0 });
  const pop = useRef<HTMLDivElement>(null);
  const id = useId();
  const list = engineMatches(options, query);
  const current = value ? ENGINE_TRAITS[value] : null;

  const show = () => {
    const r = ref.current!.getBoundingClientRect();
    const h = Math.min(options.length * 32 + 90, 340);
    const below = window.innerHeight - r.bottom;
    setPos(below < h + 8 && r.top > below ? { left: r.left, width: r.width, bottom: window.innerHeight - r.top + 4 } : { left: r.left, width: r.width, top: r.bottom + 4 });
    setQuery("");
    setActive(Math.max(0, value ? engineMatches(options, "").indexOf(value) : 0));
    setOpen(true);
  };
  const close = (refocus = true) => {
    setOpen(false);
    if (refocus) ref.current?.focus();
  };
  const choose = (e: Engine) => {
    close();
    onChange(e);
  };

  useEffect(() => {
    if (!open) return;
    const away = (e: Event) => {
      if (!ref.current?.contains(e.target as Node) && !pop.current?.contains(e.target as Node)) setOpen(false);
    };
    const resize = () => setOpen(false);
    window.addEventListener("mousedown", away, true);
    window.addEventListener("scroll", away, true);
    window.addEventListener("resize", resize);
    return () => {
      window.removeEventListener("mousedown", away, true);
      window.removeEventListener("scroll", away, true);
      window.removeEventListener("resize", resize);
    };
  }, [open, ref]);

  const activeEngine = list[active];
  useEffect(() => {
    if (open && activeEngine) document.getElementById(`${id}-opt-${activeEngine}`)?.scrollIntoView({ block: "nearest" });
  }, [open, activeEngine, id]);

  const onButtonKey = (e: KeyboardEvent) => {
    if (["ArrowDown", "ArrowUp", "Enter", " "].includes(e.key)) {
      e.preventDefault();
      if (!open) show();
    }
  };
  const onSearchKey = (e: KeyboardEvent) => {
    const move = (to: number) => {
      e.preventDefault();
      setActive(Math.min(list.length - 1, Math.max(0, to)));
    };
    if (e.key === "ArrowDown") move(active + 1);
    else if (e.key === "ArrowUp") move(active - 1);
    else if (e.key === "Home") move(0);
    else if (e.key === "End") move(list.length - 1);
    else if (e.key === "Enter") {
      e.preventDefault();
      if (list[active]) choose(list[active]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      close();
    } else if (e.key === "Tab") close(false);
  };

  let lastServer: boolean | null = null;
  return (
    <>
      <button
        ref={ref}
        type="button"
        role="combobox"
        aria-label="Server type"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={`${id}-list`}
        autoFocus={autoFocus}
        onClick={() => (open ? close(false) : show())}
        onKeyDown={onButtonKey}
        className={`${INPUT} flex items-center gap-2 text-left ${open ? "border-accent" : ""}`}
      >
        {current && value ? (
          <>
            <EngineMark engine={value} logo={LOGOS[value]} size={16} />
            <span className="truncate font-semibold">{current.label}</span>
            <span className="flex-1 truncate text-[11px] text-muted">{current.locations.includes("server") ? `Server · default port ${current.defaultPort}` : "File on this computer"}</span>
          </>
        ) : (
          <span className="flex-1 truncate text-muted">Choose a server type…</span>
        )}
        <span className="text-muted"><Icon d={ICON.chevronDown} size={14} /></span>
      </button>
      {open && (
        <div ref={pop} className="q-popover-in fixed z-50 rounded-md border border-line bg-panel shadow-xl p-1.5 flex flex-col gap-1" style={pos}>
          <div className="relative">
            <span className="absolute left-2 top-1/2 -translate-y-1/2 text-muted"><Icon d={ICON.search} size={13} /></span>
            <input
              className="q-input w-full h-7 pl-7" autoFocus role="combobox" aria-expanded="true" aria-controls={`${id}-list`} aria-label="Search server types"
              aria-activedescendant={list[active] ? `${id}-opt-${list[active]}` : undefined} aria-autocomplete="list" data-no-enter-save
              placeholder={`Search ${options.length} server types`} value={query} onChange={(e) => { setQuery(e.target.value); setActive(0); }} onKeyDown={onSearchKey}
            />
          </div>
          <ul id={`${id}-list`} role="listbox" aria-label="Server type" className="max-h-[260px] overflow-auto">
            {list.map((e, i) => {
              const server = ENGINE_TRAITS[e].locations.includes("server");
              const head = server !== lastServer ? (server ? "On a server" : "File on this computer") : null;
              lastServer = server;
              return [
                head && <li key={`h-${head}`} role="presentation" className="px-2.5 pt-1.5 pb-0.5 text-[9.5px] font-bold tracking-wider uppercase text-muted">{head}</li>,
                <li
                  key={e}
                  id={`${id}-opt-${e}`}
                  role="option"
                  aria-selected={e === value}
                  onMouseEnter={() => setActive(i)}
                  onMouseDown={(ev) => ev.preventDefault()}
                  onClick={() => choose(e)}
                  className={`flex items-center gap-2.5 h-[32px] px-2.5 rounded text-[12px] cursor-pointer ${i === active ? "bg-hover text-fg" : "text-fg"}`}
                >
                  <EngineMark engine={e} logo={LOGOS[e]} size={18} />
                  <span className="flex-1 truncate">{ENGINE_TRAITS[e].label}</span>
                  {e === value && <span className="text-accent"><Icon d={ICON.check} size={14} /></span>}
                </li>,
              ];
            })}
            {!list.length && <li role="presentation" className="px-2.5 py-2 text-[11.5px] text-muted">No server type matches “{query}”.</li>}
          </ul>
        </div>
      )}
    </>
  );
};


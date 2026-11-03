// A connection's environment as a word in its color (§12: never color alone; the Explorer's row tag, for screens
// outside the Explorer), and the server chip a server-bound dialog shows: "PROD Billing › SalesDB".
import type { EnvironmentKind } from "../ipc/contract";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { Icon } from "./icons";
import { readableOnSurface, useThemeKey } from "./tabStyle";

/** "DEV", "PROD" (with a shield)… in `color`, which the caller has made readable on its surface. */
export function EnvTag({ env, color }: { env: EnvironmentKind; color: string }) {
  if (!envLabel[env]) return null; // a profile from an older store without an environment
  return (
    <span className="shrink-0 inline-flex items-center gap-[3px] text-[10px] font-bold tracking-wide" style={{ color }} data-env={env}>
      {env === "prod" && <Icon name="shield" size={10} strokeWidth={2.5} />}
      {envLabel[env].toUpperCase()}
    </span>
  );
}

/** What a dialog acts on: the environment, the server (profile) and the database. `surface`: the background it sits on. */
export function ServerChip({ profileId, database, surface = "var(--q-panel)" }: { profileId: string | undefined; database?: string | null; surface?: string }) {
  useThemeKey();
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const groups = useProfiles((s) => s.groups);
  if (!profile) return null;
  const raw = envColor(profile, groups);
  const env = envLabel[profile.environment]?.toUpperCase() ?? "";
  return (
    <span className="inline-flex items-center gap-1.5 min-w-0 max-w-full align-bottom text-[11px]" data-testid="server-chip" title={`${env ? `${env} ` : ""}${profile.name}${database ? ` › ${database}` : ""}`}>
      <EnvTag env={profile.environment} color={readableOnSurface(raw, surface) ?? raw} />
      <span className="font-semibold text-fg truncate">{profile.name}</span>
      {database && (
        <>
          <span aria-hidden className="text-muted">›</span>
          <span className="text-fg truncate">{database}</span>
        </>
      )}
    </span>
  );
}

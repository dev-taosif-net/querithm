using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Execution;

/// <summary>
/// How strictly Querithm confirms work on a Production connection (§13 rule 7, §16 2026-11-27).
/// </summary>
public enum ProdGuardMode
{
    /// <summary>Asks before the first query on each Prod database, before destructive SQL, and before grid edits or imports.</summary>
    Strict,
    /// <summary>Default: runs harmless reads without a prompt; still asks before destructive SQL, grid edits and imports.</summary>
    Risky,
    /// <summary>Runs on Prod without confirmations; file and configuration policy refusals still hold.</summary>
    Off,
}

/// <summary>
/// The guards on caller SQL (§13 rule 7), enforced in the sidecar: one function for every RPC that runs text the user
/// gave — query.execute, plan.get and debug.attach — so none of them can skip the file-policy refusal, the first run on
/// Prod or the destructive confirm. Querithm-generated writes (grid patches, imports, syncs) go through <see cref="Tool"/>.
/// </summary>
public sealed class QueryGuard(SessionManager sessions)
{
    /// <summary>
    /// The store.db setting behind Settings › Query execution › "Production confirmations" (§16, 2026-11-18 / 2026-11-27).
    /// Stored as a JSON string (<c>"strict"</c>, <c>"risky"</c>, <c>"off"</c>); legacy <c>true</c>/<c>false</c> map to
    /// <see cref="ProdGuardMode.Strict"/> / <see cref="ProdGuardMode.Off"/>. Missing uses the default (<see cref="ProdGuardMode.Risky"/>);
    /// anything unrecognised fails closed to <see cref="ProdGuardMode.Strict"/>.
    /// </summary>
    public const string SettingKey = "guard.prodConfirmations";

    volatile ProdGuardMode _prodMode = ProdGuardMode.Risky;

    /// <summary>
    /// Active confirmation level on Prod targets. Refusals hold under every mode. Set by the composition root from
    /// store.db at start and by settings.set.
    /// </summary>
    public ProdGuardMode ProdMode
    {
        get => _prodMode;
        set => _prodMode = value;
    }

    /// <summary>The stored value as the guard reads it: missing = Risky (default), unknown/corrupt = Strict (fail-closed).</summary>
    public static ProdGuardMode ParseSetting(string? valueJson)
    {
        if (valueJson == null) return ProdGuardMode.Risky;
        return valueJson.Trim() switch
        {
            "\"risky\"" => ProdGuardMode.Risky,
            "\"strict\"" or "true" => ProdGuardMode.Strict,
            "\"off\"" or "false" => ProdGuardMode.Off,
            _ => ProdGuardMode.Strict,
        };
    }

    /// <summary>Strict parser for <c>settings.set</c>: returns null for any value outside the documented JSON literals.</summary>
    public static (ProdGuardMode Mode, string CanonicalJson)? TryParseInput(string? valueJson) =>
        valueJson?.Trim() switch
        {
            "\"strict\"" or "true" => (ProdGuardMode.Strict, "\"strict\""),
            "\"risky\"" => (ProdGuardMode.Risky, "\"risky\""),
            "\"off\"" or "false" => (ProdGuardMode.Off, "\"off\""),
            _ => null,
        };

    /// <summary>
    /// Refusal (Phase D0c: a hard error no confirmation lifts), then on Prod the destructive confirm — text the
    /// classifier could not parse counts as destructive — and in <see cref="ProdGuardMode.Strict"/> the first-run confirm
    /// on non-destructive queries. Checking destructive findings first avoids asking two back-to-back modals on a
    /// destructive first run. Null when the text may run; the first-run confirm is recorded only when a confirmation was
    /// actually satisfied (never silently on an unprompted read in <see cref="ProdGuardMode.Risky"/>).
    /// </summary>
    public GuardBlock? Check(Session session, Classification classification, bool confirmedProd, bool confirmedDestructive)
    {
        if (classification.Refusal is { } refusal) throw new QuerithmException("refused", refusal);
        var mode = _prodMode;
        if (mode == ProdGuardMode.Off || !sessions.IsProd(session)) return null;
        var findings = classification.Destructive.ToList();
        if (classification.ParseErrors.Length > 0 && findings.Count == 0)
        {
            // The first error says why: a syntax error, or a script too deep or too long to analyse (a valid script then,
            // which "syntax errors" misdescribed — completion audit 2026-11-25 review).
            var first = classification.ParseErrors[0];
            findings.Add(new DestructiveFinding
            {
                Rule = "unparsed", Statement = "", Line = first.Line, Column = first.Column,
                Description = $"The script could not be checked for destructive statements. Line {first.Line}: {first.Message}",
            });
        }
        if (findings.Count > 0)
        {
            if (!confirmedDestructive) return Block(session, GuardReason.Destructive, findings.ToArray());
            sessions.ConfirmProd(session);
            return null;
        }
        if (mode == ProdGuardMode.Strict)
        {
            if (!sessions.IsProdConfirmed(session) && !confirmedProd) return Block(session, GuardReason.ProdFirstRun, []);
            sessions.ConfirmProd(session);
        }
        return null;
    }

    /// <summary>
    /// §13 rule 7 for Querithm-generated writes: on a Prod profile every such write needs confirmation (not only the
    /// first run). When the write also carries destructive changes (deletes, drops), one combined <see cref="GuardReason.Destructive"/>
    /// block carries both the write summary and the destructive findings so the user confirms once instead of twice.
    /// </summary>
    public GuardBlock? Tool(Session session, bool confirmedProd, bool confirmedDestructive, DestructiveFinding[] findings, DestructiveFinding[] destructive)
    {
        if (_prodMode == ProdGuardMode.Off || !sessions.IsProd(session)) return null;
        if (destructive.Length > 0)
        {
            if (!confirmedDestructive) return Block(session, GuardReason.Destructive, [.. findings, .. destructive]);
            sessions.ConfirmProd(session);
            return null;
        }
        if (!confirmedProd) return Block(session, GuardReason.ProdWrite, findings);
        sessions.ConfirmProd(session);
        return null;
    }

    GuardBlock Block(Session s, GuardReason reason, DestructiveFinding[] findings) => new()
    {
        Reason = reason, ProfileName = s.Profile.Name, Server = s.Profile.Host ?? "(local)", Database = s.Database,
        Environment = sessions.EnvironmentOf(s), Findings = findings,
    };
}

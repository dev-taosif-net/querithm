using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Execution;

/// <summary>
/// The guards on caller SQL (§13 rule 7), enforced in the sidecar: one function for every RPC that runs text the user
/// gave — query.execute, plan.get and debug.attach — so none of them can skip the file-policy refusal, the first run on
/// Prod or the destructive confirm. Querithm-generated writes (grid patches, imports, syncs) go through <see cref="Tool"/>.
/// </summary>
public sealed class QueryGuard(SessionManager sessions)
{
    /// <summary>
    /// Refusal (Phase D0c: a hard error no confirmation lifts), then on Prod the first-run confirm and the destructive
    /// confirm — text the classifier could not parse counts as destructive. Null when the text may run; the first-run
    /// confirm is recorded only then.
    /// </summary>
    public GuardBlock? Check(Session session, Classification classification, bool confirmedProd, bool confirmedDestructive)
    {
        if (classification.Refusal is { } refusal) throw new QuerithmException("refused", refusal);
        if (!sessions.IsProd(session)) return null;
        if (!sessions.IsProdConfirmed(session) && !confirmedProd) return Block(session, GuardReason.ProdFirstRun, []);
        var findings = classification.Destructive.ToList();
        if (classification.ParseErrors.Length > 0 && findings.Count == 0)
            findings.Add(new DestructiveFinding
            {
                Rule = "unparsed", Statement = "", Line = classification.ParseErrors[0].Line, Column = classification.ParseErrors[0].Column,
                Description = "The script has syntax errors, so it could not be checked for destructive statements.",
            });
        if (findings.Count > 0 && !confirmedDestructive) return Block(session, GuardReason.Destructive, findings.ToArray());
        sessions.ConfirmProd(session);
        return null;
    }

    /// <summary>
    /// §13 rule 7 for Querithm-generated writes: on a Prod profile every such write needs <paramref name="confirmedProd"/>
    /// (not only the first run), and destructive ones (deletes, drops) also <paramref name="confirmedDestructive"/>.
    /// </summary>
    public GuardBlock? Tool(Session session, bool confirmedProd, bool confirmedDestructive, DestructiveFinding[] findings, DestructiveFinding[] destructive)
    {
        if (!sessions.IsProd(session)) return null;
        if (!confirmedProd) return Block(session, GuardReason.ProdWrite, findings);
        if (destructive.Length > 0 && !confirmedDestructive) return Block(session, GuardReason.Destructive, destructive);
        sessions.ConfirmProd(session);
        return null;
    }

    GuardBlock Block(Session s, GuardReason reason, DestructiveFinding[] findings) => new()
    {
        Reason = reason, ProfileName = s.Profile.Name, Server = s.Profile.Host ?? "(local)", Database = s.Database,
        Environment = sessions.EnvironmentOf(s), Findings = findings,
    };
}

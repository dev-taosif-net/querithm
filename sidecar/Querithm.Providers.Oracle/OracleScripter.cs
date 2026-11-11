using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Script as ▸ for Oracle (D4 S08: packages only; every other kind keeps refusing until D4 S11). A package is scripted as
/// whole objects — its specification, its body, or both — never member by member:
/// <list type="bullet">
/// <item>CREATE: the definition (<c>CREATE OR REPLACE PACKAGE …</c> / <c>PACKAGE BODY …</c>, ending in <c>/</c>); a
/// specification with <c>withBody</c> also carries its body.</item>
/// <item>ALTER: Oracle's own <c>ALTER PACKAGE … COMPILE SPECIFICATION | BODY</c> (recompiles an invalid one).</item>
/// <item>DROP: <c>DROP PACKAGE</c> drops the specification and the body; <c>DROP PACKAGE BODY</c> the body alone.</item>
/// <item>DROP and CREATE of a specification always re-creates its body as well, since DROP PACKAGE removes it.</item>
/// </list>
/// A body the login cannot read (another schema without DEBUG) is left out with a comment saying so, and a script that
/// would drop it says it would be lost. Scripts are text for a new tab, never run here (§13 rule 7).
/// </summary>
public sealed class OracleScripter : IObjectScripter
{
    readonly Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> _definition;
    readonly Func<string, string> _editable;

    public OracleScripter(OracleMetadata metadata)
        : this(metadata.GetDefinitionAsync, OracleMetadata.Editable) { }

    /// <param name="definition">Tests: the definition reader (GET_DDL, or ALL_SOURCE without it).</param>
    /// <param name="editable">CREATE → CREATE OR REPLACE where the text lacks it.</param>
    internal OracleScripter(Func<DbConnection, ObjectRef, CancellationToken, Task<string?>> definition, Func<string, string> editable)
    {
        _definition = definition;
        _editable = editable;
    }

    internal const string NotRun = "A package is not run as a whole: run one of its subprograms. Scripting package members is not available yet.";
    internal const string BodyLeftOut = "-- The package body is not visible to this login (it needs DEBUG on the package), or there is none: it is not in this script.";
    internal const string BodyLost = "-- WARNING: DROP PACKAGE also drops the package body, which this login cannot read. It is not in this script and would be lost.";
    internal const string DropsBoth = "-- DROP PACKAGE drops the specification and its body.";

    public Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct) =>
        ScriptAsync(connection, obj, kind, withBody: false, ct);

    public async Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, bool withBody, CancellationToken ct)
    {
        if (obj.Kind is not (ObjectKind.Package or ObjectKind.PackageBody)) throw new NotSupportedException(OracleUnavailable.Reason);
        var body = obj.Kind == ObjectKind.PackageBody;
        var name = $"{OracleIdentifiers.QuoteIfNeeded(obj.Schema)}.{OracleIdentifiers.QuoteIfNeeded(obj.Name)}";
        switch (kind)
        {
            case ScriptKind.Alter:
                return $"ALTER PACKAGE {name} COMPILE {(body ? "BODY" : "SPECIFICATION")};";
            case ScriptKind.Drop:
                return body ? $"DROP PACKAGE BODY {name};" : $"{DropsBoth}\nDROP PACKAGE {name};";
            case ScriptKind.Create:
            case ScriptKind.DropCreate:
            {
                var self = await RequiredAsync(connection, obj, ct).ConfigureAwait(false);
                var parts = new List<string>();
                if (kind == ScriptKind.DropCreate) parts.Add(body ? $"DROP PACKAGE BODY {name};" : $"DROP PACKAGE {name};");
                parts.Add(self);
                if (!body && (withBody || kind == ScriptKind.DropCreate))
                {
                    var bodyRef = new ObjectRef { ProfileId = obj.ProfileId, Database = obj.Database, Schema = obj.Schema, Name = obj.Name, Kind = ObjectKind.PackageBody };
                    if (await _definition(connection, bodyRef, ct).ConfigureAwait(false) is { } b) parts.Add(_editable(b));
                    else parts.Insert(0, kind == ScriptKind.DropCreate ? BodyLost : BodyLeftOut);
                }
                return string.Join("\n\n", parts);
            }
            default:
                throw new NotSupportedException(NotRun);
        }
    }

    async Task<string> RequiredAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) =>
        await _definition(connection, obj, ct).ConfigureAwait(false) is { } d
            ? _editable(d)
            : throw new InvalidOperationException($"No definition is available for {obj.Schema}.{obj.Name}: it no longer exists, or this login cannot read its source.");
}

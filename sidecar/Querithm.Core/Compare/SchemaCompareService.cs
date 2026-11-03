using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;

namespace Querithm.Core.Compare;

/// <summary>
/// schemaCompare.*: reads both models, compares them, keeps the last few comparisons so the diff view and the
/// migration script work from the sidecar's own models — the webview only names items by key.
/// </summary>
public sealed class SchemaCompareService(SessionManager sessions, ObjectService objects, ProviderRegistry providers, DefinitionFetcher definitions, ILogger log)
{
    sealed record Entry(Comparison Comparison, CompareSideInfo Source, CompareSideInfo Target, string? Refusal, DateTime At);
    sealed record Loaded(SchemaModel Model, CompareSideInfo Info, ISchemaCompareDialect? Dialect);

    readonly ConcurrentDictionary<string, Entry> _compares = new();
    const int Keep = 4;
    public const long MaxModelFileBytes = 64L * 1024 * 1024;

    /// <summary>The background definitions warm-up after a compare read: far above a full fetch on the §14 fixture (metric #24).</summary>
    static readonly TimeSpan WarmUpLimit = TimeSpan.FromMinutes(10);

    static QuerithmException Unsupported(Engine engine) =>
        new("compareUnsupported", $"Schema compare is not available for {engine} yet.");

    static string CheckedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            throw new QuerithmException("fileToken", "The schema file was not picked with the file dialog (no path came with the token). Pick it again.");
        if (!Path.IsPathFullyQualified(path)) throw new QuerithmException("fileToken", "The picked file path is not absolute.");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new QuerithmException("compareFile", $"{Path.GetFileName(full)} no longer exists.");
        return full;
    }

    async Task<SchemaModel> ReadLiveAsync(string sessionId, string? database, CancellationToken ct)
    {
        var s = sessions.Get(sessionId);
        FeatureGuard.Require(s.Provider, Feature.SchemaCompare); // the provider's own reason first
        var dialect = s.Provider.SchemaCompare ?? throw Unsupported(s.Provider.Engine);
        var db = database ?? s.Database ?? throw QuerithmException.Invalid("Choose a database to compare.");
        try
        {
            return await objects.OnConnectionAsync(sessionId, db, c => dialect.ReadModelAsync(c, ct), ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new QuerithmException("compareUnsupported", ex.Message);
        }
    }

    async Task<Loaded> LoadAsync(CompareSide side, string role, CancellationToken ct)
    {
        if (side.FileToken != null || side.FilePath != null)
        {
            var path = CheckedPath(side.FilePath);
            var name = Path.GetFileName(path);
            if (new FileInfo(path).Length > MaxModelFileBytes) throw new QuerithmException("compareFile", $"{name} is too large to be a schema file.");
            SchemaModel? model;
            try
            {
                await using var f = File.OpenRead(path);
                model = await JsonSerializer.DeserializeAsync(f, Json.Context.SchemaModel, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new QuerithmException("compareFile", $"{name} is not a Querithm schema file: {ex.Message}");
            }
            if (model == null || model.Format != SchemaModel.CurrentFormat)
                throw new QuerithmException("compareFile", $"{name} is not a schema file this version of Querithm can read.");
            var dialect = providers.All.FirstOrDefault(p => p.Engine == model.Engine)?.SchemaCompare;
            return new Loaded(model, new CompareSideInfo
            {
                Label = $"{name} ({model.Database})", Engine = model.Engine, ServerVersion = model.ServerVersion, CapturedAt = model.CapturedAt, Live = false,
            }, dialect);
        }
        if (side.SessionId == null) throw QuerithmException.Invalid($"Choose the {role} database.");
        var s = sessions.Get(side.SessionId);
        var db = side.Database ?? s.Database ?? throw QuerithmException.Invalid($"Choose the {role} database.");
        var live = await ReadLiveAsync(side.SessionId, db, ct).ConfigureAwait(false);
        // Warm definition_cache for find usages (M7); never holds up the compare, and nobody waits for it, so it has a
        // bound of its own: a server that stops answering does not keep its pooled connection for good.
        var warm = new CancellationTokenSource(WarmUpLimit);
        _ = definitions.EnsureAsync(side.SessionId, db, warm.Token).ContinueWith(t =>
        {
            warm.Dispose();
            if (t.IsFaulted) log.LogDebug("definition fetch: {Message}", t.Exception?.GetBaseException().Message);
        }, TaskScheduler.Default);
        return new Loaded(live, new CompareSideInfo
        {
            Label = LiveLabel(side), Engine = s.Provider.Engine, ServerVersion = live.ServerVersion,
            CapturedAt = live.CapturedAt, Live = true,
        }, s.Provider.SchemaCompare);
    }

    string LiveLabel(CompareSide side)
    {
        var s = sessions.Get(side.SessionId!);
        return $"{s.Profile.Name} · {side.Database ?? s.Database}{(side.Schema != null ? $" · {side.Schema}" : "")}";
    }

    public async Task<SchemaCompareResult> RunAsync(SchemaCompareParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sameSession = p.Source.SessionId != null && p.Source.SessionId == p.Target.SessionId &&
                          string.Equals(p.Source.Database, p.Target.Database, StringComparison.Ordinal);
        Loaded src, tgt;
        if (sameSession)
        {
            src = await LoadAsync(p.Source, "source", ct).ConfigureAwait(false);
            tgt = src with { Info = src.Info with { Label = LiveLabel(p.Target) } };
        }
        else
        {
            var a = LoadAsync(p.Source, "source", ct);
            var b = LoadAsync(p.Target, "target", ct);
            await Task.WhenAll(a, b).ConfigureAwait(false);
            (src, tgt) = (a.Result, b.Result);
        }
        if (src.Model.Engine != tgt.Model.Engine)
            throw new QuerithmException("compareEngines",
                $"Schema compare needs both sides on the same engine: the source is {EngineName(src.Model.Engine)}, the target {EngineName(tgt.Model.Engine)}.");
        var d = src.Dialect ?? tgt.Dialect ?? throw Unsupported(src.Model.Engine);

        var srcSchema = p.Source.Schema ?? p.Target.Schema;
        var tgtSchema = p.Target.Schema ?? p.Source.Schema;
        // MySQL / MariaDB: each side's database is its one schema; shop_dev and shop_prod compare by object name.
        if (d.DatabaseIsSchema && srcSchema == null && src.Model.Schemas.Length == 1 && tgt.Model.Schemas.Length == 1)
            (srcSchema, tgtSchema) = (src.Model.Schemas[0], tgt.Model.Schemas[0]);
        foreach (var (model, schema, role) in new[] { (src.Model, srcSchema, "source"), (tgt.Model, tgtSchema, "target") })
            if (schema != null && !model.Schemas.Contains(schema, d.Names))
                throw new QuerithmException("compareSchema", $"The {role} has no schema {schema}.");
        var sm = SchemaComparer.Filter(src.Model, srcSchema, d.Names);
        var tm = SchemaComparer.Filter(tgt.Model, tgtSchema, d.Names);
        if (srcSchema != null && tgtSchema != null) sm = SchemaComparer.MapSchema(sm, srcSchema, tgtSchema, d);
        var cmp = await Task.Run(() => SchemaComparer.Compare(sm, tm, d), ct).ConfigureAwait(false);

        var refusal = tgt.Info.Live ? null : "The target is a saved schema file: compare against a live database to generate a migration.";
        var id = Remember(cmp, src.Info, tgt.Info, refusal);
        log.LogInformation("Schema compare {Source} → {Target}: {Count} objects, {Different} different in {Ms} ms",
            src.Info.Label, tgt.Info.Label, cmp.Items.Count, cmp.Items.Count(i => i.Class != CompareClass.Identical), sw.ElapsedMilliseconds);
        return new SchemaCompareResult
        {
            CompareId = id, Source = src.Info, Target = tgt.Info, Items = cmp.Items.ToArray(), PossibleRenames = cmp.Renames.ToArray(),
            Notes = cmp.Notes.ToArray(), MigrationRefusal = refusal, TransactionalDdl = d.TransactionalDdl, ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>Keeps a comparison for the diff view and the script (the last <see cref="Keep"/>).</summary>
    internal string Remember(Comparison cmp, CompareSideInfo source, CompareSideInfo target, string? refusal)
    {
        var id = Ulid.New().ToString();
        _compares[id] = new Entry(cmp, source, target, refusal, DateTime.UtcNow);
        foreach (var old in _compares.OrderByDescending(kv => kv.Value.At).Skip(Keep).ToList()) _compares.TryRemove(old.Key, out _);
        return id;
    }

    static string EngineName(Engine e) => ProfileRules.EngineName(e);

    Entry Get(string compareId) =>
        _compares.TryGetValue(compareId, out var e) ? e : throw new QuerithmException("compareGone", "This comparison is no longer available. Compare again.");

    public CompareObjectSql ObjectSql(CompareObjectParams p)
    {
        var e = Get(p.CompareId);
        if (!e.Comparison.Pairs.TryGetValue(p.Key, out var pair)) throw QuerithmException.Invalid("Unknown compare item.");
        var d = e.Comparison.Dialect;
        return new CompareObjectSql
        {
            Source = MigrationScriptBuilder.Script(d, pair.Kind, pair.Source, e.Comparison.Source.MajorVersion),
            Target = MigrationScriptBuilder.Script(d, pair.Kind, pair.Target, e.Comparison.Target.MajorVersion),
        };
    }

    public SchemaMigrationResult MigrationSql(SchemaMigrationParams p)
    {
        var e = Get(p.CompareId);
        if (e.Refusal != null) throw new QuerithmException("compareTarget", e.Refusal);
        if (providers.TryGet(e.Target.Engine) is { } target) FeatureGuard.Require(target, Feature.SchemaCompareApply);
        var cmp = e.Comparison;
        foreach (var k in p.Keys)
            if (!cmp.Pairs.ContainsKey(k)) throw QuerithmException.Invalid("Unknown compare item.");
        // Only renames the comparer offered are accepted (the sidecar never trusts names from the webview).
        var renames = (p.Renames ?? []).Where(r => cmp.Renames.Any(o => o.TableKey == r.TableKey && o.From == r.From && o.To == r.To)).ToList();
        if (renames.Count != (p.Renames?.Length ?? 0)) throw QuerithmException.Invalid("A confirmed rename does not match this comparison.");
        var header = string.Join("\n",
            "-- Querithm schema migration",
            $"-- Source: {CommentText.Safe(e.Source.Label)} (read {Iso(e.Source.CapturedAt)})",
            $"-- Target: {CommentText.Safe(e.Target.Label)} (read {Iso(e.Target.CapturedAt)})",
            cmp.Dialect.TransactionalDdl
                ? "-- One transaction: if any statement fails, nothing is changed. Review it before running it on the target."
                : "-- Review it before running it on the target (the engine commits each DDL statement; see below).");
        return new MigrationScriptBuilder(cmp).Build(p.Keys, renames, p.IncludeDataLoss, header);
    }

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<SchemaModelFile> ExportAsync(SchemaExportParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var dialect = s.Provider.SchemaCompare ?? throw Unsupported(s.Provider.Engine);
        var model = SchemaComparer.Filter(await ReadLiveAsync(p.SessionId, p.Database, ct).ConfigureAwait(false), p.Schema, dialect.Names);
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(model.CapturedAt).ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var baseName = string.Concat($"{model.Database}{(p.Schema != null ? $"-{p.Schema}" : "")}".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch));
        return new SchemaModelFile { FileName = $"{baseName}-{stamp}.qschema.json", Json = JsonSerializer.Serialize(model, Json.Context.SchemaModel) };
    }
}

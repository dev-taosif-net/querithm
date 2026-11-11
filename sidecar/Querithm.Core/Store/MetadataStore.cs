using System.Text.Json;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Store;

public sealed record SnapshotRow(SnapshotStatus Status, long? RefreshedAt, string? ServerVersion, string? Error, string[]? Notes = null);

/// <summary>obj/col/param/fk/idx tables (§8). Autocomplete and the explorer read only from here.</summary>
public sealed class MetadataStore(LocalStore store)
{
    public SnapshotRow? GetSnapshotRow(string profileId, string database)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("SELECT status, refreshed_at, server_version, error, notes FROM schema_snapshot WHERE profile_id=$p AND database=$d", null,
            ("$p", profileId), ("$d", database));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SnapshotRow(EnumText.Parse(r.Str(0), SnapshotStatus.Empty), r.Long(1), r.Str(2), r.Str(3), NotesOf(r.Str(4))) : null;
    }

    public void SetStatus(string profileId, string database, SnapshotStatus status, string? error = null, string? serverVersion = null, long? refreshedAt = null)
    {
        using var c = store.Open();
        c.Cmd("""
            INSERT INTO schema_snapshot (profile_id, database, refreshed_at, server_version, status, error) VALUES ($p,$d,$t,$v,$s,$e)
            ON CONFLICT(profile_id, database) DO UPDATE SET status=$s, error=$e,
              refreshed_at=COALESCE($t, refreshed_at), server_version=COALESCE($v, server_version)
            """, null, ("$p", profileId), ("$d", database), ("$t", refreshedAt), ("$v", serverVersion), ("$s", EnumText.Of(status)), ("$e", error))
            .ExecuteNonQuery();
    }

    static string[]? NotesOf(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize(json, StoreJsonContext.Default.StringArray) is { Length: > 0 } notes ? notes : null; }
        catch (JsonException) { return null; } // a damaged cell loses its notes, never the snapshot
    }

    /// <summary>Current change markers (modify_date ticks / xmin) per object id.</summary>
    public Dictionary<long, long> GetMarkers(string profileId, string database)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("SELECT object_id, modify_date FROM obj WHERE profile_id=$p AND database=$d", null, ("$p", profileId), ("$d", database));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, long>();
        while (r.Read()) map[r.GetInt64(0)] = r.Long(1) ?? 0;
        return map;
    }

    /// <summary>Writes one refresh in one transaction (§8 step 4).</summary>
    /// <param name="notes">What the provider could not read this time (<see cref="IMetadataProvider.Notes"/>); replaces the last refresh's.</param>
    public void ApplyRefresh(string profileId, string database, bool full, IReadOnlyList<ObjectStamp> changed, IReadOnlyCollection<long> removed,
        MetadataBundle details, string serverVersion, IReadOnlyList<string>? notes = null)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        (string, object?)[] Key() => [("$p", profileId), ("$d", database)];

        if (full)
        {
            foreach (var t in new[] { "obj", "col", "param", "fk", "idx", "definition_cache" })
                c.Cmd($"DELETE FROM {t} WHERE profile_id=$p AND database=$d", tx, Key()).ExecuteNonQuery();
        }
        else
        {
            var touched = changed.Select(s => s.Id).Concat(removed).ToArray();
            foreach (var chunk in touched.Chunk(500))
            {
                var ids = string.Join(",", chunk);
                foreach (var t in new[] { "col", "param", "idx", "definition_cache" })
                    c.Cmd($"DELETE FROM {t} WHERE profile_id=$p AND database=$d AND object_id IN ({ids})", tx, Key()).ExecuteNonQuery();
                c.Cmd($"DELETE FROM fk WHERE profile_id=$p AND database=$d AND parent_object_id IN ({ids})", tx, Key()).ExecuteNonQuery();
                c.Cmd($"DELETE FROM obj WHERE profile_id=$p AND database=$d AND object_id IN ({ids})", tx, Key()).ExecuteNonQuery();
            }
        }

        var members = (details.Members ?? []).GroupBy(x => x.ObjectId)
            .ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.OrderBy(x => x.Ordinal).Select(x => x.Text).ToArray(), StoreJsonContext.Default.StringArray));
        using (var cmd = c.Cmd("INSERT OR REPLACE INTO obj (profile_id, database, object_id, schema_name, name, kind, modify_date, return_type, parent_object_id, no_rollback_engine, note, spec_object_id, members, target_object_id, row_address) VALUES ($p,$d,$id,$s,$n,$k,$m,$rt,$par,$nr,$note,$spec,$mem,$tgt,$ra)", tx))
        {
            var pId = Param(cmd, "$p", profileId); var pDb = Param(cmd, "$d", database);
            var id = Param(cmd, "$id"); var s = Param(cmd, "$s"); var n = Param(cmd, "$n"); var k = Param(cmd, "$k"); var m = Param(cmd, "$m"); var rt = Param(cmd, "$rt"); var par = Param(cmd, "$par"); var nr = Param(cmd, "$nr"); var note = Param(cmd, "$note");
            var spec = Param(cmd, "$spec"); var mem = Param(cmd, "$mem"); var tgt = Param(cmd, "$tgt"); var ra = Param(cmd, "$ra");
            foreach (var o in changed)
            {
                ra.Value = (object?)o.RowAddress ?? DBNull.Value;
                spec.Value = (object?)o.SpecObjectId ?? DBNull.Value;
                tgt.Value = (object?)o.TargetObjectId ?? DBNull.Value;
                mem.Value = members.TryGetValue(o.Id, out var json) ? json : DBNull.Value;
                id.Value = o.Id; s.Value = o.Schema; n.Value = o.Name; k.Value = EnumText.Of(o.Kind); m.Value = o.Marker; rt.Value = (object?)o.ReturnType ?? DBNull.Value;
                par.Value = (object?)o.ParentObjectId ?? DBNull.Value;
                nr.Value = (object?)o.NoRollbackEngine ?? DBNull.Value;
                note.Value = (object?)o.Note ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.Cmd("""
            INSERT OR REPLACE INTO col (profile_id, database, object_id, ordinal, name, type_name, max_len, precision, scale, nullable, is_identity, is_computed, default_expr)
            VALUES ($p,$d,$o,$ord,$n,$t,$ml,$pr,$sc,$nu,$id,$cp,$def)
            """, tx))
        {
            Param(cmd, "$p", profileId); Param(cmd, "$d", database);
            var o = Param(cmd, "$o"); var ord = Param(cmd, "$ord"); var n = Param(cmd, "$n"); var t = Param(cmd, "$t"); var ml = Param(cmd, "$ml");
            var pr = Param(cmd, "$pr"); var sc = Param(cmd, "$sc"); var nu = Param(cmd, "$nu"); var idn = Param(cmd, "$id"); var cp = Param(cmd, "$cp"); var def = Param(cmd, "$def");
            foreach (var col in details.Columns)
            {
                o.Value = col.ObjectId; ord.Value = col.Ordinal; n.Value = col.Name; t.Value = col.TypeName; ml.Value = Db(col.MaxLen);
                pr.Value = Db(col.Precision); sc.Value = Db(col.Scale); nu.Value = col.Nullable ? 1 : 0; idn.Value = col.Identity ? 1 : 0;
                cp.Value = col.Computed ? 1 : 0; def.Value = (object?)col.Default ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.Cmd("INSERT OR REPLACE INTO param (profile_id, database, object_id, ordinal, name, type_name, max_len, is_output, has_default) VALUES ($p,$d,$o,$ord,$n,$t,$ml,$out,$def)", tx))
        {
            Param(cmd, "$p", profileId); Param(cmd, "$d", database);
            var o = Param(cmd, "$o"); var ord = Param(cmd, "$ord"); var n = Param(cmd, "$n"); var t = Param(cmd, "$t"); var ml = Param(cmd, "$ml"); var outp = Param(cmd, "$out"); var def = Param(cmd, "$def");
            foreach (var p in details.Params)
            {
                o.Value = p.ObjectId; ord.Value = p.Ordinal; n.Value = p.Name; t.Value = p.TypeName; ml.Value = Db(p.MaxLen); outp.Value = p.Output ? 1 : 0; def.Value = p.HasDefault ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.Cmd("INSERT OR REPLACE INTO idx (profile_id, database, object_id, name, is_unique, is_primary, columns_json, not_key) VALUES ($p,$d,$o,$n,$u,$pk,$cols,$nk)", tx))
        {
            Param(cmd, "$p", profileId); Param(cmd, "$d", database);
            var o = Param(cmd, "$o"); var n = Param(cmd, "$n"); var u = Param(cmd, "$u"); var pk = Param(cmd, "$pk"); var cols = Param(cmd, "$cols"); var nk = Param(cmd, "$nk");
            foreach (var i in details.Indexes)
            {
                o.Value = i.ObjectId; n.Value = i.Name; u.Value = i.Unique ? 1 : 0; pk.Value = i.Primary ? 1 : 0; cols.Value = JsonSerializer.Serialize(i.Columns, StoreJsonContext.Default.StringArray); nk.Value = i.NotAKey ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.Cmd("INSERT OR REPLACE INTO fk (profile_id, database, name, parent_object_id, referenced_object_id, columns_json) VALUES ($p,$d,$n,$par,$ref,$cols)", tx))
        {
            Param(cmd, "$p", profileId); Param(cmd, "$d", database);
            var n = Param(cmd, "$n"); var par = Param(cmd, "$par"); var rf = Param(cmd, "$ref"); var cols = Param(cmd, "$cols");
            foreach (var f in details.Fks)
            {
                n.Value = f.Name; par.Value = f.Parent; rf.Value = f.Referenced; cols.Value = JsonSerializer.Serialize(f.Columns, Json.Context.FkColumnPairArray);
                cmd.ExecuteNonQuery();
            }
        }
        c.Cmd("""
            INSERT INTO schema_snapshot (profile_id, database, refreshed_at, server_version, status, error, notes) VALUES ($p,$d,$t,$v,'fresh',NULL,$n)
            ON CONFLICT(profile_id, database) DO UPDATE SET refreshed_at=$t, server_version=$v, status='fresh', error=NULL, notes=$n
            """, tx, ("$p", profileId), ("$d", database), ("$t", Time.NowMs()), ("$v", serverVersion),
            ("$n", notes is { Count: > 0 } ? JsonSerializer.Serialize(notes.ToArray(), StoreJsonContext.Default.StringArray) : null)).ExecuteNonQuery();
        tx.Commit();
    }

    static object Db(int? v) => v.HasValue ? v.Value : DBNull.Value;

    static SqliteParameter Param(SqliteCommand cmd, string name, object? value = null)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>Whole snapshot for tier 1 and the explorer (bench metric 9: ≤ 80 ms for the fixture).</summary>
    public SchemaSnapshot Load(string profileId, string database, long[]? onlyIds = null)
    {
        using var c = store.Open();
        var row = GetSnapshotRow(profileId, database);
        var filter = onlyIds == null ? "" : $" AND object_id IN ({string.Join(",", onlyIds.Length == 0 ? [-1] : onlyIds)})";
        (string, object?)[] key = [("$p", profileId), ("$d", database)];

        var pkCols = new Dictionary<long, HashSet<string>>();
        using (var cmd = c.Cmd($"SELECT object_id, columns_json FROM idx WHERE profile_id=$p AND database=$d AND is_primary=1{filter}", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                pkCols[r.GetInt64(0)] = new HashSet<string>(JsonSerializer.Deserialize(r.GetString(1), StoreJsonContext.Default.StringArray) ?? [], StringComparer.OrdinalIgnoreCase);

        var columns = new Dictionary<long, List<SnapColumn>>();
        using (var cmd = c.Cmd($"SELECT object_id, name, type_name, nullable, is_identity, is_computed, default_expr, ordinal FROM col WHERE profile_id=$p AND database=$d{filter} ORDER BY object_id, ordinal", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var oid = r.GetInt64(0);
                if (!columns.TryGetValue(oid, out var list)) columns[oid] = list = [];
                var name = r.GetString(1);
                list.Add(new SnapColumn
                {
                    Name = name, Type = r.Str(2) ?? "", Nullable = r.Bool(3), Identity = r.Bool(4), Computed = r.Bool(5), Default = r.Str(6),
                    Pk = pkCols.TryGetValue(oid, out var pks) && pks.Contains(name), Ordinal = r.GetInt32(7),
                });
            }

        var parms = new Dictionary<long, List<SnapParam>>();
        using (var cmd = c.Cmd($"SELECT object_id, ordinal, name, type_name, is_output, has_default FROM param WHERE profile_id=$p AND database=$d{filter} ORDER BY object_id, ordinal", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var oid = r.GetInt64(0);
                if (!parms.TryGetValue(oid, out var list)) parms[oid] = list = [];
                list.Add(new SnapParam { Ordinal = r.GetInt32(1), Name = r.Str(2) ?? "", Type = r.Str(3) ?? "", Output = r.Bool(4), HasDefault = r.Bool(5) });
            }

        var objects = new List<SnapObject>();
        using (var cmd = c.Cmd($"SELECT object_id, schema_name, name, kind, return_type, modify_date, parent_object_id, no_rollback_engine, note, spec_object_id, members, target_object_id, row_address FROM obj WHERE profile_id=$p AND database=$d{filter} ORDER BY schema_name, name", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var oid = r.GetInt64(0);
                // A kind this build does not know (written by a newer one) is left out, never shown as some other kind.
                if (!EnumText.TryParse<ObjectKind>(r.Str(3), out var kind)) continue;
                objects.Add(new SnapObject
                {
                    Id = oid, Schema = r.Str(1) ?? "", Name = r.Str(2) ?? "", Kind = kind, ReturnType = r.Str(4),
                    ModifyDate = r.Long(5), ParentObjectId = r.Long(6),
                    NoRollbackEngine = r.Str(7), Note = r.Str(8), SpecObjectId = r.Long(9), Members = NotesOf(r.Str(10)), TargetObjectId = r.Long(11), RowAddress = r.Str(12),
                    Columns = columns.TryGetValue(oid, out var cl) ? cl.ToArray() : null,
                    Params = parms.TryGetValue(oid, out var pl) ? pl.ToArray() : null,
                });
            }

        var fks = new List<SnapFk>();
        if (onlyIds == null)
        {
            using var cmd = c.Cmd("SELECT name, parent_object_id, referenced_object_id, columns_json FROM fk WHERE profile_id=$p AND database=$d", null, key);
            using var r = cmd.ExecuteReader();
            while (r.Read()) fks.Add(ReadFk(r));
        }

        return new SchemaSnapshot
        {
            ProfileId = profileId,
            Database = database,
            Status = row?.Status ?? SnapshotStatus.Empty,
            RefreshedAt = row?.RefreshedAt,
            ServerVersion = row?.ServerVersion,
            Error = row?.Error,
            Notes = row?.Notes,
            Objects = objects.ToArray(),
            Fks = fks.ToArray(),
        };
    }

    static SnapFk ReadFk(SqliteDataReader r) => new()
    {
        Name = r.GetString(0), Parent = r.GetInt64(1), Referenced = r.GetInt64(2),
        Columns = JsonSerializer.Deserialize(r.GetString(3), Json.Context.FkColumnPairArray) ?? [],
    };

    public ObjectDetails Details(string profileId, string database, long objectId)
    {
        var snap = Load(profileId, database, [objectId]);
        var obj = snap.Objects.FirstOrDefault();
        using var c = store.Open();
        (string, object?)[] key = [("$p", profileId), ("$d", database), ("$o", objectId)];
        var indexes = new List<IndexInfo>();
        using (var cmd = c.Cmd("SELECT name, is_unique, is_primary, columns_json, not_key FROM idx WHERE profile_id=$p AND database=$d AND object_id=$o ORDER BY is_primary DESC, name", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                indexes.Add(new IndexInfo { Name = r.GetString(0), IsUnique = r.Bool(1), IsPrimary = r.Bool(2), Columns = JsonSerializer.Deserialize(r.GetString(3), StoreJsonContext.Default.StringArray) ?? [], NotAKey = r.Bool(4) });
        var fks = new List<SnapFk>();
        var refBy = new List<SnapFk>();
        using (var cmd = c.Cmd("SELECT name, parent_object_id, referenced_object_id, columns_json FROM fk WHERE profile_id=$p AND database=$d AND (parent_object_id=$o OR referenced_object_id=$o)", null, key))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var fk = ReadFk(r);
                if (fk.Parent == objectId) fks.Add(fk); else refBy.Add(fk);
            }
        return new ObjectDetails
        {
            Columns = obj?.Columns ?? [], Params = obj?.Params ?? [], Indexes = indexes.ToArray(), ForeignKeys = fks.ToArray(), ReferencedBy = refBy.ToArray(),
        };
    }

    /// <summary>Prefix + substring search over obj_name (§10). Prefix hits rank first.</summary>
    public ObjectRef[] Search(string profileId, string database, string text, ObjectKind[]? kinds, int limit)
    {
        var needle = text.Trim().Trim('[', ']', '"');
        var dot = needle.LastIndexOf('.');
        string? schema = null;
        if (dot > 0) { schema = needle[..dot].Trim('[', ']', '"'); needle = needle[(dot + 1)..].Trim('[', ']', '"'); }
        var like = needle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var kindFilter = kinds is { Length: > 0 } ? $" AND kind IN ({string.Join(",", kinds.Select(k => $"'{EnumText.Of(k)}'"))})" : "";
        using var c = store.Open();
        using var cmd = c.Cmd($"""
            SELECT object_id, schema_name, name, kind, CASE WHEN name LIKE $prefix ESCAPE '\' THEN 0 ELSE 1 END AS rank
            FROM obj WHERE profile_id=$p AND database=$d AND name LIKE $sub ESCAPE '\'{kindFilter}
              AND ($schema IS NULL OR schema_name LIKE $schema)
            ORDER BY rank, length(name), name LIMIT $limit
            """, null, ("$p", profileId), ("$d", database), ("$prefix", like + "%"), ("$sub", "%" + like + "%"), ("$schema", schema),
            ("$limit", Math.Clamp(limit, 1, 1000)));
        using var r = cmd.ExecuteReader();
        var list = new List<ObjectRef>();
        while (r.Read())
        {
            if (!EnumText.TryParse<ObjectKind>(r.Str(3), out var kind)) continue; // a newer build's kind: never another one
            list.Add(new ObjectRef
            {
                ProfileId = profileId, Database = database, ObjectId = r.GetInt64(0), Schema = r.Str(1) ?? "", Name = r.Str(2) ?? "",
                Kind = kind,
            });
        }
        return list.ToArray();
    }

    public void CacheDefinition(string profileId, string database, long objectId, string definition)
    {
        using var c = store.Open();
        c.Cmd("INSERT OR REPLACE INTO definition_cache (profile_id, database, object_id, definition, fetched_at) VALUES ($p,$d,$o,$def,$t)", null,
            ("$p", profileId), ("$d", database), ("$o", objectId), ("$def", definition), ("$t", Time.NowMs())).ExecuteNonQuery();
    }

    /// <summary>Views, routines and triggers of the snapshot whose definition is not cached yet.</summary>
    public long[] MissingDefinitionIds(string profileId, string database)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("""
            SELECT o.object_id FROM obj o
            LEFT JOIN definition_cache d ON d.profile_id = o.profile_id AND d.database = o.database AND d.object_id = o.object_id
            WHERE o.profile_id=$p AND o.database=$d AND o.kind IN ('view','proc','func','tvf','trigger','package','packageBody') AND d.object_id IS NULL
            """, null, ("$p", profileId), ("$d", database));
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list.ToArray();
    }

    /// <summary>Many definitions in one transaction; a null definition records "not available" (encrypted).</summary>
    public void CacheDefinitions(string profileId, string database, IEnumerable<(long Id, string? Definition)> definitions)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.Cmd("INSERT OR REPLACE INTO definition_cache (profile_id, database, object_id, definition, fetched_at) VALUES ($p,$d,$o,$def,$t)", tx);
        Param(cmd, "$p", profileId); Param(cmd, "$d", database); Param(cmd, "$t", Time.NowMs());
        var o = Param(cmd, "$o"); var def = Param(cmd, "$def");
        foreach (var (id, text) in definitions)
        {
            o.Value = id; def.Value = (object?)text ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Cached definitions by object id (null: encrypted / not available).</summary>
    public Dictionary<long, string?> Definitions(string profileId, string database)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("SELECT object_id, definition FROM definition_cache WHERE profile_id=$p AND database=$d", null, ("$p", profileId), ("$d", database));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, string?>();
        while (r.Read()) map[r.GetInt64(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return map;
    }

    /// <summary>Drops everything recorded for (profile, database): a closed session's own view (Phase D0d).</summary>
    public void Forget(string profileId, string database)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        foreach (var t in new[] { "schema_snapshot", "obj", "col", "param", "fk", "idx", "definition_cache" })
            c.Cmd($"DELETE FROM {t} WHERE profile_id=$p AND database=$d", tx, ("$p", profileId), ("$d", database)).ExecuteNonQuery();
        tx.Commit();
    }

    public string[] KnownDatabases(string profileId)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("SELECT database FROM schema_snapshot WHERE profile_id=$p ORDER BY database", null, ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list.ToArray();
    }
}

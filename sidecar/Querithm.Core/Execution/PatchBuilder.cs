using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Execution;

public enum PatchStatementKind { Delete, Update, Insert }

/// <summary>A value sent with a statement, typed by its table column.</summary>
public sealed record PatchValue(string? Type, GridValue Value);

/// <summary>One generated statement: SQL with placeholders, its parameter values in placeholder order, and the grid row it belongs to.</summary>
public sealed record PatchStatement(PatchStatementKind Kind, int Row, string Sql, IReadOnlyList<PatchValue> Values);

/// <summary>
/// Turns staged grid edits into statements: deletes, then updates, then inserts (so a key freed by a
/// delete can be reused). Only names from <see cref="ResultEditInfo"/> (the sidecar's own metadata) reach the SQL
/// text; names from the patch are only looked up, and every value is a parameter.
/// </summary>
public static class PatchBuilder
{
    public static List<PatchStatement> Build(IDataEditDialect d, ResultEditInfo edit, GridPatchParams p, bool force)
    {
        if (!edit.Editable || edit.Schema == null || edit.Table == null) throw QuerithmException.Invalid(edit.Reason ?? "This result set is read-only.");
        var table = $"{d.Quote(edit.Schema)}.{d.Quote(edit.Table)}";
        var byName = edit.Columns.Where(c => c.Column != null).GroupBy(c => c.Column!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var keyCols = edit.KeyColumns.Select(k => byName.TryGetValue(k, out var c) ? c : throw QuerithmException.Invalid($"Key column {k} is not in the result.")).ToArray();
        var list = new List<PatchStatement>();

        foreach (var del in p.Deletes)
        {
            var values = new List<PatchValue>();
            var where = KeyPredicate(d, keyCols, del.Key, values);
            where += Concurrency(d, edit, byName, del.Token, del.Original, null, values, force);
            list.Add(new PatchStatement(PatchStatementKind.Delete, del.RowIndex, $"DELETE FROM {table} WHERE {where}", values));
        }

        foreach (var up in p.Updates)
        {
            if (up.Changes.Count == 0) continue;
            var values = new List<PatchValue>();
            var sets = new List<string>();
            foreach (var (name, v) in up.Changes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var c = Writable(byName, name);
                if (v.UseDefault) throw QuerithmException.Invalid($"DEFAULT is only allowed on inserted rows ({name}).");
                if (v.IsNull && !c.Nullable) throw QuerithmException.Invalid($"{name} does not allow NULL.");
                sets.Add($"{d.Quote(name)} = {Add(d, values, c.Type, v)}");
            }
            var where = KeyPredicate(d, keyCols, up.Key, values);
            where += Concurrency(d, edit, byName, up.Token, up.Original, up.Changes.Keys, values, force);
            list.Add(new PatchStatement(PatchStatementKind.Update, up.RowIndex, $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {where}", values));
        }

        if (p.Inserts.Length > 0 && edit.InsertBlockedReason != null) throw QuerithmException.Invalid(edit.InsertBlockedReason);
        foreach (var ins in p.Inserts)
        {
            var values = new List<PatchValue>();
            var cols = new List<(string, string)>();
            foreach (var (name, v) in ins.Values.OrderBy(kv => byName.TryGetValue(kv.Key, out var c) ? c.Ordinal : int.MaxValue))
            {
                var c = Writable(byName, name);
                if (v.UseDefault) continue;
                if (v.IsNull && !c.Nullable) throw QuerithmException.Invalid($"{name} does not allow NULL.");
                cols.Add((name, Add(d, values, c.Type, v)));
            }
            var sql = d.InsertReturningKey(table, cols, keyCols.Select(k => (k.Column!, k.Type, k.Generated)).ToList());
            list.Add(new PatchStatement(PatchStatementKind.Insert, ins.TempIndex, sql, values));
        }
        return list;
    }

    static EditColumn Writable(Dictionary<string, EditColumn> byName, string name)
    {
        if (!byName.TryGetValue(name, out var c)) throw QuerithmException.Invalid($"Unknown column {name}: it is not a column of this result's table.");
        if (c.Locked) throw QuerithmException.Invalid($"{name} cannot be written: {c.LockReason}.");
        return c;
    }

    static string Add(IDataEditDialect d, List<PatchValue> values, string? type, GridValue v)
    {
        values.Add(new PatchValue(type, v));
        return d.TypedPlaceholder(values.Count - 1, type);
    }

    static string KeyPredicate(IDataEditDialect d, EditColumn[] keyCols, GridValue[] key, List<PatchValue> values)
    {
        if (key.Length != keyCols.Length) throw QuerithmException.Invalid($"A row key needs {keyCols.Length} value(s), got {key.Length}.");
        var parts = new List<string>();
        for (var i = 0; i < keyCols.Length; i++)
        {
            if (key[i].IsNull || key[i].Text == null) throw QuerithmException.Invalid($"Key column {keyCols[i].Column} has no value.");
            parts.Add($"{d.Quote(keyCols[i].Column!)} = {Add(d, values, keyCols[i].Type, key[i])}");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>The lost-update check: the row's token, else the original values of the changed (update) or all comparable (delete) columns.</summary>
    static string Concurrency(IDataEditDialect d, ResultEditInfo edit, Dictionary<string, EditColumn> byName, string? token, Dictionary<string, GridValue>? original,
        IEnumerable<string>? changed, List<PatchValue> values, bool force)
    {
        if (force) return "";
        if (edit.TokenKind != null)
        {
            if (string.IsNullOrEmpty(token)) throw QuerithmException.Invalid("Every edited row needs its version token (data.rowTokens) before it can be written.");
            values.Add(new PatchValue(d.TokenType, new GridValue { Text = token }));
            return " AND " + d.TokenMatch("", edit.TokenColumn ?? "", d.Placeholder(values.Count - 1));
        }
        var sb = new StringBuilder();
        var names = changed?.ToArray() ?? original?.Keys.ToArray() ?? [];
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(name, out var c) || !c.Comparable) continue;
            if (original == null || !original.TryGetValue(name, out var v))
            {
                if (changed != null) throw QuerithmException.Invalid($"The original value of {name} is needed to detect concurrent changes.");
                continue;
            }
            sb.Append(" AND ").Append(d.NullSafeEquals(d.Quote(name), Add(d, values, c.Type, v)));
        }
        return sb.ToString();
    }

    /// <summary>The preview: each statement with its values as literals, never executed (values are sent as parameters).</summary>
    public static string Preview(IDataEditDialect d, IReadOnlyList<PatchStatement> statements, string? header)
    {
        var sb = new StringBuilder();
        if (header != null) sb.Append(header).Append('\n');
        foreach (var s in statements)
        {
            var text = s.Sql;
            // Placeholders are replaced from the highest index down so @p1 does not match the start of @p10.
            for (var i = s.Values.Count - 1; i >= 0; i--)
                text = ReplacePlaceholder(text, d.Placeholder(i), d.PreviewLiteral(s.Values[i].Type, s.Values[i].Value));
            sb.Append(text);
            if (!text.TrimEnd().EndsWith(d.PreviewTerminator, StringComparison.Ordinal)) sb.Append(d.PreviewTerminator);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Replaces a placeholder we generated (not part of a longer one). Our SQL holds no literals, so this text is ours alone.</summary>
    static string ReplacePlaceholder(string text, string placeholder, string literal)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (true)
        {
            var at = text.IndexOf(placeholder, i, StringComparison.Ordinal);
            if (at < 0) break;
            var end = at + placeholder.Length;
            if (end < text.Length && char.IsDigit(text[end]))
            {
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            sb.Append(text, i, at - i).Append(literal);
            i = end;
        }
        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }
}

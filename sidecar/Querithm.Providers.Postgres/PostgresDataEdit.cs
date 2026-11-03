using System.Data.Common;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Grid edits on PostgreSQL: values travel as text parameters cast to the catalog type in the statement
/// ($1::integer), so the server's own input functions parse them; every row carries xmin as its version token.
/// </summary>
public sealed class PostgresDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string Placeholder(int index) => "$" + (index + 1).ToString(CultureInfo.InvariantCulture);

    public string TypedPlaceholder(int index, string? columnType) =>
        SafeType(columnType) is { } t ? $"{Placeholder(index)}::{t}" : Placeholder(index);

    /// <summary>
    /// A cast target taken from the catalog (format_type output). It comes from the sidecar's own metadata, never from
    /// the webview; anything that could end the cast is refused anyway.
    /// </summary>
    static string? SafeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (type.Contains(';') || type.Contains("--") || type.Contains("/*") || type.Contains('\'')) return null;
        return type;
    }

    /// <summary>The base type name without arguments or array brackets ("character varying(20)[]" → "character varying").</summary>
    public static string BaseName(string? type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        var cut = t.IndexOfAny(['(', '[']);
        return (cut < 0 ? t : t[..cut]).Trim();
    }

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value)
    {
        var text = value.IsNull || value.Text == null ? null : ToInput(BaseName(columnType), value.Text);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = (object?)text ?? DBNull.Value });
    }

    /// <summary>The grid's text → the server's input syntax where the two differ (bytea is shown as 0x…, input as \x…).</summary>
    static string ToInput(string baseName, string text)
    {
        if (baseName == "bytea")
        {
            if (text.EndsWith('…')) throw new FormatException("The binary value is shortened in the grid.");
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "\\x" + text[2..] : text;
        }
        return text;
    }

    public (string? Kind, string? Column) Token(SnapObject table) => ("xmin", "xmin");

    static string Prefix(string alias) => alias.Length > 0 ? alias + "." : "";

    public string TokenText(string alias, string column) => $"{Prefix(alias)}xmin::text";

    public string TokenMatch(string alias, string column, string valueExpr) => $"{Prefix(alias)}xmin = {valueExpr}::xid";

    public string? TokenType => null;

    public string NullSafeEquals(string left, string right) => $"{left} IS NOT DISTINCT FROM {right}";

    public string ValueText(string expr, string? columnType) => BaseName(columnType) == "bytea"
        ? $"('0x' || encode({expr}, 'hex'))"
        : $"({expr})::text";

    public string? LockReason(SnapColumn column) => null;

    // No equality operator (json, xml, geometric types), or a form the grid cannot reproduce exactly (float, bytea).
    static readonly HashSet<string> NotComparable =
    [
        "json", "xml", "point", "line", "lseg", "box", "path", "polygon", "circle", "bytea", "real", "double precision",
        "float4", "float8",
    ];

    public bool Comparable(string? columnType) => !NotComparable.Contains(BaseName(columnType));

    public bool ExactKey(string? columnType) => BaseName(columnType) is not ("real" or "double precision" or "float4" or "float8" or "bytea" or "json" or "xml");

    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) =>
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        var sb = new StringBuilder("INSERT INTO ").Append(table);
        if (columns.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", columns.Select(c => Quote(c.Column)))).Append(") VALUES (");
            sb.Append(string.Join(", ", columns.Select(c => c.ValueExpr))).Append(')');
        }
        else sb.Append(" DEFAULT VALUES");
        sb.Append(" RETURNING ").Append(string.Join(", ", key.Select(k => ValueText(Quote(k.Column), k.Type))));
        return sb.ToString();
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        // TypedPlaceholder already carries the cast, so the literal stays plain.
        return Literal(ToInput(BaseName(columnType), value.Text));
    }

    /// <summary>
    /// A string literal that reads the same whatever <c>standard_conforming_strings</c> is (L7): with it off, a backslash in
    /// '…' is an escape, so the text <c>\'</c> (rendered <c>\''</c>) would end the literal early. Text with a backslash becomes an escape string (E'…', valid in
    /// every mode) with each backslash doubled; any other text stays a plain literal. Quotes are doubled in both.
    /// </summary>
    internal static string Literal(string s) =>
        s.Contains('\\') ? "E'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'" : "'" + s.Replace("'", "''") + "'";

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) =>
        "Rows are matched on their key and xmin: a row someone else changed since it was read is reported as a conflict, not overwritten.";
}

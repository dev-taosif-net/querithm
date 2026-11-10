using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Querithm.Contract;

namespace Querithm.Core.Store;

public static class EnumText
{
    public static string Of<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static T Parse<T>(string? text, T fallback) where T : struct, Enum =>
        TryParse<T>(text, out var v) ? v : fallback;

    /// <summary>
    /// A member name as <see cref="Of{T}"/> writes it (case-insensitive). Numbers and unknown names are refused: a value
    /// written by a newer build must never turn into some other member (Phase D0a).
    /// </summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(text) || !char.IsAsciiLetter(text[0]) || text.Contains(',')) return false; // no numbers, no flag lists
        return Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}

/// <summary>Connection profiles. No secret ever touches this table (§13 rule 1).</summary>
public sealed class ProfileStore(LocalStore store)
{
    /// <summary>Raised with the profile's id after it was saved or deleted: in-memory copies of stored profiles drop it.</summary>
    public event Action<string>? Changed;

    const string Columns = "id, name, engine, host, port, auth, username, database_default, environment, color, group_name, tls_mode, trust_cert, timeout_s, options_json, created_at, updated_at, location, file_grant_id";

    public Profile[] List()
    {
        using var c = store.Open();
        using var cmd = c.Cmd($"SELECT {Columns} FROM profile ORDER BY group_name, name");
        using var r = cmd.ExecuteReader();
        var list = new List<Profile>();
        while (r.Read()) list.Add(Read(r));
        return list.ToArray();
    }

    public Profile? Get(string id)
    {
        using var c = store.Open();
        using var cmd = c.Cmd($"SELECT {Columns} FROM profile WHERE id = $id", null, ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public Profile Save(Profile p)
    {
        if (p.Unsupported != null) throw new ArgumentException(p.Unsupported);
        if (p.PasswordRetention is not (null or "once" or "session" or "duration" or "forever"))
            throw new ArgumentException("Invalid password retention.");
        if (p.PasswordRetention == "duration" && p.PasswordRetentionMinutes is not (>= 1 and <= 525600))
            throw new ArgumentException("Password duration must be between 1 and 525600 minutes.");
        var now = Time.NowMs();
        var saved = p with
        {
            Id = string.IsNullOrWhiteSpace(p.Id) ? Ulid.NewId() : p.Id,
            CreatedAt = p.CreatedAt == 0 ? now : p.CreatedAt,
            UpdatedAt = now,
            TimeoutS = p.TimeoutS ?? 15,
            CommandTimeoutS = p.CommandTimeoutS ?? 0,
        };
        using var c = store.Open();
        using var cmd = c.Cmd($"""
            INSERT INTO profile ({Columns}) VALUES ($id,$name,$engine,$host,$port,$auth,$user,$db,$env,$color,$group,$tls,$trust,$timeout,$options,$created,$updated,$location,$grant)
            ON CONFLICT(id) DO UPDATE SET name=$name, engine=$engine, host=$host, port=$port, auth=$auth, username=$user,
              database_default=$db, environment=$env, color=$color, group_name=$group, tls_mode=$tls, trust_cert=$trust,
              timeout_s=$timeout, options_json=$options, updated_at=$updated, location=$location, file_grant_id=$grant
            """, null,
            ("$id", saved.Id), ("$name", saved.Name), ("$engine", EnumText.Of(saved.Engine)), ("$host", saved.Host), ("$port", saved.Port),
            ("$auth", EnumText.Of(saved.Auth)), ("$user", saved.Username), ("$db", saved.DatabaseDefault), ("$env", EnumText.Of(saved.Environment)),
            ("$color", saved.Color), ("$group", saved.GroupName), ("$tls", EnumText.Of(saved.TlsMode)), ("$trust", saved.TrustCert ? 1 : 0),
            ("$timeout", saved.TimeoutS), ("$options", JsonSerializer.Serialize(new StoredOptions(saved.Options, saved.CaPath, saved.CommandTimeoutS, saved.Ssh, saved.PasswordRetention,
                saved.PasswordRetentionMinutes, saved.File?.DisplayName), StoreJsonContext.Default.StoredOptions)),
            ("$created", saved.CreatedAt), ("$updated", saved.UpdatedAt),
            // Server stays NULL: what every profile before Phase D reads as.
            ("$location", saved.Location is { } loc and not LocationKind.Server ? EnumText.Of(loc) : null), ("$grant", saved.File?.GrantId));
        cmd.ExecuteNonQuery();
        Changed?.Invoke(saved.Id);
        return saved;
    }

    public void Delete(string id)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        foreach (var table in new[] { "profile" })
            c.Cmd($"DELETE FROM {table} WHERE id = $id", tx, ("$id", id)).ExecuteNonQuery();
        // Everything recorded for the profile goes with it: metadata cache, query history (FTS rows via trigger), script bindings.
        foreach (var table in new[] { "schema_snapshot", "obj", "col", "param", "fk", "idx", "definition_cache", "history", "script" })
            c.Cmd($"DELETE FROM {table} WHERE profile_id = $id", tx, ("$id", id)).ExecuteNonQuery();
        tx.Commit();
        Changed?.Invoke(id);
    }

    internal sealed record StoredOptions(Dictionary<string, string>? Options, string? CaPath, int? CommandTimeoutS, SshSettings? Ssh = null, string? PasswordRetention = null,
        int? PasswordRetentionMinutes = null, string? FileDisplayName = null);

    /// <summary>
    /// A stored row as a profile. An engine, sign-in or location this build does not know (a newer build wrote it) is never
    /// read as some other value: the profile carries <see cref="Profile.Unsupported"/> and is never opened (Phase D0a).
    /// </summary>
    static Profile Read(SqliteDataReader r)
    {
        string? unsupported = null;
        StoredOptions? opts = null;
        // A row whose options no longer parse is listed as unsupported (it can be deleted), never failing the whole list.
        try { opts = r.Str(14) is { } json ? JsonSerializer.Deserialize(json, StoreJsonContext.Default.StoredOptions) : null; }
        catch (JsonException) { unsupported = "This profile's stored settings cannot be read (they were changed outside Querithm). Delete it and create it again."; }
        if (!EnumText.TryParse<Engine>(r.Str(2), out var engine))
            unsupported = $"This profile is for an engine this version of Querithm does not know ('{Clip(r.Str(2))}'). It was probably saved by a newer version.";
        // Legacy rows may have no auth: they were SQL logins (the enum default) before auth kinds existed.
        AuthKind auth = AuthKind.Sql;
        if (r.Str(5) is { } authText && !EnumText.TryParse(authText, out auth))
            unsupported ??= $"This profile uses a sign-in this version of Querithm does not know ('{Clip(authText)}'). It was probably saved by a newer version.";
        LocationKind? location = null;
        if (r.Str(17) is { } locText)
        {
            if (EnumText.TryParse<LocationKind>(locText, out var loc)) location = loc;
            else unsupported ??= $"This profile uses a database location this version of Querithm does not know ('{Clip(locText)}').";
        }
        // options_json keeps enums as numbers: an SSH sign-in no member names (PT8-6: "privateKey,agent" was stored as 3)
        // is never read as another one, and never reaches a reply the enum converter would refuse to write.
        var ssh = opts?.Ssh;
        if (ssh != null && !Enum.IsDefined(ssh.Auth))
        {
            unsupported ??= "This profile uses an SSH sign-in this version of Querithm does not know. Delete it and create it again.";
            ssh = ssh with { Auth = SshAuthKind.Password };
        }
        return new Profile
        {
            Id = r.GetString(0),
            Name = r.GetString(1),
            Engine = engine,
            Location = location,
            File = r.Str(18) is { } grant ? new FileLocation { GrantId = grant, DisplayName = opts?.FileDisplayName } : null,
            Unsupported = unsupported,
            Host = r.Str(3),
            Port = r.Int(4),
            Auth = auth,
            Username = r.Str(6),
            DatabaseDefault = r.Str(7),
            Environment = EnumText.Parse(r.Str(8), EnvironmentKind.Dev),
            Color = r.Str(9),
            GroupName = r.Str(10),
            TlsMode = EnumText.Parse(r.Str(11), TlsMode.Default),
            TrustCert = r.Bool(12),
            TimeoutS = r.Int(13),
            PasswordRetention = opts?.PasswordRetention,
            PasswordRetentionMinutes = opts?.PasswordRetentionMinutes,
            Options = opts?.Options,
            CaPath = opts?.CaPath,
            CommandTimeoutS = opts?.CommandTimeoutS,
            Ssh = ssh,
            CreatedAt = r.Long(15) ?? 0,
            UpdatedAt = r.Long(16) ?? 0,
        };
    }

    static string Clip(string? text) => text is { Length: > 32 } t ? t[..32] + "…" : text ?? "";
}

public sealed class SettingsStore(LocalStore store)
{
    public SettingEntry[] Get(string[]? keys)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("SELECT key, value_json FROM setting");
        using var r = cmd.ExecuteReader();
        var list = new List<SettingEntry>();
        while (r.Read())
        {
            var key = r.GetString(0);
            if (keys == null || keys.Contains(key)) list.Add(new SettingEntry { Key = key, ValueJson = r.GetString(1) });
        }
        return list.ToArray();
    }

    public void Set(string key, string valueJson)
    {
        using var _ = JsonDocument.Parse(valueJson); // validate
        using var c = store.Open();
        c.Cmd("INSERT INTO setting (key, value_json) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value_json = $v", null,
            ("$k", key), ("$v", valueJson)).ExecuteNonQuery();
    }

    /// <summary>Typed access in the wire format; pass metadata from <see cref="Json.Context"/>.</summary>
    public T? Read<T>(string key, JsonTypeInfo<T> type)
    {
        var e = Get([key]).FirstOrDefault();
        return e == null ? default : JsonSerializer.Deserialize(e.ValueJson, type);
    }

    public void Write<T>(string key, T value, JsonTypeInfo<T> type) => Set(key, JsonSerializer.Serialize(value, type));
}

/// <summary>
/// Query history (§8). L17: list and search send previews (the stored <c>preview</c> column, or the first
/// <see cref="HistoryLimits.PreviewChars"/> characters of a row an older build wrote), history.get the whole text; a
/// retention pass deletes entries past the age and count limits in short batches, off the RPC and run paths.
/// </summary>
public sealed class HistoryStore(LocalStore store, ILogger? log = null)
{
    const string RetentionKey = "history.retention";
    /// <summary>Set once the user was told that retention deletes entries (the first pass that deleted any, ever).</summary>
    const string PrunedNoticeKey = "history.retentionNoticed";

    /// <summary>
    /// 8a review: the first retention pass that deletes entries — on an upgraded store that is entries past 90 days, gone
    /// without a word — raises one notice naming the setting; never again on this store.
    /// </summary>
    public event Action<SystemNotice>? Notice;
    int _sincePrune;
    Task _prune = Task.CompletedTask;
    readonly object _pruneLock = new();
    bool _pruning, _pruneAgain;

    /// <summary>Test seam: the clock entries are stamped with and retention compares against.</summary>
    internal Func<long> Now { get; set; } = Time.NowMs;

    public long Add(string? profileId, string? database, string sql, double elapsedMs, long? rows, string status, string? error)
    {
        long id;
        using (var c = store.Open())
        using (var cmd = c.Cmd("""
            INSERT INTO history (profile_id, database, executed_at, sql, elapsed_ms, rows_affected, status, error, preview, sql_length)
            VALUES ($p, $d, $t, $sql, $e, $r, $s, $err, $pre, $len); SELECT last_insert_rowid();
            """, null, ("$p", profileId), ("$d", database), ("$t", Now()), ("$sql", sql), ("$e", (long)elapsedMs),
            ("$r", rows), ("$s", status), ("$err", error), ("$pre", Preview(sql)), ("$len", (long)sql.Length)))
            id = (long)cmd.ExecuteScalar()!;
        if (Interlocked.Increment(ref _sincePrune) >= HistoryLimits.PruneEvery)
        {
            Interlocked.Exchange(ref _sincePrune, 0);
            SchedulePrune();
        }
        return id;
    }

    /// <summary>The preview of a text: its first characters, never ending inside a surrogate pair.</summary>
    static string Preview(string sql)
    {
        if (sql.Length <= HistoryLimits.PreviewChars) return sql;
        var n = HistoryLimits.PreviewChars;
        if (char.IsHighSurrogate(sql[n - 1])) n--;
        return sql[..n];
    }

    // An older build's rows have no preview: cut on read (substr counts characters, as Preview does).
    static readonly string Columns = $"""
        h.id, h.profile_id, h.database, h.executed_at, COALESCE(h.preview, substr(h.sql, 1, {HistoryLimits.PreviewChars})),
        h.elapsed_ms, h.rows_affected, h.status, h.error, COALESCE(h.sql_length, length(h.sql))
        """;

    public HistoryEntry[] List(HistoryListParams p)
    {
        using var c = store.Open();
        using var cmd = c.Cmd($"""
            SELECT {Columns} FROM history h
            WHERE ($p IS NULL OR h.profile_id = $p) AND ($d IS NULL OR h.database = $d) AND ($before IS NULL OR h.id < $before)
            ORDER BY h.id DESC LIMIT $limit
            """, null, ("$p", p.ProfileId), ("$d", p.Database), ("$before", p.BeforeId), ("$limit", Math.Clamp(p.Limit ?? 200, 1, 2000)));
        return ReadAll(cmd);
    }

    public HistoryEntry[] Search(HistorySearchParams p)
    {
        var terms = p.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return List(new HistoryListParams { ProfileId = p.ProfileId, Limit = p.Limit });
        // Each term becomes a quoted FTS5 prefix query, so user text is never interpreted as FTS syntax.
        var match = string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));
        using var c = store.Open();
        using var cmd = c.Cmd($"""
            SELECT {Columns}
            FROM history_fts f JOIN history h ON h.id = f.rowid
            WHERE history_fts MATCH $m AND ($p IS NULL OR h.profile_id = $p)
            ORDER BY h.id DESC LIMIT $limit
            """, null, ("$m", match), ("$p", p.ProfileId), ("$limit", Math.Clamp(p.Limit ?? 200, 1, 2000)));
        return ReadAll(cmd);
    }

    /// <summary>One entry with its whole text.</summary>
    public HistoryEntry Get(long id)
    {
        using var c = store.Open();
        using var cmd = c.Cmd("""
            SELECT id, profile_id, database, executed_at, sql, elapsed_ms, rows_affected, status, error, length(sql)
            FROM history WHERE id = $id
            """, null, ("$id", id));
        return ReadAll(cmd, full: true).FirstOrDefault()
               ?? throw new QuerithmException("historyGone", "This history entry is no longer kept (history keeps the newest entries only).");
    }

    static HistoryEntry[] ReadAll(SqliteCommand cmd, bool full = false)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (r.Read())
        {
            var text = r.GetString(4);
            var length = r.Long(9) ?? text.Length;
            list.Add(new HistoryEntry
            {
                Id = r.GetInt64(0), ProfileId = r.Str(1), Database = r.Str(2), ExecutedAt = r.GetInt64(3), Sql = text,
                ElapsedMs = r.Long(5), RowsAffected = r.Long(6), Status = r.Str(7) ?? "done", Error = r.Str(8),
                SqlLength = length, SqlTruncated = !full && length > text.Length,
            });
        }
        return list.ToArray();
    }

    // ------------------------------------------------------------------------------------------------ retention (L17)

    /// <summary>The stored retention; a missing or unreadable value (or one outside the limits) reads as the default.</summary>
    public HistoryRetention Retention()
    {
        try
        {
            using var c = store.Open();
            using var cmd = c.Cmd("SELECT value_json FROM setting WHERE key = $k", null, ("$k", RetentionKey));
            if (cmd.ExecuteScalar() is string json && JsonSerializer.Deserialize(json, Json.Context.HistoryRetention) is { } r && Refusal(r) == null) return r;
        }
        catch (JsonException) { /* unreadable: the default */ }
        return new HistoryRetention();
    }

    static string? Refusal(HistoryRetention r) =>
        r.Days is < HistoryLimits.MinDays or > HistoryLimits.MaxDays ? $"Keep history between {HistoryLimits.MinDays} and {HistoryLimits.MaxDays:N0} days."
        : r.MaxEntries is < HistoryLimits.MinEntries or > HistoryLimits.MaxEntries ? $"Keep between {HistoryLimits.MinEntries:N0} and {HistoryLimits.MaxEntries:N0} history entries."
        : null;

    /// <summary>Validates and stores the retention, then starts a pass that applies it.</summary>
    public HistoryRetention SetRetention(HistoryRetention r)
    {
        if (Refusal(r) is { } why) throw QuerithmException.Invalid(why);
        using (var c = store.Open())
            c.Cmd("INSERT INTO setting (key, value_json) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value_json = $v", null,
                ("$k", RetentionKey), ("$v", JsonSerializer.Serialize(r, Json.Context.HistoryRetention))).ExecuteNonQuery();
        SchedulePrune();
        return r;
    }

    /// <summary>
    /// Starts a retention pass on the thread pool (after <paramref name="delay"/>; at start the sidecar waits, so the pass
    /// stays off the cold start). One pass at a time: a request while one runs makes it run once more. The task completes
    /// when the passes end; a failing pass is logged, never thrown.
    /// </summary>
    public Task SchedulePrune(TimeSpan delay = default)
    {
        lock (_pruneLock)
        {
            if (_pruning) { _pruneAgain = true; return _prune; }
            _pruning = true;
            _prune = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
                    while (true)
                    {
                        try
                        {
                            var deleted = Prune();
                            if (deleted > 0)
                            {
                                log?.LogInformation("History retention deleted {Count} entries", deleted);
                                NoticeFirstPrune(deleted);
                            }
                        }
                        catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException)
                        {
                            log?.LogWarning("History retention pass failed ({Type}); the next one tries again", e.GetType().Name);
                        }
                        // Decided under the lock a request takes too, so a request made while this pass ends is never lost.
                        lock (_pruneLock)
                        {
                            if (!_pruneAgain) { _pruning = false; return; }
                            _pruneAgain = false;
                        }
                    }
                }
                catch (Exception e)
                {
                    // Anything else (the store disposed at shutdown): this pass ends, a later request starts a new one.
                    lock (_pruneLock) _pruning = false;
                    log?.LogWarning("History retention stopped ({Type})", e.GetType().Name);
                }
            });
            return _prune;
        }
    }

    void NoticeFirstPrune(int deleted)
    {
        int marked;
        using (var c = store.Open())
        using (var cmd = c.Cmd("INSERT OR IGNORE INTO setting (key, value_json) VALUES ($k, 'true')", null, ("$k", PrunedNoticeKey)))
            marked = cmd.ExecuteNonQuery();
        if (marked == 0) return; // told before
        var r = Retention();
        Notice?.Invoke(new SystemNotice
        {
            Code = "historyPruned",
            Message = $"Querithm deleted {deleted:N0} query history {(deleted == 1 ? "entry" : "entries")} past the history retention setting, which keeps {r.Days:N0} days and at most {r.MaxEntries:N0} entries. Change it under Retention in the History dialog to keep more.",
        });
    }

    /// <summary>
    /// One retention pass: entries older than the age limit, then the oldest beyond the count limit, each deleted
    /// <see cref="HistoryLimits.PruneBatch"/> rows per statement (its own short write transaction; the FTS trigger keeps the
    /// index in step). Returns how many were deleted.
    /// </summary>
    internal int Prune()
    {
        var r = Retention();
        var cutoff = Now() - r.Days * 86_400_000L;
        var total = 0;
        using var c = store.Open();
        long? Scalar(string sql, params (string, object?)[] ps) { using var cmd = c.Cmd(sql, null, ps); return cmd.ExecuteScalar() is long v ? v : null; }
        int DeleteUpTo(long last, string extra, params (string, object?)[] ps)
        {
            var n = 0;
            while (true)
            {
                using var cmd = c.Cmd($"DELETE FROM history WHERE id IN (SELECT id FROM history WHERE id <= $last{extra} ORDER BY id LIMIT $b)", null,
                    [("$last", last), ("$b", HistoryLimits.PruneBatch), .. ps]);
                var d = cmd.ExecuteNonQuery();
                n += d;
                if (d < HistoryLimits.PruneBatch) return n;
            }
        }
        if (Scalar("SELECT max(id) FROM history WHERE executed_at < $cut", ("$cut", cutoff)) is { } old)
            total += DeleteUpTo(old, " AND executed_at < $cut", ("$cut", cutoff));
        if (Scalar("SELECT id FROM history ORDER BY id DESC LIMIT 1 OFFSET $max", ("$max", (long)r.MaxEntries)) is { } beyond)
            total += DeleteUpTo(beyond, "");
        return total;
    }
}

/// <summary>
/// The snippet library (Phase A3): user snippets in the snippet table, the built-in set in code. Every save is validated
/// here, not in the webview; messages name the field, never the body (user data, kept out of logs and errors).
/// </summary>
public sealed class SnippetStore(LocalStore store)
{
    const string Columns = "id, title, prefix, body, dialect, tags_json, updated_at";

    /// <summary>Built-ins first, then the user's snippets by prefix.</summary>
    public Snippet[] List()
    {
        using var c = store.Open();
        using var cmd = c.Cmd($"SELECT {Columns} FROM snippet ORDER BY prefix COLLATE NOCASE");
        using var r = cmd.ExecuteReader();
        var list = new List<Snippet>(BuiltInSnippets.All);
        while (r.Read())
            if (Read(r) is { } s) list.Add(s);
        return list.ToArray();
    }

    /// <summary>
    /// Null for a snippet of a dialect this build does not know (a newer build saved it): it stays in the table for that
    /// build and is never offered here, rather than turning into a T-SQL snippet (Phase D0a).
    /// </summary>
    static Snippet? Read(SqliteDataReader r)
    {
        Dialect? dialect = null;
        if (r.Str(4) is { } d)
        {
            if (!EnumText.TryParse<Dialect>(d, out var known)) return null;
            dialect = known;
        }
        return new()
        {
            Id = r.GetString(0), Title = r.GetString(1), Prefix = r.GetString(2), Body = r.GetString(3), Dialect = dialect,
            Tags = JsonSerializer.Deserialize(r.Str(5) ?? "[]", StoreJsonContext.Default.StringArray) ?? [], UpdatedAt = r.Long(6) ?? 0,
        };
    }

    static QuerithmException Invalid(string field, string message) => new("invalid", message, new Dictionary<string, string> { ["field"] = field });

    /// <summary>The snippet as it would be stored, or the first reason it cannot be.</summary>
    public static Snippet Validate(Snippet s)
    {
        var title = (s.Title ?? "").Trim();
        if (title.Length == 0) throw Invalid("title", "Give the snippet a title.");
        if (title.Length > SnippetLimits.Title) throw Invalid("title", $"Titles are at most {SnippetLimits.Title} characters.");
        if (title.Any(char.IsControl)) throw Invalid("title", "A title cannot contain line breaks or control characters.");
        // The prefix is a plain string, not SQL: checked character by character.
        var prefix = s.Prefix ?? "";
        if (prefix.Length is 0 or > SnippetLimits.Prefix || !prefix.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            throw Invalid("prefix", $"The prefix is 1–{SnippetLimits.Prefix} letters, digits or underscores (what you type to insert the snippet).");
        var body = s.Body ?? "";
        if (body.Trim().Length == 0) throw Invalid("body", "The snippet body is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(body) > SnippetLimits.BodyBytes) throw Invalid("body", $"The body is larger than {SnippetLimits.BodyBytes / 1024} KB.");
        if (body.Contains('\0')) throw Invalid("body", "The body contains a NUL character.");
        var tags = (s.Tags ?? []).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length > SnippetLimits.Tags) throw Invalid("tags", $"At most {SnippetLimits.Tags} tags.");
        if (tags.Any(t => t.Length > SnippetLimits.Tag || t.Any(char.IsControl))) throw Invalid("tags", $"Tags are at most {SnippetLimits.Tag} characters, without line breaks.");
        if (s.Dialect is { } d && !Enum.IsDefined(d)) throw Invalid("dialect", "Unknown dialect.");
        return s with { Title = title, Prefix = prefix, Body = body, Tags = tags, BuiltIn = false };
    }

    static QuerithmException Taken(string prefix, string title) =>
        new("snippetPrefixTaken", $"The prefix '{prefix}' is already used by \"{title}\".", new Dictionary<string, string> { ["field"] = "prefix" });

    public Snippet Save(Snippet input)
    {
        var s = Validate(input);
        if ((s.Id ?? "").StartsWith(BuiltInSnippets.IdPrefix, StringComparison.Ordinal))
            throw new QuerithmException("snippetReadOnly", "Built-in snippets are read-only: save a copy under another prefix.");
        // A built-in of an overlapping dialect owns its prefix (a snippet for every dialect overlaps all of them).
        if (BuiltInSnippets.All.FirstOrDefault(b => string.Equals(b.Prefix, s.Prefix, StringComparison.OrdinalIgnoreCase) && (s.Dialect == null || b.Dialect == s.Dialect)) is { } clash)
            throw Taken(s.Prefix, clash.Title);
        var now = Time.NowMs();
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        if (string.IsNullOrEmpty(s.Id))
        {
            if ((long)c.Cmd("SELECT count(*) FROM snippet", tx).ExecuteScalar()! >= SnippetLimits.Count)
                throw new QuerithmException("snippetLimit", $"The library holds at most {SnippetLimits.Count:N0} snippets: delete some first.");
            s = s with { Id = Ulid.NewId() };
        }
        else if (s.Id.Length > 64 || c.Cmd("SELECT 1 FROM snippet WHERE id = $id", tx, ("$id", s.Id)).ExecuteScalar() == null)
            throw QuerithmException.NotFound("Snippet", s.Id.Length > 64 ? s.Id[..64] : s.Id);
        // Same rule among user snippets: two snippets clash when a tab could offer both (BeginTransaction is IMMEDIATE,
        // so a concurrent save waits rather than slipping past this check).
        var dialectText = s.Dialect is { } sd ? EnumText.Of(sd) : null;
        if (c.Cmd("SELECT title FROM snippet WHERE prefix = $p COLLATE NOCASE AND id <> $id AND ($d IS NULL OR dialect IS NULL OR dialect = $d)", tx,
                ("$p", s.Prefix), ("$id", s.Id), ("$d", dialectText)).ExecuteScalar() is string other)
            throw Taken(s.Prefix, other);
        s = s with { UpdatedAt = now };
        c.Cmd("""
            INSERT INTO snippet (id, title, prefix, body, dialect, tags_json, created_at, updated_at) VALUES ($id, $t, $p, $b, $d, $tags, $now, $now)
            ON CONFLICT(id) DO UPDATE SET title = $t, prefix = $p, body = $b, dialect = $d, tags_json = $tags, updated_at = $now
            """, tx, ("$id", s.Id), ("$t", s.Title), ("$p", s.Prefix), ("$b", s.Body), ("$d", s.Dialect is { } dl ? EnumText.Of(dl) : null),
            ("$tags", JsonSerializer.Serialize(s.Tags, StoreJsonContext.Default.StringArray)), ("$now", now)).ExecuteNonQuery();
        tx.Commit();
        return s;
    }

    public void Delete(string id)
    {
        if (id.StartsWith(BuiltInSnippets.IdPrefix, StringComparison.Ordinal)) throw new QuerithmException("snippetReadOnly", "Built-in snippets cannot be deleted.");
        using var c = store.Open();
        if (c.Cmd("DELETE FROM snippet WHERE id = $id", null, ("$id", id)).ExecuteNonQuery() == 0)
            throw QuerithmException.NotFound("Snippet", id.Length > 64 ? id[..64] : id);
    }
}

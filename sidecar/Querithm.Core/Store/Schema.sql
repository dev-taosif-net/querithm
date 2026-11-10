-- Local store (§8). One SQLite file, WAL mode, owned by the sidecar. The current schema (user_version = LocalStore.Version);
-- LocalStore migrates older files to it before this script runs.
CREATE TABLE IF NOT EXISTS profile (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, engine TEXT NOT NULL,          -- EnumText of Engine; unknown text = unsupported profile
  host TEXT, port INTEGER, auth TEXT, username TEXT,                      -- no password column, ever
  database_default TEXT, environment TEXT, color TEXT, group_name TEXT,
  tls_mode TEXT, trust_cert INTEGER, timeout_s INTEGER, options_json TEXT,
  created_at INTEGER, updated_at INTEGER,
  location TEXT,                                                          -- NULL = server | 'file' | 'memory' (v4)
  file_grant_id TEXT                                                      -- the shell's grant for a file database; never a path (v4)
);

CREATE TABLE IF NOT EXISTS schema_snapshot (
  profile_id TEXT, database TEXT, refreshed_at INTEGER, server_version TEXT,
  status TEXT,                                                            -- 'fresh' | 'stale' | 'refreshing' | 'error'
  error TEXT,
  notes TEXT,                                                             -- JSON array: what the last refresh could not read (v7)
  PRIMARY KEY (profile_id, database)
);

CREATE TABLE IF NOT EXISTS obj (
  profile_id TEXT, database TEXT, object_id INTEGER,
  schema_name TEXT, name TEXT, kind TEXT,                                 -- 'table','view','proc','func','tvf','type','sequence','synonym','trigger'
  modify_date INTEGER, definition_hash TEXT, return_type TEXT,
  parent_object_id INTEGER,                                               -- a trigger's table or view (v2)
  no_rollback_engine TEXT,                                                -- MySQL / MariaDB: a table engine without transactions (v6)
  note TEXT,                                                              -- shown beside the name: invalid, materialized view … (v7)
  PRIMARY KEY (profile_id, database, object_id)
);
CREATE INDEX IF NOT EXISTS obj_name ON obj (profile_id, database, name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS col (
  profile_id TEXT, database TEXT, object_id INTEGER, ordinal INTEGER,
  name TEXT, type_name TEXT, max_len INTEGER, precision INTEGER, scale INTEGER,
  nullable INTEGER, is_identity INTEGER, is_computed INTEGER, default_expr TEXT,
  PRIMARY KEY (profile_id, database, object_id, ordinal)
);

CREATE TABLE IF NOT EXISTS param (
  profile_id TEXT, database TEXT, object_id INTEGER, ordinal INTEGER,
  name TEXT, type_name TEXT, max_len INTEGER, is_output INTEGER, has_default INTEGER,
  PRIMARY KEY (profile_id, database, object_id, ordinal)
);

CREATE TABLE IF NOT EXISTS fk (
  profile_id TEXT, database TEXT, name TEXT, parent_object_id INTEGER, referenced_object_id INTEGER,
  columns_json TEXT,                                                      -- [{parent:'x', referenced:'y'}]
  PRIMARY KEY (profile_id, database, name)
);

CREATE TABLE IF NOT EXISTS idx (
  profile_id TEXT, database TEXT, object_id INTEGER, name TEXT, is_unique INTEGER, is_primary INTEGER,
  columns_json TEXT,
  not_key INTEGER,                                                        -- partial / filtered, expression part, disabled: never a grid row key (v6)
  PRIMARY KEY (profile_id, database, object_id, name)
);

CREATE TABLE IF NOT EXISTS definition_cache (
  profile_id TEXT, database TEXT, object_id INTEGER, definition TEXT, fetched_at INTEGER,
  PRIMARY KEY (profile_id, database, object_id)
);

-- v5 (L17): preview (the first 500 characters) and sql_length, so lists never read whole texts; NULL in rows an older
-- build wrote (read with substr / length instead).
CREATE TABLE IF NOT EXISTS history (
  id INTEGER PRIMARY KEY, profile_id TEXT, database TEXT, executed_at INTEGER,
  sql TEXT, elapsed_ms INTEGER, rows_affected INTEGER, status TEXT, error TEXT,
  preview TEXT, sql_length INTEGER
);
CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5(sql, content='history', content_rowid='id');
CREATE TRIGGER IF NOT EXISTS history_ai AFTER INSERT ON history BEGIN
  INSERT INTO history_fts(rowid, sql) VALUES (new.id, new.sql);
END;
CREATE TRIGGER IF NOT EXISTS history_ad AFTER DELETE ON history BEGIN
  INSERT INTO history_fts(history_fts, rowid, sql) VALUES ('delete', old.id, old.sql);
END;

CREATE TABLE IF NOT EXISTS script (
  id TEXT PRIMARY KEY, path TEXT, title TEXT, profile_id TEXT, database TEXT,
  last_opened_at INTEGER, cursor_offset INTEGER
);

CREATE TABLE IF NOT EXISTS setting (key TEXT PRIMARY KEY, value_json TEXT);

-- Snippet library (v3). User data: bodies are never logged.
CREATE TABLE IF NOT EXISTS snippet (
  id TEXT PRIMARY KEY, title TEXT NOT NULL, prefix TEXT NOT NULL, body TEXT NOT NULL,
  dialect TEXT,                                                           -- 'tsql' | 'pg' | NULL (any)
  tags_json TEXT, created_at INTEGER, updated_at INTEGER
);
-- Prefixes are unique per dialect, where a NULL dialect overlaps every dialect: SnippetStore.Save checks that inside
-- its write transaction, which a plain index cannot express. (snippet_prefix was a global UNIQUE index in dev builds.)
DROP INDEX IF EXISTS snippet_prefix;
CREATE INDEX IF NOT EXISTS snippet_prefix_lookup ON snippet (prefix COLLATE NOCASE);

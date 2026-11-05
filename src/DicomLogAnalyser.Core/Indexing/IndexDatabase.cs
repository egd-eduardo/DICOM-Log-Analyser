using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Core.Indexing;

/// <summary>
/// The local SQLite index (WAL mode, one writer, many readers). It must be on a local disk: SQLite locking is not
/// reliable on network file systems, so <see cref="Storage.StorageGuard"/> refuses network locations at start.
/// </summary>
public sealed class IndexDatabase
{
    public const int SchemaVersion = 3;
    private readonly string _connectionString;

    public IndexDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    private long _dataVersion;

    /// <summary>Changes whenever indexed data changes; used to invalidate cached summaries.</summary>
    public long DataVersion => Interlocked.Read(ref _dataVersion);

    public void MarkChanged() => Interlocked.Increment(ref _dataVersion);

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public void Initialise()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        var version = Scalar<long>(c, "PRAGMA user_version;");
        if (version > SchemaVersion)
            throw new InvalidOperationException("The index was created by a newer version of this application. Update the application or delete the index.");
        if (version == SchemaVersion) return;

        using var tx = c.BeginTransaction();
        if (version == 0) Exec(c, Schema, tx);
        // Version 2: per-folder file selection (5-day limit) and counts of files left out.
        if (version == 1) Exec(c, MigrationTo2, tx);
        // Version 3: substring search (trigram full-text index); existing events are re-indexed in place.
        if (version is 1 or 2) Exec(c, MigrationTo3, tx);
        Exec(c, $"PRAGMA user_version={SchemaVersion};", tx);
        tx.Commit();
    }

    /// <summary>Removes every indexed event and file state; source definitions and the audit trail are kept.</summary>
    public void PurgeEvents()
    {
        MarkChanged();
        using var c = Open();
        using (var tx = c.BeginTransaction())
        {
            Exec(c, "DELETE FROM events; DELETE FROM files; INSERT INTO events_fts(events_fts) VALUES('delete-all');" +
                    "UPDATE sources SET last_scan_utc=NULL, last_scan_status=NULL, last_scan_message=NULL;", tx);
            tx.Commit();
        }

        Compact(c);
    }

    /// <summary>Returns freed pages to the file system and truncates the write-ahead log.</summary>
    public static void Compact(SqliteConnection c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static T Scalar<T>(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string MigrationTo2 = """
        ALTER TABLE sources ADD COLUMN selected_files TEXT NULL;
        ALTER TABLE sources ADD COLUMN selection_utc TEXT NULL;
        ALTER TABLE sources ADD COLUMN selection_seen TEXT NULL;
        ALTER TABLE sources ADD COLUMN unselected_files INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sources ADD COLUMN new_files INTEGER NOT NULL DEFAULT 0;
        """;

    private const string MigrationTo3 = """
        DROP TRIGGER IF EXISTS events_ai;
        DROP TRIGGER IF EXISTS events_ad;
        DROP TABLE IF EXISTS events_fts;
        CREATE VIRTUAL TABLE events_fts USING fts5 (
            message, detail, category, component,
            content='events', content_rowid='id',
            tokenize='trigram remove_diacritics 1'
        );
        CREATE TRIGGER events_ai AFTER INSERT ON events BEGIN
            INSERT INTO events_fts(rowid, message, detail, category, component) VALUES (new.id, new.message, new.detail, new.category, new.component);
        END;
        CREATE TRIGGER events_ad AFTER DELETE ON events BEGIN
            INSERT INTO events_fts(events_fts, rowid, message, detail, category, component) VALUES ('delete', old.id, old.message, old.detail, old.category, old.component);
        END;
        INSERT INTO events_fts(events_fts) VALUES ('rebuild');
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sources (
            id INTEGER PRIMARY KEY,
            label TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('folder','bundle')),
            path TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            recursive INTEGER NOT NULL DEFAULT 1,
            include_patterns TEXT NOT NULL DEFAULT '*',
            time_zone TEXT NULL,
            format_hint TEXT NOT NULL DEFAULT 'auto',
            created_utc TEXT NOT NULL,
            last_scan_utc TEXT NULL,
            last_scan_status TEXT NULL,
            last_scan_message TEXT NULL,
            bundle_sha256 TEXT NULL,
            bundle_bytes INTEGER NULL,
            selected_files TEXT NULL,
            selection_utc TEXT NULL,
            selection_seen TEXT NULL,
            unselected_files INTEGER NOT NULL DEFAULT 0,
            new_files INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
            rel_path TEXT NOT NULL,
            format TEXT NULL,
            size INTEGER NOT NULL DEFAULT 0,
            mtime_utc INTEGER NOT NULL DEFAULT 0,
            fp_len INTEGER NOT NULL DEFAULT 0,
            fp_hash BLOB NULL,
            committed_offset INTEGER NOT NULL DEFAULT 0,
            committed_line INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL DEFAULT 'pending',
            status_message TEXT NULL,
            unparsed_lines INTEGER NOT NULL DEFAULT 0,
            last_scanned_utc TEXT NULL,
            UNIQUE (source_id, rel_path)
        );
        CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            source_id INTEGER NOT NULL,
            line_start INTEGER NOT NULL,
            line_end INTEGER NOT NULL,
            byte_start INTEGER NOT NULL,
            byte_end INTEGER NOT NULL,
            ts INTEGER NULL,
            sort_ts INTEGER NOT NULL,
            utc_known INTEGER NOT NULL,
            ts_text TEXT NULL,
            level INTEGER NOT NULL,
            level_text TEXT NOT NULL,
            kind INTEGER NOT NULL,
            category TEXT NOT NULL,
            component TEXT NULL,
            message TEXT NOT NULL,
            detail TEXT NULL,
            has_exception INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_events_sort ON events (sort_ts, id);
        CREATE INDEX IF NOT EXISTS ix_events_file ON events (file_id, byte_start);
        CREATE INDEX IF NOT EXISTS ix_events_source ON events (source_id, sort_ts);
        CREATE INDEX IF NOT EXISTS ix_events_level ON events (level, sort_ts);
        CREATE VIRTUAL TABLE IF NOT EXISTS events_fts USING fts5 (
            message, detail, category, component,
            content='events', content_rowid='id',
            tokenize='trigram remove_diacritics 1'
        );
        CREATE TRIGGER IF NOT EXISTS events_ai AFTER INSERT ON events BEGIN
            INSERT INTO events_fts(rowid, message, detail, category, component) VALUES (new.id, new.message, new.detail, new.category, new.component);
        END;
        CREATE TRIGGER IF NOT EXISTS events_ad AFTER DELETE ON events BEGIN
            INSERT INTO events_fts(events_fts, rowid, message, detail, category, component) VALUES ('delete', old.id, old.message, old.detail, old.category, old.component);
        END;
        CREATE TABLE IF NOT EXISTS audit (
            id INTEGER PRIMARY KEY,
            ts_utc TEXT NOT NULL,
            actor TEXT NOT NULL,
            action TEXT NOT NULL,
            detail TEXT NULL
        );
        """;
}

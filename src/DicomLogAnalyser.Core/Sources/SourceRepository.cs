using System.Globalization;
using DicomLogAnalyser.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace DicomLogAnalyser.Core.Sources;

public sealed class SourceRepository(IndexDatabase db, TimeProvider time)
{
    private const string Columns = "id, label, kind, path, enabled, recursive, include_patterns, time_zone, format_hint, created_utc, last_scan_utc, last_scan_status, last_scan_message, bundle_sha256, bundle_bytes";

    public IReadOnlyList<SourceDefinition> List()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM sources ORDER BY kind, label COLLATE NOCASE, id";
        using var r = cmd.ExecuteReader();
        var list = new List<SourceDefinition>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public SourceDefinition? Get(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM sources WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public long Add(SourceDefinition s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sources (label, kind, path, enabled, recursive, include_patterns, time_zone, format_hint, created_utc, bundle_sha256, bundle_bytes)
            VALUES ($label, $kind, $path, $enabled, $recursive, $include, $tz, $fmt, $created, $sha, $bytes);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, s);
        cmd.Parameters.AddWithValue("$kind", s.Kind);
        cmd.Parameters.AddWithValue("$path", s.Path);
        cmd.Parameters.AddWithValue("$created", Now());
        cmd.Parameters.AddWithValue("$sha", (object?)s.BundleSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytes", (object?)s.BundleBytes ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        db.MarkChanged();
        return id;
    }

    /// <summary>Updates the editable settings. The path and kind of a source never change; add a new source instead.</summary>
    public bool Update(SourceDefinition s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sources SET label=$label, enabled=$enabled, recursive=$recursive, include_patterns=$include, time_zone=$tz, format_hint=$fmt WHERE id=$id";
        Bind(cmd, s);
        cmd.Parameters.AddWithValue("$id", s.Id);
        var ok = cmd.ExecuteNonQuery() == 1;
        db.MarkChanged();
        return ok;
    }

    /// <summary>Forgets the file state of a source so the next scan re-reads everything (after a time-zone or format change).</summary>
    public void ResetIndex(long id)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM events WHERE source_id=$id; DELETE FROM files WHERE source_id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        db.MarkChanged();
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM events WHERE source_id=$id; DELETE FROM files WHERE source_id=$id; DELETE FROM sources WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        var gone = IndexDatabase.Scalar<long>(c, "SELECT changes()", tx) == 1;
        tx.Commit();
        db.MarkChanged();
        return gone;
    }

    public void RecordScan(long id, string status, string? message)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sources SET last_scan_utc=$t, last_scan_status=$s, last_scan_message=$m WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        db.MarkChanged();
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static void Bind(SqliteCommand cmd, SourceDefinition s)
    {
        cmd.Parameters.AddWithValue("$label", s.Label);
        cmd.Parameters.AddWithValue("$enabled", s.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$recursive", s.Recursive ? 1 : 0);
        cmd.Parameters.AddWithValue("$include", s.IncludePatterns);
        cmd.Parameters.AddWithValue("$tz", (object?)s.TimeZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fmt", s.FormatHint);
    }

    private static SourceDefinition Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Label = r.GetString(1),
        Kind = r.GetString(2),
        Path = r.GetString(3),
        Enabled = r.GetInt64(4) != 0,
        Recursive = r.GetInt64(5) != 0,
        IncludePatterns = r.GetString(6),
        TimeZone = r.IsDBNull(7) ? null : r.GetString(7),
        FormatHint = r.GetString(8),
        CreatedUtc = r.GetString(9),
        LastScanUtc = r.IsDBNull(10) ? null : r.GetString(10),
        LastScanStatus = r.IsDBNull(11) ? null : r.GetString(11),
        LastScanMessage = r.IsDBNull(12) ? null : r.GetString(12),
        BundleSha256 = r.IsDBNull(13) ? null : r.GetString(13),
        BundleBytes = r.IsDBNull(14) ? null : r.GetInt64(14),
    };
}

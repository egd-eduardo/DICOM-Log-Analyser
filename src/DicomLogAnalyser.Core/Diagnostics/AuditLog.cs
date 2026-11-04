using System.Globalization;
using System.Text.Json;
using DicomLogAnalyser.Core.Indexing;

namespace DicomLogAnalyser.Core.Diagnostics;

public sealed record AuditEntry(long Id, string TimestampUtc, string Actor, string Action, string? Detail);

/// <summary>
/// Accountability trail kept in the index database. Entries record who did what (configuration changes, scans,
/// imports, deletions, exports) using ids and counts only: never search terms, log content, identifiers or file names.
/// The audit table survives an index purge; it is removed only with the whole data folder.
/// </summary>
public sealed class AuditLog(IndexDatabase db, TimeProvider time)
{
    public const int Retain = 20_000;

    public void Write(string actor, string action, IReadOnlyDictionary<string, object?>? detail = null)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit (ts_utc, actor, action, detail) VALUES ($t, $a, $x, $d); DELETE FROM audit WHERE id <= (SELECT max(id) FROM audit) - $keep;";
        cmd.Parameters.AddWithValue("$t", time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$a", actor.Length > 200 ? actor[..200] : actor);
        cmd.Parameters.AddWithValue("$x", action);
        cmd.Parameters.AddWithValue("$d", detail is null ? DBNull.Value : JsonSerializer.Serialize(detail));
        cmd.Parameters.AddWithValue("$keep", Retain);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, ts_utc, actor, action, detail FROM audit ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (r.Read()) list.Add(new AuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }
}

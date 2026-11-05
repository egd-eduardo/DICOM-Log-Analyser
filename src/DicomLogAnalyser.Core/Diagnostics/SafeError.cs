namespace DicomLogAnalyser.Core.Diagnostics;

/// <summary>
/// Exception text can contain file paths, share names or log content. Routine diagnostics only record the exception
/// type and a correlation reference.
/// </summary>
public static class SafeError
{
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var inner = ex.InnerException is null ? "" : " <- " + ex.InnerException.GetType().Name;
        // SQLite result codes are numeric and contain no data; they make storage errors diagnosable.
        var sqlite = ex is Microsoft.Data.Sqlite.SqliteException se
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (SQLite {se.SqliteErrorCode}/{se.SqliteExtendedErrorCode})")
            : "";
        return ex.GetType().Name + sqlite + inner;
    }

    /// <summary>SQLITE_BUSY (5) or SQLITE_LOCKED (6): another connection held a lock longer than the busy timeout.</summary>
    public static bool IsTransientStorageError(Exception ex) => ex is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 };

    public static string NewReference() => Guid.NewGuid().ToString("N")[..12];
}

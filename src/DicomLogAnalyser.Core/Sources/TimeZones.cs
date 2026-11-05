namespace DicomLogAnalyser.Core.Sources;

/// <summary>
/// Resolves time-zone names on every operating system. Windows uses its own names (GMT Standard Time); browsers, macOS and
/// Linux use IANA names (Europe/Lisbon). .NET translates between the two only when the ICU library is available, which
/// is not the case on some Windows servers (for example Windows Server 2016, or .NET running in NLS mode). A built-in
/// table taken from the Unicode CLDR windowsZones mapping covers the zones of the supported languages' countries.
/// </summary>
public static class TimeZones
{
    /// <summary>IANA name to Windows name (CLDR windowsZones, territory-specific entries).</summary>
    public static readonly IReadOnlyDictionary<string, string> IanaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
        ["Europe/Lisbon"] = "GMT Standard Time",
        ["Atlantic/Madeira"] = "GMT Standard Time",
        ["Atlantic/Azores"] = "Azores Standard Time",
        ["Europe/London"] = "GMT Standard Time",
        ["Europe/Dublin"] = "GMT Standard Time",
        ["Atlantic/Canary"] = "GMT Standard Time",
        ["Europe/Madrid"] = "Romance Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/Brussels"] = "Romance Standard Time",
        ["Europe/Copenhagen"] = "Romance Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Amsterdam"] = "W. Europe Standard Time",
        ["Europe/Vienna"] = "W. Europe Standard Time",
        ["Europe/Zurich"] = "W. Europe Standard Time",
        ["Europe/Luxembourg"] = "W. Europe Standard Time",
        ["Europe/Stockholm"] = "W. Europe Standard Time",
        ["Europe/Oslo"] = "W. Europe Standard Time",
        ["Europe/Budapest"] = "Central Europe Standard Time",
        ["Europe/Prague"] = "Central Europe Standard Time",
        ["Europe/Bratislava"] = "Central Europe Standard Time",
        ["Europe/Ljubljana"] = "Central Europe Standard Time",
        ["Europe/Zagreb"] = "Central European Standard Time",
        ["Europe/Warsaw"] = "Central European Standard Time",
        ["Europe/Sarajevo"] = "Central European Standard Time",
        ["Europe/Vilnius"] = "FLE Standard Time",
        ["Europe/Riga"] = "FLE Standard Time",
        ["Europe/Tallinn"] = "FLE Standard Time",
        ["Europe/Helsinki"] = "FLE Standard Time",
        ["Europe/Kiev"] = "FLE Standard Time",
        ["Europe/Kyiv"] = "FLE Standard Time",
        ["Europe/Sofia"] = "FLE Standard Time",
        ["Europe/Athens"] = "GTB Standard Time",
        ["Europe/Bucharest"] = "GTB Standard Time",
        ["Asia/Nicosia"] = "GTB Standard Time",
        ["Europe/Istanbul"] = "Turkey Standard Time",
    };

    /// <summary>Finds a time zone by IANA or Windows name, or returns null when this computer does not know it.</summary>
    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
        id = id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out tz)) return tz;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out tz)) return tz;
        if (IanaToWindows.TryGetValue(id, out windows) && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out tz)) return tz;
        return null;
    }

    /// <summary>The time zone of the computer running the analyser.</summary>
    public static TimeZoneInfo Server => TimeZoneInfo.Local;

    /// <summary>Names this computer accepts: its own time-zone names plus the IANA names of the built-in table that resolve here.</summary>
    public static IReadOnlyList<string> KnownNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in TimeZoneInfo.GetSystemTimeZones()) names.Add(z.Id);

        // The server's own zone can be an alias missing from the list above (macOS reports "Universal").
        names.Add(Server.Id);
        foreach (var iana in IanaToWindows.Keys)
        {
            if (Resolve(iana) is not null) names.Add(iana);
        }

        return names.ToList();
    }
}

using DicomLogAnalyser.Core.Sources;

namespace DicomLogAnalyser.Tests;

/// <summary>Time-zone names are accepted in IANA and Windows form, with a built-in table for servers without ICU.</summary>
public class TimeZoneTests
{
    private static readonly Dictionary<string, string> Reference = new()
    {
        ["UTC"] = "Etc/UTC",
        ["GMT Standard Time"] = "Europe/London",
        ["Azores Standard Time"] = "Atlantic/Azores",
        ["Romance Standard Time"] = "Europe/Paris",
        ["W. Europe Standard Time"] = "Europe/Berlin",
        ["Central Europe Standard Time"] = "Europe/Budapest",
        ["Central European Standard Time"] = "Europe/Warsaw",
        ["FLE Standard Time"] = "Europe/Helsinki",
        ["GTB Standard Time"] = "Europe/Bucharest",
        ["Turkey Standard Time"] = "Europe/Istanbul",
    };

    public static TheoryData<string, string> Table()
    {
        var data = new TheoryData<string, string>();
        foreach (var (iana, windows) in TimeZones.IanaToWindows) data.Add(iana, windows);
        return data;
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Built_in_table_matches_the_operating_system_rules(string iana, string windows)
    {
        // Windows has the Windows zones natively; elsewhere compare with a reference IANA zone of the same rules
        // (CLDR "001" territory), as .NET's own Windows-name translation depends on the local tz database.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var a)) return; // this machine lacks the name (for example Europe/Kiev)
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(windows, out var b)) b = TimeZoneInfo.FindSystemTimeZoneById(Reference[windows]);
        DateTime[] instants =
        [
            new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            new(2026, 3, 29, 12, 0, 0, DateTimeKind.Utc), new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc),
        ];
        foreach (var t in instants) Assert.Equal(a.GetUtcOffset(t), b.GetUtcOffset(t));
    }

    [Fact]
    public void Lisbon_resolves_to_the_same_rules_in_every_mode()
    {
        // In NLS mode (Windows Server 2016) .NET cannot translate IANA names; the built-in table must then be used.
        var tz = TimeZones.Resolve("Europe/Lisbon");
        Assert.NotNull(tz);
        Assert.Equal(TimeSpan.Zero, tz.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(1), tz.GetUtcOffset(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("Europe/Lisbon")]
    [InlineData("GMT Standard Time")]
    [InlineData(" Europe/Lisbon ")]
    [InlineData("UTC")]
    public void Accepts_iana_and_windows_names(string name)
    {
        Assert.NotNull(TimeZones.Resolve(name));
        Assert.Null(PathValidator.CheckTimeZone(name.Trim()));
    }

    [Fact]
    public void Rejects_unknown_names_with_guidance()
    {
        Assert.Null(TimeZones.Resolve("Nowhere/Atlantis"));
        Assert.Contains("Choose one from the list", PathValidator.CheckTimeZone("Nowhere/Atlantis"), StringComparison.Ordinal);
        Assert.Contains(TimeZones.Server.Id, TimeZones.KnownNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Europe/Lisbon", TimeZones.KnownNames(), StringComparer.OrdinalIgnoreCase);
    }
}

using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Core.Storage;
using DicomLogAnalyser.Web;

namespace DicomLogAnalyser.Tests;

public class ConfigurationTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) => k => values.TryGetValue(k, out var v) ? v : null;

    private static Dictionary<string, string> ValidServer() => new()
    {
        ["DLA_MODE"] = "server",
        ["DLA_PUBLIC_ORIGIN"] = "https://dicomlogs.example.internal",
        ["DLA_TRUSTED_PROXIES"] = "172.30.0.0/24",
        ["DLA_ALLOWED_EMAIL_DOMAINS"] = "example.org",
        ["DLA_SOURCE_ROOTS"] = "/sources",
        ["DLA_DATA_DIR"] = "/data",
    };

    [Fact]
    public void Local_mode_is_the_default_and_loopback_only()
    {
        var o = AppOptions.Parse([], Env([]));
        Assert.Equal(AppMode.Local, o.Mode);
        Assert.Empty(o.Validate());
        Assert.True(System.Net.IPAddress.IsLoopback(o.ListenAddress));
        var bad = AppOptions.Parse([], Env(new() { ["DLA_LISTEN_ADDRESS"] = "0.0.0.0" }));
        Assert.NotEmpty(bad.Validate());
    }

    [Fact]
    public void Server_mode_with_complete_configuration_is_valid() => Assert.Empty(AppOptions.Parse([], Env(ValidServer())).Validate());

    [Theory]
    [InlineData("DLA_PUBLIC_ORIGIN", null)]
    [InlineData("DLA_PUBLIC_ORIGIN", "http://dicomlogs.example.internal")]
    [InlineData("DLA_TRUSTED_PROXIES", null)]
    [InlineData("DLA_TRUSTED_PROXIES", "0.0.0.0/0")]
    [InlineData("DLA_ALLOWED_EMAIL_DOMAINS", null)]
    [InlineData("DLA_ALLOWED_EMAIL_DOMAINS", "*")]
    [InlineData("DLA_SOURCE_ROOTS", null)]
    public void Server_mode_refuses_incomplete_or_open_configuration(string key, string? value)
    {
        var env = ValidServer();
        if (value is null) env.Remove(key);
        else env[key] = value;
        Assert.NotEmpty(AppOptions.Parse([], Env(env)).Validate());
    }

    [Fact]
    public void Allow_list_matches_users_and_domains_exactly()
    {
        var env = ValidServer();
        env["DLA_ALLOWED_USERS"] = "someone@partner.example";
        var o = AppOptions.Parse([], Env(env));
        Assert.True(o.IsAllowedUser("a.person@example.org"));
        Assert.True(o.IsAllowedUser("SOMEONE@partner.example"));
        Assert.False(o.IsAllowedUser("a.person@evil-example.org"));
        Assert.False(o.IsAllowedUser("a.person@example.org.evil"));
        Assert.False(o.IsAllowedUser("example.org"));
    }

    [Fact]
    public void Folder_validation_reports_problems_clearly()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Sub("data"));
        var v = new PathValidator(paths, []);
        Assert.False(v.ValidateFolder("").Ok);
        Assert.False(v.ValidateFolder("relative/path").Ok);
        Assert.False(v.ValidateFolder(Path.Combine(t.Path, "missing")).Ok);
        Assert.False(v.ValidateFolder(paths.Root).Ok);
        var logs = t.Sub("logs");
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");
        var ok = v.ValidateFolder(logs);
        Assert.True(ok.Ok);
        Assert.Contains(ok.Info, i => i.Contains("1 file", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            var win = v.ValidateFolder(@"Z:\3dnet\audits\Gateway\0");
            Assert.Contains(win.Errors, e => e.Contains("Docker", StringComparison.Ordinal) || e.Contains("Windows path", StringComparison.Ordinal));
            Assert.False(v.ValidateFolder(@"\\server\share\logs").Ok);
        }
    }

    [Fact]
    public void Central_mode_confines_sources_to_mounted_roots_and_rejects_links()
    {
        using var t = new TempDir();
        var root = t.Sub("sources");
        var inside = t.Sub("sources/gateway");
        var outside = t.Sub("other");
        var v = new PathValidator(new AppPaths(t.Sub("data")), [root]);
        Assert.True(v.ValidateFolder(inside).Ok);
        Assert.False(v.ValidateFolder(outside).Ok);
        Assert.False(v.ValidateFolder(root + "-sibling").Ok);
        Assert.False(v.ValidateFolder(Path.Combine(inside, "..", "..", "other")).Ok);
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, outside);
            Assert.False(v.ValidateFolder(link).Ok);
        }
    }

    [Fact]
    public void Mount_info_detects_read_only_mounts()
    {
        var lines = new[]
        {
            "22 1 0:21 / / rw,relatime - overlay overlay rw",
            "30 22 0:40 /logs /sources/gateway ro,relatime - cifs //server/share rw",
            "31 22 0:41 / /data rw,relatime - ext4 /dev/sda1 rw",
        };
        var m = MountInfo.FindIn(lines, "/sources/gateway/0")!;
        Assert.True(m.ReadOnly);
        Assert.Equal("cifs", m.FileSystem);
        Assert.False(MountInfo.FindIn(lines, "/data/index")!.ReadOnly);
    }

    [Theory]
    [InlineData("*.log", "Gateway.log", true)]
    [InlineData("*.log", "Gateway.LOG", true)]
    [InlineData("*.log", "Gateway.log.1", false)]
    [InlineData("DicomRouter*", "DicomRouter20261027.log", true)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("*", "anything", true)]
    public void Glob_matching(string pattern, string name, bool expected) => Assert.Equal(expected, FileEnumerator.Glob(pattern, name));

    [Fact]
    public void Network_data_folder_is_refused()
    {
        Assert.NotNull(StorageGuard.NetworkLocationProblem(@"\\server\share\data"));
        using var t = new TempDir();
        Assert.Null(StorageGuard.NetworkLocationProblem(t.Path));
    }
}

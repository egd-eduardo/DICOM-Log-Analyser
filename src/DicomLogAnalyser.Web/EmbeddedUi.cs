namespace DicomLogAnalyser.Web;

/// <summary>Serves the embedded single-page UI. Only an explicit allow-list of files exists; nothing is read from disk.</summary>
public static class EmbeddedUi
{
    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.Ordinal)
    {
        ["/"] = ("ui/index.html", "text/html; charset=utf-8"),
        ["/index.html"] = ("ui/index.html", "text/html; charset=utf-8"),
        ["/app.js"] = ("ui/app.js", "text/javascript; charset=utf-8"),
        ["/app.css"] = ("ui/app.css", "text/css; charset=utf-8"),
        ["/favicon.svg"] = ("ui/favicon.svg", "image/svg+xml"),
    };

    private static readonly Dictionary<string, byte[]> Cache = Load();

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        foreach (var (path, (resource, contentType)) in Files)
        {
            var bytes = Cache[resource];
            app.MapGet(path, () => Results.Bytes(bytes, contentType));
        }
    }

    private static Dictionary<string, byte[]> Load()
    {
        var asm = typeof(EmbeddedUi).Assembly;
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in Files.Values.Select(v => v.Resource).Distinct())
        {
            using var stream = asm.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Embedded UI resource {resource} is missing.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[resource] = ms.ToArray();
        }

        return result;
    }
}

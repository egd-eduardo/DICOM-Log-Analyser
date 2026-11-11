using System.Net;
using DicomLogAnalyser.Core.Bundles;
using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Core.Indexing;
using DicomLogAnalyser.Core.Sources;
using DicomLogAnalyser.Core.Storage;
using DicomLogAnalyser.Web.Api;
using DicomLogAnalyser.Web.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DicomLogAnalyser.Web;

public static class AppHost
{
    public const long DefaultMaxRequestBodyBytes = 1024 * 1024;

    /// <summary>
    /// Builds the web application with no external configuration sources: environment variables such as
    /// ASPNETCORE_URLS, appsettings files and command-line "urls" cannot change the binding.
    /// </summary>
    public static WebApplication Build(AppOptions options, TimeProvider? time = null, Action<ILoggingBuilder>? logging = null, Platform.IFolderPicker? folderPicker = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = options.Validate();
        if (problems.Count > 0) throw new InvalidOperationException("Configuration problems:\n - " + string.Join("\n - ", problems));
        time ??= TimeProvider.System;

        var paths = new AppPaths(options.DataDirectory);
        var storageProblem = StorageGuard.NetworkLocationProblem(paths.Root);
        if (storageProblem is not null) throw new InvalidOperationException(storageProblem);
        paths.EnsureCreated();
        paths.CleanTemp();

        var db = new IndexDatabase(paths.DatabaseFile);
        db.Initialise();

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "DicomLogAnalyser",
        });

        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            var address = options.Mode == AppMode.Local ? IPAddress.Loopback : options.ListenAddress;
            k.Listen(address, options.Port, listen => listen.Protocols = HttpProtocols.Http1);
            k.Limits.MaxRequestBodySize = DefaultMaxRequestBodyBytes;
            k.Limits.MaxConcurrentConnections = 256;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            k.Limits.MaxRequestLineSize = 8 * 1024;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddLogging(l =>
        {
            l.ClearProviders();
            l.SetMinimumLevel(LogLevel.Information);
            // Framework categories can include request URLs (with search text) and headers; only warnings and above.
            l.AddFilter("Microsoft", LogLevel.Warning);
            l.AddFilter("System", LogLevel.Warning);
            if (logging is null) l.AddSimpleConsole(c => c.SingleLine = true);
            else logging(l);
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
            json.SerializerOptions.MaxDepth = 16;
            json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });

        var sources = new SourceRepository(db, time);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton(new AuditLog(db, time));
        builder.Services.AddSingleton(new Ingestor(db, sources, time) { NetworkReadBytesPerSecond = options.NetworkReadLimitMb * 1024L * 1024L });
        var searchService = new SearchService(db);
        builder.Services.AddSingleton(searchService);
        builder.Services.AddSingleton(new Core.Exam.ExamHistoryService(searchService));
        builder.Services.AddSingleton(new Core.Worklist.WorklistAnalysis(db));
        builder.Services.AddSingleton(new OverviewService(db));
        builder.Services.AddSingleton(new ActivityService(db));
        builder.Services.AddSingleton(new EquipmentService(db));
        builder.Services.AddSingleton(new PathValidator(paths, options.Mode == AppMode.Server ? options.SourceRoots : []));
        // Stopping waits at most 10 seconds for background work (a scan stops at its next checkpoint).
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton(new BundleImporter(paths, new BundleLimits { MaxUploadBytes = options.MaxUploadBytes }));
        builder.Services.AddSingleton(new ImportTracker());
        builder.Services.AddSingleton(new AppRuntime(options));
        builder.Services.AddSingleton(new SessionService(time, options.SessionIdle));
        builder.Services.AddSingleton(new LaunchTokenService(time));
        builder.Services.AddSingleton<ScanCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScanCoordinator>());
        // Native folder dialogs exist only where the analyser runs on the user's own desktop (local mode).
        builder.Services.AddSingleton(folderPicker ?? (options.Mode == AppMode.Local ? Platform.FolderPickers.ForThisComputer() : new Platform.UnsupportedFolderPicker()));
        builder.Services.AddSingleton(new Platform.FolderBrowser(options));
        builder.Services.AddSingleton<LiveIndexer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveIndexer>());

        var app = builder.Build();
        app.UseMiddleware<ErrorHandlingMiddleware>();
        // Routing first so the security middleware can see endpoint metadata (role requirements) before any binding.
        app.UseRouting();
        app.UseMiddleware<SecurityMiddleware>();
        EmbeddedUi.Map(app);
        Endpoints.Map(app);
        return app;
    }

    /// <summary>Starts the server and, in local mode, verifies that every bound address is a loopback address.</summary>
    public static async Task<int> StartAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<AppOptions>();
        await app.StartAsync().ConfigureAwait(false);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var port = 0;
        foreach (var address in addresses)
        {
            var uri = new Uri(address);
            if (options.Mode == AppMode.Local && (!IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)))
            {
                await app.StopAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Refusing to run: local mode is bound to a non-loopback address.");
            }

            port = uri.Port;
        }

        if (port == 0)
        {
            await app.StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The server did not report a bound address.");
        }

        app.Services.GetRequiredService<AppRuntime>().SetPort(port);
        RemoveOrphanBundles(app.Services.GetRequiredService<AppPaths>(), app.Services.GetRequiredService<SourceRepository>(), app.Services.GetRequiredService<BundleImporter>());
        app.Services.GetRequiredService<AuditLog>().Write("system", "app_started", new Dictionary<string, object?> { ["version"] = Endpoints.Version, ["mode"] = options.Mode.ToString() });
        return port;
    }

    /// <summary>Deletes bundle folders that no source refers to (left over when a deletion was interrupted).</summary>
    internal static int RemoveOrphanBundles(AppPaths paths, SourceRepository sources, BundleImporter importer)
    {
        if (!Directory.Exists(paths.BundlesDirectory)) return 0;
        var referenced = sources.List().Where(s => s.Kind == SourceKinds.Bundle).Select(s => Path.GetFullPath(s.Path)).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(paths.BundlesDirectory))
        {
            var files = Path.GetFullPath(Path.Combine(dir, "files"));
            if (referenced.Contains(files)) continue;
            try
            {
                if (importer.Delete(files)) removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}

using System.Diagnostics;
using DicomLogAnalyser.Core.Diagnostics;
using DicomLogAnalyser.Web;
using DicomLogAnalyser.Web.Security;

AppOptions options;
try
{
    options = AppOptions.Parse(args, Environment.GetEnvironmentVariable);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or UriFormatException)
{
    await Console.Error.WriteLineAsync("Invalid option: " + ex.Message).ConfigureAwait(false);
    PrintUsage();
    return 2;
}

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

if (options.HealthCheck) return await HealthCheck(options).ConfigureAwait(false);

// One instance per data folder: two writers on the same index would compete for the database lock.
Directory.CreateDirectory(options.DataDirectory);
FileStream instanceLock;
try
{
    instanceLock = new FileStream(Path.Combine(options.DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    await Console.Error.WriteLineAsync("DICOM Log Analyser is already running with this data folder. Use its window (press O there to open the browser again).").ConfigureAwait(false);
    return 3;
}

using (instanceLock)
{
    WebApplication app;
    int port;
    try
    {
        app = AppHost.Build(options);
        port = await AppHost.StartAsync(app).ConfigureAwait(false);
    }
    catch (InvalidOperationException ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }
    catch (IOException ex)
    {
        await Console.Error.WriteLineAsync("Could not start: " + SafeError.Describe(ex) + ". Is the port already in use?").ConfigureAwait(false);
        return 1;
    }

    await using (app.ConfigureAwait(false))
    {
        if (options.Mode == AppMode.Local)
        {
            var tokens = app.Services.GetRequiredService<LaunchTokenService>();
            var baseUrl = $"http://127.0.0.1:{port}";
            Console.WriteLine();
            Console.WriteLine($"DICOM Log Analyser {DicomLogAnalyser.Web.Api.Endpoints.Version} is running on this computer only ({baseUrl}).");
            Console.WriteLine("Data folder: " + Path.GetFullPath(options.DataDirectory));
            Console.WriteLine("Nothing is sent to other computers or online services. Keep this window open; close it or press Ctrl+C to stop.");
            if (options.OpenBrowser) OpenBrowser(baseUrl + "/launch?t=" + tokens.Issue());
            Console.WriteLine("Press O to open the interface in your browser (a new one-time link is created each time).");
            if (!options.OpenBrowser || Console.IsInputRedirected) Console.WriteLine("One-time sign-in link (valid 2 minutes): " + baseUrl + "/launch?t=" + tokens.Issue());
            _ = Task.Run(() => KeyLoop(tokens, baseUrl, app.Lifetime.ApplicationStopping));
        }
        else
        {
            Console.WriteLine($"DICOM Log Analyser {DicomLogAnalyser.Web.Api.Endpoints.Version} (central mode) listening on port {port} for {options.Origin} behind the access proxy.");
        }

        await app.WaitForShutdownAsync().ConfigureAwait(false);
    }
}

return 0;

static void KeyLoop(LaunchTokenService tokens, string baseUrl, CancellationToken ct)
{
    if (Console.IsInputRedirected) return;
    while (!ct.IsCancellationRequested)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.O) OpenBrowser(baseUrl + "/launch?t=" + tokens.Issue());
    }
}

static void OpenBrowser(string url)
{
    try
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url])
            : new ProcessStartInfo("xdg-open", [url]);
        using var _ = Process.Start(psi);
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
    {
        Console.WriteLine("The browser could not be opened automatically. Press O again or start with --no-browser to print a link.");
    }
}

static async Task<int> HealthCheck(AppOptions options)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var port = options.Port == 0 ? 8080 : options.Port;
        var text = await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/healthz")).ConfigureAwait(false);
        return text == "ok" ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        DICOM Log Analyser

        Local mode (default; this computer only, no password, opens the browser):
          DicomLogAnalyser [--port N] [--no-browser] [--data-dir PATH]

        Central mode (behind an identity-aware access proxy; see README):
          DicomLogAnalyser --mode server   (configured with DLA_* environment variables)

        Other:
          --healthcheck   probe the running server (used by Docker)
          --help          this text
        """);
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DicomLogAnalyser.Web.Platform;

/// <summary>
/// Lets a second start of the analyser, by the same user and with the same data folder, ask the running one to open the
/// browser, instead of failing. The channel is a named pipe restricted to the current user
/// (<see cref="PipeOptions.CurrentUserOnly"/>), so other people logged on to the same computer cannot use it; it carries
/// a single "open" request and no data.
/// </summary>
public static class LocalInstance
{
    private const byte OpenRequest = (byte)'O';

    /// <summary>Pipe name derived from the user and the data folder (no path or name is exposed).</summary>
    public static string PipeName(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        var key = Environment.UserName + "|" + Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "dla-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>Serves "open the browser" requests from later starts until <paramref name="ct"/> is cancelled.</summary>
    public static async Task ListenAsync(string dataDirectory, Action onOpen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onOpen);
        var name = PipeName(dataDirectory);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[1];
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await server.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) == 1 && buffer[0] == OpenRequest) onOpen();
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return;
            }
            catch (IOException)
            {
                // A broken or refused connection; keep serving.
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return; // The name is taken by another user's process: this convenience is simply unavailable.
            }
        }
    }

    /// <summary>Asks a running instance of this user with this data folder to open the browser. True when it answered.</summary>
    public static bool TryOpenRunning(string dataDirectory, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(dataDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            client.WriteByte(OpenRequest);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Console settings for the Windows window of the analyser.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsConsole
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    /// <summary>
    /// Turns off QuickEdit for this window. With QuickEdit on, a click in the window selects text and pauses the program
    /// until a key is pressed, which looks like a hang and blocks stopping it.
    /// </summary>
    public static void DisableQuickEdit()
    {
        var handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var mode)) return;
        _ = SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

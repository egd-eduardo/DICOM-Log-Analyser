using System.Runtime.InteropServices;

namespace DicomLogAnalyser.Web.Platform;

/// <summary>
/// Runs the calling thread at background priority for CPU and disk, so that scans and index writes give way to the
/// programs people are using (video calls, viewers). Only the scanning thread is lowered; the web interface keeps
/// normal priority.
/// <list type="bullet">
/// <item>Windows: <c>THREAD_MODE_BACKGROUND_BEGIN</c> (lower CPU, disk and memory priority).</item>
/// <item>macOS: <c>PRIO_DARWIN_BG</c> for the thread (background QoS: CPU and disk I/O throttled while others are busy).</item>
/// <item>Linux: nice 19 for the thread (also lowers its disk I/O priority with the default schedulers).</item>
/// </list>
/// Failures are ignored: priority is a courtesy, never a reason not to scan.
/// </summary>
public static partial class BackgroundPriority
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;
    private const int PrioDarwinThread = 3;
    private const int PrioDarwinBg = 0x1000;
    private const int PrioProcess = 0;
    private const int LinuxNice = 19;

    /// <summary>Lowers the current thread; disposing restores it where the operating system allows.</summary>
    public static IDisposable Enter()
    {
        var applied = false;
        try
        {
            if (OperatingSystem.IsWindows()) applied = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            else if (OperatingSystem.IsMacOS()) applied = setpriority(PrioDarwinThread, 0, PrioDarwinBg) == 0;
            else if (OperatingSystem.IsLinux()) applied = setpriority(PrioProcess, gettid(), LinuxNice) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            applied = false;
        }

        return new Scope(applied);
    }

    /// <summary>Whether the current thread is lowered, as far as it can be read (Linux nice, macOS background flag).</summary>
    internal static bool IsCurrentThreadLowered()
    {
        if (OperatingSystem.IsMacOS()) return getpriority(PrioDarwinThread, 0) == 1;
        if (OperatingSystem.IsLinux()) return getpriority(PrioProcess, gettid()) >= LinuxNice;
        return false;
    }

    private sealed class Scope(bool applied) : IDisposable
    {
        public void Dispose()
        {
            if (!applied) return;
            try
            {
                if (OperatingSystem.IsWindows()) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                else if (OperatingSystem.IsMacOS()) setpriority(PrioDarwinThread, 0, 0);

                // Linux: raising priority again needs privileges; the scan thread ends right after, so nothing to undo.
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);

#pragma warning disable SA1300, IDE1006 // C library names
    [LibraryImport("libc", SetLastError = true)]
    private static partial int setpriority(int which, int who, int prio);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getpriority(int which, int who);

    [LibraryImport("libc")]
    private static partial int gettid();
#pragma warning restore SA1300, IDE1006
}

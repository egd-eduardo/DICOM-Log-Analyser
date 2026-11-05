using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DicomLogAnalyser.Web.Platform;

/// <summary>
/// Native folder dialog shown on the desktop of the computer running the analyser (local mode only). The chosen path
/// comes from the operating system's dialog, never from the HTTP request; it is still validated like a typed path.
/// </summary>
public interface IFolderPicker
{
    bool IsSupported { get; }

    /// <summary>Returns the selected folder, or null when cancelled.</summary>
    Task<DirectoryInfo?> PickAsync(CancellationToken ct);
}

public static class FolderPickers
{
    public static IFolderPicker ForThisComputer()
    {
        if (OperatingSystem.IsWindows()) return new WindowsFolderPicker();
        if (OperatingSystem.IsMacOS()) return new CommandFolderPicker("/usr/bin/osascript",
            ["-e", "activate", "-e", "POSIX path of (choose folder with prompt \"DICOM Log Analyser: choose a folder with log files\")"]);
        foreach (var zenity in new[] { "/usr/bin/zenity", "/usr/local/bin/zenity" })
        {
            if (File.Exists(zenity) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY") ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                return new CommandFolderPicker(zenity, ["--file-selection", "--directory", "--title=DICOM Log Analyser: choose a folder with log files"]);
        }

        return new UnsupportedFolderPicker();
    }
}

public sealed class UnsupportedFolderPicker : IFolderPicker
{
    public bool IsSupported => false;

    public Task<DirectoryInfo?> PickAsync(CancellationToken ct) => throw new PlatformNotSupportedException("No folder dialog is available on this computer.");
}

/// <summary>macOS (AppleScript "choose folder") and Linux (zenity): a helper program prints the chosen path; cancel exits non-zero.</summary>
public sealed class CommandFolderPicker(string program, IReadOnlyList<string> arguments) : IFolderPicker, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsSupported => true;

    public void Dispose() => _gate.Dispose();

    public async Task<DirectoryInfo?> PickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A folder selection dialog is already open.");
        try
        {
            var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("The folder dialog could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            var path = output.Trim();
            if (path.Length == 0) return null;
            if (path.Length > 1) path = path.TrimEnd('/');
            return new DirectoryInfo(path);
        }
        finally
        {
            _gate.Release();
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsFolderPicker : IFolderPicker, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public bool IsSupported => true;

    public async Task<DirectoryInfo?> PickAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("A folder selection dialog is already open.");
        try
        {
            var tcs = new TaskCompletionSource<DirectoryInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(ShowDialog());
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "FolderPicker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DirectoryInfo? ShowDialog()
    {
        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800, FOS_DONTADDTORECENT = 0x2000000;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        var dialog = (IFileOpenDialog)new FileOpenDialogCom();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR | FOS_DONTADDTORECENT);
            dialog.SetTitle("DICOM Log Analyser - choose a folder with log files");
            dialog.SetOkButtonLabel("Use this folder");

            // Owning the dialog with the current foreground window (normally the browser) keeps it in front.
            var hr = dialog.Show(GetForegroundWindow());
            if (hr == ERROR_CANCELLED) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    return string.IsNullOrEmpty(path) ? null : new DirectoryInfo(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IntPtr psi);
        void SetFolder(IntPtr psi);
        void GetFolder(out IntPtr ppsi);
        void GetCurrentSelection(out IntPtr ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName(out IntPtr pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IntPtr psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}

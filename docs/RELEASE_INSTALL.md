## Which file

| Computer | Download |
|---|---|
| Windows 10/11 (most PCs) | `DicomLogAnalyser-win-x64.zip` |
| Windows on ARM (e.g. Surface Pro X / Snapdragon) | `DicomLogAnalyser-win-arm64.zip` |
| Mac with Apple silicon (M1 to M4) | `DicomLogAnalyser-osx-arm64.zip` |
| Mac with Intel processor | `DicomLogAnalyser-osx-x64.zip` |
| Linux | `DicomLogAnalyser-linux-x64.zip` |

No installation, no .NET and no administrator rights are needed.

## Windows

1. Extract the zip into a folder you own on the local disk (for example `%LOCALAPPDATA%\Programs\DicomLogAnalyser`).
2. Double-click **Start DICOM Log Analyser.cmd**. The packages are not code-signed yet: if Windows shows "Windows protected
   your PC", choose **More info**, then **Run anyway** (first time only).
3. Your browser opens the analyser, already signed in. Keep the console window open; press **O** in it to reopen the
   browser, close it to stop.

## macOS

1. Extract the zip (double-click), for example into `~/Applications`.
2. The packages are not notarised yet. In Terminal, remove the download quarantine once:
   `xattr -dr com.apple.quarantine ~/Applications/DicomLogAnalyser-osx-arm64` (or `-osx-x64` on Intel).
3. Double-click **start.command** (or run `./DicomLogAnalyser` in that folder). The browser opens, already signed in.
   Keep the Terminal window open; press **O** to reopen the browser, Ctrl+C to stop.

## Check the download (optional)

Compare the file's SHA-256 with the list below: `certutil -hashfile DicomLogAnalyser-win-x64.zip SHA256` (Windows) or
`shasum -a 256 DicomLogAnalyser-osx-arm64.zip` (macOS). Provenance can be verified with
`gh attestation verify <file> --repo egd-eduardo/DICOM-Log-Analyser`.

Full documentation: README.md in the zip.

#!/usr/bin/env bash
# Builds a portable, self-contained package (no .NET installation needed on the target computer).
#   ./scripts/package.sh win-x64 | win-arm64 | osx-arm64 | osx-x64 | linux-x64 | linux-arm64
set -euo pipefail
rid="${1:-}"
case "$rid" in win-x64|win-arm64|osx-arm64|osx-x64|linux-x64|linux-arm64) ;; *) echo "usage: $0 <win-x64|win-arm64|osx-arm64|osx-x64|linux-x64|linux-arm64>"; exit 2;; esac
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out="artifacts/package/DicomLogAnalyser-$rid"
rm -rf "${out:?}" "${out:?}.zip"
dotnet publish src/DicomLogAnalyser.Web -c Release -r "$rid" -o "$out"
find "${out:?}" -maxdepth 1 -name '*.pdb' -delete
cp README.md "$out/README.md"
if [[ "$rid" == win-* ]]; then
  printf '@echo off\r\nrem Starts DICOM Log Analyser on this computer only and opens the browser.\r\n"%%~dp0DicomLogAnalyser.exe" %%*\r\n' > "$out/Start DICOM Log Analyser.cmd"
else
  printf '#!/bin/sh\n# Starts DICOM Log Analyser on this computer only and opens the browser.\ncd "$(dirname "$0")" && exec ./DicomLogAnalyser "$@"\n' > "$out/start.command"
  chmod +x "$out/start.command" "$out/DicomLogAnalyser"
fi
cd artifacts/package
zip -qr "DicomLogAnalyser-$rid.zip" "DicomLogAnalyser-$rid"
if command -v sha256sum >/dev/null; then sha256sum "DicomLogAnalyser-$rid.zip"; else shasum -a 256 "DicomLogAnalyser-$rid.zip"; fi > "DicomLogAnalyser-$rid.zip.sha256"
echo "Package: artifacts/package/DicomLogAnalyser-$rid.zip"

#!/usr/bin/env bash
# One-step local build on macOS (or Linux), without GitHub: checks the prerequisites, runs the tests and builds the
# portable package for this computer.
#
#   ./scripts/build.sh               tests + package for this computer (for example osx-arm64 on an Apple silicon Mac)
#   ./scripts/build.sh --all         tests + every release package (Windows, macOS, Linux) and SHA256SUMS.txt
#   ./scripts/build.sh --skip-tests  package only
#   ./scripts/build.sh --checks      the full checks first (same as CI: PHI scan, format, tests, audit, UI type check)
#
# Needs the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0). --checks also needs Python 3 and Node.js.
# Packages built on your own Mac are not quarantined, so they start without the xattr step a download needs.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

all=0; tests=1; checks=0
for arg in "$@"; do
  case "$arg" in
    --all) all=1 ;;
    --skip-tests) tests=0 ;;
    --checks) checks=1 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg (use --all, --skip-tests, --checks or --help)"; exit 2 ;;
  esac
done

# Prerequisites
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 10 SDK is not installed. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again."
  exit 1
fi
sdk=$(dotnet --version 2>/dev/null || true)
if [[ "${sdk%%.*}" != "10" ]]; then
  echo "This build needs the .NET 10 SDK; found '${sdk:-none}'. Install it from https://dotnet.microsoft.com/download/dotnet/10.0."
  exit 1
fi
if ! command -v zip >/dev/null 2>&1; then echo "The zip command is missing."; exit 1; fi

# This computer's runtime identifier
case "$(uname -s)/$(uname -m)" in
  Darwin/arm64) rid=osx-arm64 ;;
  Darwin/x86_64) rid=osx-x64 ;;
  Linux/x86_64) rid=linux-x64 ;;
  Linux/aarch64|Linux/arm64) rid=linux-arm64 ;;
  *) echo "Unsupported computer: $(uname -s) $(uname -m)."; exit 1 ;;
esac
echo "== .NET SDK $sdk, this computer: $rid"

if [[ $checks == 1 ]]; then
  ./scripts/check.sh
elif [[ $tests == 1 ]]; then
  echo "== Restore (locked)"; dotnet restore DicomLogAnalyser.slnx --locked-mode
  echo "== Build";            dotnet build DicomLogAnalyser.slnx -c Release --no-restore
  echo "== Tests";            dotnet test DicomLogAnalyser.slnx -c Release --no-build
fi

if [[ $all == 1 ]]; then
  ./scripts/release.sh
  echo "Packages: artifacts/release/ (with SHA256SUMS.txt)"
else
  ./scripts/package.sh "$rid"
  echo "To start it: unzip artifacts/package/DicomLogAnalyser-$rid.zip and open start.command."
fi

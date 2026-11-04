#!/usr/bin/env bash
# Runs every automated check locally (the same steps as CI). Requires the .NET 10 SDK, Node.js 22 and Python 3.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== Secret / PHI scan";            python3 scripts/check-secrets.py
echo "== Restore (locked)";             dotnet restore DicomLogAnalyser.slnx --locked-mode
echo "== Format / style";               dotnet format DicomLogAnalyser.slnx --verify-no-changes --no-restore
echo "== Build (warnings as errors)";   dotnet build DicomLogAnalyser.slnx -c Release --no-restore
echo "== Tests";                        dotnet test DicomLogAnalyser.slnx -c Release --no-build
echo "== Vulnerable packages"
out=$(dotnet list DicomLogAnalyser.slnx package --vulnerable --include-transitive 2>&1); echo "$out"
if grep -q "has the following vulnerable packages" <<<"$out"; then echo "Vulnerable packages found."; exit 1; fi
echo "== UI type check"
(cd tools/ui-check && npm ci --no-fund --ignore-scripts && npm audit --audit-level=low && npm run -s typecheck)
echo "All checks passed."

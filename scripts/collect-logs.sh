#!/usr/bin/env bash
# Collects log files from a folder into a .zip bundle for import into DICOM Log Analyser (macOS/Linux).
# Read-only: files are only read. The bundle contains PHI: keep it on approved encrypted storage, delete after import.
#   ./collect-logs.sh /Volumes/ClinicalLogs/Gateway/US ~/Desktop/gateway-logs.zip [days]
set -euo pipefail
src="${1:?source folder}"; dest="${2:?destination .zip}"; days="${3:-0}"
[[ -e "$dest" ]] && { echo "Destination already exists: $dest"; exit 1; }
dest="$(cd "$(dirname "$dest")" && pwd)/$(basename "$dest")"
cd "$src"
args=(. -type f)
[[ "$days" -gt 0 ]] && args+=(-mtime "-$days")
find "${args[@]}" | grep -E '(\.log|\.txt|\.out|\.log\.[0-9]{1,4}|\.[0-9]{1,4}|\.log\.[0-9]{4}-?[0-9]{2}-?[0-9]{2})$' | sed 's#^\./##' | zip -q -@ "$dest"
echo "Written: $dest"
echo "This file contains patient data: keep it on approved encrypted storage and delete it after import."

#!/usr/bin/env bash
# Builds every release package and a SHA256SUMS.txt into artifacts/release/ (used by .github/workflows/release.yml;
# can also be run locally). Requires the .NET 10 SDK and zip.
set -euo pipefail
cd "$(dirname "$0")/.."
rids=(win-x64 win-arm64 osx-arm64 osx-x64 linux-x64)
rm -rf artifacts/release
mkdir -p artifacts/release
for rid in "${rids[@]}"; do
  ./scripts/package.sh "$rid"
  cp "artifacts/package/DicomLogAnalyser-$rid.zip" artifacts/release/
done
cd artifacts/release
if command -v sha256sum >/dev/null; then sha256sum ./*.zip; else shasum -a 256 ./*.zip; fi | sed 's#\./##' > SHA256SUMS.txt
cat SHA256SUMS.txt

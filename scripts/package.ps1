<#
.SYNOPSIS
  Builds a portable, self-contained package of DICOM Log Analyser (no .NET installation needed on the target).
.EXAMPLE
  ./scripts/package.ps1 -Runtime win-x64
#>
param([ValidateSet('win-x64', 'win-arm64', 'osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64')][string] $Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$out = "artifacts/package/DicomLogAnalyser-$Runtime"
foreach ($p in @($out, "$out.zip")) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
dotnet publish src/DicomLogAnalyser.Web -c Release -r $Runtime -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem -LiteralPath $out -Filter *.pdb | Remove-Item -Force
Copy-Item README.md (Join-Path $out 'README.md')
if ($Runtime -like 'win-*') {
  Set-Content -LiteralPath (Join-Path $out 'Start DICOM Log Analyser.cmd') -Value "@echo off`r`nrem Starts DICOM Log Analyser in its own window (this computer only) and opens the browser. Press Q in that window to stop.`r`nstart `"DICOM Log Analyser`" `"%~dp0DicomLogAnalyser.exe`" %*" -Encoding ascii
}
Compress-Archive -Path $out -DestinationPath "$out.zip"
(Get-FileHash "$out.zip" -Algorithm SHA256).Hash.ToLowerInvariant() + "  DicomLogAnalyser-$Runtime.zip" | Set-Content "$out.zip.sha256" -Encoding ascii
Write-Host "Package: $out.zip"

<#
.SYNOPSIS
  One-step local build on Windows, without GitHub: checks the prerequisites, runs the tests and builds the portable
  package for this computer.
.DESCRIPTION
  Needs the .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0). -Checks also needs Python 3 and Node.js.
  Run from PowerShell in the repository folder, for example:
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
.PARAMETER All
  Build every release package (Windows, macOS, Linux) into artifacts\release with SHA256SUMS.txt.
.PARAMETER SkipTests
  Package only.
.PARAMETER Checks
  Run the full checks first (same as CI: PHI scan, format, tests, vulnerability audit, UI type check).
.EXAMPLE
  scripts\build.ps1
.EXAMPLE
  scripts\build.ps1 -All
#>
param([switch] $All, [switch] $SkipTests, [switch] $Checks)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step([string] $Title, [scriptblock] $Command) {
  Write-Host "== $Title"
  & $Command
  if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

# Prerequisites
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw 'The .NET 10 SDK is not installed. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.'
}
$sdk = (& dotnet --version).Trim()
if (-not $sdk.StartsWith('10.')) {
  throw "This build needs the .NET 10 SDK; found '$sdk'. Install it from https://dotnet.microsoft.com/download/dotnet/10.0."
}

# This computer's runtime identifier
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$rid = if ($arch -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
Write-Host "== .NET SDK $sdk, this computer: $rid"

if ($Checks) {
  & (Join-Path $PSScriptRoot 'check.ps1')
}
elseif (-not $SkipTests) {
  Invoke-Step 'Restore (locked)' { dotnet restore DicomLogAnalyser.slnx --locked-mode }
  Invoke-Step 'Build' { dotnet build DicomLogAnalyser.slnx -c Release --no-restore }
  Invoke-Step 'Tests' { dotnet test DicomLogAnalyser.slnx -c Release --no-build }
}

if ($All) {
  $release = 'artifacts/release'
  if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
  New-Item -ItemType Directory -Path $release | Out-Null
  $lines = @()
  foreach ($r in @('win-x64', 'win-arm64', 'osx-arm64', 'osx-x64', 'linux-x64')) {
    & (Join-Path $PSScriptRoot 'package.ps1') -Runtime $r
    $zip = "artifacts/package/DicomLogAnalyser-$r.zip"
    Copy-Item -LiteralPath $zip -Destination $release
    $lines += (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  DicomLogAnalyser-$r.zip"
  }
  $lines | Set-Content (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
  Write-Host "Packages: $release (with SHA256SUMS.txt)"
  Write-Host 'Note: macOS packages built on Windows lose the executable flag inside the zip; build those on a Mac when possible.'
}
else {
  & (Join-Path $PSScriptRoot 'package.ps1') -Runtime $rid
  Write-Host "To start it: extract artifacts\package\DicomLogAnalyser-$rid.zip and double-click 'Start DICOM Log Analyser.cmd'."
}

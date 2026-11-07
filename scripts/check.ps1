<#
.SYNOPSIS
  Runs every automated check locally on Windows (the same steps as CI and scripts/check.sh).
.DESCRIPTION
  Needs the .NET 10 SDK, Node.js 22 and Python 3 (python or py on the PATH).
#>
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Step([string] $Title, [scriptblock] $Command) {
  Write-Host "== $Title"
  & $Command
  if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

$python = @('python3', 'python', 'py') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $python) { throw 'Python 3 is needed for the secret and PHI scan (https://www.python.org/downloads/).' }

Invoke-Step 'Secret / PHI scan' { & $python scripts/check-secrets.py }
Invoke-Step 'Restore (locked)' { dotnet restore DicomLogAnalyser.slnx --locked-mode }
Invoke-Step 'Format / style' { dotnet format DicomLogAnalyser.slnx --verify-no-changes --no-restore }
Invoke-Step 'Build (warnings as errors)' { dotnet build DicomLogAnalyser.slnx -c Release --no-restore }
Invoke-Step 'Tests' { dotnet test DicomLogAnalyser.slnx -c Release --no-build }

Write-Host '== Vulnerable packages'
$out = (& dotnet list DicomLogAnalyser.slnx package --vulnerable --include-transitive 2>&1) -join "`n"
Write-Host $out
if ($out -match 'has the following vulnerable packages') { throw 'Vulnerable packages found.' }

Push-Location tools/ui-check
try {
  Invoke-Step 'UI dependencies' { npm ci --no-fund --ignore-scripts }
  Invoke-Step 'npm audit' { npm audit --audit-level=low }
  Invoke-Step 'UI type check' { npm run -s typecheck }
}
finally { Pop-Location }
Write-Host 'All checks passed.'

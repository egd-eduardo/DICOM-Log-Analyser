<#
.SYNOPSIS
  Collects log files from a folder into a .zip bundle for import into DICOM Log Analyser.
.DESCRIPTION
  Read-only: files are opened for reading with sharing (works while the logging service is writing) and are never
  modified, moved or deleted. Only log-like files are included (.log, .txt, .out and rotated names such as .log.1).
  The bundle contains PHI: store it only on approved, encrypted media and delete it after import.
.EXAMPLE
  ./collect-logs.ps1 -Source 'C:\3dnet\DicomRouter\logs' -Destination "$env:USERPROFILE\Desktop\router-logs.zip" -Days 14
#>
param(
  [Parameter(Mandatory)][string] $Source,
  [Parameter(Mandatory)][string] $Destination,
  [int] $Days = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$root = (Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Destination) { throw "Destination already exists: $Destination" }
$pattern = '(\.log|\.txt|\.out|\.log\.\d{1,4}|\.\d{1,4}|\.log\.\d{4}-?\d{2}-?\d{2})$'
$cutoff = if ($Days -gt 0) { (Get-Date).AddDays(-$Days) } else { [datetime]::MinValue }
$files = Get-ChildItem -LiteralPath $root -File -Recurse -Attributes !ReparsePoint | Where-Object { $_.Name -match $pattern -and $_.LastWriteTime -ge $cutoff }
$zipStream = [System.IO.File]::Open($Destination, 'CreateNew', 'Write', 'None')
$zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
    $in = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite, Delete')
    try { $outStream = $entry.Open(); try { $in.CopyTo($outStream) } finally { $outStream.Dispose() } } finally { $in.Dispose() }
  }
} finally { $zip.Dispose(); $zipStream.Dispose() }
Write-Host "$($files.Count) file(s) written to $Destination"
Write-Host 'This file contains patient data: keep it on approved encrypted storage and delete it after import.'

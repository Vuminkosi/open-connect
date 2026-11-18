<#
.SYNOPSIS
  Fails if any eligible packed NuGet package lacks lib assets for every required TFM.

.DESCRIPTION
  CF-IMP-017 (CF-REQ-095/096/100): after `dotnet pack`, every eligible .nupkg
  must ship lib/net8.0, lib/net9.0 and lib/net10.0 assets. A package is
  eligible when its .nupkg contains a lib/ folder at all; asset-less
  (metapackage/tool) packages are ignored. Package IDs listed in -Exclude
  (prefix match) are skipped and reported.

.PARAMETER PackageDir
  Directory containing the packed *.nupkg files.

.PARAMETER RequiredTfms
  TFMs that must each have a lib/<tfm>/ entry. Defaults to net8.0, net9.0, net10.0.

.PARAMETER Exclude
  Optional package-ID prefixes to skip (e.g. single-TFM Blazor-only packs).

.EXAMPLE
  ./build/scripts/verify-tfm-assets.ps1 -PackageDir ./artifacts/packages
#>
[CmdletBinding()]
param(
  [string]$PackageDir = './artifacts/packages',
  [string[]]$RequiredTfms = @('net8.0', 'net9.0', 'net10.0'),
  [string[]]$Exclude = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $PackageDir)) {
  Write-Error "Package directory not found: $PackageDir"
  exit 1
}

$packages = Get-ChildItem $PackageDir -Filter '*.nupkg' | Where-Object { $_.Name -notmatch '\.symbols\.' }
if ($packages.Count -eq 0) {
  Write-Error "No .nupkg files found in $PackageDir. Run 'dotnet pack' first."
  exit 1
}

$failures = @()
$checked = 0
$skipped = @()

foreach ($pkg in $packages) {
  $excluded = $false
  foreach ($prefix in $Exclude) {
    if ($pkg.Name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $excluded = $true; break }
  }
  if ($excluded) { $skipped += $pkg.Name; continue }

  $zip = [System.IO.Compression.ZipFile]::OpenRead($pkg.FullName)
  try {
    $libTfms = @(
      $zip.Entries |
        Where-Object { $_.FullName -like 'lib/*/*' } |
        ForEach-Object { ($_.FullName -split '/')[1] } |
        Sort-Object -Unique
    )
    if ($libTfms.Count -eq 0) {
      Write-Host "SKIP (no lib assets): $($pkg.Name)"
      continue
    }
    $checked++
    $missing = @($RequiredTfms | Where-Object { $libTfms -notcontains $_ })
    if ($missing.Count -gt 0) {
      $failures += "$($pkg.Name) has lib/$($libTfms -join ', lib/') but is missing lib/$($missing -join ', lib/')"
    } else {
      Write-Host "PASS: $($pkg.Name) ships lib/$($RequiredTfms -join ', lib/')"
    }
  } finally {
    $zip.Dispose()
  }
}

foreach ($s in $skipped) { Write-Host "SKIP (excluded): $s" }
Write-Host "Checked: $checked package(s). Required TFMs: $($RequiredTfms -join ', ')."

if ($failures.Count -gt 0) {
  Write-Error ("TFM asset verification FAILED:`n" + ($failures -join "`n"))
  exit 1
}

Write-Host 'TFM asset verification PASSED.'

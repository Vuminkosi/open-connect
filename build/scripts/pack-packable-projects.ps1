[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$OutputDirectory,
  [string]$Configuration = 'Release',
  [string]$Version,
  [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$packageDirectory = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repositoryRoot $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null

$projects = Get-ChildItem (Join-Path $repositoryRoot 'src') -Filter '*.csproj' -Recurse | Sort-Object FullName
if ($projects.Count -eq 0) { throw 'No library projects were found under src/.' }

$packed = 0
foreach ($project in $projects) {
  $packable = (& dotnet msbuild $project.FullName -getProperty:IsPackable).Trim()
  if ($LASTEXITCODE -ne 0) { throw "Could not read IsPackable for $($project.FullName)." }
  if ($packable -ne 'true') {
    Write-Host "SKIP (IsPackable=$packable): $($project.Name)"
    continue
  }

  $packArguments = @('pack', $project.FullName, '--configuration', $Configuration, '--output', $packageDirectory, '-warnaserror', '-p:ContinuousIntegrationBuild=true')
  if ($NoRestore) { $packArguments += '--no-restore' }
  if (-not [string]::IsNullOrWhiteSpace($Version)) { $packArguments += "/p:Version=$Version" }

  Write-Host "PACK: $($project.Name)"
  & dotnet @packArguments
  if ($LASTEXITCODE -ne 0) { throw "Packing failed for $($project.FullName) with exit code $LASTEXITCODE." }
  $packed++
}

if ($packed -eq 0) { throw 'No packable library projects were found.' }
Write-Host "Packed $packed library projects into $packageDirectory."

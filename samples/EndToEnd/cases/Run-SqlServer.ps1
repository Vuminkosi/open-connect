#Requires -Version 5.1
<#
.SYNOPSIS
  Live SQL Server sample check, gated on explicitly configured disposable fixtures.
  Applies sample migrations; never creates credentials, pulls images, or deletes databases.
#>
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [string]$DbFile = '',
    [int]$TimeoutSec = 300
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\E2E-Common.ps1')

$case = 'SqlServer'
if ([string]::IsNullOrWhiteSpace($env:OPENCONNECT_E2E_SQLSERVER_HOST) -or
    [string]::IsNullOrWhiteSpace($env:OPENCONNECT_SQLSERVER_CONNECTION)) {
    Write-E2ECaseResult -Case $case -Status 'NOT VERIFIED' -ExitCode 3 -Detail 'Disposable fixture host and connection environment variables are required; no execution attempted'
    exit 3
}
$failures = @()
foreach ($verb in @('migrate', 'run', 'verify')) {
    $log = Join-Path $LogDir ("sqlserver-" + $verb + '.log')
    $r = Invoke-E2EStep -StepName $verb -RepoRoot $RepoRoot -LogFile $log `
        -DotnetArgs @('run', '--project', 'samples/SqlServer/OpenConnect.Samples.SqlServer.csproj', '--', $verb) `
        -TimeoutSec $TimeoutSec -ExtraEnv @{}
    Write-Host ("E2E-STEP {0}/{1} exit={2} timedOut={3} elapsedSec={4}" -f $case, $verb, $r.ExitCode, $r.TimedOut, $r.ElapsedSec)
    Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
    if ($r.TimedOut -or ($r.ExitCode -ne 0)) { $failures += $verb }
}
Write-Host 'E2E-CLEANUP SqlServer db=not-applicable (configured disposable database retained; no volume deletion)'
if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail 'migrate:0 run:0 verify:0'; exit 0 }
Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed steps: ' + ($failures -join ','))
exit 1

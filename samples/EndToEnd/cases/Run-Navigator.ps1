#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: Navigator sample (lease fencing + outbox relay, own SQLite file).
  Verbs exercised: migrate, lease-demo x2 (rerun-safe), outbox-relay-demo x2
  (rerun-safe), verify. No receipt paths are exercised: receipt carrier is
  UNDECIDED and this slice implements none (see MATRIX.md).
#>
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$DbFile,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [int]$TimeoutSec = 300
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\E2E-Common.ps1')

$case = 'Navigator'
$failures = @()
$extra = @{ 'OPENCONNECT_NAVIGATOR_SQLITE_PATH' = $DbFile }
$project = 'samples/Navigator/OpenConnect.Samples.Navigator.csproj'
$runIndex = 0

function Invoke-NavigatorVerb($verb, $label) {
    $runIndexLocal = $script:runIndex
    $script:runIndex = $runIndexLocal + 1
    $log = Join-Path $LogDir ("navigator-" + $label + '.log')
    $r = Invoke-E2EStep -StepName $label -RepoRoot $RepoRoot -LogFile $log `
        -DotnetArgs @('run', '--project', $project, '--', $verb) `
        -TimeoutSec $TimeoutSec -ExtraEnv $extra
    Write-Host ("E2E-STEP {0}/{1} exit={2} timedOut={3} elapsedSec={4}" -f $case, $label, $r.ExitCode, $r.TimedOut, $r.ElapsedSec)
    Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
    if ($r.TimedOut -or ($r.ExitCode -ne 0)) { $script:failures += $label }
}

try {
    Invoke-NavigatorVerb 'migrate' 'migrate'
    Invoke-NavigatorVerb 'lease-demo' 'lease-demo-1'
    Invoke-NavigatorVerb 'lease-demo' 'lease-demo-2-rerun'
    Invoke-NavigatorVerb 'outbox-relay-demo' 'outbox-relay-demo-1'
    Invoke-NavigatorVerb 'outbox-relay-demo' 'outbox-relay-demo-2-rerun'
    Invoke-NavigatorVerb 'verify' 'verify'
}
finally {
    $cleanup = Remove-E2EFile -Case $case -Path $DbFile
    Write-Host ("E2E-CLEANUP {0} db={1}" -f $case, $cleanup)
}

if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail 'migrate:0 lease-demo:0,0-rerun outbox-relay-demo:0,0-rerun verify:0'; exit 0 }
else { Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed steps: ' + ($failures -join ',')); exit 1 }

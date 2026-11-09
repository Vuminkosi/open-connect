#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: InMemory console sample (NON-DURABLE, dev-only smoke check).
  Verbs exercised: run, verify. `migrate` is rejected by design and is NOT
  invoked. No file is created; isolation comes from a unique database name.
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

$case = 'InMemory'
$failures = @()
$dbName = ('oc-e2e-inmemory-{0}' -f $PID)
$extra = @{ 'OPENCONNECT_INMEMORY_DBNAME' = $dbName }

try {
    foreach ($verb in @('run', 'verify')) {
        $log = Join-Path $LogDir ("inmemory-" + $verb + '.log')
        $r = Invoke-E2EStep -StepName $verb -RepoRoot $RepoRoot -LogFile $log `
            -DotnetArgs @('run', '--project', 'samples/InMemory/OpenConnect.Samples.InMemory.csproj', '--', $verb) `
            -TimeoutSec $TimeoutSec -ExtraEnv $extra
        Write-Host ("E2E-STEP {0}/{1} exit={2} timedOut={3} elapsedSec={4}" -f $case, $verb, $r.ExitCode, $r.TimedOut, $r.ElapsedSec)
        Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
        if ($r.TimedOut -or ($r.ExitCode -ne 0)) { $failures += $verb }
    }
    Write-Host 'E2E-CLEANUP InMemory db=not-applicable (no file; in-process store dies with the sample process)'
}
finally { }

if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail 'run:0 verify:0 (migrate not invoked: rejected by design)'; exit 0 }
else { Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed steps: ' + ($failures -join ',')); exit 1 }

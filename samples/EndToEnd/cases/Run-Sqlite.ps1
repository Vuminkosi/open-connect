#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: Sqlite console sample (own SQLite file, oc_ prefix, own history).
  Verbs exercised: run, verify. Seeds no data. Contract: OC-PERSIST-001.
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

$case = 'Sqlite'
$failures = @()
$extra = @{ 'OPENCONNECT_SQLITE_PATH' = $DbFile }

try {
    foreach ($verb in @('run', 'verify')) {
        $log = Join-Path $LogDir ("sqlite-" + $verb + '.log')
        $r = Invoke-E2EStep -StepName $verb -RepoRoot $RepoRoot -LogFile $log `
            -DotnetArgs @('run', '--project', 'samples/Sqlite/OpenConnect.Samples.Sqlite.csproj', '--', $verb) `
            -TimeoutSec $TimeoutSec -ExtraEnv $extra
        Write-Host ("E2E-STEP {0}/{1} exit={2} timedOut={3} elapsedSec={4}" -f $case, $verb, $r.ExitCode, $r.TimedOut, $r.ElapsedSec)
        Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
        if ($r.TimedOut -or ($r.ExitCode -ne 0)) { $failures += $verb }
    }
}
finally {
    $cleanup = Remove-E2EFile -Case $case -Path $DbFile
    Write-Host ("E2E-CLEANUP {0} db={1}" -f $case, $cleanup)
}

if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail 'run:0 verify:0'; exit 0 }
else { Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed steps: ' + ($failures -join ',')); exit 1 }

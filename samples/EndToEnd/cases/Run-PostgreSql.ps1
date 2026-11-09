#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: PostgreSql sample. LIVE steps run ONLY when a disposable database
  is reachable via pre-existing environment (gate below). This harness NEVER
  invents, derives, or logs connection strings/credentials.

  Gate (exact): $env:OPENCONNECT_E2E_POSTGRES_HOST must already be set in the
  process environment AND $env:OPENCONNECT_POSTGRESQL_CONNECTION (the sample's
  own connection variable) must already be set. Otherwise the live cell is
  recorded NOT VERIFIED and the script exits 3.

  The offline `parity` verb (no connection, no seeding) always runs as an
  informational step; it does not verify live persistence.
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

$case = 'PostgreSql'
$failures = @()
$project = 'samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj'

$parityLog = Join-Path $LogDir 'postgresql-parity-offline.log'
$parity = Invoke-E2EStep -StepName 'parity-offline' -RepoRoot $RepoRoot -LogFile $parityLog `
    -DotnetArgs @('run', '--project', $project, '--', 'parity') `
    -TimeoutSec $TimeoutSec -ExtraEnv @{}
Write-Host ("E2E-STEP {0}/parity-offline exit={1} timedOut={2} elapsedSec={3}" -f $case, $parity.ExitCode, $parity.TimedOut, $parity.ElapsedSec)
Write-Host (Get-E2EScrubbedTail -LogFile $parityLog -MaxLines 12)
if ($parity.TimedOut -or ($parity.ExitCode -ne 0)) { $failures += 'parity-offline' }

$hostGate = $env:OPENCONNECT_E2E_POSTGRES_HOST
$connGate = $env:OPENCONNECT_POSTGRESQL_CONNECTION
if ([string]::IsNullOrWhiteSpace($hostGate)) {
    Write-Host 'E2E-GATE PostgreSql live steps NOT VERIFIED: OPENCONNECT_E2E_POSTGRES_HOST is not set (missing-fixture). Harness will not invent a target.'
    Write-Host 'E2E-CLEANUP PostgreSql db=not-applicable (no live database touched; no file created)'
    Write-E2ECaseResult -Case $case -Status 'GATED-PARITY-ONLY' -ExitCode 3 -Detail 'parity-offline executed; live run/verify NOT VERIFIED (missing fixture)'
    exit 3
}
if ([string]::IsNullOrWhiteSpace($connGate)) {
    Write-Host 'E2E-GATE PostgreSql live steps NOT VERIFIED: OPENCONNECT_E2E_POSTGRES_HOST is set but OPENCONNECT_POSTGRESQL_CONNECTION is not set. Harness will not derive credentials from a host name.'
    Write-Host 'E2E-CLEANUP PostgreSql db=not-applicable (no live database touched; no file created)'
    Write-E2ECaseResult -Case $case -Status 'GATED-PARITY-ONLY' -ExitCode 3 -Detail 'parity-offline executed; live run/verify NOT VERIFIED (no connection string provided)'
    exit 3
}

try {
    foreach ($verb in @('run', 'verify')) {
        $log = Join-Path $LogDir ("postgresql-" + $verb + '.log')
        # Connection string flows ONLY from the pre-existing process environment
        # into the child; it is never printed by this harness.
        $r = Invoke-E2EStep -StepName $verb -RepoRoot $RepoRoot -LogFile $log `
            -DotnetArgs @('run', '--project', $project, '--', $verb) `
            -TimeoutSec $TimeoutSec -ExtraEnv @{}
        Write-Host ("E2E-STEP {0}/{1} exit={2} timedOut={3} elapsedSec={4}" -f $case, $verb, $r.ExitCode, $r.TimedOut, $r.ElapsedSec)
        Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
        if ($r.TimedOut -or ($r.ExitCode -ne 0)) { $failures += $verb }
    }
}
finally {
    Write-Host 'E2E-CLEANUP PostgreSql db=not-applicable (live disposable database left untouched; no temp file created)'
}

if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail 'parity:0 run:0 verify:0 against disposable PG from pre-existing env'; exit 0 }
else { Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed steps: ' + ($failures -join ',')); exit 1 }

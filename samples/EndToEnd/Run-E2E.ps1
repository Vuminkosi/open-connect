#Requires -Version 5.1
<#
.SYNOPSIS
  Orchestrator for the PERSIST-12 contract-first E2E harness.

.DESCRIPTION
  Runs each per-sample case script (cases/Run-*.ps1) as an isolated child
  PowerShell process with its own temp SQLite file. This script is a thin
  sequential launcher with a bounded per-case timeout: it is NOT a framework,
  NOT a scheduler, NOT a daemon, and performs NO product work itself.

  Exit codes: 0 = no FAIL cell (PASS and/or NOT VERIFIED only);
              1 = at least one FAIL cell.
  NOT VERIFIED (missing-fixture/gated) cells do NOT fail the run.

  Every database touched is a temp-dir file unique to this run (separate DB
  per sample). No production/shared databases, no credentials in output, no
  image pulls. Temp files are removed at the end and cleanup is reported.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File samples/EndToEnd/Run-E2E.ps1
#>
param(
    [int]$StepTimeoutSec = 300,
    [int]$CaseTimeoutSec = 1200,
    [int]$BlazorPort = 5399,
    [int]$BlazorStartupTimeoutSec = 300
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Get-Item (Join-Path $PSScriptRoot '..\..')).FullName
$EndToEndDir = $PSScriptRoot
$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("oc-e2e-" + $runId)
$LogDir = Join-Path $TempRoot 'logs'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

Write-Host ("E2E-RUN repoRoot={0} tempRoot={1} runId={2}" -f $RepoRoot, $TempRoot, $runId)
try { $head = (& git -C $RepoRoot rev-parse HEAD 2>$null); Write-Host ("E2E-RUN HEAD={0}" -f $head) } catch { Write-Host 'E2E-RUN HEAD=unknown (git unavailable)' }

$cases = @(
    @{ Name = 'Minimal';    Script = 'Run-Minimal.ps1';    Db = 'minimal-sample.db';   ExtraArgs = @() },
    @{ Name = 'Sqlite';     Script = 'Run-Sqlite.ps1';     Db = 'sqlite-sample.db';    ExtraArgs = @() },
    @{ Name = 'InMemory';   Script = 'Run-InMemory.ps1';   Db = 'inmemory.unused';     ExtraArgs = @() },
    @{ Name = 'Blazor';     Script = 'Run-Blazor.ps1';     Db = 'blazor-sample.db';    ExtraArgs = @('-Port', "$BlazorPort", '-StartupTimeoutSec', "$BlazorStartupTimeoutSec") },
    @{ Name = 'Navigator';  Script = 'Run-Navigator.ps1'; Db = 'navigator-sample.db'; ExtraArgs = @() },
    @{ Name = 'PostgreSql'; Script = 'Run-PostgreSql.ps1'; Db = 'postgresql.unused';   ExtraArgs = @() },
    @{ Name = 'SqlServer';  Script = 'Run-SqlServer.ps1';  Db = 'sqlserver.unused';    ExtraArgs = @() }
)

$results = @()
foreach ($c in $cases) {
    $scriptPath = Join-Path $EndToEndDir ("cases\" + $c.Script)
    $dbFile = Join-Path $TempRoot $c.Db
    $caseLog = Join-Path $LogDir ("case-" + $c.Name + '.log')
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath,
        '-RepoRoot', $RepoRoot, '-LogDir', $LogDir) + @('-DbFile', $dbFile) + $c.ExtraArgs +
        @('-TimeoutSec', "$StepTimeoutSec")

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $powerShellExe
    $quoted = @()
    foreach ($a in $argList) {
        if ($a -match '\s') { $quoted += ('"' + $a + '"') } else { $quoted += $a }
    }
    $psi.Arguments = [string]::Join(' ', $quoted)
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $exited = $p.WaitForExit($CaseTimeoutSec * 1000)
    if (-not $exited) {
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(15000) | Out-Null } catch { }
        $results += [pscustomobject]@{ Case = $c.Name; ExitCode = 124; Status = 'FAIL'; Detail = 'case timed out (killed)' }
        Write-Host ("E2E-RESULT {0} exit=124 status=FAIL detail=case-timed-out" -f $c.Name)
    }
    else {
        $code = $p.ExitCode
        $status = 'FAIL'
        if ($code -eq 0) { $status = 'PASS' }
        elseif ($code -eq 3) { $status = 'NOT VERIFIED' }
        $results += [pscustomobject]@{ Case = $c.Name; ExitCode = $code; Status = $status; Detail = 'see case log' }
        Write-Host ("E2E-RESULT {0} exit={1} status={2}" -f $c.Name, $code, $status)
    }
    try { Set-Content -Path $caseLog -Value $outTask.Result } catch { }
    try { Set-Content -Path ($caseLog + '.stderr.log') -Value $errTask.Result } catch { }
    $p.Dispose()
}

Write-Host ''
Write-Host 'E2E-SUMMARY case=status(exit)'
foreach ($r in $results) { Write-Host ("E2E-SUMMARY {0}={1}({2})" -f $r.Case, $r.Status, $r.ExitCode) }

$failed = @($results | Where-Object { $_.Status -eq 'FAIL' })
$runExit = 0
if ($failed.Count -gt 0) { $runExit = 1 }

$cleanup = 'removed'
try {
    if (Test-Path -Path $TempRoot) { Remove-Item -Path $TempRoot -Recurse -Force }
    else { $cleanup = 'absent-already' }
}
catch { $cleanup = 'remove-failed' }
Write-Host ("E2E-CLEANUP tempRoot={0} logsRemovedWithTempRoot=true" -f $cleanup)

if ($runExit -eq 0) { Write-Host 'E2E-RUN overall=PASS-or-NOT-VERIFIED exit=0' }
else { Write-Host 'E2E-RUN overall=FAIL exit=1' }
exit $runExit

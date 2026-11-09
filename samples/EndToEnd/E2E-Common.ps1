#Requires -Version 5.1
<#
.SYNOPSIS
  Shared helpers for the PERSIST-12 contract-first E2E harness (samples/EndToEnd).

.DESCRIPTION
  Dot-sourced by each per-sample case script. This file is harness plumbing only:
  it is NOT product code, NOT a test framework, NOT a scheduler, NOT a daemon.
  All product verification runs through the repo-approved `dotnet` CLI only.

  Rules enforced here:
  - SQLite files live in caller-supplied temp paths (never production/shared DBs).
  - Secret values (connection strings, passwords, tokens) are NEVER written to
    host output. Log tails are scrubbed before printing.
  - Every step has a bounded timeout and an explicit exit code.

  Case-script exit-code convention (also documented in MATRIX.md):
    0 = PASS, 1 = FAIL, 3 = NOT VERIFIED (missing fixture / gated / skipped).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$E2E_PASS = 0
$E2E_FAIL = 1
$E2E_NOT_VERIFIED = 3
$E2E_TIMED_OUT_CODE = 124

function Invoke-E2EStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StepName,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$LogFile,
        [Parameter(Mandatory = $true)][string[]]$DotnetArgs,
        [int]$TimeoutSec = 300,
        [hashtable]$ExtraEnv = @{}
    )

    $errFile = "$LogFile.stderr.log"
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'dotnet'
        $quoted = @()
        foreach ($a in $DotnetArgs) {
            if ($a -match '\s') { $quoted += ('"' + $a + '"') } else { $quoted += $a }
        }
        $psi.Arguments = [string]::Join(' ', $quoted)
        $psi.WorkingDirectory = $RepoRoot
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        foreach ($key in $ExtraEnv.Keys) {
            $psi.EnvironmentVariables[$key] = [string]$ExtraEnv[$key]
        }
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        [void]$proc.Start()

        # Async reads avoid pipe-deadlock on large build output.
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $exited = $proc.WaitForExit($TimeoutSec * 1000)
        if (-not $exited) {
            try { $proc.Kill() } catch { }
            try { $proc.WaitForExit(15000) | Out-Null } catch { }
            $out = ''
            $err = ''
            try { $out = $stdoutTask.Result } catch { }
            try { $err = $stderrTask.Result } catch { }
            Set-Content -Path $LogFile -Value $out
            Set-Content -Path $errFile -Value $err
            $stopwatch.Stop()
            return [pscustomobject]@{
                StepName = $StepName; ExitCode = $E2E_TIMED_OUT_CODE; TimedOut = $true
                ElapsedSec = [int]$stopwatch.Elapsed.TotalSeconds
                LogFile = $LogFile; ErrFile = $errFile
            }
        }
        $out = $stdoutTask.Result
        $err = $stderrTask.Result
        Set-Content -Path $LogFile -Value $out
        Set-Content -Path $errFile -Value $err
        $code = $proc.ExitCode
        $stopwatch.Stop()
        return [pscustomobject]@{
            StepName = $StepName; ExitCode = $code; TimedOut = $false
            ElapsedSec = [int]$stopwatch.Elapsed.TotalSeconds
            LogFile = $LogFile; ErrFile = $errFile
        }
    }
    finally {
        if ($proc -ne $null) { $proc.Dispose() }
    }
}

function Get-E2EScrubbedTail {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LogFile,
        [int]$MaxLines = 12
    )
    if (-not (Test-Path -Path $LogFile)) { return '(no log file produced)' }
    # Drop any line shaped like an assigned secret before printing. Samples are
    # designed not to emit secrets; this is defense-in-depth for harness output.
    $lines = Get-Content -Path $LogFile |
        Where-Object { $_ -notmatch '(?i)(password|passwd|pwd|secret|token|connection\s*string)\s*=' } |
        Select-Object -Last $MaxLines
    if ($lines -eq $null -or $lines.Count -eq 0) { return '(log empty after scrub)' }
    return ([string]::Join([Environment]::NewLine, $lines))
}

function Write-E2ECaseResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Case,
        [Parameter(Mandatory = $true)][string]$Status,
        [int]$ExitCode = 0,
        [Parameter(Mandatory = $true)][string]$Detail
    )
    Write-Host ("E2E-CASE {0} status={1} exit={2} detail={3}" -f $Case, $Status, $ExitCode, $Detail)
}

function Remove-E2EFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Case,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return 'not-applicable' }
    try {
        if (Test-Path -Path $Path) { Remove-Item -Path $Path -Force; return 'removed' }
        return 'absent-already'
    }
    catch {
        return 'remove-failed'
    }
}

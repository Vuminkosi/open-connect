#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: Blazor sample HTTP smoke (no browser).
  Starts the app against an isolated temp SQLite file, polls GET / for HTTP
  200 plus a stable content marker, then stops the app AND its child server
  process (dotnet run parents a dotnet exec child; both are stopped so the
  loopback port is released for later runs). No browser automation.
  Pre-flight fails fast with port-in-use-at-start if the port is occupied.
#>
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$DbFile,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [int]$Port = 5399,
    [int]$TimeoutSec = 300,
    [int]$StartupTimeoutSec = 300,
    [int]$PollIntervalSec = 2
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\E2E-Common.ps1')

$case = 'Blazor'
$failures = @()
$log = Join-Path $LogDir 'blazor-http-smoke.log'
$errLog = "$log.stderr.log"
$baseUrl = ("http://127.0.0.1:{0}/" -f $Port)

if (-not [string]::IsNullOrWhiteSpace($env:OPENCONNECT_E2E_BLAZOR_PORT)) {
    $parsed = 0
    if ([int]::TryParse($env:OPENCONNECT_E2E_BLAZOR_PORT, [ref]$parsed) -and ($parsed -gt 0)) {
        $Port = $parsed
        $baseUrl = ("http://127.0.0.1:{0}/" -f $Port)
    }
}

function Test-E2EPortOpen($port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        $connected = $iar.AsyncWaitHandle.WaitOne(2000)
        if (-not $connected) { return $false }
        $client.EndConnect($iar)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Get-E2EChildPids($parentPid) {
    try {
        $kids = Get-CimInstance -ClassName Win32_Process -Filter ("ParentProcessId=" + $parentPid) -ErrorAction Stop
        $ids = @()
        foreach ($k in $kids) { $ids += [int]$k.ProcessId }
        return $ids
    }
    catch { return @() }
}

$proc = $null
try {
    if (Test-E2EPortOpen $Port) {
        $failures += 'port-in-use-at-start'
        Write-Host ("E2E-STEP {0}/preflight port {1} already occupied; refusing to start (fail-fast)" -f $case, $Port)
    }
    else {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'dotnet'
        $psi.Arguments = 'run --project samples/Blazor/OpenConnect.Samples.Blazor.csproj --no-launch-profile'
        $psi.WorkingDirectory = $RepoRoot
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.EnvironmentVariables['OPENCONNECT_BLAZOR_SQLITE_PATH'] = $DbFile
        $psi.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'
        $psi.EnvironmentVariables['ASPNETCORE_URLS'] = ("http://127.0.0.1:{0}" -f $Port)
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        [void]$proc.Start()
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSec)
        $ok = $false
        $lastStatus = 0
        $sawMarker = $false
        while (([DateTime]::UtcNow -lt $deadline) -and (-not $ok) -and (-not $proc.HasExited)) {
            Start-Sleep -Seconds $PollIntervalSec
            try {
                $resp = Invoke-WebRequest -Uri $baseUrl -TimeoutSec 5 -UseBasicParsing
                $lastStatus = [int]$resp.StatusCode
                if ($lastStatus -eq 200) {
                    if ($resp.Content -match 'What needs attention\?') { $sawMarker = $true; $ok = $true }
                }
            }
            catch { }
        }

        if ($proc.HasExited -and (-not $ok)) { $failures += 'app-exited-before-ready' }
        if (-not $ok) { $failures += 'http-200-with-marker' }
        Write-Host ("E2E-STEP {0}/http-smoke ready={1} lastStatus={2} sawMarker={3} url={4}" -f $case, $ok, $lastStatus, $sawMarker, $baseUrl)
    }
}
finally {
    if ($proc -ne $null) {
        $childPids = Get-E2EChildPids $proc.Id
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        try { $proc.WaitForExit(15000) | Out-Null } catch { }
        foreach ($cp in $childPids) {
            try {
                $ck = Get-Process -Id $cp -ErrorAction Stop
                if ($ck.ProcessName -eq 'dotnet') { $ck.Kill() }
            }
            catch { }
        }
        Start-Sleep -Seconds 2
        $portStillOpen = Test-E2EPortOpen $Port
        try { Set-Content -Path $log -Value $stdoutTask.Result } catch { }
        try { Set-Content -Path $errLog -Value $stderrTask.Result } catch { }
        $proc.Dispose()
        Write-Host ("E2E-CLEANUP {0} portReleased={1}" -f $case, ((-not $portStillOpen)))
    }
    Write-Host (Get-E2EScrubbedTail -LogFile $log -MaxLines 12)
    $cleanup = Remove-E2EFile -Case $case -Path $DbFile
    Write-Host ("E2E-CLEANUP {0} db={1} appProcessTree=stopped" -f $case, $cleanup)
}

if ($failures.Count -eq 0) { Write-E2ECaseResult -Case $case -Status 'PASS' -ExitCode 0 -Detail ('GET / -> 200 with content marker on port ' + $Port); exit 0 }
else { Write-E2ECaseResult -Case $case -Status 'FAIL' -ExitCode 1 -Detail ('failed: ' + ($failures -join ',')); exit 1 }

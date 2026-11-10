<#
.SYNOPSIS
  Cross-TFM smoke harness: forward (net10 host + net8 client) and reverse
  (net8 host + net10 client) legs from a single command.

.DESCRIPTION
  CF-IMP-020c. Checked-in, CI-invocable reproduction of the twice-verified
  temp PoC legs CF-AT-023 (forward) and CF-AT-024 (reverse).

  Steps:
    (a) `dotnet pack` OpenConnect.Abstractions + Core + Agents + Client +
        Hosting.AspNetCore into a temp local feed;
    (b) scaffold host + client console apps in a temp dir from embedded
        sources for the FORWARD leg (host net10.0, client net8.0) and the
        REVERSE leg (host net8.0, client net10.0) -- self-contained, no
        dependency on temp PoC leftovers;
    (c) run the forward pair, assert CLIENT_PASS + exit 0;
    (d) run the reverse pair, assert CLIENT_PASS + exit 0;
    (e) ALWAYS stop hosts + verify ports free (try/finally); non-zero exit
        with a fail-closed reason naming the failed leg/step.

  Fixture auth uses the fixed ULID tenant/principal pair from the PoC
  (01ARZ3NDEKTSV4RRFFQ69G5FAV / 01ARZ3NDEKTSV4RRFFQ69G5FAW) because the host
  FixtureHandler validates credentials with Ulid.Parse and maps them to
  tenant/principal claims -- random per-run GUIDs would fail ULID parsing.
  No secrets: fixture creds only. No PR publishing.

.PARAMETER Configuration
  Build configuration for pack/scaffold builds. Default: Release.

.PARAMETER PortBase
  Forward-leg loopback port. Reverse leg uses PortBase + 1. Default: 5251
  (matches the PoC: forward 5251, reverse 5252).

.PARAMETER TimeoutSec
  Per-leg budget for host-ready wait + client run. Default: 180.

.PARAMETER DryRun
  Print the planned steps (pack list, legs, ports) and exit 0 without
  invoking dotnet or starting hosts. Cheap CI parse/smoke check.

.EXAMPLE
  powershell -NoProfile -File build/scripts/run-cross-tfm-smoke.ps1 -DryRun

.EXAMPLE
  powershell -NoProfile -File build/scripts/run-cross-tfm-smoke.ps1 -Configuration Release -PortBase 5251 -TimeoutSec 180
#>
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [int]$PortBase = 5251,
  [int]$TimeoutSec = 180,
  [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$PackProjects = @(
  'src/OpenConnect.Abstractions/OpenConnect.Abstractions.csproj',
  'src/OpenConnect.Core/OpenConnect.Core.csproj',
  'src/OpenConnect.Agents/OpenConnect.Agents.csproj',
  'src/OpenConnect.Client/OpenConnect.Client.csproj',
  'src/OpenConnect.Hosting.AspNetCore/OpenConnect.Hosting.AspNetCore.csproj'
)

# Fixed fixture ULIDs (see .SYNOPSIS note: host requires Ulid.Parse-able creds).
$FixtureTenant = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
$FixturePrincipal = '01ARZ3NDEKTSV4RRFFQ69G5FAW'

$ForwardPort = $PortBase
$ReversePort = $PortBase + 1

function Get-HarnessHostSource {
  param([int]$Port)
  # Mirrors the CF-AT-023/024 PoC host Program.cs (Kestrel loopback,
  # Fixture auth with ULID validation, AllowAuthorizer,
  # AppendAndCompleteExecutor). Only the port varies per leg.
  $template = @'
// CF-IMP-020c harness host (embedded): serves the OpenConnect HTTP API over real Kestrel.
// Mirrors OpenConnect.Hosting.Tests HttpFixture (AuthenticatedTaskHttpTests) with a fixed loopback port.
using System;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, __PORT__));
builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureHandler>("Fixture", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddOpenConnectTaskExecution(options => { options.Capacity = 8; options.WorkerConcurrency = 1; });
builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, AllowAuthorizer>();
builder.Services.AddSingleton<IHostTaskExecutor, AppendAndCompleteExecutor>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenConnect();
Console.WriteLine("HOST_READY http://127.0.0.1:__PORT__ framework=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
await app.RunAsync();

sealed class FixtureHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public FixtureHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    private static bool IsUlid(string text)
    {
        try { _ = Ulid.Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (!System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(value, out var header) ||
            header.Scheme != "Fixture" || header.Parameter is null)
            return Task.FromResult(AuthenticateResult.NoResult());
        var parts = header.Parameter.Split(':', StringSplitOptions.None);
        if (parts.Length != 2 || !IsUlid(parts[0]) || !IsUlid(parts[1]))
            return Task.FromResult(AuthenticateResult.Fail("Invalid fixture credentials."));
        var identity = new ClaimsIdentity(
            [new Claim("tenant_id", parts[0]), new Claim(ClaimTypes.NameIdentifier, parts[1])], "Fixture");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Fixture")));
    }
}

sealed class AllowAuthorizer : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
}

sealed class AppendAndCompleteExecutor(InMemorySessionStore sessions) : IHostTaskExecutor
{
    public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
        => sessions.AppendEventAsync(task.SessionId.Value, "task.note", $"completed:{task.TaskId.Value}", cancellationToken);
}
'@
  return $template.Replace('__PORT__', $Port.ToString())
}

function Get-HarnessClientSource {
  param([int]$Port, [string]$TaskName, [string]$IdemKey, [string]$Greeting)
  # Mirrors the CF-AT-023/024 PoC client Program.cs verbatim (health wait,
  # 401 anon check, raw-HTTP session create, capabilities, task
  # create/poll, session read, events at cursor 0, exclusive replay cursor,
  # CLIENT_PASS assertion). Only port/task/greeting vary per leg.
  # NOTE: SocketsHttpHandler UseProxy=false is the PoC proxy workaround --
  # loopback must bypass any machine proxy or the client hits the proxy.
  $template = @'
// CF-IMP-020c harness client (embedded): drives an authenticated task lifecycle
// against the harness host over HTTP. Steps: health, session create (raw HTTP;
// IOpenConnectClient has no CreateSession), task create, task read/poll,
// events read at cursor 0, events replay at last cursor (expect empty).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Client;

const string Tenant = "__TENANT__";
const string Principal = "__PRINCIPAL__";

try
{
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri("http://127.0.0.1:__PORT__/openconnect/v1/"),
        Timeout = TimeSpan.FromSeconds(10),
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Fixture", $"{Tenant}:{Principal}");
    Console.WriteLine("CLIENT_FRAMEWORK " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    // Wait for the host (unauthenticated health route).
    var healthy = false;
    for (var i = 0; i < 150 && !healthy; i++)
    {
        try
        {
            using var h = await http.GetAsync("health");
            healthy = h.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { }
        if (!healthy) await Task.Delay(200);
    }
    if (!healthy) { Console.WriteLine("FAIL host health check never succeeded"); return 10; }
    Console.WriteLine("HEALTH ok");

    // Unauthenticated must be 401 (auth boundary proof).
    using var anon = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
    using var anonResp = await anon.GetAsync("tasks/01ARZ3NDEKTSV4RRFFQ69G5FAV");
    Console.WriteLine($"ANON_STATUS {(int)anonResp.StatusCode}");
    if (anonResp.StatusCode != System.Net.HttpStatusCode.Unauthorized) { Console.WriteLine("FAIL expected 401 for anonymous request"); return 11; }

    // Session create via raw HTTP (client contract has Get but no Create).
    using var sessResp = await http.PostAsync("sessions", JsonContent.Create(new { }));
    Console.WriteLine($"SESSION_STATUS {(int)sessResp.StatusCode}");
    if (!sessResp.IsSuccessStatusCode) { Console.WriteLine("FAIL session create: " + await sessResp.Content.ReadAsStringAsync()); return 12; }
    var session = await sessResp.Content.ReadFromJsonAsync<SessionRecord>(OpenConnectHttpJson.CreateOptions());
    if (session is null) { Console.WriteLine("FAIL session body empty"); return 13; }
    Console.WriteLine($"SESSION id={session.Id.Value} tenant={session.TenantId.Value} principal={session.PrincipalId.Value}");

    IOpenConnectClient client = new OpenConnectClient(http);

    var caps = await client.GetCapabilitiesAsync();
    Console.WriteLine($"CAPABILITIES count={caps.Count}");

    var created = await client.CreateTaskAsync(new CreateTaskRequest(
        "__TASKNAME__", "__IDEMKEY__", "__GREETING__", session.Id.Value.ToString()));
    Console.WriteLine($"TASK_CREATED id={created.Id.Value} state={created.State}");

    var current = created;
    for (var i = 0; i < 100 && current.State != AgentRunState.Completed; i++)
    {
        await Task.Delay(200);
        current = await client.GetTaskAsync(created.Id);
    }
    Console.WriteLine($"TASK_READ id={current.Id.Value} state={current.State}");
    if (current.State != AgentRunState.Completed) { Console.WriteLine("FAIL task never completed"); return 14; }

    var fetchedSession = await client.GetSessionAsync(session.Id);
    Console.WriteLine($"SESSION_READ id={fetchedSession.Id.Value}");
    if (!fetchedSession.Id.Equals(session.Id)) { Console.WriteLine("FAIL session id mismatch"); return 15; }

    var events = new List<EventRecord>();
    await foreach (var e in client.SubscribeEventsAsync(session.Id, 0))
        events.Add(e);
    Console.WriteLine($"EVENTS count={events.Count}");
    foreach (var e in events)
        Console.WriteLine($"EVENT seq={e.Sequence} type={e.Type} data={e.Data}");
    if (events.Count == 0) { Console.WriteLine("FAIL expected executor-appended event"); return 16; }
    var last = events.Max(e => e.Sequence);

    var replayed = new List<EventRecord>();
    await foreach (var e in client.SubscribeEventsAsync(session.Id, last))
        replayed.Add(e);
    Console.WriteLine($"REPLAY_AFTER_{last} count={replayed.Count}");
    if (replayed.Count != 0) { Console.WriteLine("FAIL replay cursor not exclusive"); return 17; }

    Console.WriteLine("CLIENT_PASS lifecycle complete: session+task+read+events+cursor");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL exception: " + ex.GetType().FullName + ": " + ex.Message);
    return 99;
}
'@
  return $template.Replace('__PORT__', $Port.ToString()).Replace('__TENANT__', $FixtureTenant).Replace('__PRINCIPAL__', $FixturePrincipal).Replace('__TASKNAME__', $TaskName).Replace('__IDEMKEY__', $IdemKey).Replace('__GREETING__', $Greeting)
}

function Get-HarnessHostCsproj {
  param([string]$Tfm)
  return @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>$Tfm</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="OpenConnect.Abstractions" Version="*" />
    <PackageReference Include="OpenConnect.Hosting.AspNetCore" Version="*" />
  </ItemGroup>
</Project>
"@
}

function Get-HarnessClientCsproj {
  param([string]$Tfm)
  return @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$Tfm</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenConnect.Abstractions" Version="*" />
    <PackageReference Include="OpenConnect.Client" Version="*" />
  </ItemGroup>
</Project>
"@
}

function Get-HarnessNuGetConfig {
  param([string]$FeedDir)
  $escaped = [System.Security.SecurityElement]::Escape($FeedDir)
  return @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="cf-smoke-feed" value="$escaped" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
}

function Test-HarnessPortFree {
  param([int]$Port)
  $client = $null
  try {
    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
    if ($iar.AsyncWaitHandle.WaitOne(1000) -and $client.Connected) { return $false }
    return $true
  } catch { return $true }
  finally { if ($client -ne $null) { $client.Close() } }
}

function Invoke-HarnessLeg {
  param(
    [string]$LegName,
    [string]$HostDir,
    [string]$ClientDir,
    [string]$HostDll,
    [string]$ClientDll,
    [int]$Port,
    [int]$TimeoutSec
  )
  $hostLog = Join-Path $HostDir 'host.log'
  $clientLog = Join-Path $ClientDir 'client.log'
  $hostProc = $null
  try {
    Write-Host "[$LegName] starting host on 127.0.0.1:$Port ..."
    $hostProc = Start-Process -FilePath 'dotnet' -ArgumentList "`"$HostDll`"" -WorkingDirectory $HostDir -RedirectStandardOutput $hostLog -RedirectStandardError ($hostLog + '.err') -PassThru -NoNewWindow

    # Wait for HOST_READY / health.
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
      if ($hostProc.HasExited) { break }
      if ((Test-Path $hostLog) -and (Select-String -Path $hostLog -Pattern 'HOST_READY' -Quiet)) { $ready = $true; break }
      Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
      $tail = if (Test-Path $hostLog) { (Get-Content $hostLog -Tail 20) -join "`n" } else { '(no host log)' }
      Write-Error "[$LegName] FAIL step=host-ready port=$Port hostExited=$($hostProc.HasExited). Tail:`n$tail"
      exit 1
    }
    Write-Host "[$LegName] host ready."

    Write-Host "[$LegName] running client ..."
    $clientProc = Start-Process -FilePath 'dotnet' -ArgumentList "`"$ClientDll`"" -WorkingDirectory $ClientDir -RedirectStandardOutput $clientLog -RedirectStandardError ($clientLog + '.err') -PassThru -NoNewWindow -Wait
    $output = if (Test-Path $clientLog) { Get-Content $clientLog -Raw } else { '' }
    Write-Host $output
    if ($clientProc.ExitCode -ne 0) {
      Write-Error "[$LegName] FAIL step=client exit=$($clientProc.ExitCode) (expected 0). See $clientLog"
      exit 1
    }
    if ($output -notmatch 'CLIENT_PASS') {
      Write-Error "[$LegName] FAIL step=client-assert exit=0 but CLIENT_PASS missing in output. See $clientLog"
      exit 1
    }
    Write-Host "[$LegName] PASS (exit 0 + CLIENT_PASS)."
  }
  finally {
    if ($hostProc -ne $null -and -not $hostProc.HasExited) {
      Write-Host "[$LegName] stopping host (pid $($hostProc.Id)) ..."
      try { Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue } catch { }
      if (-not $hostProc.WaitForExit(10000)) {
        Write-Error "[$LegName] FAIL step=host-stop host did not exit within 10s."
        exit 1
      }
    }
    Start-Sleep -Milliseconds 1000
    if (-not (Test-HarnessPortFree -Port $Port)) {
      Write-Error "[$LegName] FAIL step=port-free port $Port still bound after host stop."
      exit 1
    }
    Write-Host "[$LegName] port $Port free."
  }
}

# ---- DryRun: print plan only ----
if ($DryRun) {
  Write-Host 'DRYRUN cross-TFM smoke harness plan:'
  Write-Host "  repo=$RepoRoot config=$Configuration timeout=${TimeoutSec}s"
  Write-Host '  pack (to temp feed):'
  foreach ($p in $PackProjects) { Write-Host "    dotnet pack $p -c $Configuration" }
  Write-Host "  FORWARD leg: host net10.0 + client net8.0 on 127.0.0.1:$ForwardPort, assert CLIENT_PASS + exit 0"
  Write-Host "  REVERSE leg: host net8.0 + client net10.0 on 127.0.0.1:$ReversePort, assert CLIENT_PASS + exit 0"
  Write-Host '  cleanup: stop hosts (try/finally), verify ports free, non-zero exit on any failure'
  exit 0
}

# ---- Live run ----
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cf-smoke-' + [Guid]::NewGuid().ToString('N'))
$FeedDir = Join-Path $TempRoot 'feed'
New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null
Write-Host "Workspace: $TempRoot"

try {
  # (a) Pack the five contract/hosting/client packages to the temp feed.
  Write-Host 'Step (a): dotnet pack 5 packages to temp feed ...'
  foreach ($rel in $PackProjects) {
    $proj = Join-Path $RepoRoot $rel
    Write-Host "  packing $rel ..."
    & dotnet pack $proj -c $Configuration -o $FeedDir --nologo
    if ($LASTEXITCODE -ne 0) {
      Write-Error "FAIL step=pack project=$rel exit=$LASTEXITCODE"
      exit 1
    }
  }

  # (b) Scaffold the four apps.
  Write-Host 'Step (b): scaffolding 4 apps from embedded sources ...'
  $legs = @(
    @{ Name = 'FORWARD'; HostTfm = 'net10.0'; ClientTfm = 'net8.0'; Port = $ForwardPort; TaskName = 'x-tfm-task-1'; IdemKey = 'idem-x-tfm-1'; Greeting = 'hello from net8' },
    @{ Name = 'REVERSE'; HostTfm = 'net8.0'; ClientTfm = 'net10.0'; Port = $ReversePort; TaskName = 'x-tfm-task-1-rev'; IdemKey = 'idem-x-tfm-1-rev'; Greeting = 'hello from net10' }
  )
  $built = @()
  foreach ($leg in $legs) {
    $hostDir = Join-Path $TempRoot ("$($leg.Name.ToLower())-host")
    $clientDir = Join-Path $TempRoot ("$($leg.Name.ToLower())-client")
    New-Item -ItemType Directory -Path $hostDir -Force | Out-Null
    New-Item -ItemType Directory -Path $clientDir -Force | Out-Null
    $hostProj = Join-Path $hostDir 'harness-host.csproj'
    $clientProj = Join-Path $clientDir 'harness-client.csproj'
    Set-Content -Path $hostProj -Value (Get-HarnessHostCsproj -Tfm $leg.HostTfm) -Encoding UTF8
    Set-Content -Path (Join-Path $hostDir 'Program.cs') -Value (Get-HarnessHostSource -Port $leg.Port) -Encoding UTF8
    Set-Content -Path $clientProj -Value (Get-HarnessClientCsproj -Tfm $leg.ClientTfm) -Encoding UTF8
    Set-Content -Path (Join-Path $clientDir 'Program.cs') -Value (Get-HarnessClientSource -Port $leg.Port -TaskName $leg.TaskName -IdemKey $leg.IdemKey -Greeting $leg.Greeting) -Encoding UTF8
    Set-Content -Path (Join-Path $hostDir 'NuGet.config') -Value (Get-HarnessNuGetConfig -FeedDir $FeedDir) -Encoding UTF8
    Set-Content -Path (Join-Path $clientDir 'NuGet.config') -Value (Get-HarnessNuGetConfig -FeedDir $FeedDir) -Encoding UTF8

    foreach ($proj in @($hostProj, $clientProj)) {
      Write-Host "  building $proj ..."
      & dotnet build $proj -c $Configuration --nologo
      if ($LASTEXITCODE -ne 0) {
        Write-Error "FAIL step=scaffold-build leg=$($leg.Name) proj=$proj exit=$LASTEXITCODE"
        exit 1
      }
    }
    $hostDll = Get-ChildItem -Path (Join-Path $hostDir "bin/$Configuration") -Recurse -Filter 'harness-host.dll' | Select-Object -First 1
    $clientDll = Get-ChildItem -Path (Join-Path $clientDir "bin/$Configuration") -Recurse -Filter 'harness-client.dll' | Select-Object -First 1
    if ($null -eq $hostDll -or $null -eq $clientDll) {
      Write-Error "FAIL step=scaffold-build leg=$($leg.Name) built dll not found."
      exit 1
    }
    $built += @{ Name = $leg.Name; HostDir = $hostDir; ClientDir = $clientDir; HostDll = $hostDll.FullName; ClientDll = $clientDll.FullName; Port = $leg.Port }
  }

  # (c)+(d) Run legs sequentially (ports are distinct but sequential keeps logs deterministic).
  foreach ($b in $built) {
    Invoke-HarnessLeg -LegName $b.Name -HostDir $b.HostDir -ClientDir $b.ClientDir -HostDll $b.HostDll -ClientDll $b.ClientDll -Port $b.Port -TimeoutSec $TimeoutSec
  }

  Write-Host 'SMOKE_PASS forward+reverse CLIENT_PASS x2.'
  exit 0
}
finally {
  # (e) Best-effort sweep: no stray harness hosts may survive this script.
  Get-Process -Name 'harness-host' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
  }
}

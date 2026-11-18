[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$PackageDirectory,
  [Parameter(Mandatory = $true)]
  [ValidateSet('net8.0', 'net9.0', 'net10.0')]
  [string]$Framework
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$feed = (Resolve-Path $PackageDirectory).Path
$package = Get-ChildItem $feed -Filter 'OpenConnect.LiveUI.Blazor.*.nupkg' |
  Where-Object { $_.Name -notmatch '\.symbols\.' } |
  Sort-Object Name |
  Select-Object -First 1
if (-not $package) { throw "OpenConnect.LiveUI.Blazor package not found in $feed." }
if ($package.Name -notmatch '^OpenConnect\.LiveUI\.Blazor\.(.+)\.nupkg$') {
  throw "Could not read the OpenConnect.LiveUI.Blazor package version from $($package.Name)."
}
$packageVersion = $Matches[1]
$consumer = Join-Path ([System.IO.Path]::GetTempPath()) ("oc-lui-" + $Framework.Replace('.', '') + '-' + [guid]::NewGuid().ToString('N'))

& dotnet new web -o $consumer -f $Framework --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Could not create the temporary ASP.NET Core consumer.' }

$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
$packageCache = Join-Path ([System.IO.Path]::GetTempPath()) ("oc-nuget-" + [guid]::NewGuid().ToString('N'))
$globalPackages = [System.Security.SecurityElement]::Escape($packageCache)
@"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$globalPackages" />
  </config>
</configuration>
"@ | Set-Content -Path (Join-Path $consumer 'NuGet.Config')

@"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>$Framework</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenConnect.LiveUI.Blazor" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path (Join-Path $consumer 'Consumer.csproj')

@"
@page "/"
<h1>Package consumer</h1>
<button>Continue</button>
"@ | Set-Content -Path (Join-Path $consumer 'Home.razor')

@'
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;

var manifest = LiveUICompiledManifestLoader.LoadFromEntryAssembly();
if (manifest is null || manifest.Routes.Count == 0)
{
    throw new InvalidOperationException("The NuGet build target did not embed a usable LiveUI manifest.");
}

var route = manifest.Routes.SingleOrDefault(candidate => candidate.Path == "/")
    ?? throw new InvalidOperationException("The embedded manifest did not include the consumer's root route.");
var button = route.Targets.SingleOrDefault(target => target.Kind == LiveUITargetKind.Button && target.Label == "Continue")
    ?? throw new InvalidOperationException("The embedded manifest did not discover the unannotated Continue button.");
if (!button.Capabilities.Contains(LiveUICapability.Activate) || !button.Capabilities.Contains(LiveUICapability.Highlight))
{
    throw new InvalidOperationException("The unannotated Continue button is missing its baseline capabilities.");
}

Console.WriteLine($"consumer-ok:{route.Id}:{route.Targets.Count}:{button.Id}");
'@ | Set-Content -Path (Join-Path $consumer 'Program.cs')

$previousErrorActionPreference = $ErrorActionPreference
try {
  $ErrorActionPreference = 'Continue'
  $runOutput = & dotnet run --project (Join-Path $consumer 'Consumer.csproj') --configuration Release --framework $Framework 2>&1
  $exitCode = $LASTEXITCODE
}
finally {
  $ErrorActionPreference = $previousErrorActionPreference
}
$runOutput | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) { throw "The package consumer failed on $Framework with exit code $exitCode." }
if (($runOutput -join "`n") -notmatch 'consumer-ok:route\.root:\d+:route\.root\.home-razor-button-continue') {
  throw "The consumer did not load an embedded manifest with the unannotated button target on $Framework."
}
Write-Host "PASS: packaged Blazor compiler discovered an unannotated button on $Framework (consumer at $consumer)."

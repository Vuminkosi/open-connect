[CmdletBinding()]
param(
    [string[]]$TargetFramework = @('net8.0','net9.0','net10.0'),
    [string]$PackageVersion = '1.2.1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$workRoot = Join-Path ([IO.Path]::GetTempPath()) "openconnect-published-consumers-$([guid]::NewGuid().ToString('N'))"
$reportRoot = Join-Path $repoRoot 'build/evidence/published-consumers'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null

$packageIds = @(
    'OpenConnect.Abstractions',
    'OpenConnect.Core',
    'OpenConnect.Agents',
    'OpenConnect.Client',
    'OpenConnect.Components.Contracts',
    'OpenConnect.Persistence.EFCore'
)
$extensionVersion = @{
    'net8.0' = '8.0.1'
    'net9.0' = '10.0.12'
    'net10.0' = '10.0.14'
}

try {
    foreach ($tfm in $TargetFramework) {
        if (-not $extensionVersion.ContainsKey($tfm)) { throw "Unsupported consumer TFM: $tfm" }
        $dir = Join-Path $workRoot $tfm
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $consumerPackageIds = if ($tfm -eq 'net8.0') {
            $packageIds | Where-Object { $_ -ne 'OpenConnect.Agents' }
        } else {
            $packageIds
        }
        $refs = ($consumerPackageIds | ForEach-Object { '    <PackageReference Include="{0}" Version="{1}" />' -f $_, $PackageVersion }) -join "`n"
        $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$tfm</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
$refs
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="$($extensionVersion[$tfm])" />
  </ItemGroup>
</Project>
"@
        Set-Content (Join-Path $dir 'Consumer.csproj') $project
        Set-Content (Join-Path $dir 'NuGet.Config') @"
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
        Set-Content (Join-Path $dir 'Program.cs') @'
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Core;
#if NET9_0_OR_GREATER
using OpenConnect.Agents.Templates;
#endif

var services = new ServiceCollection();
services.AddOpenConnect();
#if NET9_0_OR_GREATER
services.AddOpenConnectAgent("published-consumer-direct");
#endif
using var provider = services.BuildServiceProvider();
var registry = provider.GetRequiredService<OpenConnect.Abstractions.IAgentDefinitionRegistry>();
Console.WriteLine($"consumer-ok:{registry.GetAll().Count}");
'@
        $cache = Join-Path $dir 'packages'
        $env:NUGET_PACKAGES = $cache
        $env:NUGET_HTTP_CACHE_PATH = (Join-Path $dir 'http-cache')
        dotnet restore (Join-Path $dir 'Consumer.csproj') --configfile (Join-Path $dir 'NuGet.Config') --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
        if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed for $tfm" }
        dotnet build (Join-Path $dir 'Consumer.csproj') -c Release --no-restore -warnaserror
        if ($LASTEXITCODE -ne 0) { throw "Consumer build failed for $tfm" }
        $run = dotnet run --project (Join-Path $dir 'Consumer.csproj') -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0 -or $run -notmatch 'consumer-ok:') { throw "Consumer runtime failed for ${tfm}: $run" }
        dotnet list (Join-Path $dir 'Consumer.csproj') package --include-transitive --format json | Set-Content (Join-Path $reportRoot "$tfm-dependency-tree.json")
        [ordered]@{
            schemaVersion = 'openconnect.published-consumer.v1'
            targetFramework = $tfm
            packageVersion = $PackageVersion
            publishedMafAgent = if ($tfm -eq 'net8.0') { 'UNAVAILABLE: OpenConnect.Agents 1.2.1 dependency graph requires Microsoft.Extensions.DependencyInjection >=10.0.12' } else { 'tested' }
            source = 'https://api.nuget.org/v3/index.json'
            output = $run.Trim()
            status = 'PASS'
        } | ConvertTo-Json | Set-Content (Join-Path $reportRoot "$tfm-result.json")
        Write-Host "PASS $tfm OpenConnect published consumer $PackageVersion"
    }
}
finally {
    Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue
    if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

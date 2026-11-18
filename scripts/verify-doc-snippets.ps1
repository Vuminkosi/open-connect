[CmdletBinding()]
param([string]$PackageVersion = '1.2.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$docs = Get-ChildItem (Join-Path $root 'site/OpenConnect.Docs/Content') -Recurse -File -Filter '*.md'
$snippets = @()
foreach ($doc in $docs) {
    $text = Get-Content $doc.FullName -Raw
    foreach ($match in [regex]::Matches($text, '(?ms)```csharp\s+runnable\s*\r?\n(?<code>.*?)\r?\n```')) {
        $snippets += [pscustomobject]@{ File = $doc.FullName; Code = $match.Groups['code'].Value }
    }
}
if (-not $snippets) { throw 'No runnable C# snippets were marked with ```csharp runnable.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) "openconnect-snippets-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OpenConnect.Core" Version="$PackageVersion" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.14" />
  </ItemGroup>
</Project>
"@ -replace '<ImplicitUsings>enable>', '<ImplicitUsings>enable</ImplicitUsings>'
    Set-Content (Join-Path $temp 'Snippet.csproj') $project
    Set-Content (Join-Path $temp 'NuGet.Config') @'
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources></configuration>
'@
    $code = ($snippets | Select-Object -First 1).Code
    Set-Content (Join-Path $temp 'Program.cs') $code
    $env:NUGET_PACKAGES = Join-Path $temp 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $temp 'http-cache'
    dotnet restore (Join-Path $temp 'Snippet.csproj') --configfile (Join-Path $temp 'NuGet.Config') --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Runnable snippet restore failed.' }
    dotnet build (Join-Path $temp 'Snippet.csproj') -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Runnable snippet compilation failed.' }
    Write-Host "PASS: compiled $($snippets.Count) runnable C# documentation snippet(s) against OpenConnect.Core $PackageVersion."
}
finally {
    Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

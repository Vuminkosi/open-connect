#Requires -Version 5.1
<#
.SYNOPSIS
  E2E case: SqlServer sample. INTENTIONALLY NOT EXECUTED by this task: no SQL
  Server fixture exists and none is being provisioned. Records NOT VERIFIED
  with the exact gate and exits 3. No `dotnet` invocation, no connection
  attempt, no container/image pull.
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

Write-Host 'E2E-GATE SqlServer NOT VERIFIED: no SQL Server fixture exists and none is being provisioned in this task (exact gate). No execution attempted.'
Write-Host 'E2E-CLEANUP SqlServer db=not-applicable (nothing created, nothing touched)'
Write-E2ECaseResult -Case 'SqlServer' -Status 'NOT VERIFIED' -ExitCode 3 -Detail 'SKIP: no fixture, none provisioned'
exit 3


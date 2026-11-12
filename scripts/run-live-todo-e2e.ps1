[CmdletBinding()]
param(
    [string]$GatewayUrl = 'http://127.0.0.1:8080',
    [string]$TodoUrl = 'http://127.0.0.1:5000',
    [int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$tenant = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
$principal = '01ARZ3NDEKTSV4RRFFQ69G5FB0'
$otherTenant = '01ARZ3NDEKTSV4RRFFQ69G5FB1'
$internalToken = $env:TODO_INTERNAL_TOKEN
if ([string]::IsNullOrWhiteSpace($internalToken)) { $internalToken = 'e2e-todo-internal-do-not-use-in-prod' }

function Get-Json($uri, $headers) {
    Invoke-WebRequest -UseBasicParsing -Uri $uri -Headers $headers -TimeoutSec 15
}

$auth = @{
    'X-OpenConnect-Test-Tenant' = $tenant
    'X-OpenConnect-Test-Principal' = $principal
}
$internal = @{
    Authorization = "Bearer $internalToken"
    'X-OpenConnect-Tenant' = $tenant
}

$health = Get-Json "$GatewayUrl/openconnect/v1/health" @{}
if ($health.StatusCode -ne 200) { throw 'Gateway health failed.' }
$unauthorized = 0
try { Invoke-WebRequest -UseBasicParsing -Uri "$GatewayUrl/openconnect/v1/sessions" -Method Post -Headers @{} -ContentType 'application/json' -Body '{}' -TimeoutSec 15 | Out-Null } catch { $unauthorized = $_.Exception.Response.StatusCode.value__ }
if ($unauthorized -ne 401) { throw "Expected unauthenticated session request to return 401, got $unauthorized." }

$session = Invoke-WebRequest -UseBasicParsing -Uri "$GatewayUrl/openconnect/v1/sessions" -Method Post -Headers $auth -ContentType 'application/json' -Body '{}' -TimeoutSec 15
if ($session.StatusCode -ne 201) { throw 'Authenticated session creation failed.' }

$taskHeaders = $auth.Clone()
$taskHeaders['Idempotency-Key'] = [guid]::NewGuid().ToString('N')
$taskBody = '{"clientTaskId":"live-todo-005d-script","message":"Create a Todo to inspect the weighbridge calibration tomorrow, then list my outstanding Todos."}'
$accepted = Invoke-WebRequest -UseBasicParsing -Uri "$GatewayUrl/openconnect/v1/tasks" -Method Post -Headers $taskHeaders -ContentType 'application/json' -Body $taskBody -TimeoutSec 15
if ($accepted.StatusCode -ne 202) { throw "Task was not accepted: $($accepted.StatusCode)." }
$taskId = ($accepted.Content | ConvertFrom-Json).id

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$terminal = $null
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 2
    $status = (Get-Json "$GatewayUrl/openconnect/v1/tasks/$taskId" $auth).Content | ConvertFrom-Json
    if ($status.state -in @('completed','failed','cancelled')) { $terminal = $status; break }
}
if ($null -eq $terminal) { throw "Task $taskId did not reach a terminal state." }
if ($terminal.state -ne 'completed') { throw "Live Todo task ended in '$($terminal.state)'." }

$items = (Get-Json "$TodoUrl/internal/openconnect/todos/" $internal).Content | ConvertFrom-Json
$matches = @($items | Where-Object { $_.title -match 'weighbridge' })
if ($matches.Count -lt 1) { throw 'Live Meta task completed without a verified weighbridge Todo mutation.' }

$crossTenant = 0
try {
    Get-Json "$TodoUrl/internal/openconnect/todos/" @{ Authorization = "Bearer $internalToken"; 'X-OpenConnect-Tenant' = $otherTenant } | Out-Null
} catch { $crossTenant = $_.Exception.Response.StatusCode.value__ }
if ($crossTenant -ne 401) { throw "Expected internal cross-tenant Todo request to return 401, got $crossTenant." }

Write-Host "PASS: live Meta Todo task completed and verified in the existing store; task=$taskId; matches=$($matches.Count); unauthorized=$unauthorized; crossTenant=$crossTenant"

[CmdletBinding()]
param(
    [string] $CredentialFile = (Join-Path $env:LOCALAPPDATA 'OpenConnect/cloud-local/identity.credentials'),
    [switch] $Create,
    [switch] $ShowLogin
)

$ErrorActionPreference = 'Stop'
if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'This local credential helper requires Windows user-scoped data protection.'
}

function New-LocalSecret {
    $bytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes); return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
    finally { $generator.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
}

if ($Create) {
    $directory = Split-Path -Parent $CredentialFile
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $credentials = [ordered]@{
        OC_LOCAL_OIDC_ADMIN_USERNAME = 'local-admin'
        OC_LOCAL_OIDC_ADMIN_PASSWORD = New-LocalSecret
        OC_LOCAL_OIDC_USERNAME = 'local-user'
        OC_LOCAL_OIDC_PASSWORD = New-LocalSecret
        OC_LOCAL_OIDC_CLIENT_SECRET = New-LocalSecret
    }
    $json = $credentials | ConvertTo-Json -Compress
    $secure = ConvertTo-SecureString $json -AsPlainText -Force
    $encrypted = ConvertFrom-SecureString $secure
    $stream = [System.IO.File]::Open($CredentialFile, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.Encoding]::ASCII)
        try { $writer.Write($encrypted) } finally { $writer.Dispose() }
    }
    finally { $stream.Dispose(); $secure.Dispose(); $json = $null; $credentials = $null }
}

if (!(Test-Path -LiteralPath $CredentialFile -PathType Leaf)) {
    throw 'Local identity credentials do not exist. Run this script with -Create first.'
}
$encrypted = [System.IO.File]::ReadAllText($CredentialFile)
$secure = ConvertTo-SecureString $encrypted
$pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $json = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $credentials = $json | ConvertFrom-Json
    foreach ($name in @('OC_LOCAL_OIDC_ADMIN_USERNAME', 'OC_LOCAL_OIDC_ADMIN_PASSWORD', 'OC_LOCAL_OIDC_USERNAME', 'OC_LOCAL_OIDC_PASSWORD', 'OC_LOCAL_OIDC_CLIENT_SECRET')) {
        $value = $credentials.$name
        if ([string]::IsNullOrWhiteSpace($value)) { throw 'Local identity credentials are incomplete.' }
        [System.Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    if ($ShowLogin) {
        Write-Host "Local identity username: $($credentials.OC_LOCAL_OIDC_USERNAME)"
        Write-Host "Local identity password: $($credentials.OC_LOCAL_OIDC_PASSWORD)"
    }
    else { Write-Host 'Local identity credentials loaded into this process.' }
}
finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    $secure.Dispose()
    $json = $null
    $credentials = $null
}

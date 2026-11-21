[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This local certificate helper requires Windows directory access controls.'
}
$openssl = (Get-Command openssl.exe -ErrorAction Stop).Source
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) {
    throw 'The output directory already exists; existing certificate material will not be replaced.'
}

# Local development certificates are isolated from other Windows users and are
# never installed as trusted roots. Each service receives only its own key.
$security = New-Object Security.AccessControl.DirectorySecurity
$security.SetAccessRuleProtection($true, $false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$rule = New-Object Security.AccessControl.FileSystemAccessRule(
    $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$security.AddAccessRule($rule)
[IO.Directory]::CreateDirectory($target, $security) | Out-Null
$staging = Join-Path $target 'issuer'
[IO.Directory]::CreateDirectory($staging) | Out-Null

function New-LocalCertificate([string] $Name, [string] $Usage) {
    $arguments = @('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-quiet', '-days', '7',
        '-subj', "/CN=$Name", '-keyout', "$Name.key", '-out', "$Name.crt",
        '-addext', 'basicConstraints=critical,CA:TRUE',
        '-addext', 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign',
        '-addext', "extendedKeyUsage=$Usage")
    if ($Usage -eq 'serverAuth') {
        $arguments += @('-addext', 'subjectAltName=DNS:localhost,DNS:cloud-voice-authority,IP:127.0.0.1')
    }
    $stderr = Join-Path $staging "$Name.stderr"
    $stdout = Join-Path $staging "$Name.stdout"
    $process = Start-Process -FilePath $openssl -ArgumentList $arguments -WorkingDirectory $staging `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Unable to generate the $Name certificate; inspect the output directory before retrying." }
    Remove-Item -LiteralPath $stdout, $stderr
}

foreach ($name in @('authority', 'voice', 'screen')) { New-LocalCertificate $name 'serverAuth' }
foreach ($name in @('gateway', 'worker-client')) { New-LocalCertificate $name 'clientAuth' }

foreach ($name in @('authority', 'gateway', 'voice', 'screen')) {
    [IO.Directory]::CreateDirectory((Join-Path $target $name)) | Out-Null
}
foreach ($entry in @(
    @('authority', @('authority.crt', 'authority.key')),
    @('gateway', @('gateway.crt', 'gateway.key', 'authority.crt', 'voice.crt', 'screen.crt')),
    @('voice', @('voice.crt', 'voice.key', 'worker-client.crt', 'worker-client.key', 'authority.crt', 'gateway.crt')),
    @('screen', @('screen.crt', 'screen.key', 'gateway.crt'))
)) {
    foreach ($file in $entry[1]) {
        Copy-Item -LiteralPath (Join-Path $staging $file) -Destination (Join-Path $target $entry[0])
    }
}

foreach ($name in @('authority', 'voice', 'screen', 'gateway', 'worker-client')) {
    Remove-Item -LiteralPath (Join-Path $staging "$name.crt"), (Join-Path $staging "$name.key")
}
Remove-Item -LiteralPath $staging
Write-Host 'Local voice certificates created. They expire after seven days and are not production credentials.'

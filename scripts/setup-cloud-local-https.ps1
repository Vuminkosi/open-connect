[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CertificateDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$certificate = Join-Path $CertificateDirectory 'localhost.pem'
$privateKey = Join-Path $CertificateDirectory 'localhost.key'
if (!(Test-Path -LiteralPath $certificate -PathType Leaf) -or !(Test-Path -LiteralPath $privateKey -PathType Leaf)) {
    throw 'The certificate directory must contain localhost.pem and localhost.key.'
}
$certificate = (Resolve-Path -LiteralPath $certificate).Path
$privateKey = (Resolve-Path -LiteralPath $privateKey).Path
$helper = 'oc-docs-tls-init-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$compose = @('compose', '-f', (Join-Path $repoRoot 'docker-compose-all.yml'), '-f', (Join-Path $repoRoot 'docker-compose-cloud-local.yml'))
$locked = $false
try {
    & docker @compose run -d --no-deps --name $helper documentation-site-tls-init | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to start the isolated TLS initializer. Build documentation-site first.' }
    & docker exec $helper mkdir /target/.openconnect-tls-provisioning
    if ($LASTEXITCODE -ne 0) { throw 'TLS initialization is already in progress.' }
    $locked = $true
    & docker exec $helper sh -c 'test ! -e /target/localhost.pem && test ! -e /target/localhost.key'
    if ($LASTEXITCODE -ne 0) { throw 'TLS volume already contains certificate material; it was not replaced.' }
    & docker cp $certificate "${helper}:/tmp/localhost.pem"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to copy the certificate into the initializer.' }
    & docker cp $privateKey "${helper}:/tmp/localhost.key"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to copy the private key into the initializer.' }
    & docker exec $helper sh -c 'install -d -m 0700 -o app -g app /target && install -m 0400 -o app -g app /tmp/localhost.pem /target/localhost.pem && install -m 0400 -o app -g app /tmp/localhost.key /target/localhost.key'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize the TLS volume; inspect it before retrying.' }
    Write-Host 'Local TLS certificate installed into the site-owned volume.'
}
finally {
    if ($locked) { & docker exec $helper rmdir /target/.openconnect-tls-provisioning 2>$null | Out-Null }
    & docker rm -f $helper 2>$null | Out-Null
}

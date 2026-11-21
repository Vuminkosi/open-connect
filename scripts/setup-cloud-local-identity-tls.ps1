[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $CertificateDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$certificate = (Resolve-Path -LiteralPath (Join-Path $CertificateDirectory 'localhost.pem')).Path
$privateKey = (Resolve-Path -LiteralPath (Join-Path $CertificateDirectory 'localhost.key')).Path
$helper = 'oc-local-identity-init-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
$compose = @('compose', '-f', (Join-Path $root 'docker-compose-all.yml'), '-f', (Join-Path $root 'docker-compose-cloud-local.yml'), '-f', (Join-Path $root 'docker-compose-cloud-local-identity.yml'))
$locked = $false
try {
    & docker @compose run -d --no-deps --name $helper local-identity-tls-init | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to start the identity TLS initializer.' }
    & docker exec $helper mkdir /target/tls/.initializing
    if ($LASTEXITCODE -ne 0) { throw 'Identity TLS initialization is already in progress.' }
    $locked = $true
    & docker exec $helper sh -c 'test ! -e /target/tls/cert.pem && test ! -e /target/tls/key.pem && test ! -e /target/ca/ca-bundle.crt'
    if ($LASTEXITCODE -ne 0) { throw 'Identity TLS material already exists; it was not replaced.' }
    & docker cp $certificate "${helper}:/tmp/cert.pem"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to copy the certificate.' }
    & docker cp $privateKey "${helper}:/tmp/key.pem"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to copy the private key.' }
    & docker exec $helper sh -c 'install -d -m 0755 -o 1000 -g 1000 /target/tls && install -m 0444 -o 1000 -g 1000 /tmp/cert.pem /target/tls/cert.pem && install -m 0400 -o 1000 -g 1000 /tmp/key.pem /target/tls/key.pem && cat /etc/ssl/certs/ca-certificates.crt /tmp/cert.pem > /target/ca/ca-bundle.crt && chmod 0444 /target/ca/ca-bundle.crt'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize identity TLS; inspect the volumes before retrying.' }
    Write-Host 'Local identity certificate and trust bundle installed.'
}
finally {
    if ($locked) { & docker exec $helper rmdir /target/tls/.initializing 2>$null | Out-Null }
    & docker rm -f $helper 2>$null | Out-Null
}

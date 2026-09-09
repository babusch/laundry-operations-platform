$ErrorActionPreference = 'Stop'

# Normal OS certificate verification is deliberate: never bypass TLS validation.
$expectedIssuer = 'https://localhost:8443/realms/master'
$metadata = Invoke-RestMethod "$expectedIssuer/.well-known/openid-configuration" -TimeoutSec 10
if ($metadata.issuer -cne $expectedIssuer) { throw 'Unexpected identity issuer.' }
foreach ($endpoint in @($metadata.token_endpoint, $metadata.authorization_endpoint, $metadata.jwks_uri)) {
    $uri = [uri]$endpoint
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'localhost' -or $uri.Port -ne 8443) {
        throw 'Identity discovery advertised an unexpected endpoint.'
    }
}
$keys = Invoke-RestMethod $metadata.jwks_uri -TimeoutSec 10
if (@($keys.keys).Count -eq 0) { throw 'No public signing keys published.' }
Write-Host 'Identity discovery and public signing keys verified over trusted HTTPS.'
Write-Host 'This checks service setup only, not gateway authentication or authorization.'

# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Spike S2: checks whether a certificate works for a Microsoft Graph app-only token (client assertion).
.EXAMPLE
  ./Test-GraphClientAssertion.ps1 -TenantId <guid> -ClientId <guid> -PfxBase64Path ./out/ecdsa.pfx.b64
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ClientId,
    [Parameter(Mandatory)] [string] $PfxBase64Path
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SpikeCertificate.ps1')

$cert = Import-SpikeCertificate $PfxBase64Path
$tokenUrl = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

function ConvertTo-Base64Url([byte[]] $bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$alg = if ($rsa) { 'PS256' } else { 'ES256' }
$header = @{
    alg        = $alg
    typ        = 'JWT'
    'x5t#S256' = ConvertTo-Base64Url ($cert.GetCertHash([System.Security.Cryptography.HashAlgorithmName]::SHA256))
} | ConvertTo-Json -Compress
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$claims = @{
    aud = $tokenUrl; iss = $ClientId; sub = $ClientId; jti = [guid]::NewGuid().ToString()
    nbf = $now; iat = $now; exp = $now + 600
} | ConvertTo-Json -Compress

$unsigned = (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))) + '.' +
    (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($claims)))
$data = [Text.Encoding]::ASCII.GetBytes($unsigned)
$signature = if ($rsa) {
    $rsa.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
} else {
    $ecdsa = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert)
    $ecdsa.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
$assertion = "$unsigned.$(ConvertTo-Base64Url $signature)"

Write-Host "Requesting a Graph token with a $alg client assertion…"
try {
    $token = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body @{
        client_id             = $ClientId
        scope                 = 'https://graph.microsoft.com/.default'
        grant_type            = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion      = $assertion
    }
} catch {
    $detail = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    Write-Host "✕ Couldn't get a Graph token with a $alg client assertion. $detail"
    exit 1
}
$org = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/organization?$select=id,displayName' `
    -Headers @{ Authorization = "Bearer $($token.access_token)" }
Write-Host "✓ $alg works for Graph. GET /organization returned $($org.value[0].displayName)."

# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Spike S2: creates one RSA 3072 and one ECDSA P-256 self-signed certificate for the test app registration.
.DESCRIPTION
  Writes rsa.cer and ecdsa.cer (public parts, upload both to the app registration) and rsa.pfx.b64 and
  ecdsa.pfx.b64 (private keys, base64 PFX with no password) into ./out. Delete ./out when the spike is done.
#>
param([string] $OutDir = (Join-Path $PSScriptRoot 'out'))

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Certificate([string] $name, [System.Security.Cryptography.X509Certificates.CertificateRequest] $request) {
    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $cert = $request.CreateSelfSigned($notBefore, $notBefore.AddMonths(12))
    [IO.File]::WriteAllBytes((Join-Path $OutDir "$name.cer"), $cert.Export('Cert'))
    [IO.File]::WriteAllText((Join-Path $OutDir "$name.pfx.b64"), [Convert]::ToBase64String($cert.Export('Pfx')))
    Write-Host "✓ $name certificate created. Thumbprint $($cert.Thumbprint), expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
}

$subject = 'CN=Rollcall spike'
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
Save-Certificate 'rsa' ([System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
Save-Certificate 'ecdsa' ([System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $subject, $ecdsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256))

Write-Host "Upload $OutDir/rsa.cer and $OutDir/ecdsa.cer to the test app registration (Certificates & secrets)."

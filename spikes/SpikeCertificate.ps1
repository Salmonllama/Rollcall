# SPDX-License-Identifier: GPL-3.0-or-later
# Loads a certificate with its private key from base64 PFX bytes, in memory only (EphemeralKeySet: no key file on disk).

function Import-SpikeCertificate([string] $PfxBase64Path) {
    $bytes = [Convert]::FromBase64String((Get-Content -Raw -Path $PfxBase64Path).Trim())
    [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12(
        $bytes, $null, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
}

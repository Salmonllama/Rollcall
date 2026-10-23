# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Spike S1: connects to Exchange Online app-only with an in-memory certificate on Linux, converts a test
  mailbox to shared and back, and reports session start time and memory.
.DESCRIPTION
  Run only against a test tenant. The test mailbox is converted to shared and then back to a user mailbox.
.EXAMPLE
  ./Test-ExchangeOnline.ps1 -ClientId <guid> -Organization contoso.onmicrosoft.com -PfxBase64Path ./out/rsa.pfx.b64 -Mailbox pilot@contoso.onmicrosoft.com
#>
param(
    [Parameter(Mandatory)] [string] $ClientId,
    [Parameter(Mandatory)] [string] $Organization,
    [Parameter(Mandatory)] [string] $PfxBase64Path,
    [Parameter(Mandatory)] [string] $Mailbox,
    # Defaults to the newest version on the PowerShell Gallery; record the version used in ADR 0002.
    [string] $ModuleVersion
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SpikeCertificate.ps1')

function Get-WorkingSetMB { [math]::Round([Diagnostics.Process]::GetCurrentProcess().WorkingSet64 / 1MB) }
if (-not $ModuleVersion) {
    $ModuleVersion = (Find-Module -Name ExchangeOnlineManagement -Repository PSGallery).Version.ToString()
}
$results = [ordered]@{ ModuleVersion = $ModuleVersion; MemoryBeforeMB = Get-WorkingSetMB }

# Risk R1: fetch the module from the PowerShell Gallery at runtime instead of bundling it in an image.
$moduleDir = Join-Path $PSScriptRoot 'out/modules'
if (-not (Test-Path (Join-Path $moduleDir 'ExchangeOnlineManagement'))) {
    Write-Host "Downloading ExchangeOnlineManagement $ModuleVersion from the PowerShell Gallery…"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Save-Module -Name ExchangeOnlineManagement -RequiredVersion $ModuleVersion -Path $moduleDir -Repository PSGallery
    $results.ModuleDownloadSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
}
$psd1 = Get-ChildItem -Recurse -Path $moduleDir -Filter ExchangeOnlineManagement.psd1 | Select-Object -First 1
# One hash over every file in the module, to pin and verify the download in the worker later.
$fileHashes = (Get-ChildItem -Recurse -Path $moduleDir -File | Sort-Object FullName |
    ForEach-Object { (Get-FileHash -Algorithm SHA256 $_.FullName).Hash }) -join ''
$results.ModuleTreeSha256 = (Get-FileHash -Algorithm SHA256 -InputStream (
    [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($fileHashes)))).Hash
Import-Module $psd1.FullName

$cert = Import-SpikeCertificate $PfxBase64Path
$results.CertificateAlgorithm = $cert.PublicKey.Oid.FriendlyName

$watch = [Diagnostics.Stopwatch]::StartNew()
Connect-ExchangeOnline -Certificate $cert -AppId $ClientId -Organization $Organization -ShowBanner:$false
$results.SessionStartSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
$results.MemoryAfterConnectMB = Get-WorkingSetMB

try {
    $before = Get-Mailbox -Identity $Mailbox
    $results.MailboxTypeBefore = "$($before.RecipientTypeDetails)"

    $watch.Restart()
    Set-Mailbox -Identity $Mailbox -Type Shared
    do {
        Start-Sleep -Seconds 5
        $type = "$((Get-Mailbox -Identity $Mailbox).RecipientTypeDetails)"
    } until ($type -eq 'SharedMailbox' -or $watch.Elapsed.TotalMinutes -gt 10)
    $results.ConvertedTo = $type
    $results.ConvertSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)

    Set-Mailbox -Identity $Mailbox -Type Regular
    $results.RestoredToRegular = $true
    $results.MemoryAfterCommandsMB = Get-WorkingSetMB
} finally {
    Disconnect-ExchangeOnline -Confirm:$false
}

$results.GetEnumerator() | ForEach-Object { '{0,-24} {1}' -f $_.Key, $_.Value }
if ($results.ConvertedTo -ne 'SharedMailbox') {
    Write-Host '✕ The mailbox did not become shared within 10 minutes.'
    exit 1
}
Write-Host '✓ Exchange Online app-only with an in-memory certificate works on Linux. Copy the numbers above into ADR 0002.'

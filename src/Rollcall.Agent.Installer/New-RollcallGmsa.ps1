# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Creates the Rollcall gMSA and delegates least-privilege rights on allowed OUs only.
.EXAMPLE
  .\New-RollcallGmsa.ps1 -AccountName rollcall-gmsa -DnsHostName rollcall-gmsa.contoso.local -AgentHostComputer 'ADAGENT01$' -AllowedOus @('OU=Staff,DC=contoso,DC=local','OU=Offboarding,DC=contoso,DC=local')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AccountName,
    [Parameter(Mandatory)] [string] $DnsHostName,
    [Parameter(Mandatory)] [string] $AgentHostComputer,
    [Parameter(Mandatory)] [string[]] $AllowedOus,
    [switch] $GrantAdSyncOperators
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory

if (-not (Get-ADServiceAccount -Filter "Name -eq '$AccountName'" -ErrorAction SilentlyContinue)) {
    New-ADServiceAccount -Name $AccountName -DNSHostName $DnsHostName -PrincipalsAllowedToRetrieveManagedPassword $AgentHostComputer
    Write-Host "✓ Created gMSA $AccountName$."
} else {
    Write-Host "✓ gMSA $AccountName$ already exists."
}

$domainNetBios = (Get-ADDomain).NetBIOSName
$identity = "$domainNetBios\$AccountName$"

foreach ($ou in $AllowedOus) {
    Write-Host "Delegating user and group management on $ou to $identity"
    # Create/delete/modify users & groups and reset passwords within this OU only.
    dsacls $ou /I:T /G "${identity}:CCDC;user" | Out-Null
    dsacls $ou /I:T /G "${identity}:CCDC;group" | Out-Null
    dsacls $ou /I:S /G "${identity}:RPWP;;user" | Out-Null
    dsacls $ou /I:S /G "${identity}:RPWP;;group" | Out-Null
    dsacls $ou /I:S /G "${identity}:CA;Reset Password;user" | Out-Null
    Write-Host "✓ Delegated rights on $ou."
}

if ($GrantAdSyncOperators) {
    Add-LocalGroupMember -Group 'ADSyncOperators' -Member $identity -ErrorAction SilentlyContinue
    Write-Host "✓ Added $identity to local ADSyncOperators (for Entra Connect delta sync)."
}

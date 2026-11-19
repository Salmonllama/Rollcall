# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Installs the Rollcall AD agent as a Windows Service running under a Group Managed Service Account (gMSA).
.EXAMPLE
  .\install-agent.ps1 -PortalUrl https://rollcall.contoso.com -Token <one-time-token> -ServiceAccount 'CONTOSO\rollcall-gmsa$'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PortalUrl,
    [Parameter(Mandatory)] [string] $Token,
    [Parameter(Mandatory)] [string] $ServiceAccount,
    [string] $InstallDir = 'C:\Program Files\Rollcall\Agent',
    [string] $ServiceName = 'RollcallAdAgent',
    [string] $AgentBinaryPath
)

$ErrorActionPreference = 'Stop'

Write-Host "Checking administrator rights"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run install-agent.ps1 from an elevated PowerShell session."
}
Write-Host "✓ Running as Administrator."

Write-Host "Checking Group Managed Service Account $ServiceAccount"
$sam = $ServiceAccount.Split('\')[-1].TrimEnd('$')
if (-not (Test-ADServiceAccount -Identity $sam)) {
    throw "Test-ADServiceAccount failed for $ServiceAccount. Confirm this server is in the gMSA PrincipalsAllowedToRetrieveManagedPassword group and reboot if just added."
}
Write-Host "✓ gMSA $ServiceAccount is usable on this host."

Write-Host "Preparing install directory $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$stateDir = Join-Path $InstallDir 'state'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

if ($AgentBinaryPath -and (Test-Path $AgentBinaryPath)) {
    Copy-Item -Path (Join-Path $AgentBinaryPath '*') -Destination $InstallDir -Recurse -Force
}

$configPath = Join-Path $InstallDir 'appsettings.Production.json'
@{
    Agent = @{
        PortalUrl = $PortalUrl.TrimEnd('/')
        EnrollmentToken = $Token
        StateDirectory = $stateDir
    }
} | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding UTF8

# Restrict state directory and config file to SYSTEM, Administrators, and the gMSA only.
$acl = Get-Acl $stateDir
$acl.SetAccessRuleProtection($true, $false)
foreach ($id in @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', $ServiceAccount)) {
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $stateDir -AclObject $acl
Set-Acl -Path $configPath -AclObject $acl
Write-Host "✓ Configuration written and restricted to $ServiceAccount."

$exePath = Join-Path $InstallDir 'Rollcall.Agent.exe'
Write-Host "Registering Windows Service $ServiceName"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Note: gMSA password is empty string; Windows manages it automatically.
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto obj= "$ServiceAccount" | Out-Null
sc.exe description $ServiceName "Rollcall outbound-only Active Directory agent." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
if ($svc.Status -ne 'Running') {
    throw "Service $ServiceName is $($svc.Status). Check the Windows Event Log (Application) for details."
}
Write-Host "✓ $ServiceName is running under $ServiceAccount."
Write-Host "Open $PortalUrl/admin/agents to confirm the agent shows as Connected."

<#
.SYNOPSIS
    Removes Stratum from the Windows 11 context menu and (with -RemoveCertificate, from an
    elevated prompt) the development certificate that Register-ContextMenu.ps1 trusted.
#>
param([switch]$RemoveCertificate)

$ErrorActionPreference = 'Stop'
Get-AppxPackage -Name 'dor2500.Stratum' | Remove-AppxPackage
Write-Host 'Removed the Stratum package identity.'

if ($RemoveCertificate) {
    Get-ChildItem Cert:\LocalMachine\TrustedPeople, Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=Stratum Dev' | Remove-Item
    Write-Host 'Removed the development certificate.'
}

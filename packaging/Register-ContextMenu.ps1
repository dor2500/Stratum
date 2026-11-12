<#
.SYNOPSIS
    Adds Stratum to the Windows 11 (modern) File Explorer context menu.

.DESCRIPTION
    Windows 11 only shows top-level context menu entries from apps that have a package identity.
    This script gives the built Stratum folder that identity by registering a small "sparse"
    package that points at it:

      1. creates a self-signed code-signing certificate (once) and trusts it on this PC,
      2. packs and signs packaging\sparse into an .msix,
      3. registers the package with the Stratum folder as its external location.

    Step 1 changes this computer's trusted certificates, so the script must be run from an
    elevated (Administrator) PowerShell, and you should only run it on your own machine.
    For public distribution, sign with a real code-signing certificate instead.

.PARAMETER InstallDir
    The folder that contains Stratum.exe and Stratum.ShellExt.dll (the output of build.ps1).

.EXAMPLE
    .\build.ps1
    .\packaging\Register-ContextMenu.ps1
#>
param([string]$InstallDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\Stratum'))

$ErrorActionPreference = 'Stop'
$publisher = 'CN=Stratum Dev'
$packageName = 'dor2500.Stratum'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell window.'
}

$InstallDir = (Resolve-Path $InstallDir).Path
foreach ($required in 'Stratum.exe', 'Stratum.ShellExt.dll') {
    if (-not (Test-Path (Join-Path $InstallDir $required))) { throw "$required was not found in $InstallDir. Run build.ps1 first." }
}

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $kit) { throw 'The Windows SDK (makeappx.exe, signtool.exe) is required.' }
$makeappx = Join-Path $kit.FullName 'x64\makeappx.exe'
$signtool = Join-Path $kit.FullName 'x64\signtool.exe'

# 1. Certificate
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'Stratum development certificate' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "Created certificate $($cert.Thumbprint)"
}
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    $cer = Join-Path $env:TEMP 'stratum-dev.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Remove-Item $cer
    Write-Host 'Trusted the certificate on this PC (LocalMachine\TrustedPeople).'
}

# 2. Pack and sign
$outDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $outDir | Out-Null
$msix = Join-Path $outDir 'Stratum.Identity.msix'
& $makeappx pack /o /nv /d (Join-Path $PSScriptRoot 'sparse') /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }

# 3. Register
Get-AppxPackage -Name $packageName | Remove-AppxPackage
Add-AppxPackage -Path $msix -ExternalLocation $InstallDir
Write-Host ''
Write-Host 'Done. Right-click a file in Explorer: "Stratum" now appears in the Windows 11 menu.'
Write-Host 'If it does not show up yet, restart Explorer or sign out and back in.'

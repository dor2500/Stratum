<#
.SYNOPSIS
    Builds everything into .\artifacts\Stratum: the app (self-contained, x64), the 7-Zip engine,
    and the Explorer shell extension. Run the tests first with -Test.
#>
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts\Stratum'

if ($Test) {
    dotnet test (Join-Path $root 'tests\Stratum.Core.Tests') -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src\Stratum.App') -c Release -p:Platform=x64 -r win-x64 --self-contained -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'The app failed to build.' }

& (Join-Path $root 'src\Stratum.ShellExt\build.ps1') -OutDir (Join-Path $root 'src\Stratum.ShellExt\build')
Copy-Item (Join-Path $root 'src\Stratum.ShellExt\build\Stratum.ShellExt.dll') $out -Force
Copy-Item (Join-Path $root 'third_party\7zip\License.txt') (Join-Path $out '7-Zip-License.txt') -Force

Write-Host ""
Write-Host "Stratum is ready in $out"
Write-Host "Run it:  $out\Stratum.exe"

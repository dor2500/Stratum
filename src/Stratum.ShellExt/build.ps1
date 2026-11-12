<#
.SYNOPSIS
    Builds Stratum.ShellExt.dll (x64) with the MSVC toolchain from Visual Studio or Build Tools.
.PARAMETER OutDir
    Where the DLL is written. Defaults to .\build.
#>
param([string]$OutDir = (Join-Path $PSScriptRoot 'build'))

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio (or Build Tools) with the C++ workload is required.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The MSVC C++ build tools were not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

New-Item -ItemType Directory -Force $OutDir | Out-Null
$obj = Join-Path $OutDir 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

$source = Join-Path $PSScriptRoot 'ShellExt.cpp'
$def = Join-Path $PSScriptRoot 'ShellExt.def'
$dll = Join-Path $OutDir 'Stratum.ShellExt.dll'

$compile = "cl /nologo /std:c++17 /EHsc /O2 /MT /W3 /DUNICODE /D_UNICODE /DNOMINMAX /LD `"$source`" /Fo`"$obj\\`" /Fe`"$dll`" " +
           "/link /DEF:`"$def`" /IMPLIB:`"$obj\Stratum.ShellExt.lib`" shlwapi.lib shell32.lib ole32.lib runtimeobject.lib"
cmd /c "`"$vcvars`" >nul && $compile"
if ($LASTEXITCODE -ne 0) { throw "The shell extension failed to build (exit code $LASTEXITCODE)." }

Write-Host "Built $dll"

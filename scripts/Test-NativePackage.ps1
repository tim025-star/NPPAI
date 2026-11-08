$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Visual Studio C++ Build Tools are required for the native package probe.' }
$developerCommand = Join-Path $visualStudio 'Common7\Tools\VsDevCmd.bat'
$source = Join-Path $root 'tests\NativePackageProbe.c'
$probe = Join-Path $root 'artifacts\NativePackageProbe.exe'
$objectFile = Join-Path $root 'artifacts\NativePackageProbe.obj'
& cmd.exe /d /c "call `"$developerCommand`" -arch=x64 -host_arch=x64 >nul && cl /nologo /W4 /MT `"$source`" /Fe:`"$probe`" /Fo:`"$objectFile`""
if ($LASTEXITCODE -ne 0) { throw 'Native package probe compilation failed.' }
& $probe (Join-Path $root 'artifacts\NPPAI')
if ($LASTEXITCODE -ne 0) { throw "Native package probe failed: $LASTEXITCODE" }

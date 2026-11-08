$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$localNpp = Join-Path $root '.local\Notepad++'
$exe = Join-Path $localNpp 'notepad++.exe'
if (Get-Process notepad++ -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) {
    Start-Process -FilePath $exe -WindowStyle Normal
    return
}
& "$PSScriptRoot\Build.ps1"
New-Item -ItemType Directory -Force "$localNpp\plugins\NPPAI" | Out-Null
if (!(Test-Path $exe)) {
    $installed = Join-Path $env:ProgramFiles 'Notepad++\notepad++.exe'
    if (!(Test-Path $installed)) { throw 'Install x64 Notepad++ first.' }
    Copy-Item $installed $exe
    $lexer = Join-Path $env:ProgramFiles 'Notepad++\SciLexer.dll'
    if (Test-Path $lexer) { Copy-Item $lexer $localNpp }
    Set-Content "$localNpp\doLocalConf.xml" ''
}
Copy-Item (Join-Path $env:ProgramFiles 'Notepad++\*.model.xml') $localNpp -Force
Copy-Item "$root\artifacts\NPPAI\*" "$localNpp\plugins\NPPAI" -Recurse -Force
Start-Process -FilePath $exe -ArgumentList '-multiInst -nosession' -WindowStyle Normal

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$localNpp = Join-Path $root '.local\Notepad++'
$exe = Join-Path $localNpp 'notepad++.exe'
foreach ($required in @($exe, "$localNpp\plugins\NPPAI\NPPAI.dll", "$localNpp\plugins\NPPAI\NPPAI.Managed.dll")) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing $required. Run scripts\Start-Local.ps1 first." }
}
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'Notepad++ with NPPAI.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Arguments = '-multiInst'
$shortcut.WorkingDirectory = $localNpp
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Open the local Notepad++ installation containing NPPAI.'
$shortcut.Save()
Write-Host "Created $shortcutPath"

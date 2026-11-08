#requires -Version 7.0
param([string]$NotepadDirectory = (Join-Path $env:ProgramFiles 'Notepad++'))
$ErrorActionPreference = 'Stop'
$package = if (Test-Path "$PSScriptRoot\NPPAI.Managed.dll") { $PSScriptRoot } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\NPPAI' }
$notepadExe = Join-Path $NotepadDirectory 'notepad++.exe'
if (!(Test-Path -LiteralPath $notepadExe)) { throw "Notepad++ was not found at $notepadExe. Specify -NotepadDirectory for your x64 installation." }
$image = [IO.BinaryReader]::new([IO.File]::OpenRead($notepadExe))
try {
    $image.BaseStream.Position = 0x3c
    $image.BaseStream.Position = $image.ReadInt32()
    if ($image.ReadUInt32() -ne 0x00004550 -or $image.ReadUInt16() -ne 0x8664) { throw 'This package requires x64 Notepad++.' }
} finally { $image.Dispose() }
if (Get-Process notepad++ -ErrorAction SilentlyContinue | Where-Object Path -eq $notepadExe) { throw 'Save and close this Notepad++ installation before installing NPPAI.' }
foreach ($required in @('NPPAI.dll','NPPAI.Managed.dll','hostfxr.dll','coreclr.dll','runtime\vulkan\llama-server.exe','runtime\cpu\llama-server.exe','runtime\cpu\libc++.dll','runtime\vulkan\vcruntime140.dll','models\models.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required))) { throw "Incomplete NPPAI package: $required is missing." }
}
$manifest = Get-Content "$package\models\models.json" -Raw | ConvertFrom-Json
foreach ($model in $manifest.models) {
    if ([IO.Path]::GetFileName($model.file) -ne $model.file -or !$model.file.EndsWith('.gguf')) { throw 'Invalid model manifest path.' }
    if ((Get-FileHash -LiteralPath (Join-Path "$package\models" $model.file)).Hash -ne $model.sha256) { throw "Bundled model checksum mismatch: $($model.id)" }
}
$target = Join-Path $NotepadDirectory 'plugins\NPPAI'
if ([IO.Path]::GetFullPath($target).TrimEnd('\') -eq [IO.Path]::GetFullPath($package).TrimEnd('\')) { throw 'The package is already in this installation.' }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$package\*" $target -Recurse -Force
foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($package, $file.FullName)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target $relative)).Hash) { throw "Installed file mismatch: $relative" }
}
Write-Host "Installed and verified NPPAI in $target. Open Notepad++ normally; inference starts automatically."

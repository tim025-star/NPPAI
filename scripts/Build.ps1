$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\src\NPPAI\NPPAI.csproj" -c Release -o "$root\artifacts\NPPAI" --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
Write-Host "Plugin package: $root\artifacts\NPPAI"

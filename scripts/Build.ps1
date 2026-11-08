#requires -Version 7.0
param([switch]$CodeOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\src\NPPAI\NPPAI.csproj" -c Release -o "$root\artifacts\NPPAI" --self-contained true
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$package = "$root\artifacts\NPPAI"
New-Item -ItemType Directory -Force "$package\licenses" | Out-Null
$restore = Get-Content "$root\src\NPPAI\obj\project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($dependency in $restore.project.frameworks.Values.downloadDependencies | Where-Object name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') {
    $version = $dependency.version.Trim('[', ']').Split(',')[0]
    $libraryPath = $dependency.name.ToLowerInvariant() + '/' + $version
    foreach ($folder in $restore.packageFolders.Keys) {
        $runtimePackage = Join-Path $folder $libraryPath
        if (!(Test-Path -LiteralPath $runtimePackage)) { continue }
        foreach ($notice in Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES)') {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path "$package\licenses" ($dependency.name + '-' + $version + '-' + $notice.Name)) -Force
        }
    }
}
if (!$CodeOnly) { & "$PSScriptRoot\Prepare-Assets.ps1" }
Copy-Item "$PSScriptRoot\Install.ps1" "$root\artifacts\NPPAI\Install.ps1" -Force
Write-Host "Plugin package: $root\artifacts\NPPAI"
if ($CodeOnly) { Write-Host 'Code-only build: not a complete plug-and-play distribution.' }

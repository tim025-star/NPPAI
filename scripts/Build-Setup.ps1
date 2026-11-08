#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseManifest,
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\setup"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content -LiteralPath $ReleaseManifest -Raw | ConvertFrom-Json
if ($metadata.tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid release tag.' }
$resources = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'resources'
New-Item -ItemType Directory -Force $resources | Out-Null
# Pin the existing public release's asset IDs and hashes into the executable. Runtime setup
# never trusts a mutable remote checksum manifest or downloads scripts to execute.
$release = gh api "repos/tim025-star/NPPAI/releases/tags/$($metadata.tag)" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.draft) { throw 'A published release is required.' }
foreach ($asset in $metadata.assets | Where-Object name -like '*.zip.*') {
    $remote = @($release.assets | Where-Object name -eq $asset.name)
    if ($remote.Count -ne 1 -or $remote[0].size -ne $asset.bytes -or $remote[0].digest -ne ('sha256:' + $asset.sha256)) { throw "Published asset mismatch: $($asset.name)" }
    $asset | Add-Member -NotePropertyName id -NotePropertyValue $remote[0].id -Force
}
$metadata | ConvertTo-Json -Depth 6 | Set-Content "$resources\release-assets.json" -Encoding utf8
$manifest = Get-Content "$PSScriptRoot\runtime-assets.json" -Raw | ConvertFrom-Json
$licences = [Collections.Generic.List[string]]::new()
dotnet restore "$root\src\NPPAI.Setup\NPPAI.Setup.csproj" -p:SetupResources="$resources"
if ($LASTEXITCODE -ne 0) { throw 'Setup restore failed.' }
$restore = Get-Content "$root\src\NPPAI.Setup\obj\project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
foreach ($dependency in $restore.project.frameworks.Values.downloadDependencies | Where-Object name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') {
    $version = $dependency.version.Trim('[', ']').Split(',')[0]
    foreach ($folder in $restore.packageFolders.Keys) {
        $runtimePackage = Join-Path $folder ($dependency.name.ToLowerInvariant() + '/' + $version)
        if (!(Test-Path -LiteralPath $runtimePackage)) { continue }
        foreach ($notice in Get-ChildItem -LiteralPath $runtimePackage -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES)') {
            $licences.Add("$($dependency.name) $version $($notice.Name)`r`n" + (Get-Content -LiteralPath $notice.FullName -Raw))
        }
    }
}
foreach ($model in $manifest.models) {
    $license = Join-Path $resources "$($model.file).LICENSE"
    Invoke-WebRequest "https://registry.ollama.ai/v2/library/$($model.repository)/blobs/sha256:$($model.licenseSha256)" -OutFile $license
    if ((Get-FileHash -LiteralPath $license).Hash -ne $model.licenseSha256) { throw 'Model licence checksum mismatch.' }
    $licences.Add("$($model.id)`r`n" + (Get-Content -LiteralPath $license -Raw))
}
($licences -join "`r`n`r`n") | Set-Content "$resources\Model-Licenses.txt" -Encoding utf8
dotnet publish "$root\src\NPPAI.Setup\NPPAI.Setup.csproj" -c Release --self-contained true -p:SetupResources="$resources" -p:OutputPath="$OutputDirectory\build\" -o "$OutputDirectory\publish"
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$exe = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "NPPAI-$($metadata.tag)-Setup.exe"
Copy-Item "$OutputDirectory\publish\NPPAI-Setup.exe" $exe -Force
$hash = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($exe))" | Set-Content "$exe.sha256" -Encoding ascii
Write-Host "Setup: $exe ($((Get-Item $exe).Length) bytes), SHA256 $hash"

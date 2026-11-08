#requires -Version 7.0
param([string]$PackageDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\NPPAI'),
      [string]$LlamaSourceDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\lib\ollama'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root '.local\assets'
$assets = Get-Content "$PSScriptRoot\runtime-assets.json" -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $cache, "$PackageDirectory\runtime", "$PackageDirectory\models", "$PackageDirectory\licenses" | Out-Null

function Get-VerifiedAsset([string]$Url, [string]$Hash, [string]$LocalSource = '') {
    $cachedFile = Join-Path $cache $Hash
    if (!(Test-Path -LiteralPath $cachedFile)) {
        if ($LocalSource -and (Test-Path -LiteralPath $LocalSource)) {
            Copy-Item -LiteralPath $LocalSource -Destination $cachedFile
        } else {
            Write-Host "Downloading $Url"
            & curl.exe --fail --location --retry 3 --connect-timeout 20 --speed-time 60 --speed-limit 1024 --silent --show-error --output "$cachedFile.partial" $Url
            if ($LASTEXITCODE -ne 0) { throw "Asset download failed: $Url" }
            Move-Item -LiteralPath "$cachedFile.partial" -Destination $cachedFile -Force
        }
    }
    if ((Get-FileHash -LiteralPath $cachedFile).Hash -ne $Hash) { throw "Checksum mismatch for $Url. Remove the corrupt cache file $cachedFile and retry." }
    return $cachedFile
}

foreach ($backend in @('cpu','vulkan')) {
    $destination = [IO.Path]::GetFullPath((Join-Path "$PackageDirectory\runtime" $backend))
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\') + '\'
    if (!$destination.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime destination escaped the package.' }
    # These are generated package directories. Clear incompatible binaries from earlier runtime builds.
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Force $destination | Out-Null
    $files = @($assets.runtime.files)
    if ($backend -eq 'vulkan') { $files += @($assets.runtime.vulkanFiles) }
    foreach ($file in $files) {
        $sourceFile = Join-Path $LlamaSourceDirectory $file.file
        if ((Get-FileHash -LiteralPath $sourceFile).Hash -ne $file.sha256) { throw "Compatible llama.cpp runtime checksum mismatch: $($file.file)" }
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $destination ([IO.Path]::GetFileName($file.file))) -Force
    }
}
foreach ($model in $assets.models) {
    $modelPath = Join-Path "$PackageDirectory\models" $model.file
    if (!(Test-Path -LiteralPath $modelPath) -or (Get-FileHash -LiteralPath $modelPath).Hash -ne $model.sha256) {
        $source = Join-Path $env:USERPROFILE ".ollama\models\blobs\sha256-$($model.sha256)"
        $file = Get-VerifiedAsset "https://registry.ollama.ai/v2/library/$($model.repository)/blobs/sha256:$($model.sha256)" $model.sha256 $source
        Copy-Item -LiteralPath $file -Destination $modelPath -Force
        if ((Get-FileHash -LiteralPath $modelPath).Hash -ne $model.sha256) { throw "Packaged model checksum mismatch: $($model.id)" }
    }
    $licenseSource = Join-Path $env:USERPROFILE ".ollama\models\blobs\sha256-$($model.licenseSha256)"
    $license = Get-VerifiedAsset "https://registry.ollama.ai/v2/library/$($model.repository)/blobs/sha256:$($model.licenseSha256)" $model.licenseSha256 $licenseSource
    Copy-Item -LiteralPath $license -Destination "$PackageDirectory\licenses\$($model.file).LICENSE" -Force
}
@{ models = @($assets.models | Select-Object id,file,sha256) } | ConvertTo-Json -Depth 5 | Set-Content "$PackageDirectory\models\models.json" -Encoding utf8
Copy-Item "$PSScriptRoot\runtime-assets.json" "$PackageDirectory\runtime\provenance.json" -Force
Copy-Item (Join-Path $LlamaSourceDirectory 'LLAMA_CPP_LICENSE') "$PackageDirectory\licenses\llama.cpp.LICENSE" -Force
Write-Host 'Bundled runtimes and model hashes verified.'

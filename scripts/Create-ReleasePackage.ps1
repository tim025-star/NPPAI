#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Release output must be a new directory.' }
if ((git -C $sourceRoot rev-parse HEAD) -ne $SourceSha) { throw 'Source checkout does not match the release commit.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$upload = Join-Path $outputRoot 'upload'
New-Item -ItemType Directory -Path $upload | Out-Null
$manifest = Get-Content "$PSScriptRoot\runtime-assets.json" -Raw | ConvertFrom-Json
$baseName = "NPPAI-$Tag-win-x64"
Add-Type -Path "$PSScriptRoot\release\SegmentedArchive.cs"

# Compile the exact tagged application and verify native self-contained hosting before packaging.
& "$sourceRoot\scripts\Build.ps1" -CodeOnly
& "$sourceRoot\scripts\Test-NativePackage.ps1"
$payload = Join-Path $sourceRoot 'artifacts\NPPAI'
New-Item -ItemType Directory -Force "$payload\runtime", "$payload\models", "$payload\licenses" | Out-Null
$runtimeZip = Join-Path $outputRoot 'runtime-distribution.zip'
Write-Host "Fetching pinned compatible runner $($manifest.runtime.distribution.version)"
& curl.exe --fail --location --retry 3 --connect-timeout 30 --silent --show-error --output $runtimeZip $manifest.runtime.distribution.url
if ($LASTEXITCODE -ne 0) { throw 'Runtime distribution download failed.' }
if ((Get-FileHash -LiteralPath $runtimeZip).Hash -ne $manifest.runtime.distribution.sha256) { throw 'Runtime distribution checksum mismatch.' }
$runtimeArchive = [IO.Compression.ZipFile]::OpenRead($runtimeZip)
try {
    foreach ($backend in @('cpu','vulkan')) {
        $destination = Join-Path $payload "runtime\$backend"
        New-Item -ItemType Directory -Path $destination | Out-Null
        $files = @($manifest.runtime.files)
        if ($backend -eq 'vulkan') { $files += @($manifest.runtime.vulkanFiles) }
        foreach ($file in $files) {
            $expected = 'lib/ollama/' + $file.file.Replace('\','/')
            $entry = $runtimeArchive.GetEntry($expected)
            if ($null -eq $entry) { throw "Runner archive is missing $expected" }
            $path = Join-Path $destination ([IO.Path]::GetFileName($file.file))
            $inputFile = $entry.Open(); $outputFile = [IO.File]::Create($path)
            try { $copied = [ReleaseTransfer]::Copy($inputFile,$outputFile) }
            finally { $outputFile.Dispose(); $inputFile.Dispose() }
            if ($copied.Sha256 -ne $file.sha256) { throw "Runner file checksum mismatch: $expected" }
        }
    }
} finally { $runtimeArchive.Dispose() }
# Only this generated, explicitly resolved file is removed; models stream directly into ZIP parts below.
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($runtimeZip)) -ne $outputRoot) { throw 'Unexpected temporary runtime path.' }
Remove-Item -LiteralPath $runtimeZip -ErrorAction Stop
Copy-Item "$payload\runtime\cpu\LLAMA_CPP_LICENSE" "$payload\licenses\llama.cpp.LICENSE"
Copy-Item "$PSScriptRoot\runtime-assets.json" "$payload\runtime\provenance.json"
@{ models = @($manifest.models | Select-Object id,file,sha256) } | ConvertTo-Json -Depth 5 | Set-Content "$payload\models\models.json" -Encoding utf8
foreach ($model in $manifest.models) {
    if ([IO.Path]::GetFileName($model.file) -ne $model.file -or !$model.file.EndsWith('.gguf')) { throw 'Invalid model filename.' }
    $url = "https://registry.ollama.ai/v2/library/$($model.repository)/blobs/sha256:$($model.licenseSha256)"
    $path = "$payload\licenses\$($model.file).LICENSE"
    Invoke-WebRequest $url -OutFile $path
    if ((Get-FileHash -LiteralPath $path).Hash -ne $model.licenseSha256) { throw 'Model licence checksum mismatch.' }
}
foreach ($licence in @(
    @{url='https://raw.githubusercontent.com/AaronRobinsonMSFT/DNNE/9f1bc9ef3e1df4fda598ccdc1427e30b0edacf15/LICENSE';name='DNNE.LICENSE'},
    @{url='https://raw.githubusercontent.com/dotnet/efcore/v8.0.10/LICENSE.txt';name='Microsoft.Data.Sqlite.LICENSE'},
    @{url='https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.6/LICENSE.TXT';name='SQLitePCLRaw.LICENSE'},
    @{url='https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.6/NOTICE.TXT';name='SQLitePCLRaw.NOTICE'}
)) { Invoke-WebRequest $licence.url -OutFile (Join-Path "$payload\licenses" $licence.name) }
@'
Qwen is licensed under the Qwen RESEARCH LICENSE AGREEMENT, Copyright (c) Alibaba Cloud. All Rights Reserved.

Both GGUF model files are distributed unchanged. See the original agreements in licenses.
The Qwen2.5-3B agreement permits non-commercial research/evaluation use and requires
a separate licence for commercial use. Qwen3.5-9B uses Apache 2.0.
llama.cpp, Ollama compatibility code and native dependency notices are under runtime.
.NET, DNNE, Microsoft.Data.Sqlite and SQLitePCLRaw licence texts are under licenses.
'@ | Set-Content "$payload\Notice.txt" -Encoding utf8
Copy-Item "$sourceRoot\README.md" "$payload\README.md"
$installReadme = @"
NPPAI $Tag - full self-contained Windows x64 Notepad++ plugin
Application source commit: $SourceSha

Save and close x64 Notepad++, then copy or move this ENTIRE NPPAI folder into
its plugins directory: <Notepad++ directory>\plugins\NPPAI\NPPAI.dll.
Program Files may request administrator permission. Reopen Notepad++ normally.
NPPAI starts the bundled models automatically. Check Plugins > NPPAI > Status
while they load. Type at the end of a line and pause; Tab accepts, Ctrl+Right
accepts one word, and Escape dismisses a suggestion.

No Ollama installation/service, separate .NET installation, developer tools,
account, API key or internet connection is needed for inference. Compatible
Vulkan GPU drivers enable acceleration; CPU fallback is included. 16 GB or
more RAM is recommended. CPU inference can be substantially slower.

Manual folder copying needs no PowerShell 7 or 7-Zip. The optional Install.ps1
script requires PowerShell 7. Personal settings and learning data are created
in the target user's LOCALAPPDATA\NPPAI directory; none are bundled.

MODEL LICENCE: qwen2.5:3b uses the Qwen RESEARCH LICENSE AGREEMENT (non-commercial
research/evaluation; commercial use requires a separate licence). qwen3.5:9b
uses Apache 2.0. Original agreements and attribution notices are included.
"@
$installReadme | Set-Content "$payload\INSTALL-README.txt" -Encoding utf8

$records = [Collections.Generic.List[object]]::new()
$writer = [ReleasePartWriter]::new((Join-Path $upload "$baseName.zip."),1900MB)
$zip = [IO.Compression.ZipArchive]::new($writer,[IO.Compression.ZipArchiveMode]::Create,$true)
try {
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($payload,$file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,"NPPAI/$relative",[IO.Compression.CompressionLevel]::Fastest) | Out-Null
        $records.Add([pscustomobject]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()})
    }
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    try {
        foreach ($model in $manifest.models) {
            Write-Host "Streaming unchanged model $($model.id) into release parts"
            $url = "https://registry.ollama.ai/v2/library/$($model.repository)/blobs/sha256:$($model.sha256)"
            $response = $client.GetAsync($url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                $response.EnsureSuccessStatusCode() | Out-Null
                $inputFile = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $entry = $zip.CreateEntry("NPPAI/models/$($model.file)",[IO.Compression.CompressionLevel]::NoCompression)
                $outputFile = $entry.Open()
                try { $copied = [ReleaseTransfer]::Copy($inputFile,$outputFile) }
                finally { $outputFile.Dispose(); $inputFile.Dispose() }
                if ($copied.Sha256 -ne $model.sha256) { throw "Model checksum mismatch: $($model.id)" }
                $records.Add([pscustomobject]@{path="models/$($model.file)";bytes=$copied.Bytes;sha256=$copied.Sha256})
                Write-Host "Verified model $($model.id): $($copied.Bytes) bytes"
            } finally { $response.Dispose() }
        }
    } finally { $client.Dispose() }
    $packageManifest = $records.ToArray() | ConvertTo-Json -Depth 4
    $entryStream = $zip.CreateEntry('NPPAI/PACKAGE-FILES.json').Open()
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($packageManifest); $entryStream.Write($bytes,0,$bytes.Length) }
    finally { $entryStream.Dispose() }
} finally { $zip.Dispose(); $writer.Dispose() }
$paths = $writer.Parts.ToArray()

# Verify every archived byte through the same ZIP reader used by normal extraction tools.
Write-Host 'Verifying the split ZIP and every packaged file checksum'
$reader = [ReleasePartReader]::new($paths)
$readZip = [IO.Compression.ZipArchive]::new($reader,[IO.Compression.ZipArchiveMode]::Read,$false)
try {
    if ($readZip.Entries.Count -ne ($records.Count + 1)) { throw 'ZIP file count mismatch.' }
    foreach ($record in $records) {
        $entry = $readZip.GetEntry('NPPAI/'+$record.path)
        if ($null -eq $entry) { throw "Missing ZIP entry: $($record.path)" }
        $inputFile = $entry.Open()
        try { $checked = [ReleaseTransfer]::Copy($inputFile,$null) } finally { $inputFile.Dispose() }
        if ($checked.Bytes -ne $record.bytes -or $checked.Sha256 -ne $record.sha256) { throw "ZIP checksum mismatch: $($record.path)" }
    }
} finally { $readZip.Dispose(); $reader.Dispose() }
$reader = [ReleasePartReader]::new($paths)
try { $archive = [ReleaseTransfer]::Copy($reader,$null) } finally { $reader.Dispose() }
$parts = @($paths | ForEach-Object { [pscustomobject]@{name=[IO.Path]::GetFileName($_);bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()} })
foreach ($part in $parts) { if ($part.bytes -ge 2GB) { throw 'Part exceeds the GitHub release asset limit.' } }
$template = Get-Content "$PSScriptRoot\release\Extract-NPPAI.cmd.template" -Raw
$template = $template.Replace('__TAG__',$Tag).Replace('__BASE__',$baseName).Replace('__HASH__',$archive.Sha256)
$template = $template.Replace('__PARTS__',(($parts|ForEach-Object{'"'+$_.name+'"'}) -join ' '))
$template = $template.Replace('__COPY__',(($parts|ForEach-Object{'"'+$_.name+'"'}) -join '+'))
[IO.File]::WriteAllText((Join-Path $upload 'Extract-NPPAI.cmd'),($template -replace '\r?\n',"`r`n"),[Text.Encoding]::ASCII)
@"
NPPAI $Tag full self-contained release - application commit $SourceSha

Download ALL $($parts.Count) numbered ZIP parts and Extract-NPPAI.cmd into the same
folder. Double-click the helper on Windows 11. It combines the parts, verifies
the complete archive SHA-256 and extracts $baseName\NPPAI using built-in tools.
Allow about 40 GB temporary free space; the installed plugin is about 8.8 GB.
GitHub's automatic Source code downloads do not include the models.

$installReadme

The full ZIP SHA-256 is $($archive.Sha256)
Individual asset hashes are in SHA256SUMS.txt. Per-file hashes are inside the ZIP.
"@ | Set-Content "$upload\INSTALL-README.txt" -Encoding utf8
$assets = @($parts) + @(Get-ChildItem -LiteralPath $upload -File | Where-Object Extension -in '.cmd','.txt' | ForEach-Object {
    [pscustomobject]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$assets | Sort-Object name | ForEach-Object { $_.sha256+'  '+$_.name } | Set-Content "$upload\SHA256SUMS.txt" -Encoding ascii
$assets += [pscustomobject]@{name='SHA256SUMS.txt';bytes=(Get-Item "$upload\SHA256SUMS.txt").Length;sha256=(Get-FileHash "$upload\SHA256SUMS.txt").Hash.ToLowerInvariant()}
[pscustomobject]@{tag=$Tag;commit=$SourceSha;archiveHash=$archive.Sha256;archiveBytes=$archive.Bytes;verifiedFiles=$records.Count+1;assets=$assets} |
    ConvertTo-Json -Depth 5 | Set-Content "$outputRoot\release-assets.json" -Encoding utf8
Write-Host "Verified complete release: $($records.Count+1) files, $($parts.Count) archive parts"

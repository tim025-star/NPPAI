#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -Path "$root\scripts\release\SegmentedArchive.cs"
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('nppai-release-test-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $data = [byte[]]::new(1MB)
    [Random]::new(1234).NextBytes($data)
    $writer = [ReleasePartWriter]::new((Join-Path $testDirectory 'archive.zip.'),64KB)
    $zip = [IO.Compression.ZipArchive]::new($writer,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $entry = $zip.CreateEntry('NPPAI/models/fixture.gguf',[IO.Compression.CompressionLevel]::NoCompression)
        $stream = $entry.Open()
        try { $stream.Write($data,0,$data.Length) } finally { $stream.Dispose() }
    } finally { $zip.Dispose(); $writer.Dispose() }
    if ($writer.Parts.Count -lt 16) { throw 'Fixture did not cross archive part boundaries.' }
    $reader = [ReleasePartReader]::new($writer.Parts.ToArray())
    $zip = [IO.Compression.ZipArchive]::new($reader,[IO.Compression.ZipArchiveMode]::Read,$false)
    try {
        $entry = $zip.GetEntry('NPPAI/models/fixture.gguf')
        $stream = $entry.Open()
        $output = [IO.MemoryStream]::new()
        try {
            $copied = [ReleaseTransfer]::Copy($stream,$output)
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant()
            if ($copied.Bytes -ne $data.Length -or $copied.Sha256 -ne $expected -or $output.Length -ne $data.Length) { throw 'Split ZIP round-trip mismatch.' }
        } finally { $output.Dispose(); $stream.Dispose() }
    } finally { $zip.Dispose(); $reader.Dispose() }
    Write-Output 'PASS ZIP backpatching, cross-part reads and per-file SHA-256 round-trip'
} finally {
    # All generated files are direct children; no recursive deletion is needed.
    foreach ($file in Get-ChildItem -LiteralPath $testDirectory -File) { Remove-Item -LiteralPath $file.FullName }
    Remove-Item -LiteralPath $testDirectory
}

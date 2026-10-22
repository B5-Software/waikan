param(
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'models\kws')
)

$ErrorActionPreference = 'Stop'
$archiveName = 'sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20'
$archiveUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/$archiveName.tar.bz2"
$work = Join-Path (Split-Path $PSScriptRoot -Parent) 'models\download'
$archive = Join-Path $work "$archiveName.tar.bz2"

New-Item -ItemType Directory -Force -Path $Destination, $work | Out-Null

if (!(Test-Path $archive)) {
    Write-Host "Downloading keyword spotting model (~32 MB)..."
    & curl.exe -L --fail --retry 3 -o $archive $archiveUrl
    if ($LASTEXITCODE -ne 0) { throw "Model download failed." }
}

if (!(Test-Path (Join-Path $work $archiveName))) {
    Write-Host 'Extracting model archive...'
    & tar.exe -xf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw "Model extraction failed." }
}

$source = Join-Path $work $archiveName
Copy-Item (Join-Path $source 'encoder-epoch-13-avg-2-chunk-16-left-64.int8.onnx') (Join-Path $Destination 'encoder.int8.onnx') -Force
Copy-Item (Join-Path $source 'decoder-epoch-13-avg-2-chunk-16-left-64.onnx') (Join-Path $Destination 'decoder.onnx') -Force
Copy-Item (Join-Path $source 'joiner-epoch-13-avg-2-chunk-16-left-64.int8.onnx') (Join-Path $Destination 'joiner.int8.onnx') -Force
Copy-Item (Join-Path $source 'tokens.txt') (Join-Path $Destination 'tokens.txt') -Force

Write-Host "Model files are ready in $Destination"
Write-Host 'License: Apache-2.0. See models/kws/NOTICE.md and licenses/Apache-2.0.txt.'

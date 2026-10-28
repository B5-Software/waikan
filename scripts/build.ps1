param(
    [string]$Dotnet = 'dotnet',
    [switch]$SkipTests,
    [switch]$NoZip,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$version = '1.1.0'
$output = Join-Path $dist 'Waikan-win-x64'
$zip = Join-Path $dist "Waikan-win-x64-$version.zip"
$hash = Join-Path $dist "Waikan-win-x64-$version.sha256.txt"

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed: $($Arguments -join ' ')"
    }
}

if (!(Get-Command $Dotnet -ErrorAction SilentlyContinue) -and !(Test-Path $Dotnet)) {
    throw "dotnet SDK not found: $Dotnet. Install .NET SDK 10 or pass -Dotnet <path>."
}

$modelEncoder = Join-Path $root 'models\kws\encoder.int8.onnx'
if (!(Test-Path $modelEncoder)) {
    Write-Host '==> Fetch keyword spotting model'
    & (Join-Path $PSScriptRoot 'fetch-model.ps1')
}

Write-Host '==> Build'
Invoke-Dotnet -Arguments @('build', (Join-Path $root 'Waikan.slnx'), '-c', 'Release')

if (!$SkipTests) {
    Write-Host '==> Test'
    Invoke-Dotnet -Arguments @(
        'test',
        (Join-Path $root 'tests\Waikan.Tests\Waikan.Tests.csproj'),
        '-c', 'Release',
        '--no-build'
    )
}

Write-Host '==> Publish self-contained win-x64'
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
Invoke-Dotnet -Arguments @(
    'publish',
    (Join-Path $root 'src\Waikan\Waikan.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishReadyToRun=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $output
)

Copy-Item (Join-Path $root 'README.md') (Join-Path $output 'README.md') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $output 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $output 'LICENSE') -Force
Copy-Item (Join-Path $root 'docs') $output -Recurse -Force
Copy-Item (Join-Path $root 'licenses') $output -Recurse -Force

# Remove non-Windows native assets if a transitive package brought them in.
$runtimes = Join-Path $output 'runtimes'
if (Test-Path $runtimes) {
    Get-ChildItem $runtimes -Directory | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force
}

$size = [math]::Round(((Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "==> Published $output ($size MB)"

if (!$NoZip) {
    Write-Host '==> Package zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $output -DestinationPath $zip -CompressionLevel Optimal
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  Waikan-win-x64-$version.zip" | Set-Content $hash -Encoding ascii
    Write-Host "==> $zip"
    Write-Host "SHA256 $sha"
}

if (!$SkipInstaller) {
    Write-Host '==> Build NSIS installer'
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $version
}

Write-Host 'Done.'

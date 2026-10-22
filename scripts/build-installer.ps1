param(
    [string]$Version = '1.0.0',
    [string]$Makensis = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root 'dist\Waikan-win-x64'
$license = Join-Path $root 'LICENSE'
$nsi = Join-Path $PSScriptRoot 'installer.nsi'
$installer = Join-Path $root "dist\Waikan-$Version-x64-setup.exe"
$hashFile = Join-Path $root "dist\Waikan-$Version-x64-setup.sha256.txt"

if (!(Test-Path (Join-Path $publishDir 'Waikan.exe'))) {
    throw "Published application not found: $publishDir. Run scripts/build.ps1 first."
}

if (!(Test-Path $license)) {
    throw "LICENSE not found: $license"
}

if (!(Test-Path $nsi)) {
    throw "NSIS script not found: $nsi"
}

function Resolve-Makensis {
    param([string]$Requested)

    if (![string]::IsNullOrWhiteSpace($Requested)) {
        if (Test-Path $Requested) { return (Resolve-Path $Requested).Path }
        $command = Get-Command $Requested -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
    }

    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "${env:ProgramFiles}\NSIS\makensis.exe",
        "C:\ProgramData\chocolatey\bin\makensis.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

$makensisPath = Resolve-Makensis $Makensis
if (!$makensisPath) {
    throw "NSIS (makensis.exe) was not found. Install NSIS 3 from https://nsis.sourceforge.io/ and retry, or use -SkipInstaller."
}

Write-Host "==> Build NSIS installer with $makensisPath"
Push-Location $root
try {
    & $makensisPath /V2 "/DAPP_VERSION=$Version" $nsi
    if ($LASTEXITCODE -ne 0) {
        throw "makensis failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (!(Test-Path $installer)) {
    throw "Installer was not created: $installer"
}

$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Waikan-$Version-x64-setup.exe" | Set-Content $hashFile -Encoding ascii

$size = [math]::Round((Get-Item $installer).Length / 1MB, 1)
Write-Host "==> $installer ($size MB)"
Write-Host "SHA256 $hash"

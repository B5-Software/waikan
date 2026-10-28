param(
    [int]$Count = 3,
    [string]$Prefix = 'waikan-view',
    [string]$TimeZone = 'Asia/Shanghai'
)

$ErrorActionPreference = 'Stop'
$cloudflareRoot = $PSScriptRoot
$metaPath = Join-Path $cloudflareRoot 'pairs.local.json'
$stagingRoot = Join-Path $cloudflareRoot '.deploy'
$compatibilityDate = '2026-10-01'
$labels = @(
    '01 · 主力设备',
    '02 · 备用设备',
    '03 · 共享查看',
    '04 · 备用查看',
    '05 · 第二客户端'
)

function Invoke-Wrangler {
    param([string[]]$Arguments, [switch]$ReturnOutput)

    $output = & npx --yes wrangler @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "wrangler failed ($LASTEXITCODE): $($Arguments -join ' ')`n$($output -join "`n")"
    }
    if ($ReturnOutput) {
        return ($output -join "`n")
    }
    Write-Host ($output -join "`n")
    return ''
}

function New-ClientKey {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $key = "wk_$token"
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($key)
    )
    $hash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
    return @{ Key = $key; Hash = $hash }
}

function Wait-ForPair {
    param([string]$Url, [string]$Key)

    $deadline = (Get-Date).AddSeconds(90)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $headers = @{ Authorization = "Bearer $Key" }
            $response = Invoke-RestMethod -Uri "$Url/api/verify" -Headers $headers -TimeoutSec 20
            if ($response.ok) {
                return $response
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 3
    }
    throw "Pair endpoint did not become ready at $Url/api/verify. Last error: $lastError"
}

function Deploy-Pair {
    param([object]$Pair, [string]$PairTimeZone)

    $pairRoot = Join-Path $stagingRoot $Pair.project
    if (Test-Path $pairRoot) {
        Remove-Item $pairRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $pairRoot | Out-Null
    Copy-Item (Join-Path $cloudflareRoot 'public') (Join-Path $pairRoot 'public') -Recurse -Force
    Copy-Item (Join-Path $cloudflareRoot 'functions') (Join-Path $pairRoot 'functions') -Recurse -Force

    $escapedLabel = ([string]$Pair.label).Replace('"', '\"')
    $config = @"
name = "$($Pair.project)"
pages_build_output_dir = "public"
compatibility_date = "$compatibilityDate"

[[kv_namespaces]]
binding = "COUNTS"
id = "$($Pair.kvId)"

[vars]
PAIR_NAME = "$escapedLabel"
TIMEZONE = "$PairTimeZone"
CLIENT_KEY_HASH = "$($Pair.keyHash)"
"@
    Set-Content (Join-Path $pairRoot 'wrangler.toml') $config -Encoding utf8

    Push-Location $pairRoot
    try {
        Invoke-Wrangler -Arguments @(
            'pages', 'deploy',
            '--project-name', $Pair.project,
            '--branch', 'main',
            '--commit-dirty=true'
        )
    }
    finally {
        Pop-Location
    }

    $verified = Wait-ForPair -Url $Pair.url -Key $Pair.key
    $Pair.deployedAt = (Get-Date).ToUniversalTime().ToString('o')
    Write-Host "Verified: $($Pair.url) ($($verified.pair))" -ForegroundColor Green
    return $Pair
}

if (!(Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "Node.js/npx is required. Install Node.js first."
}

if (Test-Path $stagingRoot) {
    Remove-Item $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$pairs = @()
if (Test-Path $metaPath) {
    $pairs = @(Get-Content $metaPath -Raw -Encoding utf8 | ConvertFrom-Json)
}

foreach ($pair in $pairs) {
    if (!$pair.timezone) {
        $pair | Add-Member -NotePropertyName timezone -NotePropertyValue $TimeZone -Force
    }
    Write-Host ""
    Write-Host "==> Redeploying existing pair $($pair.pair) ($($pair.project))" -ForegroundColor Cyan
    $deployed = Deploy-Pair -Pair $pair -PairTimeZone $pair.timezone
    ConvertTo-Json -InputObject @($pairs) -Depth 5 | Set-Content $metaPath -Encoding utf8
}

for ($index = $pairs.Count + 1; $index -le $Count; $index++) {
    $project = "{0}-{1:D2}" -f $Prefix, $index
    $kvName = "$project-counters"
    $label = $labels[$index - 1]
    if ([string]::IsNullOrWhiteSpace($label)) {
        $label = "{0:D2} · 客户端" -f $index
    }

    Write-Host ""
    Write-Host "==> Creating pair $index ($project)" -ForegroundColor Cyan

    $credentials = New-ClientKey
    $kvOutput = Invoke-Wrangler -Arguments @('kv', 'namespace', 'create', $kvName) -ReturnOutput
    $kvMatch = [regex]::Match($kvOutput, '"?id"?\s*[:=]\s*"?([0-9a-fA-F]{32})"?')
    if (!$kvMatch.Success) {
        throw "Could not parse KV namespace id from wrangler output: $kvOutput"
    }
    $kvId = $kvMatch.Groups[1].Value.ToLowerInvariant()

    # Wrangler 4.155 delegates new Pages projects to Workers by default. --force
    # keeps the direct Pages legacy command for the initial project creation.
    $null = Invoke-Wrangler -Arguments @(
        'pages', 'project', 'create', $project, '--production-branch', 'main', '--force'
    ) -ReturnOutput

    $pair = [PSCustomObject]@{
        pair = $index
        label = $label
        project = $project
        url = "https://$project.pages.dev"
        key = $credentials.Key
        keyHash = $credentials.Hash
        kvNamespace = $kvName
        kvId = $kvId
        timezone = $TimeZone
        deployedAt = $null
    }
    $deployed = Deploy-Pair -Pair $pair -PairTimeZone $TimeZone
    $pairs += $deployed
    ConvertTo-Json -InputObject @($pairs) -Depth 5 | Set-Content $metaPath -Encoding utf8
}

Write-Host ""
Write-Host "Pairings" -ForegroundColor Cyan
$pairs | Select-Object pair, label, url, key | Format-Table -AutoSize
Write-Host "Pair metadata saved locally to $metaPath (git ignored)."
Write-Host "Paste each url + key into the desktop app's Cloudflare settings."

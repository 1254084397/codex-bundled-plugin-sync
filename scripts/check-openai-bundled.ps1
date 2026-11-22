[CmdletBinding()]
param(
    [switch]$DiskOnly,
    [ValidateRange(1, 100)][int]$MinimumFreeGB = 1,
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$codexRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$marketBundle = Join-Path $codexRoot '.tmp\bundled-marketplaces\openai-bundled'
$cacheParent = Join-Path $codexRoot 'plugins\cache\openai-bundled'
$managedPlugins = @('browser', 'chrome', 'computer-use')

function Get-PluginMap {
    param([Parameter(Mandatory)][string]$Root)
    $pluginsDir = Join-Path $Root 'plugins'
    if (-not (Test-Path -LiteralPath $pluginsDir -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $pluginsDir -Directory -Force | ForEach-Object {
        $manifest = Join-Path $_.FullName '.codex-plugin\plugin.json'
        if (Test-Path -LiteralPath $manifest) {
            $json = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
            "$($json.name)@$($json.version)"
        }
    } | Sort-Object)
}

function Get-MaterializedPluginMap {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$MarketRoot
    )
    $sourceEntries = @(Get-PluginMap -Root $SourceRoot)
    $sourceByName = @{}
    foreach ($entry in $sourceEntries) {
        $parts = $entry -split '@', 2
        $sourceByName[$parts[0]] = $entry
    }
    $keyPath = Join-Path $MarketRoot '.materialization-key'
    if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
        $key = Get-Content -LiteralPath $keyPath -Raw | ConvertFrom-Json
        return @($key.plugins | ForEach-Object {
            if ($sourceByName.ContainsKey($_.name)) { $sourceByName[$_.name] }
        } | Sort-Object)
    }
    $manifestPath = Join-Path $SourceRoot '.agents\plugins\marketplace.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundled-plugin marketplace manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    @($manifest.plugins | ForEach-Object {
        if ($sourceByName.ContainsKey($_.name)) { $sourceByName[$_.name] }
    } | Sort-Object)
}

function Get-FreeBytes {
    param([Parameter(Mandatory)][string]$Path)
    $fullPath = (Resolve-Path -LiteralPath $Path).Path
    $driveRoot = [IO.Path]::GetPathRoot($fullPath)
    return [int64]([IO.DriveInfo]::new($driveRoot).AvailableFreeSpace)
}

function Get-CacheVersion {
    param([Parameter(Mandatory)][string]$PluginName)
    $pluginRoot = Join-Path $cacheParent $PluginName
    if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) { return '(missing)' }
    $manifestPath = Get-ChildItem -LiteralPath $pluginRoot -Filter 'plugin.json' -Force -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\.codex-plugin\\plugin\.json$' } |
        Select-Object -First 1
    if ($null -eq $manifestPath) { return '(missing)' }
    $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw | ConvertFrom-Json
    return [string]$manifest.version
}

try {
    $codexPackage = Get-AppxPackage -Name 'OpenAI.Codex' |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $codexPackage) { throw 'OpenAI.Codex is not installed.' }

    $sourceBundle = Join-Path $codexPackage.InstallLocation 'app\resources\plugins\openai-bundled'
    $marketParent = Split-Path -Parent $marketBundle
    foreach ($path in @($sourceBundle, $marketParent, $codexRoot)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required path is unavailable: $path" }
    }

    $minimumBytes = [int64]$MinimumFreeGB * 1GB
    $sourceFree = Get-FreeBytes -Path $sourceBundle
    $codexFree = Get-FreeBytes -Path $codexRoot
    Write-Host "Codex Desktop: $($codexPackage.Version)"
    Write-Host ('Free space on source drive: {0:N1} GB' -f ($sourceFree / 1GB))
    Write-Host ('Free space on CodexHome drive: {0:N1} GB' -f ($codexFree / 1GB))
    if (($sourceFree -lt $minimumBytes) -or ($codexFree -lt $minimumBytes)) {
        throw "Insufficient free disk space. Required minimum per drive: $MinimumFreeGB GB."
    }
    if ($DiskOnly) {
        Write-Host "Disk-space preflight passed (minimum $MinimumFreeGB GB per drive)."
        exit 0
    }

    $sourceMap = @(Get-PluginMap -Root $sourceBundle)
    $targetMap = @(Get-PluginMap -Root $marketBundle)
    $materializedMap = @(Get-MaterializedPluginMap -SourceRoot $sourceBundle -MarketRoot $marketBundle)
    Write-Host ''
    Write-Host 'Desktop bundled-plugin versions:'
    $sourceMap | ForEach-Object { Write-Host "  - $_" }
    Write-Host 'Current local marketplace versions:'
    if ($targetMap.Count -eq 0) { Write-Host '  - (not yet synchronized)' } else { $targetMap | ForEach-Object { Write-Host "  - $_" } }
    Write-Host 'Expected persistent marketplace versions:'
    $materializedMap | ForEach-Object { Write-Host "  - $_" }

    Write-Host ''
    Write-Host 'Managed cache versions:'
    foreach ($pluginName in $managedPlugins) {
        Write-Host ("  - {0}: {1}" -f $pluginName, (Get-CacheVersion -PluginName $pluginName))
    }

    $lockingProcesses = @(Get-Process -Name 'chrome', 'msedge', 'ChatGPT', 'extension-host' -ErrorAction SilentlyContinue)
    if ($lockingProcesses.Count -gt 0) {
        Write-Host ''
        Write-Host 'Warning: processes that may lock plugin files are running:' -ForegroundColor Yellow
        $lockingProcesses | ForEach-Object { Write-Host "  - $($_.ProcessName) (PID $($_.Id))" -ForegroundColor Yellow }
    }

    $backups = @(Get-ChildItem -LiteralPath (Join-Path $codexRoot 'backups') -Directory -Filter 'openai-bundled-sync-*' -Force -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    $stages = @(Get-ChildItem -LiteralPath $marketParent -Directory -Filter 'openai-bundled.stage-*' -Force -ErrorAction SilentlyContinue)
    $needsSync = (($materializedMap -join "`n") -ne ($targetMap -join "`n"))
    Write-Host ''
    Write-Host ("Update needed: {0}" -f $(if ($needsSync) { 'Yes' } else { 'No' }))
    Write-Host ("Rollback backups available: {0}" -f $backups.Count)
    Write-Host ("Marketplace stage directories: {0}" -f $stages.Count)
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

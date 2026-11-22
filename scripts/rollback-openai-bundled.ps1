[CmdletBinding()]
param(
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$marketBundle = Join-Path $rootPath '.tmp\bundled-marketplaces\openai-bundled'
$cacheParent = Join-Path $rootPath 'plugins\cache\openai-bundled'
$backupParent = Join-Path $rootPath 'backups'
$managedPlugins = @('browser', 'chrome', 'computer-use')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safetyRoot = Join-Path $backupParent "rollback-before-$stamp"
$movedItems = @()

function Assert-UnderRoot {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    if (-not $resolved.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to access a path outside CodexHome: $resolved"
    }
    return $resolved
}

function Remove-Tree {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        [IO.Directory]::Delete((Assert-UnderRoot -Path $Path), $true)
    }
}

function Move-CurrentToSafety {
    param(
        [Parameter(Mandatory)][string]$Current,
        [Parameter(Mandatory)][string]$SafetyName
    )
    if (-not (Test-Path -LiteralPath $Current)) { return }
    $currentResolved = Assert-UnderRoot -Path $Current
    $safetyPath = Join-Path $safetyRoot $SafetyName
    Move-Item -LiteralPath $currentResolved -Destination $safetyPath
    $script:movedItems += [pscustomobject]@{ Current = $currentResolved; Safety = $safetyPath }
}

try {
    if (Test-Path -LiteralPath $safetyRoot) { throw "Safety path already exists: $safetyRoot" }
    New-Item -ItemType Directory -Path $safetyRoot -Force | Out-Null

    $backup = Get-ChildItem -LiteralPath $backupParent -Directory -Filter 'openai-bundled-sync-*' -Force |
        Sort-Object LastWriteTime -Descending |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'marketplace.previous') } |
        Select-Object -First 1
    if ($null -eq $backup) { throw 'No completed openai-bundled-sync backup is available for rollback.' }

    $required = @('marketplace.previous') + ($managedPlugins | ForEach-Object { "cache-$_.previous" })
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $backup.FullName $name))) {
            throw "Rollback backup is incomplete; missing $name."
        }
    }

    $lockingProcesses = @(Get-Process -Name 'chrome', 'msedge', 'ChatGPT', 'extension-host' -ErrorAction SilentlyContinue)
    if ($lockingProcesses.Count -gt 0) {
        $summary = ($lockingProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        throw "Close Chrome, Microsoft Edge, and fully quit Codex Desktop before rollback. Active processes: $summary"
    }

    Write-Host "Using rollback backup: $($backup.FullName)"
    Move-CurrentToSafety -Current $marketBundle -SafetyName 'current-marketplace'
    foreach ($pluginName in $managedPlugins) {
        Move-CurrentToSafety -Current (Join-Path $cacheParent $pluginName) -SafetyName "current-cache-$pluginName"
    }

    Copy-Item -LiteralPath (Join-Path $backup.FullName 'marketplace.previous') -Destination $marketBundle -Recurse -Force
    foreach ($pluginName in $managedPlugins) {
        Copy-Item -LiteralPath (Join-Path $backup.FullName "cache-$pluginName.previous") -Destination (Join-Path $cacheParent $pluginName) -Recurse -Force
    }

    $nativeHostRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension'
    $nativeBackup = Join-Path $backup.FullName 'com.openai.codexextension.json.previous'
    if ((Test-Path -LiteralPath $nativeBackup) -and (Test-Path -LiteralPath $nativeHostRegistryPath)) {
        $nativeManifestPath = (Get-ItemProperty -LiteralPath $nativeHostRegistryPath).'(default)'
        if (-not [string]::IsNullOrWhiteSpace($nativeManifestPath)) {
            if (Test-Path -LiteralPath $nativeManifestPath) {
                Copy-Item -LiteralPath $nativeManifestPath -Destination (Join-Path $safetyRoot 'current-native-host.json') -Force
            }
            Copy-Item -LiteralPath $nativeBackup -Destination $nativeManifestPath -Force
        }
    }

    foreach ($pluginName in $managedPlugins) {
        $manifest = Get-ChildItem -LiteralPath (Join-Path $cacheParent $pluginName) -Filter 'plugin.json' -Force -Recurse |
            Where-Object { $_.FullName -match '\\.codex-plugin\\plugin\.json$' } |
            Select-Object -First 1
        if ($null -eq $manifest) { throw "Rollback verification failed: $pluginName manifest is missing." }
    }

    Write-Host 'Rollback completed successfully.' -ForegroundColor Green
    Write-Host "Restored from: $($backup.FullName)"
    Write-Host "Previous current state preserved at: $safetyRoot"
    exit 0
}
catch {
    Write-Error "Rollback failed: $($_.Exception.Message)"
    foreach ($item in ($movedItems | Sort-Object { $_.Current.Length } -Descending)) {
        try {
            if (Test-Path -LiteralPath $item.Current) { Remove-Tree -Path $item.Current }
            if (Test-Path -LiteralPath $item.Safety) { Move-Item -LiteralPath $item.Safety -Destination $item.Current }
        } catch {
            Write-Error "Recovery of $($item.Current) failed: $($_.Exception.Message)"
        }
    }
    exit 1
}

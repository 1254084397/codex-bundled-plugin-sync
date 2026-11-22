[CmdletBinding()]
param(
    [switch]$Force,
    [string]$LogPath,
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Status {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message"
}

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $rootPath 'logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $LogPath = Join-Path $logDirectory ("sync-openai-bundled-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Start-Transcript -LiteralPath $LogPath -Append | Out-Null

try {
Write-Status 'Starting Codex Desktop bundled-plugin check.'

$codexPackage = Get-AppxPackage -Name 'OpenAI.Codex' |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $codexPackage) {
    throw 'OpenAI.Codex is not installed.'
}
Write-Status "Detected Codex Desktop version: $($codexPackage.Version)"

$sourceBundle = Join-Path $codexPackage.InstallLocation 'app\resources\plugins\openai-bundled'
$marketParent = Join-Path $rootPath '.tmp\bundled-marketplaces'
$marketBundle = Join-Path $marketParent 'openai-bundled'
$cacheParent = Join-Path $rootPath 'plugins\cache\openai-bundled'
$backupParent = Join-Path $rootPath 'backups'
$managedPlugins = @('browser', 'chrome', 'computer-use')

foreach ($path in @($sourceBundle, $marketParent)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path is unavailable: $path"
    }
}
# Codex creates openai-bundled.staging-* as a sibling under this directory.
# Its bundled source files are Application Protected; Windows copyFile fails
# on E: unless the destination inherits EFS encryption from this parent.
$marketParentItem = Get-Item -LiteralPath $marketParent -Force
if (-not ($marketParentItem.Attributes -band [System.IO.FileAttributes]::Encrypted)) {
    $cipherOutput = & cipher /e $marketParent 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enable EFS inheritance for marketplace staging parent: $marketParent ($($cipherOutput -join ' '))"
    }
}
$marketParentItem = Get-Item -LiteralPath $marketParent -Force
if (-not ($marketParentItem.Attributes -band [System.IO.FileAttributes]::Encrypted)) {
    throw "Marketplace staging parent is not EFS-encrypted: $marketParent"
}
if (-not (Test-Path -LiteralPath $backupParent -PathType Container)) {
    New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $cacheParent)) {
    New-Item -ItemType Directory -Path $cacheParent -Force | Out-Null
}

function Get-PluginMap {
    param([Parameter(Mandatory)][string]$Root)
    $pluginsDir = Join-Path $Root 'plugins'
    if (-not (Test-Path -LiteralPath $pluginsDir)) { return @() }
    Get-ChildItem -LiteralPath $pluginsDir -Directory -Force | ForEach-Object {
        $manifest = Join-Path $_.FullName '.codex-plugin\plugin.json'
        if (Test-Path -LiteralPath $manifest) {
            $json = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
            "$($json.name)@$($json.version)"
        }
    } | Sort-Object
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
    # First synchronization: use the package marketplace manifest. Later
    # launches persist the app's materialization key in the local marketplace.
    $manifestPath = Join-Path $SourceRoot '.agents\plugins\marketplace.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundled-plugin marketplace manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    @($manifest.plugins | ForEach-Object {
        if ($sourceByName.ContainsKey($_.name)) { $sourceByName[$_.name] }
    } | Sort-Object)
}

function Copy-Tree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    & robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /NJH /NJS /NDL /NFL
    if ($LASTEXITCODE -gt 7) {
        throw "Robocopy failed with exit code $LASTEXITCODE while copying $Source"
    }
    # WindowsApps bundles may carry EFS/Application Protected attributes.
    # Robocopy preserves those attributes, but Codex later stages the marketplace
    # with Node fs.copyFile, which fails on these encrypted copies. Decrypt the
    # staged tree and prevent new files there from inheriting EFS encryption.
    $cipherOutput = & cipher /d /s:$Destination 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not disable EFS encryption for staged copy: $Destination ($($cipherOutput -join ' '))"
    }
    $encryptedItems = @(Get-ChildItem -LiteralPath $Destination -Force -Recurse -ErrorAction Stop |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::Encrypted })
    if ($encryptedItems.Count -gt 0) {
        throw "EFS-encrypted items remain in staged copy: $Destination ($($encryptedItems.Count) item(s))"
    }
}

function Assert-IdenticalTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    $sourceFiles = Get-ChildItem -LiteralPath $Source -Force -Recurse -File
    $destinationFiles = Get-ChildItem -LiteralPath $Destination -Force -Recurse -File
    if ($sourceFiles.Count -ne $destinationFiles.Count) {
        throw "File-count mismatch: $Source ($($sourceFiles.Count)) vs $Destination ($($destinationFiles.Count))"
    }
    foreach ($sourceFile in $sourceFiles) {
        $relative = $sourceFile.FullName.Substring($Source.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relative
        if (-not (Test-Path -LiteralPath $destinationFile)) {
            throw "Missing copied file: $relative"
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Hash mismatch: $relative"
        }
    }
}

$sourceMap = @(Get-PluginMap -Root $sourceBundle)
$targetMap = if (Test-Path -LiteralPath $marketBundle) { @(Get-PluginMap -Root $marketBundle) } else { @() }
$materializedMap = @(Get-MaterializedPluginMap -SourceRoot $sourceBundle -MarketRoot $marketBundle)
$needsBundleSync = $Force -or (($materializedMap -join "`n") -ne ($targetMap -join "`n"))
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $backupParent "openai-bundled-sync-$stamp"

Write-Status 'Desktop bundled-plugin versions:'
$sourceMap | ForEach-Object { Write-Host "  - $_" }
Write-Status 'Current local marketplace versions:'
if ($targetMap.Count -eq 0) { Write-Host '  - (not yet synchronized)' } else { $targetMap | ForEach-Object { Write-Host "  - $_" } }
Write-Status 'Expected persistent marketplace versions:'
$materializedMap | ForEach-Object { Write-Host "  - $_" }

# A browser native-messaging host can remain alive after its visible window is
# closed.  It only matters when this run will replace a plugin cache; do not
# report a false failure when every installed version is already current.
$pluginsRequiringSync = @(
    foreach ($pluginName in $managedPlugins) {
        $sourcePlugin = Join-Path (Join-Path $sourceBundle 'plugins') $pluginName
        if (-not (Test-Path -LiteralPath $sourcePlugin)) {
            throw "Desktop bundle does not contain $pluginName"
        }
        $manifest = Get-Content -LiteralPath (Join-Path $sourcePlugin '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
        $cachedManifest = Join-Path (Join-Path (Join-Path $cacheParent $pluginName) $manifest.version) '.codex-plugin\plugin.json'
        if ($Force -or -not (Test-Path -LiteralPath $cachedManifest)) { $pluginName }
    }
)
$requiresWrite = $Force -or $needsBundleSync -or ($pluginsRequiringSync.Count -gt 0)
if ($requiresWrite) {
    $lockingProcesses = @(Get-Process -Name 'chrome', 'msedge', 'ChatGPT', 'extension-host' -ErrorAction SilentlyContinue)
    if ($lockingProcesses.Count -gt 0) {
        $processSummary = ($lockingProcesses | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
        throw "Close Chrome, Microsoft Edge, and fully quit Codex Desktop before synchronizing an update. Active processes: $processSummary"
    }
}

if ($needsBundleSync) {
    Write-Status 'Detected a version or content change; synchronizing marketplace.'
    $marketStage = Join-Path $marketParent "openai-bundled.stage-$stamp"
    if ((Test-Path -LiteralPath $marketStage) -or (Test-Path -LiteralPath $backupRoot)) {
        throw 'A synchronization staging or backup path already exists.'
    }
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    Copy-Tree -Source $sourceBundle -Destination $marketStage
    Write-Status 'Marketplace copy finished; verifying SHA-256 integrity.'
    Assert-IdenticalTree -Source $sourceBundle -Destination $marketStage
    if (Test-Path -LiteralPath $marketBundle) {
        Move-Item -LiteralPath $marketBundle -Destination (Join-Path $backupRoot 'marketplace.previous')
    }
    Move-Item -LiteralPath $marketStage -Destination $marketBundle
    Write-Status 'Marketplace synchronization finished.'
} else {
    Write-Status 'Marketplace is already current; no copy needed.'
}

foreach ($pluginName in $managedPlugins) {
    $sourcePlugin = Join-Path (Join-Path $sourceBundle 'plugins') $pluginName
    if (-not (Test-Path -LiteralPath $sourcePlugin)) {
        throw "Desktop bundle does not contain $pluginName"
    }
    $manifest = Get-Content -LiteralPath (Join-Path $sourcePlugin '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
    $version = $manifest.version
    $pluginCache = Join-Path $cacheParent $pluginName
    $cachedManifest = Join-Path (Join-Path $pluginCache $version) '.codex-plugin\plugin.json'
    $needsPluginSync = $Force -or -not (Test-Path -LiteralPath $cachedManifest)
    $staleStages = @(Get-ChildItem -LiteralPath $cacheParent -Directory -Filter "$pluginName.stage-*" -ErrorAction SilentlyContinue)
    foreach ($staleStage in $staleStages) {
        Write-Status "[$pluginName] Found an incomplete stage from a previous run: $($staleStage.FullName)"
    }
    if ($needsPluginSync) {
        Write-Status "[$pluginName] cache requires update to $version."
        if (-not (Test-Path -LiteralPath $backupRoot)) {
            New-Item -ItemType Directory -Path $backupRoot | Out-Null
        }
        $pluginStage = Join-Path $cacheParent "$pluginName.stage-$stamp"
        if (Test-Path -LiteralPath $pluginStage) { throw "Staging path exists: $pluginStage" }
        New-Item -ItemType Directory -Path $pluginStage | Out-Null
        $versionStage = Join-Path $pluginStage $version
        Copy-Tree -Source $sourcePlugin -Destination $versionStage
        Write-Status "[$pluginName] copy finished; verifying SHA-256."
        Assert-IdenticalTree -Source $sourcePlugin -Destination $versionStage
        if (Test-Path -LiteralPath $pluginCache) {
            Move-Item -LiteralPath $pluginCache -Destination (Join-Path $backupRoot "cache-$pluginName.previous")
        }
        Move-Item -LiteralPath $pluginStage -Destination $pluginCache
        Write-Status "[$pluginName] cache synchronization finished."
    } else {
        Write-Status "[$pluginName] cache is already $version; no update needed."
    }
}

# The Chrome extension starts this executable through a native-messaging manifest.
# Update that manifest after the cache has been switched to the Desktop version.
$chromeSourceManifest = Get-Content -LiteralPath (Join-Path (Join-Path (Join-Path $sourceBundle 'plugins') 'chrome') '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
$chromeHostExecutable = Join-Path $cacheParent ("chrome\{0}\extension-host\windows\x64\extension-host.exe" -f $chromeSourceManifest.version)
$chromeRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension'
if (-not (Test-Path -LiteralPath $chromeHostExecutable)) {
    throw "Current Chrome native host was not found after synchronization: $chromeHostExecutable"
}
if (Test-Path -LiteralPath $chromeRegistryPath) {
    $nativeHostManifestPath = (Get-ItemProperty -LiteralPath $chromeRegistryPath).'(default)'
    if (-not (Test-Path -LiteralPath $nativeHostManifestPath)) {
        throw "Chrome native-messaging manifest is missing: $nativeHostManifestPath"
    }
    $nativeHostManifest = Get-Content -LiteralPath $nativeHostManifestPath -Raw | ConvertFrom-Json
    if ($nativeHostManifest.path -ne $chromeHostExecutable) {
        if (-not (Test-Path -LiteralPath $backupRoot)) {
            New-Item -ItemType Directory -Path $backupRoot | Out-Null
        }
        Copy-Item -LiteralPath $nativeHostManifestPath -Destination (Join-Path $backupRoot 'com.openai.codexextension.json.previous')
        $nativeHostManifest.path = $chromeHostExecutable
        $nativeHostManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $nativeHostManifestPath -Encoding UTF8
        $verifiedNativeHost = Get-Content -LiteralPath $nativeHostManifestPath -Raw | ConvertFrom-Json
        if ($verifiedNativeHost.path -ne $chromeHostExecutable) {
            throw 'Chrome native-messaging manifest verification failed after update.'
        }
        Write-Status "Chrome native-messaging host updated to $($chromeSourceManifest.version)."
    } else {
        Write-Status 'Chrome native-messaging host is already current.'
    }
} else {
    Write-Status 'Chrome native-messaging registry entry was not found; skipping its update.'
}

$result = foreach ($pluginName in $managedPlugins) {
    $manifestPath = Get-ChildItem -LiteralPath (Join-Path $cacheParent $pluginName) -Filter plugin.json -Force -Recurse |
        Where-Object { $_.FullName -match '\\.codex-plugin\\plugin\.json$' } |
        Select-Object -First 1
    $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{ Plugin = $pluginName; Version = $manifest.version; Cache = $manifestPath.Directory.Parent.FullName }
}
$result | Format-Table -AutoSize
Write-Status "Desktop package: $($codexPackage.Version)"
Write-Status "Marketplace synchronized: $needsBundleSync"
if (Test-Path -LiteralPath $backupRoot) { Write-Status "Previous-version backup: $backupRoot" }
Write-Status "Synchronization and verification completed successfully. Log: $LogPath"
exit 0
}
catch {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Synchronization FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Keep this window open and inspect the log: $LogPath" -ForegroundColor Red
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}

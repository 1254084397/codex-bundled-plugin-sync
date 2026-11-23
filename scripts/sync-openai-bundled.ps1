[CmdletBinding()]
param(
    [switch]$Force,
    [string]$LogPath,
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' }),
    # fixture 注入（§16.1）：直接指向一个伪造的 openai-bundled 目录，跳过 AppX 定位。
    [string]$SourceBundle,
    # Simulate：只记录 EFS 状态、不调用 cipher（fixture 环境用；真机验收必须用 Real）。
    [ValidateSet('Real', 'Simulate')][string]$EfsMode = 'Real',
    # fixture 专用：跳过 Chrome/Codex 进程守护。
    [switch]$SkipProcessCheck,
    # 允许覆盖注册表路径；fixture 用 -NativeHostStateFile 完全绕开真实注册表。
    [string]$NativeHostRegistryPath,
    [string]$NativeHostStateFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\CodexPluginSync.Common.ps1')

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $rootPath 'logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $LogPath = Join-Path $logDirectory ("sync-openai-bundled-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Start-Transcript -LiteralPath $LogPath -Append | Out-Null

$lock = $null
$journal = $null
$efsPath = $null
$efsPreEncrypted = $false
$efsEnabledByThisRun = $false
$efsRestoreResult = $null
$stagePaths = New-Object 'System.Collections.Generic.List[string]'
$backupRoot = $null
$backupManifest = $null
$nativeHostUpdated = $false
$nativeHostManifestPath = $null
$nativeHostBackupPath = $null

function Write-BackupManifest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Manifest
    )
    Write-CodexSyncJsonFile -Path $Path -Object $Manifest
}

function Copy-BundledTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    & robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NJH /NJS /NDL /NFL | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy 失败（exit $LASTEXITCODE）：$Source → $Destination"
    }
    # /XJ 不展开 junction；再按原目标重建，保持与源同构（真机 chrome\latest 即此情况）。
    Copy-CodexSyncReparseLinks -Source $Source -Destination $Destination

    if ($EfsMode -eq 'Simulate') {
        Write-CodexSyncLog -Phase 'stage-efs' -Fields @{ mode = 'simulate'; path = $Destination }
    } else {
        # WindowsApps 源文件带 EFS/Application Protected；Codex 之后用 Node fs.copyFile
        # 读这些加密副本会失败，因此 stage 树必须解密并复查（§8.1 第 4 步）。
        $cipherOutput = & cipher /d /s:$Destination 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "无法解除 stage 树的 EFS 加密：$Destination（$($cipherOutput -join ' ')）"
        }
        $encryptedItems = @(Get-ChildItem -LiteralPath $Destination -Force -Recurse -ErrorAction Stop |
            Where-Object { $_.Attributes -band [System.IO.FileAttributes]::Encrypted })
        if ($encryptedItems.Count -gt 0) {
            throw "stage 树内仍存在加密项：$Destination（$($encryptedItems.Count) 项）"
        }
    }
}

function Assert-IdenticalTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    $sourceFiles = @(Get-ChildItem -LiteralPath $Source -Force -Recurse -File)
    $destinationFiles = @(Get-ChildItem -LiteralPath $Destination -Force -Recurse -File)
    if ($sourceFiles.Count -ne $destinationFiles.Count) {
        throw "文件数不一致：$Source（$($sourceFiles.Count)）vs $Destination（$($destinationFiles.Count)）"
    }
    foreach ($sourceFile in $sourceFiles) {
        $relative = $sourceFile.FullName.Substring($Source.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relative
        if (-not (Test-Path -LiteralPath $destinationFile)) {
            throw "复制后缺少文件：$relative"
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "哈希不一致：$relative"
        }
    }
}

function Get-PluginMap {
    param([Parameter(Mandatory)][string]$Root)
    $pluginsDir = Join-Path $Root 'plugins'
    if (-not (Test-Path -LiteralPath $pluginsDir)) { return @() }
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
    # 首次同步：用包内 marketplace manifest；之后 Codex 会把物化键写到本地 marketplace。
    $manifestPath = Join-Path $SourceRoot '.agents\plugins\marketplace.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "内置插件 marketplace manifest 缺失：$manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    @($manifest.plugins | ForEach-Object {
        if ($sourceByName.ContainsKey($_.name)) { $sourceByName[$_.name] }
    } | Sort-Object)
}

function Update-CodexSyncNativeHost {
    <#
        把 Chrome native-messaging 清单指向当前缓存版本。
        -DetectOnly 只报告漂移不写；写入必须提供 -BackupRoot（先备份原清单）。
        注册表默认值是外部数据，写入前一律过路径边界校验（§9.4）。
    #>
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$CacheParent,
        [Parameter(Mandatory)][string]$RootPath,
        [string]$BackupRoot,
        [string]$RegistryPath,
        [string]$StateFile,
        [switch]$FixtureMode,
        [switch]$DetectOnly
    )

    $chromeSourceManifest = Get-Content -LiteralPath (Join-Path (Join-Path (Join-Path $SourceRoot 'plugins') 'chrome') '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
    $chromeHostExecutable = Join-Path $CacheParent ("chrome\{0}\extension-host\windows\x64\extension-host.exe" -f $chromeSourceManifest.version)
    if (-not (Test-Path -LiteralPath $chromeHostExecutable)) {
        throw "找不到当前版本的 Chrome native host：$chromeHostExecutable"
    }

    $effectiveRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension'
    if (-not [string]::IsNullOrWhiteSpace($RegistryPath)) { $effectiveRegistryPath = $RegistryPath }

    $entry = [ordered]@{
        browser              = 'chrome'
        registryPath         = $effectiveRegistryPath
        registryValueExisted = $false
        previousManifestPath = $null
        manifestBackup       = $null
        previousTargetPath   = $null
        applied              = $false
    }
    $result = [pscustomobject]@{
        Entry        = $entry
        Updated      = $false
        Drift        = $false
        ManifestPath = $null
        BackupPath   = $null
        Skipped      = $null
    }

    if ($FixtureMode -and [string]::IsNullOrWhiteSpace($StateFile) -and [string]::IsNullOrWhiteSpace($RegistryPath)) {
        # fixture 运行绝不允许改写真实注册表指向的真实清单（本机真有 Chrome 注册项）。
        Write-CodexSyncLog -Phase 'native-host' -Level 'warn' -Fields @{ browser = 'chrome'; result = 'skipped-fixture' }
        $result.Skipped = 'skipped-fixture'
        return $result
    }

    $rawManifestPath = Get-CodexSyncNativeHostRegistryDefault -RegistryPath $effectiveRegistryPath -StateFile $StateFile
    if ($null -eq $rawManifestPath) {
        Write-CodexSyncLog -Phase 'native-host' -Fields @{ browser = 'chrome'; result = 'registry-missing' }
        $result.Skipped = 'registry-missing'
        return $result
    }

    $entry.registryValueExisted = $true
    $allowedRoots = Get-CodexSyncNativeHostAllowedRoots -RootPath $RootPath
    $manifestPath = Assert-CodexSyncNativeHostManifestPath -ManifestPath ([string]$rawManifestPath) -AllowedRoots $allowedRoots
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Chrome native-messaging 清单不存在：$manifestPath"
    }
    $entry.previousManifestPath = $manifestPath
    $result.ManifestPath = $manifestPath

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $entry.previousTargetPath = [string]$manifest.path

    if (Test-CodexSyncSameFinalPath -Left ([string]$manifest.path) -Right $chromeHostExecutable) {
        Write-CodexSyncLog -Phase 'native-host' -Fields @{ browser = 'chrome'; result = 'current'; path = [string]$manifest.path }
        return $result
    }

    $result.Drift = $true
    if ($DetectOnly) {
        Write-CodexSyncLog -Phase 'native-host' -Level 'warn' -Fields @{ browser = 'chrome'; result = 'drift'; expected = $chromeHostExecutable; actual = [string]$manifest.path }
        return $result
    }

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        throw '需要写入 native-messaging 清单但没有可用备份目录。'
    }
    $backupPath = Join-Path $BackupRoot 'com.openai.codexextension.json.previous'
    Copy-Item -LiteralPath $manifestPath -Destination $backupPath
    $entry.manifestBackup = 'com.openai.codexextension.json.previous'
    $result.BackupPath = $backupPath

    $manifest.path = $chromeHostExecutable
    Write-CodexSyncJsonFile -Path $manifestPath -Object $manifest -Depth 10

    $verified = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not (Test-CodexSyncSameFinalPath -Left ([string]$verified.path) -Right $chromeHostExecutable)) {
        throw 'Native Messaging 清单更新后校验失败。'
    }
    $entry.applied = $true
    $result.Updated = $true
    Write-CodexSyncLog -Phase 'native-host' -Fields @{ browser = 'chrome'; result = 'updated'; version = $chromeSourceManifest.version }
    return $result
}

$exitCode = 0
try {
    Write-CodexSyncLog -Phase 'start' -Fields @{ operation = 'sync'; root = $rootPath; efsMode = $EfsMode }

    # ---- 发现 5：并发互斥 -------------------------------------------------
    $lock = Enter-CodexSyncMutex -ScopeKey $rootPath
    Write-CodexSyncLog -Phase 'lock' -Fields @{ acquired = 'true'; name = $lock.Name }

    # ---- 发现 2：上次运行遗留的半切换状态必须按 §6.6 矩阵处理 -------------
    $recovery = Resolve-CodexSyncPendingOperation -RootPath $rootPath
    foreach ($action in $recovery.Actions) {
        Write-CodexSyncLog -Phase 'recovery' -Fields @{ result = 'ok'; detail = $action }
    }
    foreach ($row in $recovery.Rows) {
        Write-CodexSyncLog -Phase 'recovery' -Fields @{ component = $row.Component; row = $row.Row; action = $row.Action; result = $row.Outcome }
    }
    if ($recovery.Status -eq 'attention') {
        foreach ($item in $recovery.NeedsAttention) {
            Write-CodexSyncLog -Phase 'recovery' -Level 'error' -Fields @{ result = 'needs-attention'; detail = $item }
        }
        Write-Host '上次操作遗留的状态无法按矩阵唯一判定，已保留 journal 与快照，本次未写入任何内容。' -ForegroundColor Red
        exitCode = 2
        return
    }
    if ($recovery.Status -ne 'none') {
        Write-CodexSyncLog -Phase 'recovery' -Fields @{ result = $recovery.Status; detail = '按 §6.6 矩阵处理完成' }
    }
    # 'committed' = 上次已交换完成、只差收尾（native host / backup.json）。
    $pendingCommitted = $null
    if ($recovery.Status -eq 'committed') { $pendingCommitted = $recovery.Journal }

    # ---- 源与目标 --------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($SourceBundle)) {
        $codexPackage = Get-AppxPackage -Name 'OpenAI.Codex' |
            Sort-Object Version -Descending |
            Select-Object -First 1
        if ($null -eq $codexPackage) { throw 'OpenAI.Codex 未安装。' }
        $sourceRoot = Join-Path $codexPackage.InstallLocation 'app\resources\plugins\openai-bundled'
        $appxVersion = [string]$codexPackage.Version
    } else {
        $sourceRoot = (Resolve-Path -LiteralPath $SourceBundle).Path.TrimEnd('\')
        $appxVersion = 'fixture'
    }
    Write-CodexSyncLog -Phase 'detected' -Fields @{ source = $sourceRoot; appxVersion = $appxVersion }
    $fixtureMode = -not [string]::IsNullOrWhiteSpace($SourceBundle)

    $marketParent = Join-Path $rootPath '.tmp\bundled-marketplaces'
    $marketBundle = Join-Path $marketParent 'openai-bundled'
    $cacheParent = Join-Path $rootPath 'plugins\cache\openai-bundled'
    $backupParent = Join-Path $rootPath 'backups'
    $managedPlugins = @('browser', 'chrome', 'computer-use')

    foreach ($path in @($sourceRoot, $marketParent)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "必需路径不可用：$path" }
    }
    if (-not (Test-Path -LiteralPath $backupParent -PathType Container)) {
        New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $cacheParent)) {
        New-Item -ItemType Directory -Path $cacheParent -Force | Out-Null
    }

    # ---- 上次同步已完成交换但未收尾：补完 native host 与 backup.json ---------
    if ($null -ne $pendingCommitted) {
        $committedTargets = @($pendingCommitted.targets)
        try {
            foreach ($target in $committedTargets) {
                $liveDigest = Get-CodexSyncTreeDigest -Path $target.livePath
                if (-not (Test-CodexSyncDigestEqual -Left $liveDigest -Right $target.newDigest)) {
                    throw "上次同步的 live 摘要与 newDigest 不一致，需人工处理：$($target.component)"
                }
            }
            $pendingBackupRoot = Split-Path -Parent ([string]$committedTargets[0].backupPath)
            $null = Update-CodexSyncNativeHost -SourceRoot $sourceRoot -CacheParent $cacheParent -RootPath $rootPath `
                -BackupRoot $pendingBackupRoot -RegistryPath $NativeHostRegistryPath -StateFile $NativeHostStateFile `
                -FixtureMode:([bool]$fixtureMode)
            $pendingBackupJson = Join-Path $pendingBackupRoot 'backup.json'
            if (Test-Path -LiteralPath $pendingBackupJson) {
                $pendingManifest = Get-Content -LiteralPath $pendingBackupJson -Raw | ConvertFrom-Json
                $pendingManifest.status = 'complete'
                Write-CodexSyncJsonFile -Path $pendingBackupJson -Object $pendingManifest
            }
            Write-CodexSyncLog -Phase 'recovery' -Fields @{ result = 'finalized'; detail = '上次同步已提交，收尾完成' }
        } catch {
            Write-CodexSyncLog -Phase 'recovery' -Level 'error' -Fields @{ result = 'finalize-failed'; detail = $_.Exception.Message }
            Write-Host "上次同步已提交但收尾失败：$($_.Exception.Message)" -ForegroundColor Red
            $exitCode = 2
            return
        }
    }

    $sourceMap = @(Get-PluginMap -Root $sourceRoot)
    $targetMap = if (Test-Path -LiteralPath $marketBundle) { @(Get-PluginMap -Root $marketBundle) } else { @() }
    $materializedMap = @(Get-MaterializedPluginMap -SourceRoot $sourceRoot -MarketRoot $marketBundle)
    $needsBundleSync = $Force -or (($materializedMap -join "`n") -ne ($targetMap -join "`n"))

    $pluginsRequiringSync = @(
        foreach ($pluginName in $managedPlugins) {
            $sourcePlugin = Join-Path (Join-Path $sourceRoot 'plugins') $pluginName
            if (-not (Test-Path -LiteralPath $sourcePlugin)) {
                throw "内置包中缺少插件 $pluginName"
            }
            $manifest = Get-Content -LiteralPath (Join-Path $sourcePlugin '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
            $cachedManifest = Join-Path (Join-Path (Join-Path $cacheParent $pluginName) $manifest.version) '.codex-plugin\plugin.json'
            if ($Force -or -not (Test-Path -LiteralPath $cachedManifest)) { $pluginName }
        }
    )

    Write-CodexSyncLog -Phase 'planned' -Fields @{
        marketplace = if ($needsBundleSync) { 'sync' } else { 'current' }
        cacheTargets = ($pluginsRequiringSync -join ',')
    }

    $requiresWrite = $needsBundleSync -or ($pluginsRequiringSync.Count -gt 0)
    if (-not $requiresWrite) {
        # 组件都最新时仍复核 native host 指向：漂移只报告（修它需要一份备份，走 --sync -Force）。
        $drift = Update-CodexSyncNativeHost -SourceRoot $sourceRoot -CacheParent $cacheParent -RootPath $rootPath `
            -RegistryPath $NativeHostRegistryPath -StateFile $NativeHostStateFile `
            -FixtureMode:([bool]$fixtureMode) -DetectOnly
        if ($drift.Drift) {
            Write-Host 'Chrome native-messaging 清单指向与当前缓存版本不一致，运行 --sync -Force 可修正。' -ForegroundColor Yellow
        }

        Write-Host '全部组件已是最新，无需写入。' -ForegroundColor Green
        $sourceMap | ForEach-Object { Write-Host "  源: $_" }
        $targetMap | ForEach-Object { Write-Host "  本地: $_" }
        Write-Host "日志：$LogPath"
        $exitCode = 0
        return
    }

    if (-not $SkipProcessCheck) {
        Assert-CodexSyncNoLockingProcess -Action '同步'
    }

    # ---- 发现 3：EFS 只记录、只撤销自己造成的开启（§8）--------------------
    $efsPath = $marketParent
    $efsPreEncrypted = Get-CodexSyncPathEncrypted -Path $marketParent
    if (-not $efsPreEncrypted) {
        if ($EfsMode -eq 'Real') {
            Set-CodexSyncPathEncryption -Path $marketParent -Enabled $true
            $efsEnabledByThisRun = $true
            if (-not (Get-CodexSyncPathEncrypted -Path $marketParent)) {
                throw "marketplace staging 父目录开启 EFS 失败：$marketParent"
            }
        } else {
            Write-CodexSyncLog -Phase 'efs' -Level 'warn' -Fields @{ mode = 'simulate'; preEncrypted = 'false' }
        }
    }
    Write-CodexSyncLog -Phase 'efs' -Fields @{
        preEncrypted       = $efsPreEncrypted
        enabledByThisRun   = $efsEnabledByThisRun
        path               = $marketParent
    }

    $runId = Get-CodexSyncTimestamp -Unique
    $backupRoot = Join-Path $backupParent ("openai-bundled-sync-{0}" -f $runId)
    if (Test-Path -LiteralPath $backupRoot) { throw "备份路径已存在：$backupRoot" }
    New-Item -ItemType Directory -Path $backupRoot | Out-Null

    $backupManifest = [ordered]@{
        schemaVersion = 1
        kind          = 'sync'
        backupId      = $runId
        createdUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        appxVersion   = $appxVersion
        status        = 'partial'
        components    = @()
        nativeHost    = @()
    }

    $journal = New-CodexSyncJournal -OperationKind 'sync' -OperationId $runId
    $journal.efs.preEncrypted = $efsPreEncrypted
    $journal.efs.enabledByThisRun = $efsEnabledByThisRun
    $journal.phase = 'planned'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal
    Write-CodexSyncLog -Phase 'planned' -Fields @{ backup = $backupRoot; journalSeq = $journal.seq }

    # 骨架先落盘：即使随后崩在复制中途，也有确定的回滚落点（§6.3）。
    if ($needsBundleSync) {
        $backupManifest.components += [ordered]@{
            name          = 'marketplace'
            livePath      = $marketBundle
            backupPath    = (Join-Path $backupRoot 'marketplace.previous')
            existedBefore = (Test-Path -LiteralPath $marketBundle)
            state          = 'planned'
            digest         = $null
            previousDigest = $null
        }
    }
    foreach ($pluginName in $pluginsRequiringSync) {
        $backupManifest.components += [ordered]@{
            name          = "cache:$pluginName"
            livePath      = (Join-Path $cacheParent $pluginName)
            backupPath    = (Join-Path $backupRoot ("cache-{0}.previous" -f $pluginName))
            existedBefore = (Test-Path -LiteralPath (Join-Path $cacheParent $pluginName))
            state          = 'planned'
            digest         = $null
            previousDigest = $null
        }
    }
    Write-BackupManifest -Path (Join-Path $backupRoot 'backup.json') -Manifest $backupManifest

    # ---- Staged / Verified：先复制并校验全部内容，再动 live（§6.4）--------
    $stagedComponents = @()
    if ($needsBundleSync) {
        $stagePath = Join-Path $marketParent ("openai-bundled.stage-{0}" -f $runId)
        if (Test-Path -LiteralPath $stagePath) { throw "stage 路径已存在：$stagePath" }
        $stagePaths.Add($stagePath)
        Copy-BundledTree -Source $sourceRoot -Destination $stagePath
        Assert-IdenticalTree -Source $sourceRoot -Destination $stagePath
        Write-CodexSyncLog -Phase 'staged' -Fields @{ component = 'marketplace'; result = 'ok' }
        $stagedComponents += [pscustomobject]@{
            Name       = 'marketplace'
            LivePath   = $marketBundle
            StagePath  = $stagePath
            BackupPath = (Join-Path $backupRoot 'marketplace.previous')
            Existed    = (Test-Path -LiteralPath $marketBundle)
        }
    }

    foreach ($pluginName in $pluginsRequiringSync) {
        $componentName = "cache:$pluginName"
        $sourcePlugin = Join-Path (Join-Path $sourceRoot 'plugins') $pluginName
        $manifest = Get-Content -LiteralPath (Join-Path $sourcePlugin '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
        $version = $manifest.version
        $pluginCache = Join-Path $cacheParent $pluginName

        $staleStages = @(Get-ChildItem -LiteralPath $cacheParent -Directory -Filter "$pluginName.stage-*" -ErrorAction SilentlyContinue)
        foreach ($staleStage in $staleStages) {
            Write-CodexSyncLog -Phase 'stage' -Level 'warn' -Fields @{ component = $componentName; stale = $staleStage.FullName }
        }

        $pluginStage = Join-Path $cacheParent ("{0}.stage-{1}" -f $pluginName, $runId)
        if (Test-Path -LiteralPath $pluginStage) { throw "stage 路径已存在：$pluginStage" }
        $stagePaths.Add($pluginStage)
        $versionStage = Join-Path $pluginStage $version
        New-Item -ItemType Directory -Path $pluginStage | Out-Null
        Copy-BundledTree -Source $sourcePlugin -Destination $versionStage
        Assert-IdenticalTree -Source $sourcePlugin -Destination $versionStage
        Write-CodexSyncLog -Phase 'staged' -Fields @{ component = $componentName; version = $version; result = 'ok' }

        $stagedComponents += [pscustomobject]@{
            Name       = $componentName
            LivePath   = $pluginCache
            StagePath  = $pluginStage
            BackupPath = (Join-Path $backupRoot ("cache-{0}.previous" -f $pluginName))
            Existed    = (Test-Path -LiteralPath $pluginCache)
        }
    }

    $journal.phase = 'verified'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    # ---- CommitStarted：从这里开始只允许前滚或退回原状 -------------------
    $committed = @()
    foreach ($component in $stagedComponents) {
        $null = Invoke-CodexSyncComponentSwap -RootPath $rootPath -Journal $journal `
            -Component $component.Name -LivePath $component.LivePath `
            -StagePath $component.StagePath -BackupPath $component.BackupPath `
            -ExistedBefore $component.Existed
        $committed += $component.Name

        foreach ($entry in $backupManifest.components) {
            if ($entry.name -eq $component.Name) {
                $entry.state = if ($component.Existed) { 'moved' } else { 'created' }
                # digest 描述新的 live 内容；previousDigest 描述备份（同步前）内容，
                # 回滚前必须用它校验备份是否完整（§6.3 / §7.3）。
                $entry.digest = Get-CodexSyncTreeDigest -Path $component.LivePath
                if ($component.Existed) {
                    $entry.previousDigest = Get-CodexSyncTreeDigest -Path $entry.backupPath
                }
            }
        }
        Write-BackupManifest -Path (Join-Path $backupRoot 'backup.json') -Manifest $backupManifest
        Write-CodexSyncLog -Phase 'commit' -Fields @{ component = $component.Name; result = 'swapped' }
        Invoke-CodexSyncFailureInjection -Point ('commit-{0}' -f $committed.Count)
    }

    # ---- Native Messaging：路径边界校验后再写（发现 4 / §9.4）-------------
    $nativeResult = Update-CodexSyncNativeHost -SourceRoot $sourceRoot -CacheParent $cacheParent -RootPath $rootPath `
        -BackupRoot $backupRoot -RegistryPath $NativeHostRegistryPath -StateFile $NativeHostStateFile `
        -FixtureMode:([bool]$fixtureMode)
    $nativeHostUpdated = $nativeResult.Updated
    $nativeHostManifestPath = $nativeResult.ManifestPath
    $nativeHostBackupPath = $nativeResult.BackupPath

    $backupManifest.nativeHost += $nativeResult.Entry
    $journal.nativeHost += @{ browser = 'chrome'; applied = $nativeHostUpdated }
    $journal.phase = 'committed'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    # ---- 逐组件校验：新 live 必须可读，且摘要与暂存时一致 -----------------
    foreach ($entry in $backupManifest.components) {
        if ($entry.state -eq 'planned') { continue }
        $liveDigest = Get-CodexSyncTreeDigest -Path $entry.livePath
        if ($null -eq $liveDigest) { throw "同步后 live 目录不存在：$($entry.livePath)" }
        if (-not (Test-CodexSyncDigestEqual -Left $liveDigest -Right $entry.digest)) {
            throw "同步后 live 摘要与备份记录不一致：$($entry.name)"
        }
    }

    # 清理本次遗留的旧 stage 与更早运行的孤儿 stage。
    foreach ($stale in @(Get-ChildItem -LiteralPath $cacheParent -Directory -Filter '*.stage-*' -ErrorAction SilentlyContinue)) {
        Remove-CodexSyncTree -Path $stale.FullName
    }
    foreach ($stale in @(Get-ChildItem -LiteralPath $marketParent -Directory -Filter 'openai-bundled.stage-*' -ErrorAction SilentlyContinue)) {
        Remove-CodexSyncTree -Path $stale.FullName
    }

    $backupManifest.status = 'complete'
    Write-BackupManifest -Path (Join-Path $backupRoot 'backup.json') -Manifest $backupManifest

    # ---- Finalized -------------------------------------------------------
    $journal.phase = 'finalized'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal
    Remove-CodexSyncJournal -RootPath $rootPath

    $result = foreach ($pluginName in $managedPlugins) {
        $manifestPath = Get-ChildItem -LiteralPath (Join-Path $cacheParent $pluginName) -Filter plugin.json -Force -Recurse -ErrorAction Stop |
            Where-Object { $_.FullName -match '\\.codex-plugin\\plugin\.json$' } |
            Select-Object -First 1
        $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{ Plugin = $pluginName; Version = $manifest.version; Cache = $manifestPath.Directory.Parent.FullName }
    }
    $result | Format-Table -AutoSize | Out-Host

    Write-CodexSyncLog -Phase 'finalized' -Fields @{
        appxVersion = $appxVersion
        marketplace = if ($needsBundleSync) { 'synced' } else { 'current' }
        backup      = $backupRoot
    }
    $exitCode = 0
}
catch {
    $message = $_.Exception.Message
    Write-CodexSyncLog -Phase 'failed' -Level 'error' -Fields @{ detail = $message }

    # 发现 2：commit 之后失败必须把已切换的组件退回原状（与启动恢复同一套矩阵）。
    if ($null -ne $journal -and $journal.phase -eq 'commit-started') {
        try {
            $undo = Repair-CodexSyncOperation -RootPath $rootPath -Journal $journal -Goal Revert
            foreach ($action in $undo.Actions) {
                Write-CodexSyncLog -Phase 'recover' -Fields @{ result = 'reverted'; detail = $action }
            }
            foreach ($row in $undo.Rows) {
                Write-CodexSyncLog -Phase 'recover' -Fields @{ component = $row.Component; row = $row.Row; action = $row.Action; result = $row.Outcome }
            }
            if ($undo.NeedsAttention.Count -gt 0) {
                foreach ($item in $undo.NeedsAttention) {
                    Write-CodexSyncLog -Phase 'recover' -Level 'error' -Fields @{ result = 'needs-attention'; detail = $item }
                }
                $exitCode = 2
            } else {
                if ($null -ne $nativeHostBackupPath -and $nativeHostUpdated -and
                    (Test-Path -LiteralPath $nativeHostBackupPath) -and
                    -not [string]::IsNullOrWhiteSpace($nativeHostManifestPath)) {
                    Copy-Item -LiteralPath $nativeHostBackupPath -Destination $nativeHostManifestPath -Force
                    Write-CodexSyncLog -Phase 'recover' -Fields @{ result = 'reverted'; detail = 'native-host manifest' }
                }
                Remove-CodexSyncJournal -RootPath $rootPath
                $exitCode = 1
                if ($null -ne $backupManifest) {
                    $backupManifest.status = 'verify-failed'
                    try { Write-BackupManifest -Path (Join-Path $backupRoot 'backup.json') -Manifest $backupManifest } catch { }
                }
            }
        } catch {
            Write-CodexSyncLog -Phase 'recover' -Level 'error' -Fields @{ result = 'failed'; detail = $_.Exception.Message }
            $exitCode = 2
        }
    } elseif ($exitCode -eq 0) {
        $exitCode = 1
    }

    if ($exitCode -ne 2) {
        Write-Host "同步失败：$message" -ForegroundColor Red
    }
    Write-Host "保持此窗口并检查日志：$LogPath" -ForegroundColor Red
}
finally {
    # journal 仍在 = 进入 needs-attention：stage 是现场的一部分，不得清理（§6.6 配套约束）。
    $retainStages = Test-Path -LiteralPath (Get-CodexSyncJournalPath -RootPath $rootPath)
    foreach ($stage in $stagePaths) {
        if ($retainStages) {
            Write-CodexSyncLog -Phase 'cleanup' -Level 'warn' -Fields @{ result = 'retained'; path = $stage }
            continue
        }
        try { Remove-CodexSyncTree -Path $stage } catch { }
    }

    # 发现 3：无论成功失败，撤销本次自己的 EFS 开启。
    if ($null -ne $efsPath -and -not [string]::IsNullOrWhiteSpace($efsPath)) {
        try {
            $efsRestoreResult = Restore-CodexSyncEfsState -Path $efsPath -PreEncrypted $efsPreEncrypted -EnabledByThisRun $efsEnabledByThisRun
            if ($null -ne $journal) {
                $journal.efs.restoredUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
            if ($efsRestoreResult.Restored) {
                Write-CodexSyncLog -Phase 'efs' -Fields @{ result = 'restored'; preEncrypted = $efsPreEncrypted; now = $efsRestoreResult.NowEncrypted }
            } else {
                Write-CodexSyncLog -Phase 'efs' -Level 'error' -Fields @{ result = 'needs-attention'; detail = $efsRestoreResult.Message }
                if ($exitCode -eq 0) { $exitCode = 2 }
            }
        } catch {
            Write-CodexSyncLog -Phase 'efs' -Level 'error' -Fields @{ result = 'failed'; detail = $_.Exception.Message }
            if ($exitCode -eq 0) { $exitCode = 2 }
        }
    }

    Exit-CodexSyncMutex -Lock $lock
    try { Stop-Transcript | Out-Null } catch { }
}

if ($exitCode -eq 0) {
    Write-Host '同步完成并通过校验。' -ForegroundColor Green
    Write-Host "日志：$LogPath"
} elseif ($exitCode -eq 2) {
    Write-Host '同步未完成：存在需要人工处理的状态，journal 与快照已保留。' -ForegroundColor Red
    Write-Host "日志：$LogPath"
} else {
    Write-Host '同步未完成，live 已回到操作前状态。' -ForegroundColor Red
    Write-Host "日志：$LogPath"
}
exit $exitCode

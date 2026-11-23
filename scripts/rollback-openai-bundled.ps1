[CmdletBinding()]
param(
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' }),
    # 指定备份 id（backup.json 的 backupId 或目录名）；缺省取最新的 status=complete。
    [string]$BackupId,
    [string]$LogPath,
    # fixture 专用：跳过 Chrome/Codex 进程守护。
    [switch]$SkipProcessCheck,
    # fixture 专用：用工作区内的 JSON 文件代替 HKCU 注册表。
    [string]$NativeHostStateFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib\CodexPluginSync.Common.ps1')

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $rootPath 'logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $LogPath = Join-Path $logDirectory ("rollback-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force | Out-Null
Start-Transcript -LiteralPath $LogPath -Append | Out-Null

$marketParent = Join-Path $rootPath '.tmp\bundled-marketplaces'
$marketBundle = Join-Path $marketParent 'openai-bundled'
$cacheParent = Join-Path $rootPath 'plugins\cache\openai-bundled'
$backupParent = Join-Path $rootPath 'backups'
$managedPlugins = @('browser', 'chrome', 'computer-use')
$chromeRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension'

$lock = $null
$journal = $null
$safetyRoot = $null
$restorePlan = @()
$nativePlan = $null
$exitCode = 0
$stagePaths = New-Object 'System.Collections.Generic.List[string]'

function Get-ComponentLivePath {
    param([Parameter(Mandatory)][string]$Name)
    if ($Name -eq 'marketplace') { return $marketBundle }
    if ($Name.StartsWith('cache:')) { return (Join-Path $cacheParent $Name.Substring(6)) }
    throw "未知组件名：$Name"
}

function Copy-TreeVerified {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    & robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NJH /NJS /NDL /NFL | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy 失败（exit $LASTEXITCODE）：$Source → $Destination"
    }
    # /XJ 不展开 junction；再按原目标重建（真机备份里的 chrome\latest 就是这种）。
    Copy-CodexSyncReparseLinks -Source $Source -Destination $Destination
}

try {
    Write-CodexSyncLog -Phase 'start' -Fields @{ operation = 'rollback'; root = $rootPath }

    # 发现 5：与 sync 共用同一把互斥锁。
    $lock = Enter-CodexSyncMutex -ScopeKey $rootPath

    # 发现 2：上次回滚/同步崩溃遗留的状态先按 §6.6 矩阵处理。
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
        Write-Host '上次操作遗留状态无法自动判定，已保留 journal 与快照，本次未写入任何内容。' -ForegroundColor Red
        $exitCode = 2
        return
    }

    # ---- 1. 选备份：默认取最新的 status=complete（按 backup.json，不按目录名）----
    $backupDir = $null
    $mode = 'manifest'
    if (-not (Test-Path -LiteralPath $backupParent)) {
        throw '没有任何可用备份目录。'
    }

    $candidates = @(Get-ChildItem -LiteralPath $backupParent -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'openai-bundled-sync-*' })

    if (-not [string]::IsNullOrWhiteSpace($BackupId)) {
        $candidates = @($candidates | Where-Object { $_.Name -eq $BackupId -or $_.Name -eq "openai-bundled-sync-$BackupId" })
    }

    $withManifest = @($candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'backup.json') } |
        Sort-Object LastWriteTime -Descending)
    foreach ($candidate in $withManifest) {
        $manifestJson = Get-Content -LiteralPath (Join-Path $candidate.FullName 'backup.json') -Raw | ConvertFrom-Json
        if ($manifestJson.status -eq 'complete') { $backupDir = $candidate; break }
    }

    if ($null -eq $backupDir) {
        # legacy 只读兼容：旧布局没有 backup.json，按 *-previous 子集执行（§7.3）。
        $legacy = @($candidates | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'marketplace.previous')
        } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        if ($legacy.Count -eq 0) {
            throw '没有 status=complete 的备份，也没有可用的 legacy 备份布局。'
        }
        $backupDir = $legacy[0]
        $mode = 'legacy'
    }

    Write-CodexSyncLog -Phase 'backup' -Fields @{ path = $backupDir.FullName; mode = $mode }

    # ---- 2. 构建恢复计划 --------------------------------------------------
    if ($mode -eq 'manifest') {
        $manifestJson = Get-Content -LiteralPath (Join-Path $backupDir.FullName 'backup.json') -Raw | ConvertFrom-Json
        $components = @($manifestJson.components)
        if ($components.Count -eq 0) {
            throw "备份没有记录任何组件改动，不能回滚：$($backupDir.FullName)"
        }
        foreach ($entry in $components) {
            if ($entry.state -eq 'planned') { continue }
            $livePath = Get-ComponentLivePath -Name $entry.name
            $existedBefore = [bool]$entry.existedBefore
            $backupPath = $null
            if ($existedBefore) {
                $backupPath = [string]$entry.backupPath
                if ([string]::IsNullOrWhiteSpace($backupPath)) {
                    $backupPath = Join-Path $backupDir.FullName $(if ($entry.name -eq 'marketplace') { 'marketplace.previous' } else { 'cache-{0}.previous' -f $entry.name.Substring(6) })
                }
            }
            $previousDigest = $null
            if ($null -ne $entry.PSObject.Properties['previousDigest']) {
                $previousDigest = $entry.previousDigest
            }
            $restorePlan += [pscustomobject]@{
                Name          = $entry.name
                LivePath      = $livePath
                BackupPath    = $backupPath
                ExistedBefore = $existedBefore
                Digest        = $previousDigest
            }
        }
        $nativeEntries = @($manifestJson.nativeHost)
        if ($nativeEntries.Count -gt 0) { $nativePlan = $nativeEntries[0] }
    } else {
        $restorePlan += [pscustomobject]@{
            Name = 'marketplace'; LivePath = $marketBundle
            BackupPath = (Join-Path $backupDir.FullName 'marketplace.previous')
            ExistedBefore = $true; Digest = $null
        }
        foreach ($pluginName in $managedPlugins) {
            $path = Join-Path $backupDir.FullName ("cache-{0}.previous" -f $pluginName)
            if (Test-Path -LiteralPath $path) {
                $restorePlan += [pscustomobject]@{
                    Name = "cache:$pluginName"
                    LivePath = (Join-Path $cacheParent $pluginName)
                    BackupPath = $path
                    ExistedBefore = $true
                    Digest = $null
                }
            }
        }
        $nativePlan = [pscustomobject]@{
            registryValueExisted = $true
            manifestBackup       = 'com.openai.codexextension.json.previous'
            previousManifestPath = $null
        }
    }

    if ($restorePlan.Count -eq 0) { throw '恢复计划为空，拒绝执行。' }

    # ---- 3. 逐组件校验备份内容（发现 1：校验在动 live 之前）----------------
    foreach ($item in $restorePlan) {
        if (-not $item.ExistedBefore) { continue }
        if ([string]::IsNullOrWhiteSpace($item.BackupPath) -or -not (Test-Path -LiteralPath $item.BackupPath)) {
            throw "备份不完整：缺少 $($item.BackupPath)（组件 $($item.Name)）"
        }
        if ($null -eq $item.Digest -and $mode -eq 'manifest') {
            # manifest 备份必须带摘要；legacy 布局本来就没有摘要，按 §7.3 只做只读兼容。
            throw "备份没有记录内容摘要，无法校验完整性，拒绝回滚：$($item.Name)"
        }
        if ($null -ne $item.Digest) {
            $backupDigest = Get-CodexSyncTreeDigest -Path $item.BackupPath
            $expected = [pscustomobject]@{
                FileCount = $item.Digest.FileCount
                SizeBytes = $item.Digest.SizeBytes
                TreeHash  = $item.Digest.TreeHash
            }
            if (-not (Test-CodexSyncDigestEqual -Left $backupDigest -Right $expected)) {
                throw "备份内容与记录不一致，拒绝回滚：$($item.Name)（$($item.BackupPath)）"
            }
        }
        Write-CodexSyncLog -Phase 'verify-backup' -Fields @{ component = $item.Name; result = 'ok' }
    }

    if (-not $SkipProcessCheck) {
        Assert-CodexSyncNoLockingProcess -Action '回滚'
    }

    $runId = Get-CodexSyncTimestamp -Unique
    $journal = New-CodexSyncJournal -OperationKind 'rollback' -OperationId $runId
    $journal.phase = 'rollback-preflight'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    # ---- 4. 安全快照：复制 + 校验通过之前不动 live（发现 1）---------------
    $safetyRoot = Join-Path $backupParent ("rollback-before-{0}" -f $runId)
    if (Test-Path -LiteralPath $safetyRoot) { throw "安全快照路径已存在：$safetyRoot" }
    New-Item -ItemType Directory -Path $safetyRoot | Out-Null

    $safetyDigests = @{}
    foreach ($item in $restorePlan) {
        if (-not (Test-Path -LiteralPath $item.LivePath)) { continue }
        $copyPath = Join-Path $safetyRoot ($item.Name -replace ':', '-')
        Copy-TreeVerified -Source $item.LivePath -Destination $copyPath
        $liveDigest = Get-CodexSyncTreeDigest -Path $item.LivePath
        $copyDigest = Get-CodexSyncTreeDigest -Path $copyPath
        if (-not (Test-CodexSyncDigestEqual -Left $liveDigest -Right $copyDigest)) {
            throw "安全快照校验失败，未移动任何 live 内容：$($item.Name)"
        }
        $safetyDigests[$item.Name] = $liveDigest
        Write-CodexSyncLog -Phase 'safety' -Fields @{ component = $item.Name; result = 'verified' }
    }

    $journal.phase = 'rollback-safety-created'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    # ---- 5. 准备恢复内容 + 先写满 targets，再进入 commit-started ----------
    # 顺序很关键：崩溃恢复只能读 journal，所以 stage/safety/摘要必须在第一次动 live
    # 之前就落盘，不能等进程内循环结束才写。
    $planTargets = @()
    foreach ($item in $restorePlan) {
        $stagePath = $null
        $newDigest = $null
        if ($item.ExistedBefore) {
            $parent = Split-Path -Parent $item.LivePath
            $leaf = Split-Path -Leaf $item.LivePath
            $stagePath = Join-Path $parent ("{0}.rollback-stage-{1}" -f $leaf, $runId)
            if (Test-Path -LiteralPath $stagePath) { Remove-CodexSyncTree -Path $stagePath }
            $stagePaths.Add($stagePath)
            Copy-TreeVerified -Source $item.BackupPath -Destination $stagePath
            $newDigest = Get-CodexSyncTreeDigest -Path $stagePath
            if ($null -ne $item.Digest) {
                $expected = [pscustomobject]@{
                    FileCount = $item.Digest.FileCount
                    SizeBytes = $item.Digest.SizeBytes
                    TreeHash  = $item.Digest.TreeHash
                }
                if (-not (Test-CodexSyncDigestEqual -Left $newDigest -Right $expected)) {
                    throw "恢复内容复制后校验失败：$($item.Name)"
                }
            }
        }

        $safetyPath = Join-Path $safetyRoot ($item.Name -replace ':', '-')
        if (-not (Test-Path -LiteralPath $safetyPath)) { $safetyPath = $null }

        $planTargets += @{
            component     = $item.Name
            livePath      = $item.LivePath
            stagePath     = $stagePath
            backupPath    = $item.BackupPath
            safetyPath    = $safetyPath
            existedBefore = $item.ExistedBefore
            oldDigest     = $safetyDigests[$item.Name]
            newDigest     = $newDigest
            swappedOut    = $false
            swappedIn     = $false
        }
    }

    $journal.targets = @($planTargets)
    $journal.phase = 'rollback-commit-started'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    $commitIndex = 0
    foreach ($target in $planTargets) {
        $commitIndex++
        # 有安全快照，live 可以被替换；existedBefore=false 的组件回滚语义是删除。
        if (Test-Path -LiteralPath $target.livePath) {
            Remove-CodexSyncTree -Path $target.livePath
        }
        $target.swappedOut = $true
        Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

        if ($target.existedBefore) {
            Move-Item -LiteralPath $target.stagePath -Destination $target.livePath
            $target.swappedIn = $true
            Write-CodexSyncJournal -RootPath $rootPath -Journal $journal
        }
        Write-CodexSyncLog -Phase 'commit' -Fields @{ component = $target.component; result = 'restored' }
        Invoke-CodexSyncFailureInjection -Point ('rollback-commit-{0}' -f $commitIndex)
    }

    # ---- 6. native host 恢复（§9.4 存在性位 + 路径边界）-------------------
    if ($null -ne $nativePlan) {
        $registryValueExisted = $false
        if ($null -ne $nativePlan.PSObject.Properties['registryValueExisted']) {
            $registryValueExisted = [bool]$nativePlan.registryValueExisted
        }
        $manifestBackupName = 'com.openai.codexextension.json.previous'
        if ($null -ne $nativePlan.PSObject.Properties['manifestBackup'] -and
            -not [string]::IsNullOrWhiteSpace([string]$nativePlan.manifestBackup)) {
            $manifestBackupName = [string]$nativePlan.manifestBackup
        }
        $manifestBackupPath = Join-Path $backupDir.FullName $manifestBackupName

        if (-not $registryValueExisted) {
            if ($null -ne (Get-CodexSyncNativeHostRegistryDefault -RegistryPath $chromeRegistryPath -StateFile $NativeHostStateFile)) {
                Remove-CodexSyncNativeHostRegistryKey -RegistryPath $chromeRegistryPath -StateFile $NativeHostStateFile
                Write-CodexSyncLog -Phase 'native-host' -Fields @{ result = 'registry-removed' }
            }
        } elseif (Test-Path -LiteralPath $manifestBackupPath) {
            $allowedRoots = Get-CodexSyncNativeHostAllowedRoots -RootPath $rootPath
            $targetManifest = $null
            if ($null -ne $nativePlan.PSObject.Properties['previousManifestPath']) {
                $targetManifest = [string]$nativePlan.previousManifestPath
            }
            if ([string]::IsNullOrWhiteSpace($targetManifest)) {
                # 清单路径没记录时，从当前注册表值取，但同样要过边界校验。
                $currentDefault = Get-CodexSyncNativeHostRegistryDefault -RegistryPath $chromeRegistryPath -StateFile $NativeHostStateFile
                if ([string]::IsNullOrWhiteSpace($currentDefault)) {
                    throw '注册表值记存在但已消失，且备份没有记录清单路径，无法恢复 native host。'
                }
                $targetManifest = [string]$currentDefault
            }
            $targetManifest = Assert-CodexSyncNativeHostManifestPath -ManifestPath $targetManifest -AllowedRoots $allowedRoots
            Copy-Item -LiteralPath $manifestBackupPath -Destination $targetManifest -Force
            Write-CodexSyncLog -Phase 'native-host' -Fields @{ result = 'restored'; path = $targetManifest }
        } else {
            Write-CodexSyncLog -Phase 'native-host' -Level 'warn' -Fields @{ result = 'no-backup'; detail = '注册表值存在但没有清单备份，跳过' }
        }
    }

    # ---- 7. 逐组件校验（目录可读 + native host 已还原）--------------------
    foreach ($item in $restorePlan) {
        if (-not $item.ExistedBefore) { continue }
        $manifest = Get-ChildItem -LiteralPath $item.LivePath -Filter 'plugin.json' -Force -Recurse |
            Where-Object { $_.FullName -match '\\.codex-plugin\\plugin\.json$' } |
            Select-Object -First 1
        if ($null -eq $manifest) {
            throw "回滚校验失败：$($item.Name) 的 plugin.json 不可读。"
        }
    }

    $journal.phase = 'rollback-committed'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal

    $safetyManifest = [ordered]@{
        schemaVersion = 1
        kind          = 'rollback-safety'
        backupId      = $runId
        createdUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        sourceBackup  = $backupDir.FullName
        status        = 'complete'
        components    = @($restorePlan | ForEach-Object {
            [ordered]@{
                name          = $_.Name
                livePath      = $_.LivePath
                backupPath    = (Join-Path $safetyRoot ($_.Name -replace ':', '-'))
                existedBefore = (Test-Path -LiteralPath (Join-Path $safetyRoot ($_.Name -replace ':', '-')))
                state         = 'moved'
                digest        = $safetyDigests[$_.Name]
            }
        })
    }
    Write-CodexSyncJsonFile -Path (Join-Path $safetyRoot 'backup.json') -Object $safetyManifest

    $journal.phase = 'rollback-finalized'
    Write-CodexSyncJournal -RootPath $rootPath -Journal $journal
    Remove-CodexSyncJournal -RootPath $rootPath

    Write-CodexSyncLog -Phase 'finalized' -Fields @{ restoredFrom = $backupDir.FullName; safety = $safetyRoot }
    $exitCode = 0
}
catch {
    $message = $_.Exception.Message
    Write-CodexSyncLog -Phase 'failed' -Level 'error' -Fields @{ detail = $message }

    # 回滚自身失败：退回安全快照。判定完全依赖 journal + 摘要（§6.6），
    # 不依赖进程内列表，所以进程崩溃后由下次启动的同一矩阵接手。
    if ($null -ne $journal -and $journal.phase -eq 'rollback-commit-started') {
        try {
            $repair = Repair-CodexSyncOperation -RootPath $rootPath -Journal $journal -Goal Revert
            foreach ($action in $repair.Actions) {
                Write-CodexSyncLog -Phase 'recover' -Fields @{ result = 'reverted'; detail = $action }
            }
            foreach ($row in $repair.Rows) {
                Write-CodexSyncLog -Phase 'recover' -Fields @{ component = $row.Component; row = $row.Row; action = $row.Action; result = $row.Outcome }
            }
            if ($repair.Status -eq 'attention') {
                foreach ($item in $repair.NeedsAttention) {
                    Write-CodexSyncLog -Phase 'recover' -Level 'error' -Fields @{ result = 'needs-attention'; detail = $item }
                }
                $exitCode = 2
            } else {
                $exitCode = 1
            }
        } catch {
            Write-CodexSyncLog -Phase 'recover' -Level 'error' -Fields @{ result = 'failed'; detail = $_.Exception.Message }
            $exitCode = 2
        }
    } elseif ($exitCode -eq 0) {
        $exitCode = 1
    }

    if ($exitCode -ne 2) {
        Write-Host "回滚失败：$message" -ForegroundColor Red
        if ($null -ne $safetyRoot -and (Test-Path -LiteralPath $safetyRoot)) {
            Write-Host "回滚前的当前状态保留在：$safetyRoot" -ForegroundColor Yellow
        }
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

    if (Test-Path -LiteralPath $marketParent) {
        try {
            $efsState = Get-CodexSyncPathEncrypted -Path $marketParent
            Write-CodexSyncLog -Phase 'efs' -Fields @{ path = $marketParent; encrypted = $efsState; result = 'observed' }
        } catch { }
    }

    Exit-CodexSyncMutex -Lock $lock
    try { Stop-Transcript | Out-Null } catch { }
}

if ($exitCode -eq 0) {
    Write-Host '回滚完成并通过校验。' -ForegroundColor Green
    Write-Host "回滚前状态保留在：$safetyRoot"
} elseif ($exitCode -eq 2) {
    Write-Host '回滚未完成：存在需要人工处理的状态，journal 与快照已保留。' -ForegroundColor Red
} else {
    Write-Host '回滚未完成，live 已回到回滚前状态。' -ForegroundColor Red
}
Write-Host "日志：$LogPath"
exit $exitCode

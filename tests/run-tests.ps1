#requires -Version 5.1
<#
  CodexPluginSync 脚本层测试（无外部依赖，不使用 Pester）。

  覆盖 docs/DESIGN.md §16.1 的 fixture 思路与本次审查中发现的失败场景：
    - 备份只记录本次实际改动的组件，回滚按快照执行（不再要求四类备份齐全）
    - 回滚先校验备份内容、再动 live；内容不符直接拒绝
    - 同步中途失败把已切换组件退回原状
    - 崩溃遗留 journal 的恢复 / needs-attention 判定
    - EFS 只撤销本次自己的开启，失败不静默
    - Native Messaging 注册表路径边界
    - 并发互斥

  用法:
    powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
    powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Filter rollback
    powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -KeepWork

  EFS 相关用例使用注入替身，只验证状态机；真机 EFS 行为按 §8 必须在真机验收，
  fixture 结果不构成 EFS 验收依据。
#>
[CmdletBinding()]
param(
    [switch]$KeepWork,
    [string]$Filter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:repoRoot = Split-Path -Parent $PSScriptRoot
$script:scriptRoot = Join-Path $script:repoRoot 'scripts'
$script:syncScript = Join-Path $script:scriptRoot 'sync-openai-bundled.ps1'
$script:rollbackScript = Join-Path $script:scriptRoot 'rollback-openai-bundled.ps1'
$script:work = Join-Path $PSScriptRoot '.work'
$script:powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:passed = 0
$script:failed = 0
$script:failures = New-Object 'System.Collections.Generic.List[string]'
$script:efsState = @{ encrypted = $false }

. (Join-Path $script:scriptRoot 'lib\CodexPluginSync.Common.ps1')

# ---------------------------------------------------------------------------
# 断言
# ---------------------------------------------------------------------------

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-False {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if ($Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [Parameter(Mandatory)][string]$Message)
    if ($Expected -ne $Actual) { throw "$Message（期望=$Expected 实际=$Actual）" }
}

function Assert-Throws {
    param([scriptblock]$Body, [string]$MessagePattern, [Parameter(Mandatory)][string]$Message)
    $thrown = $null
    try { & $Body } catch { $thrown = $_.Exception.Message }
    if ($null -eq $thrown) { throw "$Message（未抛出异常）" }
    if (-not [string]::IsNullOrWhiteSpace($MessagePattern) -and $thrown -notlike "*$MessagePattern*") {
        throw "$Message（异常信息不匹配：$thrown）"
    }
}

# ---------------------------------------------------------------------------
# fixture 与调用
# ---------------------------------------------------------------------------

function Write-Utf8File {
    param([Parameter(Mandatory)][string]$Path, [AllowEmptyString()][string]$Text)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function New-Fixture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Version = '1.0.0'
    )

    $case = Join-Path $script:work $Name
    if (Test-Path -LiteralPath $case) { Remove-Item -LiteralPath $case -Recurse -Force }
    $codexHome = Join-Path $case 'codexhome'
    $source = Join-Path $case 'src\openai-bundled'

    foreach ($dir in @(
        (Join-Path $codexHome '.tmp\bundled-marketplaces'),
        (Join-Path $codexHome 'plugins\cache\openai-bundled'),
        (Join-Path $codexHome 'backups'),
        (Join-Path $codexHome 'native-host')
    )) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    # fixture 的 native-messaging 清单与"注册表"都在 fixture 内，绝不碰真实 HKCU。
    $nativeManifest = Join-Path $codexHome 'native-host\com.openai.codexextension.json'
    $nativeState = Join-Path $codexHome 'native-host-state.json'
    Write-Utf8File -Path $nativeManifest -Text ([ordered]@{
        name            = 'com.openai.codexextension'
        description     = 'fixture native messaging host'
        type            = 'stdio'
        allowed_origins = @('chrome-extension://fixture/')
        path            = (Join-Path $codexHome 'plugins\cache\openai-bundled\chrome\0.9.0\extension-host\windows\x64\extension-host.exe')
    } | ConvertTo-Json -Depth 5)
    Write-Utf8File -Path $nativeState -Text ([ordered]@{ defaultPath = $nativeManifest } | ConvertTo-Json)

    $plugins = @('browser', 'chrome', 'computer-use')
    foreach ($plugin in $plugins) {
        $pluginDir = Join-Path $source "plugins\$plugin"
        Write-Utf8File -Path (Join-Path $pluginDir '.codex-plugin\plugin.json') -Text ('{{"name":"{0}","version":"{1}"}}' -f $plugin, $Version)
        Write-Utf8File -Path (Join-Path $pluginDir 'README.md') -Text ("bundled {0} {1}" -f $plugin, $Version)
    }
    Write-Utf8File -Path (Join-Path $source 'plugins\chrome\extension-host\windows\x64\extension-host.exe') -Text 'fake extension host'

    $manifest = [ordered]@{
        plugins = @($plugins | ForEach-Object { [ordered]@{ name = $_ } })
    }
    Write-Utf8File -Path (Join-Path $source '.agents\plugins\marketplace.json') -Text ($manifest | ConvertTo-Json -Depth 5)

    return [pscustomobject]@{
        Case   = $case
        Home   = $codexHome
        Source = $source
        Cache  = (Join-Path $codexHome 'plugins\cache\openai-bundled')
        Market = (Join-Path $codexHome '.tmp\bundled-marketplaces\openai-bundled')
        Backups = (Join-Path $codexHome 'backups')
        NativeManifest = $nativeManifest
        NativeState = $nativeState
    }
}

function Invoke-ScriptFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ScriptArguments
    )

    $output = & $script:powerShellExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Path @ScriptArguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = (@($output) -join "`n")
    }
}

function Invoke-Sync {
    param(
        [Parameter(Mandatory)]$Fixture,
        [switch]$Force,
        [switch]$NoNativeHostState
    )
    $arguments = @('-Root', $Fixture.Home, '-SourceBundle', $Fixture.Source, '-EfsMode', 'Simulate', '-SkipProcessCheck')
    if (-not $NoNativeHostState) { $arguments += @('-NativeHostStateFile', $Fixture.NativeState) }
    if ($Force) { $arguments += '-Force' }
    return Invoke-ScriptFile -Path $script:syncScript -ScriptArguments $arguments
}

function Invoke-Rollback {
    param(
        [Parameter(Mandatory)]$Fixture,
        [string]$BackupId
    )
    $arguments = @('-Root', $Fixture.Home, '-SkipProcessCheck', '-NativeHostStateFile', $Fixture.NativeState)
    if (-not [string]::IsNullOrWhiteSpace($BackupId)) { $arguments += @('-BackupId', $BackupId) }
    return Invoke-ScriptFile -Path $script:rollbackScript -ScriptArguments $arguments
}

function Seed-Cache {
    param(
        [Parameter(Mandatory)]$Fixture,
        [Parameter(Mandatory)][string]$Plugin,
        [string]$Version = '0.9.0',
        [string]$Marker = 'old'
    )
    $dir = Join-Path $Fixture.Cache $Plugin
    Write-Utf8File -Path (Join-Path $dir ("{0}\.codex-plugin\plugin.json" -f $Version)) -Text ('{{"name":"{0}","version":"{1}"}}' -f $Plugin, $Version)
    Write-Utf8File -Path (Join-Path $dir ("{0}\{1}-{2}.txt" -f $Version, $Marker, $Plugin)) -Text $Marker
}

function Seed-Marketplace {
    param([Parameter(Mandatory)]$Fixture, [string]$Marker = 'old-market', [string]$Version = '0.9.0')
    Write-Utf8File -Path (Join-Path $Fixture.Market '.codex-plugin\plugin.json') -Text '{"name":"marketplace","version":"0.9.0"}'
    Write-Utf8File -Path (Join-Path $Fixture.Market ("{0}.txt" -f $Marker)) -Text $Marker
    foreach ($plugin in @('browser', 'chrome', 'computer-use')) {
        Write-Utf8File -Path (Join-Path $Fixture.Market ("plugins\{0}\.codex-plugin\plugin.json" -f $plugin)) -Text ('{{"name":"{0}","version":"{1}"}}' -f $plugin, $Version)
    }
}

function Get-LatestBackupDir {
    param([Parameter(Mandatory)]$Fixture)
    return @(Get-ChildItem -LiteralPath $Fixture.Backups -Directory -Filter 'openai-bundled-sync-*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
}

function Get-FixtureDigest {
    param([Parameter(Mandatory)][string]$Path)
    return Get-CodexSyncTreeDigest -Path $Path
}

# ---------------------------------------------------------------------------
# 用例
# ---------------------------------------------------------------------------

$cases = New-Object 'System.Collections.Generic.List[object]'

function Add-Case {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    $cases.Add([pscustomobject]@{ Name = $Name; Body = $Body })
}

Add-Case -Name 'sync-creates-complete-backup' -Body {
    $fixture = New-Fixture 'sync-basic'
    Seed-Cache -Fixture $fixture -Plugin 'browser'
    Seed-Cache -Fixture $fixture -Plugin 'chrome'
    Seed-Cache -Fixture $fixture -Plugin 'computer-use'
    Seed-Marketplace -Fixture $fixture

    $result = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $result.ExitCode "同步应成功：$($result.Output)"

    $backup = @(Get-LatestBackupDir -Fixture $fixture)
    Assert-Equal 1 $backup.Count '应产生一个备份目录'
    $manifest = Get-Content -LiteralPath (Join-Path $backup[0].FullName 'backup.json') -Raw | ConvertFrom-Json
    Assert-Equal 'complete' $manifest.status '备份状态必须是 complete'
    Assert-Equal 4 @($manifest.components).Count '四个组件都应被记录'

    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '成功后 journal 必须删除'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $fixture.Cache -Directory -Filter '*.stage-*' -ErrorAction SilentlyContinue).Count '不应留下 stage 目录'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Cache 'browser\1.0.0\.codex-plugin\plugin.json')) '缓存应切换到 1.0.0'

    $nativeManifest = Get-Content -LiteralPath $fixture.NativeManifest -Raw | ConvertFrom-Json
    Assert-True ($nativeManifest.path -like '*chrome\1.0.0\extension-host\windows\x64\extension-host.exe') "native host 应指向 1.0.0：$($nativeManifest.path)"
    Assert-True (Test-Path -LiteralPath (Join-Path $backup[0].FullName 'com.openai.codexextension.json.previous')) '必须备份原 native host 清单'
}

Add-Case -Name 'sync-second-run-is-noop' -Body {
    $fixture = New-Fixture 'sync-noop'
    Seed-Marketplace -Fixture $fixture
    $first = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $first.ExitCode "首次同步应成功：$($first.Output)"

    $before = @(Get-LatestBackupDir -Fixture $fixture).Count
    $second = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $second.ExitCode "第二次同步应成功：$($second.Output)"
    Assert-Equal $before @(Get-LatestBackupDir -Fixture $fixture).Count '无变化时不应产生新备份'
    Assert-True ($second.Output -like '*无需写入*') '无变化时应明确说明未写入'
}

Add-Case -Name 'sync-backup-records-only-changed-components' -Body {
    $fixture = New-Fixture 'sync-partial'
    Seed-Marketplace -Fixture $fixture
    $first = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $first.ExitCode "首次同步应成功：$($first.Output)"

    # 只让 browser 缓存缺失，其余保持最新。
    Remove-Item -LiteralPath (Join-Path $fixture.Cache 'browser') -Recurse -Force
    $second = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $second.ExitCode "增量同步应成功：$($second.Output)"

    $backup = @(Get-LatestBackupDir -Fixture $fixture)[0]
    $manifest = Get-Content -LiteralPath (Join-Path $backup.FullName 'backup.json') -Raw | ConvertFrom-Json
    Assert-Equal 'complete' $manifest.status '增量备份也必须是 complete'
    Assert-Equal 1 @($manifest.components).Count '备份必须只记录本次实际改动的组件'
    Assert-Equal 'cache:browser' @($manifest.components)[0].name '记录的组件应为 cache:browser'
}

Add-Case -Name 'rollback-restores-pre-sync-content' -Body {
    $fixture = New-Fixture 'rollback-basic'
    Seed-Cache -Fixture $fixture -Plugin 'browser' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'chrome' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'computer-use' -Marker 'old'
    Seed-Marketplace -Fixture $fixture -Marker 'old-market'

    $before = @{
        browser = Get-FixtureDigest -Path (Join-Path $fixture.Cache 'browser')
        market  = Get-FixtureDigest -Path $fixture.Market
    }

    $sync = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $sync.ExitCode "同步应成功：$($sync.Output)"
    Write-Utf8File -Path (Join-Path $fixture.Cache 'browser\1.0.0\dirty.txt') -Text 'codex wrote this'

    $rollback = Invoke-Rollback -Fixture $fixture
    Assert-Equal 0 $rollback.ExitCode "回滚应成功：$($rollback.Output)"

    $after = Get-FixtureDigest -Path (Join-Path $fixture.Cache 'browser')
    Assert-Equal $before.browser.TreeHash $after.TreeHash '缓存必须回到同步前内容'
    Assert-Equal $before.market.TreeHash (Get-FixtureDigest -Path $fixture.Market).TreeHash 'Marketplace 必须回到同步前内容'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Cache 'browser\0.9.0\old-browser.txt')) '同步前的文件必须还在'

    $nativeAfter = Get-Content -LiteralPath $fixture.NativeManifest -Raw | ConvertFrom-Json
    Assert-True ($nativeAfter.path -like '*chrome\0.9.0\extension-host\windows\x64\extension-host.exe') "回滚后 native host 应指回原路径：$($nativeAfter.path)"
    Assert-True ($nativeAfter.path -notlike '*1.0.0*') '目录回去了但注册表还指着新版 = 混合态，不允许'

    $safety = @(Get-ChildItem -LiteralPath $fixture.Backups -Directory -Filter 'rollback-before-*')
    Assert-Equal 1 $safety.Count '应保留回滚安全快照'
    Assert-True (Test-Path -LiteralPath (Join-Path $safety[0].FullName 'backup.json')) '安全快照必须带 backup.json'
    $dirtyFound = @(Get-ChildItem -LiteralPath $safety[0].FullName -Recurse -Filter 'dirty.txt' -ErrorAction SilentlyContinue)
    Assert-Equal 1 $dirtyFound.Count '回滚前的当前内容必须留在安全快照里'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '回滚完成后 journal 必须删除'
}

Add-Case -Name 'rollback-refuses-corrupt-backup' -Body {
    $fixture = New-Fixture 'rollback-corrupt'
    Seed-Marketplace -Fixture $fixture
    Seed-Cache -Fixture $fixture -Plugin 'browser'
    Seed-Cache -Fixture $fixture -Plugin 'chrome'
    Seed-Cache -Fixture $fixture -Plugin 'computer-use'
    $sync = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $sync.ExitCode "同步应成功：$($sync.Output)"

    # 篡改备份内容，模拟备份被外部改动/损坏。
    $backup = @(Get-LatestBackupDir -Fixture $fixture)[0]
    Write-Utf8File -Path (Join-Path $backup.FullName 'cache-browser.previous\tampered.txt') -Text 'tampered'
    $liveBefore = Get-FixtureDigest -Path (Join-Path $fixture.Cache 'browser')

    $rollback = Invoke-Rollback -Fixture $fixture
    Assert-True ($rollback.ExitCode -ne 0) '备份内容不符时必须拒绝回滚'
    Assert-Equal $liveBefore.TreeHash (Get-FixtureDigest -Path (Join-Path $fixture.Cache 'browser')).TreeHash '拒绝回滚时 live 必须原样未动'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $fixture.Backups -Directory -Filter 'rollback-before-*' -ErrorAction SilentlyContinue).Count '拒绝回滚时不应建安全快照'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '预检失败时不应留下 journal'
}

Add-Case -Name 'rollback-single-component-backup' -Body {
    $fixture = New-Fixture 'rollback-single'
    Seed-Marketplace -Fixture $fixture
    $first = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $first.ExitCode "首次同步应成功：$($first.Output)"

    Remove-Item -LiteralPath (Join-Path $fixture.Cache 'browser') -Recurse -Force
    $second = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $second.ExitCode "增量同步应成功：$($second.Output)"

    $marketBefore = Get-FixtureDigest -Path $fixture.Market
    $rollback = Invoke-Rollback -Fixture $fixture
    Assert-Equal 0 $rollback.ExitCode "单项备份回滚应成功：$($rollback.Output)"
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Cache 'browser')) '本次新建的组件回滚语义是删除'
    Assert-Equal $marketBefore.TreeHash (Get-FixtureDigest -Path $fixture.Market).TreeHash '未涉及的组件不应被改动'
}

Add-Case -Name 'rollback-legacy-backup-without-four-components' -Body {
    $fixture = New-Fixture 'rollback-legacy'
    Seed-Marketplace -Fixture $fixture -Marker 'current-market'
    Seed-Cache -Fixture $fixture -Plugin 'browser' -Marker 'current'

    $legacy = Join-Path $fixture.Backups 'openai-bundled-sync-20000101-000000'
    Write-Utf8File -Path (Join-Path $legacy 'marketplace.previous\.codex-plugin\plugin.json') -Text '{"name":"marketplace","version":"0.5.0"}'
    Write-Utf8File -Path (Join-Path $legacy 'marketplace.previous\legacy.txt') -Text 'legacy'

    $rollback = Invoke-Rollback -Fixture $fixture
    Assert-Equal 0 $rollback.ExitCode "legacy 备份应可回滚：$($rollback.Output)"
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Market 'legacy.txt')) 'Marketplace 应恢复为备份内容'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Cache 'browser\0.9.0\current-browser.txt')) '备份未涉及的缓存不应被删除'
}

Add-Case -Name 'sync-mid-commit-failure-reverts-everything' -Body {
    $fixture = New-Fixture 'sync-fail-mid'
    Seed-Cache -Fixture $fixture -Plugin 'browser' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'chrome' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'computer-use' -Marker 'old'
    Seed-Marketplace -Fixture $fixture -Marker 'old-market'

    $digests = @{}
    foreach ($name in @('marketplace', 'browser', 'chrome', 'computer-use')) {
        $path = if ($name -eq 'marketplace') { $fixture.Market } else { Join-Path $fixture.Cache $name }
        $digests[$name] = (Get-FixtureDigest -Path $path).TreeHash
    }

    $env:CODEXSYNC_TEST_FAIL_AT = 'commit-2'
    try { $result = Invoke-Sync -Fixture $fixture } finally { $env:CODEXSYNC_TEST_FAIL_AT = '' }
    Assert-Equal 1 $result.ExitCode "注入失败后应返回 1：$($result.Output)"

    foreach ($name in @('marketplace', 'browser', 'chrome', 'computer-use')) {
        $path = if ($name -eq 'marketplace') { $fixture.Market } else { Join-Path $fixture.Cache $name }
        Assert-Equal $digests[$name] (Get-FixtureDigest -Path $path).TreeHash "$name 必须退回同步前内容"
    }
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '退回成功后 journal 必须删除'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $fixture.Cache -Directory -Filter '*.stage-*' -ErrorAction SilentlyContinue).Count '失败后不应留下 stage 目录'

    $backup = @(Get-LatestBackupDir -Fixture $fixture)[0]
    $manifest = Get-Content -LiteralPath (Join-Path $backup.FullName 'backup.json') -Raw | ConvertFrom-Json
    Assert-Equal 'verify-failed' $manifest.status '退回后的备份必须标记为 verify-failed'
}

Add-Case -Name 'sync-first-component-failure-reverts-marketplace' -Body {
    $fixture = New-Fixture 'sync-fail-first'
    Seed-Marketplace -Fixture $fixture -Marker 'old-market'
    $marketBefore = (Get-FixtureDigest -Path $fixture.Market).TreeHash

    $env:CODEXSYNC_TEST_FAIL_AT = 'commit-1'
    try { $result = Invoke-Sync -Fixture $fixture } finally { $env:CODEXSYNC_TEST_FAIL_AT = '' }
    Assert-Equal 1 $result.ExitCode "注入失败后应返回 1：$($result.Output)"
    Assert-Equal $marketBefore (Get-FixtureDigest -Path $fixture.Market).TreeHash '第一个组件切换后失败也必须退回'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Market 'old-market.txt')) '旧内容必须还在'
}

Add-Case -Name 'journal-recovery-restores-missing-live' -Body {
    $fixture = New-Fixture 'recover-missing-live'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath '0.9.0\old.txt') -Text 'old'

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Copy-Item -LiteralPath $livePath -Destination $backupPath -Recurse
    $oldDigest = Get-FixtureDigest -Path $backupPath

    $scratch = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratch 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $scratch

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component     = 'cache:browser'
        livePath      = $livePath
        stagePath     = (Join-Path $fixture.Cache 'browser.stage-crash')
        backupPath    = $backupPath
        existedBefore = $true
        oldDigest     = $oldDigest
        newDigest     = $newDigest
        swappedOut    = $true
        swappedIn     = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal
    Remove-Item -LiteralPath $livePath -Recurse -Force

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'recovered' $recovery.Status '应识别为已恢复'
    Assert-True (Test-Path -LiteralPath (Join-Path $livePath '0.9.0\old.txt')) 'live 必须从备份恢复'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '恢复后 journal 必须删除'
}

Add-Case -Name 'journal-unknown-digest-needs-attention' -Body {
    $fixture = New-Fixture 'recover-attention'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'unknown.txt') -Text 'who wrote this'

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'old.txt') -Text 'old'

    $scratch = Join-Path $fixture.Case 'scratch-old'
    Write-Utf8File -Path (Join-Path $scratch 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $scratch
    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $scratchNew

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component     = 'cache:browser'
        livePath      = $livePath
        stagePath     = $null
        backupPath    = $backupPath
        existedBefore = $true
        oldDigest     = $oldDigest
        newDigest     = $newDigest
        swappedOut    = $true
        swappedIn     = $true
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status '摘要都不匹配必须判为 needs-attention'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'needs-attention 必须保留 journal'
    Assert-True (Test-Path -LiteralPath (Join-Path $livePath 'unknown.txt')) 'needs-attention 不得擅自删改 live'
}

Add-Case -Name 'recovery-row3-backup-generated-live-untouched' -Body {
    # §6.6 行 3：live=旧、backup=旧、无 stage → 保留 live 与回滚点
    $fixture = New-Fixture 'recovery-row3'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Copy-Item -LiteralPath $livePath -Destination $backupPath -Recurse

    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = (Get-FixtureDigest -Path $scratchNew)
        swappedOut = $true; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'recovered' $recovery.Status '行 3 应回到已恢复终态'
    Assert-Equal $oldDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 必须保持同步前内容'
    Assert-True (Test-Path -LiteralPath $backupPath) '回滚点必须保留'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'journal 应被清理'
}

Add-Case -Name 'recovery-row8-live-missing-restores-backup' -Body {
    $fixture = New-Fixture 'recovery-row8'
    $livePath = Join-Path $fixture.Cache 'browser'
    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $backupPath

    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = (Get-FixtureDigest -Path $scratchNew)
        swappedOut = $true; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'recovered' $recovery.Status '行 8 应把备份回填 live'
    Assert-Equal $oldDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 必须由备份恢复'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'journal 应被清理'
}

Add-Case -Name 'recovery-row11-corrupt-backup-needs-attention' -Body {
    $fixture = New-Fixture 'recovery-row11'
    $livePath = Join-Path $fixture.Cache 'browser'
    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'partial.txt') -Text 'partial copy only'

    $scratchOld = Join-Path $fixture.Case 'scratch-old'
    Write-Utf8File -Path (Join-Path $scratchOld 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $scratchOld
    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = (Get-FixtureDigest -Path $scratchNew)
        swappedOut = $true; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status '备份内容与 oldDigest 不一致必须拒绝自动恢复'
    Assert-False (Test-Path -LiteralPath $livePath) '不得把不完整的备份回填到 live'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
    Assert-True (Test-Path -LiteralPath $backupPath) 'attention 必须保留备份现场'
}

Add-Case -Name 'recovery-row9-nothing-left-needs-attention' -Body {
    $fixture = New-Fixture 'recovery-row9'
    $livePath = Join-Path $fixture.Cache 'browser'
    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'

    $scratchOld = Join-Path $fixture.Case 'scratch-old'
    Write-Utf8File -Path (Join-Path $scratchOld 'old.txt') -Text 'old'
    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null
        backupPath = (Join-Path $backupRoot 'cache-browser.previous')
        existedBefore = $true; oldDigest = (Get-FixtureDigest -Path $scratchOld); newDigest = (Get-FixtureDigest -Path $scratchNew)
        swappedOut = $true; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status '行 9（三份都不存在）不得自动处理'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
}

Add-Case -Name 'recovery-row6-committed-clears-stage' -Body {
    $fixture = New-Fixture 'recovery-row6'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $backupPath

    $stagePath = Join-Path $fixture.Cache 'browser.stage-crash'
    Copy-Item -LiteralPath $livePath -Destination $stagePath -Recurse

    Write-Utf8File -Path (Join-Path $backupRoot 'backup.json') -Text '{"status":"partial"}'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $stagePath; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
        swappedOut = $true; swappedIn = $true
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'committed' $recovery.Status '行 6 应判为已提交、只差收尾'
    Assert-False (Test-Path -LiteralPath $stagePath) '残留 stage 必须清理'
    Assert-Equal $newDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash '新内容必须保留'
    Assert-True (Test-Path -LiteralPath $backupPath) '回滚点必须保留'
}

Add-Case -Name 'recovery-row10-full-set-deletes-stage-keeps-backup' -Body {
    # §6.6 行 10：live=旧、backup=旧、stage=完整新树 → 删 stage、保留 backup，终态 Aborted
    $fixture = New-Fixture 'recovery-row10'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Copy-Item -LiteralPath $livePath -Destination $backupPath -Recurse

    $stagePath = Join-Path $fixture.Cache 'browser.stage-crash'
    Write-Utf8File -Path (Join-Path $stagePath '1.0.0\new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $stagePath

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $stagePath; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
        swappedOut = $false; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'aborted' $recovery.Status '行 10 的终态是 Aborted（未交换）'
    Assert-False (Test-Path -LiteralPath $stagePath) '行 10 必须删除 stage'
    Assert-True (Test-Path -LiteralPath $backupPath) '行 10 必须保留 backup 作回滚点'
    Assert-Equal $oldDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 不得被动'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '可唯一判定时 journal 应清理'
}

Add-Case -Name 'recovery-incomplete-stage-needs-attention' -Body {
    # stage 存在但摘要不等于 newDigest（只拷了一半）：矩阵不覆盖 → attention，且 stage 必须保留
    $fixture = New-Fixture 'recovery-bad-stage'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Copy-Item -LiteralPath $livePath -Destination $backupPath -Recurse

    $stagePath = Join-Path $fixture.Cache 'browser.stage-partial'
    Write-Utf8File -Path (Join-Path $stagePath '1.0.0\half.txt') -Text 'half copied'

    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'new.txt') -Text 'new'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $stagePath; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = (Get-FixtureDigest -Path $scratchNew)
        swappedOut = $false; swappedIn = $false
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status 'stage 不完整必须判为 needs-attention'
    Assert-True (Test-Path -LiteralPath $stagePath) 'attention 必须保留 stage 现场'
    Assert-Equal $oldDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 不得被动'
}

Add-Case -Name 'recovery-committed-with-corrupt-stage-needs-attention' -Body {
    # live=新、backup=旧、stage 存在但内容不完整（row 6 的伪装）：必须先落 row 11
    $fixture = New-Fixture 'recovery-row6-bad-stage'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $backupPath

    $stagePath = Join-Path $fixture.Cache 'browser.stage-partial'
    Write-Utf8File -Path (Join-Path $stagePath '1.0.0\half.txt') -Text 'half copied'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $stagePath; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
        swappedOut = $true; swappedIn = $true
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status '损坏的 stage 不得被当成 row 6 清理掉'
    Assert-True (Test-Path -LiteralPath $stagePath) 'attention 必须保留 stage 现场'
    Assert-Equal $newDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 不得被动'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
}

Add-Case -Name 'recovery-mixed-terminals-needs-attention' -Body {
    # 一个组件已提交、另一个根本没开始：混合终态不得自动收尾，且不得先执行任何动作
    $fixture = New-Fixture 'recovery-mixed-sync'

    $committedLive = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $committedLive 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $committedLive
    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $backupPath

    $untouchedLive = Join-Path $fixture.Cache 'chrome'
    Write-Utf8File -Path (Join-Path $untouchedLive 'old.txt') -Text 'old'
    $untouchedDigest = Get-FixtureDigest -Path $untouchedLive

    $scratchNew = Join-Path $fixture.Case 'scratch-new'
    Write-Utf8File -Path (Join-Path $scratchNew 'x.txt') -Text 'x'

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(
        @{
            component = 'cache:browser'; livePath = $committedLive; stagePath = $null; backupPath = $backupPath
            existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
            swappedOut = $true; swappedIn = $true
        },
        @{
            component = 'cache:chrome'; livePath = $untouchedLive; stagePath = $null
            backupPath = (Join-Path $backupRoot 'cache-chrome.previous')
            existedBefore = $true; oldDigest = $untouchedDigest; newDigest = (Get-FixtureDigest -Path $scratchNew)
            swappedOut = $false; swappedIn = $false
        }
    )
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status 'committed + aborted 混合必须判为 needs-attention'
    Assert-Equal $newDigest.TreeHash (Get-FixtureDigest -Path $committedLive).TreeHash '预检失败时不得先回退已提交组件'
    Assert-True (Test-Path -LiteralPath (Join-Path $backupRoot 'cache-browser.previous')) '备份必须保留'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
}

Add-Case -Name 'recovery-rollback-mixed-terminals-needs-attention' -Body {
    # 回滚崩溃：一个组件已回填（recovered），另一个还是回滚前内容（aborted）
    $fixture = New-Fixture 'recovery-mixed-rollback'

    $restoredLive = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $restoredLive 'restored.txt') -Text 'restored'
    $restoredDigest = Get-FixtureDigest -Path $restoredLive
    $safetyRoot = Join-Path $fixture.Backups 'rollback-before-crash'
    $safetyPath = Join-Path $safetyRoot 'cache-browser'
    Write-Utf8File -Path (Join-Path $safetyPath 'current.txt') -Text 'current'
    $currentDigest = Get-FixtureDigest -Path $safetyPath

    $untouchedLive = Join-Path $fixture.Cache 'chrome'
    Write-Utf8File -Path (Join-Path $untouchedLive 'current.txt') -Text 'current'
    $untouchedDigest = Get-FixtureDigest -Path $untouchedLive

    $journal = New-CodexSyncJournal -OperationKind 'rollback'
    $journal.phase = 'rollback-commit-started'
    $journal.targets = @(
        @{
            component = 'cache:browser'; livePath = $restoredLive; stagePath = $null
            backupPath = (Join-Path $fixture.Backups 'openai-bundled-sync-x\cache-browser.previous')
            safetyPath = $safetyPath
            existedBefore = $true; oldDigest = $currentDigest; newDigest = $restoredDigest
            swappedOut = $true; swappedIn = $true
        },
        @{
            component = 'cache:chrome'; livePath = $untouchedLive; stagePath = $null
            backupPath = (Join-Path $fixture.Backups 'openai-bundled-sync-x\cache-chrome.previous')
            safetyPath = $null
            existedBefore = $true; oldDigest = $untouchedDigest; newDigest = $restoredDigest
            swappedOut = $false; swappedIn = $false
        }
    )
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'attention' $recovery.Status 'recovered + aborted 混合必须判为 needs-attention'
    Assert-Equal $restoredDigest.TreeHash (Get-FixtureDigest -Path $restoredLive).TreeHash '预检失败时不得先回填安全快照'
    Assert-True (Test-Path -LiteralPath $safetyPath) '安全快照必须原地保留'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
}

Add-Case -Name 'revert-with-corrupt-backup-needs-attention' -Body {
    # -Goal Revert：备份内容对不上 oldDigest 时不得回填，必须 attention 并保留 journal
    $fixture = New-Fixture 'revert-corrupt-backup'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'new.txt') -Text 'new'
    $newDigest = Get-FixtureDigest -Path $livePath

    $backupRoot = Join-Path $fixture.Backups 'openai-bundled-sync-crash'
    $backupPath = Join-Path $backupRoot 'cache-browser.previous'
    Write-Utf8File -Path (Join-Path $backupPath 'partial.txt') -Text 'partial copy only'

    $scratchOld = Join-Path $fixture.Case 'scratch-old'
    Write-Utf8File -Path (Join-Path $scratchOld 'old.txt') -Text 'old'
    $oldDigest = Get-FixtureDigest -Path $scratchOld

    $journal = New-CodexSyncJournal -OperationKind 'sync'
    $journal.phase = 'commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null; backupPath = $backupPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
        swappedOut = $true; swappedIn = $true
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $repair = Repair-CodexSyncOperation -RootPath $fixture.Home -Journal (Read-CodexSyncJournal -RootPath $fixture.Home) -Goal Revert
    Assert-Equal 'attention' $repair.Status '备份不可识别时不得自动退回'
    Assert-Equal $newDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 不得被动'
    Assert-True (Test-Path -LiteralPath $backupPath) '可疑备份必须保留现场'
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'attention 必须保留 journal'
}

Add-Case -Name 'rollback-crash-recovery-uses-safety-journal' -Body {
    # 回滚提交中崩溃：journal 里必须已经够信息（safetyPath + 两端摘要），不依赖进程内状态
    $fixture = New-Fixture 'rollback-crash'
    $livePath = Join-Path $fixture.Cache 'browser'
    Write-Utf8File -Path (Join-Path $livePath 'restored.txt') -Text 'restored'
    $newDigest = Get-FixtureDigest -Path $livePath

    $safetyRoot = Join-Path $fixture.Backups 'rollback-before-crash'
    $safetyPath = Join-Path $safetyRoot 'cache-browser'
    Write-Utf8File -Path (Join-Path $safetyPath 'current.txt') -Text 'current'
    $oldDigest = Get-FixtureDigest -Path $safetyPath

    $journal = New-CodexSyncJournal -OperationKind 'rollback'
    $journal.phase = 'rollback-commit-started'
    $journal.targets = @(@{
        component = 'cache:browser'; livePath = $livePath; stagePath = $null
        backupPath = (Join-Path $fixture.Backups 'openai-bundled-sync-x\cache-browser.previous')
        safetyPath = $safetyPath
        existedBefore = $true; oldDigest = $oldDigest; newDigest = $newDigest
        swappedOut = $true; swappedIn = $true
    })
    Write-CodexSyncJournal -RootPath $fixture.Home -Journal $journal

    $recovery = Resolve-CodexSyncPendingOperation -RootPath $fixture.Home
    Assert-Equal 'recovered' $recovery.Status '回滚崩溃后应能退回安全快照'
    Assert-Equal $oldDigest.TreeHash (Get-FixtureDigest -Path $livePath).TreeHash 'live 必须回到回滚前内容'
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) 'journal 应被清理'
}

Add-Case -Name 'rollback-failure-restores-from-safety' -Body {
    $fixture = New-Fixture 'rollback-fail'
    Seed-Cache -Fixture $fixture -Plugin 'browser' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'chrome' -Marker 'old'
    Seed-Cache -Fixture $fixture -Plugin 'computer-use' -Marker 'old'
    Seed-Marketplace -Fixture $fixture -Marker 'old-market'

    $sync = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $sync.ExitCode "同步应成功：$($sync.Output)"
    Write-Utf8File -Path (Join-Path $fixture.Cache 'browser\1.0.0\dirty.txt') -Text 'codex wrote this'

    $digests = @{}
    foreach ($name in @('marketplace', 'browser', 'chrome', 'computer-use')) {
        $path = if ($name -eq 'marketplace') { $fixture.Market } else { Join-Path $fixture.Cache $name }
        $digests[$name] = (Get-FixtureDigest -Path $path).TreeHash
    }

    $env:CODEXSYNC_TEST_FAIL_AT = 'rollback-commit-2'
    try { $result = Invoke-Rollback -Fixture $fixture } finally { $env:CODEXSYNC_TEST_FAIL_AT = '' }
    Assert-Equal 1 $result.ExitCode "注入失败后应返回 1：$($result.Output)"
    foreach ($name in @('marketplace', 'browser', 'chrome', 'computer-use')) {
        $path = if ($name -eq 'marketplace') { $fixture.Market } else { Join-Path $fixture.Cache $name }
        Assert-Equal $digests[$name] (Get-FixtureDigest -Path $path).TreeHash "$name 必须退回回滚前内容"
    }
    Assert-False (Test-Path -LiteralPath (Join-Path $fixture.Home 'pending-operation.json')) '退回成功后 journal 必须删除'
}

Add-Case -Name 'fixture-guard-keeps-real-registry' -Body {
    # 不传 -NativeHostStateFile 的 fixture 运行不得碰真实注册表与真实清单
    $fixture = New-Fixture 'fixture-guard'
    Seed-Marketplace -Fixture $fixture

    $registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openai.codexextension'
    $manifestPath = $null
    $beforeHash = $null
    if (Test-Path -LiteralPath $registryPath) {
        $manifestPath = [string](Get-ItemProperty -LiteralPath $registryPath).'(default)'
        if (-not [string]::IsNullOrWhiteSpace($manifestPath) -and (Test-Path -LiteralPath $manifestPath)) {
            $beforeHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        }
    }

    $result = Invoke-Sync -Fixture $fixture -NoNativeHostState
    Assert-Equal 0 $result.ExitCode "同步应成功：$($result.Output)"

    $logDir = Join-Path $fixture.Home 'logs'
    $logText = ((Get-ChildItem -LiteralPath $logDir -Filter '*.log' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n")
    Assert-True ($logText -like '*skipped-fixture*') '不传覆盖参数时必须跳过真实注册表'

    if ($null -ne $beforeHash) {
        $afterHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        Assert-Equal $beforeHash $afterHash '真实 native-messaging 清单必须一字未动'
    }
}

Add-Case -Name 'rollback-preserves-junction-alias' -Body {
    # 真机发现：备份里的 chrome\latest 是 junction，robocopy 默认会把它展开成真实目录，
    # 导致 158 → 317 个文件、回滚校验失败。
    $fixture = New-Fixture 'junction-rollback'
    Seed-Cache -Fixture $fixture -Plugin 'chrome' -Marker 'old' -Version '0.9.0'
    Seed-Marketplace -Fixture $fixture
    $versionDir = Join-Path $fixture.Cache 'chrome\0.9.0'
    $latestPath = Join-Path $fixture.Cache 'chrome\latest'
    if (-not (Test-Path -LiteralPath $latestPath)) {
        New-Item -ItemType Junction -Path $latestPath -Target $versionDir | Out-Null
    }
    $before = (Get-FixtureDigest -Path (Join-Path $fixture.Cache 'chrome')).TreeHash

    $sync = Invoke-Sync -Fixture $fixture
    Assert-Equal 0 $sync.ExitCode "同步应成功：$($sync.Output)"

    $rollback = Invoke-Rollback -Fixture $fixture
    Assert-Equal 0 $rollback.ExitCode "junction 场景下回滚应成功：$($rollback.Output)"
    Assert-Equal $before (Get-FixtureDigest -Path (Join-Path $fixture.Cache 'chrome')).TreeHash 'junction 不应影响缓存摘要'
    $latestItem = Get-Item -LiteralPath $latestPath -Force
    Assert-True ([bool]($latestItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) 'latest 回滚后必须仍是 junction'
}

Add-Case -Name 'native-host-junction-is-same-final-path' -Body {
    # latest 是指向版本目录的 junction：真机上曾被字符串比较误判为 drift 并准备重写清单
    $fixture = New-Fixture 'junction'
    $versionDir = Join-Path $fixture.Cache 'chrome\1.0.0\extension-host\windows\x64'
    New-Item -ItemType Directory -Path $versionDir -Force | Out-Null
    Write-Utf8File -Path (Join-Path $versionDir 'extension-host.exe') -Text 'fake'

    $latest = Join-Path $fixture.Cache 'chrome\latest'
    if (-not (Test-Path -LiteralPath $latest)) {
        New-Item -ItemType Junction -Path $latest -Target (Join-Path $fixture.Cache 'chrome\1.0.0') | Out-Null
    }

    $viaLatest = Join-Path $latest 'extension-host\windows\x64\extension-host.exe'
    $direct = Join-Path $versionDir 'extension-host.exe'
    Assert-True (Test-CodexSyncSameFinalPath -Left $viaLatest -Right $direct) 'junction 别名与真实路径必须判为同一文件'
    Assert-False (Test-CodexSyncSameFinalPath -Left $viaLatest -Right (Join-Path $fixture.Cache 'chrome\0.9.0\extension-host\windows\x64\extension-host.exe')) '不存在的路径不得判为相同'
}

Add-Case -Name 'mutex-blocks-concurrent-run' -Body {
    $fixture = New-Fixture 'mutex'
    $marker = Join-Path $fixture.Case 'held.marker'
    $holderScript = Join-Path $fixture.Case 'holder.ps1'
    $lib = Join-Path $script:scriptRoot 'lib\CodexPluginSync.Common.ps1'
    Write-Utf8File -Path $holderScript -Text @"
. '$lib'
`$lock = Enter-CodexSyncMutex -ScopeKey '$($fixture.Home)'
[System.IO.File]::WriteAllText('$marker', 'held')
Start-Sleep -Seconds 20
Exit-CodexSyncMutex -Lock `$lock
"@

    $process = Start-Process -FilePath $script:powerShellExe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $holderScript) -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(20)
        while (-not (Test-Path -LiteralPath $marker)) {
            if ((Get-Date) -gt $deadline) { throw '持有互斥的子进程未按时就绪' }
            Start-Sleep -Milliseconds 100
        }
        Assert-Throws -Body {
            Enter-CodexSyncMutex -ScopeKey $fixture.Home -TimeoutMilliseconds 300
        } -MessagePattern '另一个' -Message '第二个运行必须被互斥拦住'

        # 不同 CodexHome 不应互相阻塞。
        $other = Enter-CodexSyncMutex -ScopeKey (Join-Path $fixture.Case 'other-home') -TimeoutMilliseconds 300
        Exit-CodexSyncMutex -Lock $other
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
}

Add-Case -Name 'efs-restore-after-enable' -Body {
    $target = Join-Path $script:work 'efs-target'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $script:efsState.encrypted = $false

    Set-CodexSyncOverride -Name 'GetPathEncrypted' -Value { param($Path) [bool]$script:efsState.encrypted }
    Set-CodexSyncOverride -Name 'SetPathEncryption' -Value { param($Path, $Enabled) $script:efsState.encrypted = [bool]$Enabled }

    $pre = Get-CodexSyncPathEncrypted -Path $target
    Assert-False $pre 'fixture 初始应为未加密'
    Set-CodexSyncPathEncryption -Path $target -Enabled $true
    Assert-True $script:efsState.encrypted '开启后应为加密'

    $result = Restore-CodexSyncEfsState -Path $target -PreEncrypted $pre -EnabledByThisRun $true
    Assert-True $result.Restored '应报告恢复成功'
    Assert-False $script:efsState.encrypted '必须撤销本次自己的开启'

    # 本次没开启 → 不能被误解除用户既有加密状态。
    $script:efsState.encrypted = $true
    $result2 = Restore-CodexSyncEfsState -Path $target -PreEncrypted $true -EnabledByThisRun $false
    Assert-True $result2.Restored '未开启时不应报错'
    Assert-True $script:efsState.encrypted '既有加密状态必须保持'
}

Add-Case -Name 'efs-restore-failure-is-reported' -Body {
    $target = Join-Path $script:work 'efs-target-fail'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $script:efsState.encrypted = $true

    Set-CodexSyncOverride -Name 'GetPathEncrypted' -Value { param($Path) [bool]$script:efsState.encrypted }
    Set-CodexSyncOverride -Name 'SetPathEncryption' -Value {
        param($Path, $Enabled)
        if (-not $Enabled) { throw 'cipher /d 失败（测试注入）' }
        $script:efsState.encrypted = [bool]$Enabled
    }

    $result = Restore-CodexSyncEfsState -Path $target -PreEncrypted $false -EnabledByThisRun $true
    Assert-False $result.Restored '撤销失败必须如实报告'
    Assert-True ($result.Message -like '*cipher /d 失败*') '必须带出具体失败原因'
    Assert-True $script:efsState.encrypted '失败后仍加密（不静默当成已恢复）'
}

Add-Case -Name 'native-host-manifest-path-boundary' -Body {
    $allowed = Join-Path $script:work 'native-allowed'
    $outside = Join-Path $script:work 'native-outside'
    foreach ($dir in @($allowed, $outside)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $good = Join-Path $allowed 'com.openai.codexextension.json'
    Write-Utf8File -Path $good -Text '{"path":"x"}'
    $resolved = Assert-CodexSyncNativeHostManifestPath -ManifestPath $good -AllowedRoots @($allowed)
    Assert-True ($resolved -like '*com.openai.codexextension.json') '允许目录内的正确文件名应通过'

    $wrongName = Join-Path $allowed 'evil.json'
    Write-Utf8File -Path $wrongName -Text '{}'
    Assert-Throws -Body {
        Assert-CodexSyncNativeHostManifestPath -ManifestPath $wrongName -AllowedRoots @($allowed)
    } -MessagePattern '文件名' -Message '文件名不符必须拒绝'

    $external = Join-Path $outside 'com.openai.codexextension.json'
    Write-Utf8File -Path $external -Text '{}'
    Assert-Throws -Body {
        Assert-CodexSyncNativeHostManifestPath -ManifestPath $external -AllowedRoots @($allowed)
    } -MessagePattern '允许目录之外' -Message '越界路径必须拒绝'

    Assert-Throws -Body {
        Assert-CodexSyncNativeHostManifestPath -ManifestPath '..\..\evil\com.openai.codexextension.json' -AllowedRoots @($allowed)
    } -MessagePattern '..' -Message "含 '..' 的路径必须拒绝"

    Assert-Throws -Body {
        Assert-CodexSyncNativeHostManifestPath -ManifestPath '' -AllowedRoots @($allowed)
    } -MessagePattern '为空' -Message '空值必须拒绝'
}

# ---------------------------------------------------------------------------
# 运行
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $script:work)) { New-Item -ItemType Directory -Path $script:work -Force | Out-Null }

Write-Host "CodexPluginSync 脚本测试" -ForegroundColor Cyan
Write-Host ("仓库：{0}" -f $script:repoRoot)
Write-Host ("fixture 根：{0}" -f $script:work)

foreach ($case in $cases) {
    if (-not [string]::IsNullOrWhiteSpace($Filter) -and $case.Name -notlike "*$Filter*") { continue }
    Reset-CodexSyncOverrides
    $env:CODEXSYNC_TEST_FAIL_AT = ''
    try {
        & $case.Body
        $script:passed++
        Write-Host ("  PASS  {0}" -f $case.Name) -ForegroundColor Green
    } catch {
        $script:failed++
        $script:failures.Add(('{0} :: {1}' -f $case.Name, $_.Exception.Message))
        Write-Host ("  FAIL  {0}" -f $case.Name) -ForegroundColor Red
        Write-Host ("        {0}" -f $_.Exception.Message) -ForegroundColor Red
    } finally {
        Reset-CodexSyncOverrides
        $env:CODEXSYNC_TEST_FAIL_AT = ''
    }
}

Write-Host ''
if ($script:failed -eq 0) {
    Write-Host ("全部通过：{0} 个用例" -f $script:passed) -ForegroundColor Green
} else {
    Write-Host ("通过 {0}，失败 {1}" -f $script:passed, $script:failed) -ForegroundColor Red
    foreach ($failure in $script:failures) { Write-Host "  - $failure" -ForegroundColor Red }
}

if (-not $KeepWork -and $script:failed -eq 0) {
    try { Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

if ($script:failed -gt 0) { exit 1 }
exit 0

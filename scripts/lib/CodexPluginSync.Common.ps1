<#
.SYNOPSIS
    sync / rollback 共享实现：互斥、journal、树摘要、EFS 状态、Native Messaging
    路径边界、目录交换与崩溃恢复。

.DESCRIPTION
    以 dot-source 方式引入，不提供 module manifest，保持"单文件可审计"。

    本文件对应 docs/DESIGN.md 的以下契约：
      §6.3  组件快照备份模型（backup.json，只记录本次实际改动的组件）
      §6.4  操作日志状态机（pending-operation.json，写前落盘）
      §6.6  崩溃恢复：文件系统是唯一事实源，摘要不匹配即 needs-attention
      §8    EFS：记录 preEncrypted / enabledByThisRun，只撤销本次自己的开启
      §9.4  Native Messaging 注册表路径边界

    测试可通过 Set-CodexSyncOverride 注入 GetPathEncrypted / SetPathEncryption /
    GetLockingProcesses / GetRunStamp 的替身；生产路径不读取任何环境变量。
#>

$script:CodexSyncOverrides = @{}
$script:CodexSyncJournalFileName = 'pending-operation.json'
$script:CodexSyncFinalPathHelperLoaded = $false

function Reset-CodexSyncOverrides {
    $script:CodexSyncOverrides = @{}
}

function Set-CodexSyncOverride {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Value
    )
    $script:CodexSyncOverrides[$Name] = $Value
}

function Test-CodexSyncOverride {
    param([Parameter(Mandatory)][string]$Name)
    return $script:CodexSyncOverrides.ContainsKey($Name)
}

# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------

function Get-CodexSyncTimestamp {
    param([switch]$Unique)

    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    if (-not $Unique) { return $stamp }
    if (Test-CodexSyncOverride -Name 'GetRunStamp') {
        return [string](& $script:CodexSyncOverrides['GetRunStamp'])
    }
    # 秒级时间戳会在并发/连点场景下重复，唯一后缀避免两个运行争用同一目录。
    return ('{0}-{1}-{2}' -f $stamp, $PID, (Get-Random -Minimum 1000 -Maximum 9999))
}

function Get-CodexSyncSafeName {
    <#
        组件名（如 cache:browser）含冒号，不能直接作为目录名。 #>
    param([Parameter(Mandatory)][string]$Name)

    return ($Name -replace ':', '-')
}

function Write-CodexSyncLog {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [hashtable]$Fields,
        [ValidateSet('info', 'warn', 'error')][string]$Level = 'info'
    )

    $line = '[{0}] phase={1}' -f (Get-Date).ToString('HH:mm:ss'), $Phase
    if ($null -ne $Fields) {
        foreach ($key in ($Fields.Keys | Sort-Object)) {
            $line += (' {0}={1}' -f $key, $Fields[$key])
        }
    }
    $color = 'Gray'
    if ($Level -eq 'warn') { $color = 'Yellow' }
    if ($Level -eq 'error') { $color = 'Red' }
    Write-Host $line -ForegroundColor $color
}

function Write-CodexSyncJsonFile {
    <# UTF-8 无 BOM：带 BOM 的 JSON 会被 Chrome / ConvertFrom-Json 判为非法。 #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object,
        [int]$Depth = 12
    )

    $json = $Object | ConvertTo-Json -Depth $Depth
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8)
}

function Invoke-CodexSyncFailureInjection {
    <#
        测试专用失败注入（§16.1）。只有设置了 CODEXSYNC_TEST_FAIL_AT 且值等于当前
        注入点时才抛错，生产路径读到空变量即完全无副作用。
    #>
    param([Parameter(Mandatory)][string]$Point)

    if ($env:CODEXSYNC_TEST_FAIL_AT -eq $Point) {
        throw "注入的失败（仅测试）：$Point"
    }
}

function Remove-CodexSyncTree {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
            $_.Attributes = $_.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
        }
    }
    [System.IO.Directory]::Delete($Path, $true)
}

function Test-CodexSyncPathUnder {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $p = $Path.TrimEnd('\')
    $q = $Parent.TrimEnd('\')
    if ($p.Equals($q, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $p.StartsWith($q + '\', [StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------------------
# 并发互斥（发现 5）
#   互斥名由 CodexHome 真实路径派生，因此不同 CodexHome 不会互相阻塞；
#   使用 Local\ 命名空间，不需要额外权限。
# ---------------------------------------------------------------------------

function Enter-CodexSyncMutex {
    param(
        [Parameter(Mandatory)][string]$ScopeKey,
        [int]$TimeoutMilliseconds = 0
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($ScopeKey.ToLowerInvariant())
        $hash = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 20)
    } finally {
        $sha.Dispose()
    }

    $name = 'Local\CodexPluginSync-{0}' -f $hash
    $mutex = New-Object System.Threading.Mutex($false, $name)
    try {
        $acquired = $mutex.WaitOne($TimeoutMilliseconds)
    } catch [System.Threading.AbandonedMutexException] {
        # 上一个持有者被强杀：互斥已被系统释放，可以继续。
        $acquired = $true
    }

    if (-not $acquired) {
        $mutex.Dispose()
        throw "另一个 CodexPluginSync 操作正在运行（$ScopeKey）。等它结束后重试。"
    }

    return [pscustomobject]@{ Mutex = $mutex; Name = $name; ScopeKey = $ScopeKey }
}

function Exit-CodexSyncMutex {
    param($Lock)

    if ($null -eq $Lock) { return }
    try {
        $Lock.Mutex.ReleaseMutex()
    } catch {
        # 已释放或被放弃：忽略即可，不影响结果。
    } finally {
        $Lock.Mutex.Dispose()
    }
}

# ---------------------------------------------------------------------------
# 树摘要（§6.3 / §6.6）
# ---------------------------------------------------------------------------

function Get-CodexSyncTreeDigest {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }

    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
    $files = @(Get-ChildItem -LiteralPath $root -Force -Recurse -File -ErrorAction Stop)
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $total = [long]0
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $total += [long]$file.Length
        $lines.Add(('{0}|{1}|{2}' -f $relative, $file.Length, $hash))
    }
    $lines.Sort([System.StringComparer]::Ordinal)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $treeHash = [System.BitConverter]::ToString(
            $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
        ).Replace('-', '')
    } finally {
        $sha.Dispose()
    }

    return [pscustomobject]@{
        FileCount = $files.Count
        SizeBytes = $total
        TreeHash  = $treeHash
    }
}

function Test-CodexSyncDigestEqual {
    param($Left, $Right)

    if ($null -eq $Left -or $null -eq $Right) { return $false }
    if ($Left.FileCount -ne $Right.FileCount) { return $false }
    if ($Left.SizeBytes -ne $Right.SizeBytes) { return $false }
    return ($Left.TreeHash -eq $Right.TreeHash)
}

# ---------------------------------------------------------------------------
# journal（§6.4）：write-ahead + seq，原子写
# ---------------------------------------------------------------------------

function Get-CodexSyncJournalPath {
    param([Parameter(Mandatory)][string]$RootPath)
    return (Join-Path $RootPath $script:CodexSyncJournalFileName)
}

function New-CodexSyncJournal {
    param(
        [Parameter(Mandatory)][string]$OperationKind,
        [string]$OperationId = (Get-CodexSyncTimestamp -Unique)
    )

    return [ordered]@{
        schemaVersion = 1
        operationKind = $OperationKind
        operationId   = $OperationId
        seq           = 0
        updatedUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        phase         = 'preflight'
        targets       = @()
        nativeHost    = @()
        efs           = [ordered]@{ preEncrypted = $null; enabledByThisRun = $false; restoredUtc = $null }
    }
}

function Write-CodexSyncJournal {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)]$Journal
    )

    $path = Get-CodexSyncJournalPath -RootPath $RootPath
    $Journal.seq = [int]$Journal.seq + 1
    $Journal.updatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

    $json = $Journal | ConvertTo-Json -Depth 12
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $temp = $path + '.tmp'

    $stream = New-Object System.IO.FileStream(
        $temp,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None
    )
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    [System.IO.File]::Copy($temp, $path, $true)
    [System.IO.File]::Delete($temp)
}

function Read-CodexSyncJournal {
    param([Parameter(Mandatory)][string]$RootPath)

    $path = Get-CodexSyncJournalPath -RootPath $RootPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}

function Remove-CodexSyncJournal {
    param([Parameter(Mandatory)][string]$RootPath)

    $path = Get-CodexSyncJournalPath -RootPath $RootPath
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    if (Test-Path -LiteralPath ($path + '.tmp')) { Remove-Item -LiteralPath ($path + '.tmp') -Force }
}

# ---------------------------------------------------------------------------
# EFS 契约（§8）：只记录、只撤销本次自己的开启
# ---------------------------------------------------------------------------

function Get-CodexSyncPathEncrypted {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-CodexSyncOverride -Name 'GetPathEncrypted') {
        return [bool](& $script:CodexSyncOverrides['GetPathEncrypted'] $Path)
    }
    $item = Get-Item -LiteralPath $Path -Force
    return [bool]($item.Attributes -band [System.IO.FileAttributes]::Encrypted)
}

function Set-CodexSyncPathEncryption {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Enabled
    )

    if (Test-CodexSyncOverride -Name 'SetPathEncryption') {
        & $script:CodexSyncOverrides['SetPathEncryption'] $Path $Enabled
        return
    }

    $verb = if ($Enabled) { '/e' } else { '/d' }
    $output = & cipher $verb $Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "cipher $verb 失败：$Path（exit $LASTEXITCODE）：$($output -join ' ')"
    }
}

function Restore-CodexSyncEfsState {
    <#
        收尾：若本次开启了 EFS，则恢复回 preEncrypted。
        失败不静默：返回 Restored=$false 并给出原状态/当前状态（§8.2 第 6 步）。
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [bool]$PreEncrypted,
        [bool]$EnabledByThisRun
    )

    $result = [pscustomobject]@{
        Restored     = $true
        PreEncrypted = $PreEncrypted
        NowEncrypted = $null
        Message      = $null
    }

    if (-not $EnabledByThisRun) {
        try {
            $result.NowEncrypted = Get-CodexSyncPathEncrypted -Path $Path
        } catch {
            $result.NowEncrypted = $null
        }
        if ($null -eq $result.NowEncrypted) {
            # 观察不到就不宣称成功（EFS 验收里就是用这个分支拓出来的：
            # 路径不存在时 cipher 仍报 0，但属性读不出来，不能当成“已恢复”）。
            $result.Restored = $false
            $result.Message = "无法确认 EFS 状态（读不到 $Path 的加密属性），不宣称已恢复。"
            return $result
        }
        if ($result.NowEncrypted -ne $PreEncrypted) {
            $result.Restored = $false
            $result.Message = "EFS 状态与本次进入时不同（原=$PreEncrypted 现=$($result.NowEncrypted)），不是本次改的，未处理。"
        }
        return $result
    }

    try {
        Set-CodexSyncPathEncryption -Path $Path -Enabled $PreEncrypted
    } catch {
        $result.Restored = $false
        $result.Message = $_.Exception.Message
        try { $result.NowEncrypted = Get-CodexSyncPathEncrypted -Path $Path } catch { $result.NowEncrypted = $null }
        return $result
    }

    try {
        $result.NowEncrypted = Get-CodexSyncPathEncrypted -Path $Path
    } catch {
        $result.NowEncrypted = $null
    }
    if ($null -eq $result.NowEncrypted) {
        # cipher 退出码为 0 不代表真的改好了（路径不存在时它也会报 0），必须能观察到。
        $result.Restored = $false
        $result.Message = "撤销后读不到 $Path 的加密属性，无法确认已恢复。"
        return $result
    }
    if ($result.NowEncrypted -ne $PreEncrypted) {
        $result.Restored = $false
        $result.Message = "撤销后加密状态仍不匹配（原=$PreEncrypted 现=$($result.NowEncrypted)）。"
    }
    return $result
}

# ---------------------------------------------------------------------------
# Native Messaging 路径边界（发现 4 / §9.4）
# ---------------------------------------------------------------------------

function Get-CodexSyncNativeHostRegistryDefault {
    <#
        读 native-messaging 注册表的默认值。
        返回 $null = 注册表项/值不存在；返回 '' = 存在但为空（§9.4 要求区分这两者）。
        -StateFile 只给 fixture 用：用工作区内的 JSON 文件代替 HKCU，避免测试触碰真实注册表。
    #>
    param(
        [Parameter(Mandatory)][string]$RegistryPath,
        [string]$StateFile
    )

    if (-not [string]::IsNullOrWhiteSpace($StateFile)) {
        if (-not (Test-Path -LiteralPath $StateFile)) { return $null }
        $state = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json
        if ($null -eq $state.PSObject.Properties['defaultPath']) { return $null }
        return [string]$state.defaultPath
    }

    if (-not (Test-Path -LiteralPath $RegistryPath)) { return $null }
    return [string](Get-ItemProperty -LiteralPath $RegistryPath).'(default)'
}

function Set-CodexSyncNativeHostRegistryDefault {
    param(
        [Parameter(Mandatory)][string]$RegistryPath,
        [string]$StateFile,
        [Parameter(Mandatory)][string]$DefaultPath
    )

    if (-not [string]::IsNullOrWhiteSpace($StateFile)) {
        Write-CodexSyncJsonFile -Path $StateFile -Object ([ordered]@{ defaultPath = $DefaultPath })
        return
    }

    if (-not (Test-Path -LiteralPath $RegistryPath)) {
        New-Item -Path $RegistryPath -Force | Out-Null
    }
    Set-Item -LiteralPath $RegistryPath -Value $DefaultPath
}

function Remove-CodexSyncNativeHostRegistryKey {
    param(
        [Parameter(Mandatory)][string]$RegistryPath,
        [string]$StateFile
    )

    if (-not [string]::IsNullOrWhiteSpace($StateFile)) {
        if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
        return
    }
    if (Test-Path -LiteralPath $RegistryPath) { Remove-Item -LiteralPath $RegistryPath -Force }
}

function Get-CodexSyncNativeHostAllowedRoots {
    param([Parameter(Mandatory)][string]$RootPath)

    $roots = New-Object 'System.Collections.Generic.List[string]'
    foreach ($candidate in @(
        $env:LOCALAPPDATA,
        $env:APPDATA,
        $RootPath
    )) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate) {
            $roots.Add((Resolve-Path -LiteralPath $candidate).Path.TrimEnd('\'))
        }
    }
    return @($roots)
}

function Assert-CodexSyncNativeHostManifestPath {
    <#
        注册表 default 值是外部数据：必须确认它是一个名为
        com.openai.codexextension.json 的文件，且父目录落在允许的用户目录内。
        任何不匹配都直接拒绝，绝不写。
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$ManifestPath,
        [Parameter(Mandatory)][string[]]$AllowedRoots
    )

    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        throw 'Chrome native-messaging 注册表默认值为空，拒绝写入。'
    }
    if ($ManifestPath.Contains('..')) {
        throw "Chrome native-messaging 清单路径包含 '..'，拒绝写入：$ManifestPath"
    }

    $leaf = [System.IO.Path]::GetFileName($ManifestPath)
    if ($leaf -ne 'com.openai.codexextension.json') {
        throw "Chrome native-messaging 清单文件名不是 com.openai.codexextension.json，拒绝写入：$ManifestPath"
    }

    $parent = [System.IO.Path]::GetDirectoryName($ManifestPath)
    if ([string]::IsNullOrWhiteSpace($parent) -or -not [System.IO.Directory]::Exists($parent)) {
        throw "Chrome native-messaging 清单父目录不存在，拒绝写入：$ManifestPath"
    }
    $resolvedParent = (Resolve-Path -LiteralPath $parent).Path.TrimEnd('\')

    $allowed = $false
    foreach ($root in $AllowedRoots) {
        if (Test-CodexSyncPathUnder -Path $resolvedParent -Parent $root) {
            $allowed = $true
            break
        }
    }
    if (-not $allowed) {
        throw "Chrome native-messaging 清单落在允许目录之外，拒绝写入：$resolvedParent"
    }

    return (Join-Path $resolvedParent $leaf)
}

# ---------------------------------------------------------------------------
# 进程守护
# ---------------------------------------------------------------------------

function Resolve-CodexSyncFinalPath {
    <#
        解析 junction / 符号链接，返回最终真实路径（对应 C# 侧的 GetFinalPathNameByHandle）。
        用途：`...\chrome\latest\...` 与 `...\chrome\<version>\...` 可能是同一份文件，
        字符串比较会把“已经正确”的 native-messaging 清单误判成 drift 并重写它。
    #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }

    if (-not $script:CodexSyncFinalPathHelperLoaded) {
        Add-Type -Namespace CodexPluginSync -Name NativePath -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, System.IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, System.IntPtr hTemplateFile);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern uint GetFinalPathNameByHandleW(System.IntPtr hFile, System.Text.StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool CloseHandle(System.IntPtr hObject);
'@ -ErrorAction Stop
        $script:CodexSyncFinalPathHelperLoaded = $true
    }

    $handle = [CodexPluginSync.NativePath]::CreateFileW($Path, 0, 7, [System.IntPtr]::Zero, 3, 0x02000000, [System.IntPtr]::Zero)
    if ($handle.ToInt64() -eq -1 -or $handle -eq [System.IntPtr]::Zero) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    try {
        $buffer = New-Object System.Text.StringBuilder 1024
        $length = [CodexPluginSync.NativePath]::GetFinalPathNameByHandleW($handle, $buffer, 1024, 0)
        if ($length -eq 0) { return [System.IO.Path]::GetFullPath($Path) }
        $final = $buffer.ToString()
        if ($final.StartsWith('\\?\')) { $final = $final.Substring(4) }
        return $final
    } finally {
        [void][CodexPluginSync.NativePath]::CloseHandle($handle)
    }
}

function Test-CodexSyncSameFinalPath {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    $leftFinal = Resolve-CodexSyncFinalPath -Path $Left
    $rightFinal = Resolve-CodexSyncFinalPath -Path $Right
    if ($null -eq $leftFinal -or $null -eq $rightFinal) { return $false }
    return $leftFinal.TrimEnd('\').Equals($rightFinal.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Copy-CodexSyncReparseLinks {
    <#
        robocopy /XJ 会跳过 junction/symlink，避免把一个 junction 展开成整份真实目录
        （真机上 `chrome\latest` → 版本目录，展开后文件数 158 → 317，回滚校验必然失败）。
        跳过后按原目标重建这些 reparse point，使拷贝后的结构与源保持一致；
        树摘要不计 reparse point 内部，因此重建后两边摘要仍然一致。
    #>
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $links = @(Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint })

    foreach ($link in $links) {
        $relative = $link.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destPath = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $destPath) { continue }

        $linkTarget = [string]$link.Target
        if ([string]::IsNullOrWhiteSpace($linkTarget)) {
            Write-CodexSyncLog -Phase 'copy' -Level 'warn' -Fields @{ result = 'reparse-skipped'; path = $relative }
            continue
        }
        $parent = Split-Path -Parent $destPath
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        try {
            New-Item -ItemType Junction -Path $destPath -Target $linkTarget -ErrorAction Stop | Out-Null
        } catch {
            # 目标暂时不存在时（回滚的场景：live 还拿着新版），New-Item 会拒绝，
            # 但 mklink /J 允许悬挂目标；等 stage 换入 live 后链接自然生效。
            $null = & cmd.exe /c ('mklink /J "{0}" "{1}"' -f $destPath, $linkTarget) 2>&1
            if (-not (Test-Path -LiteralPath $destPath)) {
                Write-CodexSyncLog -Phase 'copy' -Level 'warn' -Fields @{ result = 'reparse-failed'; path = $relative; detail = $_.Exception.Message }
            }
        }
    }
}

function Get-CodexSyncLockingProcesses {
    if (Test-CodexSyncOverride -Name 'GetLockingProcesses') {
        return @(& $script:CodexSyncOverrides['GetLockingProcesses'])
    }
    return @(Get-Process -Name 'chrome', 'msedge', 'ChatGPT', 'extension-host' -ErrorAction SilentlyContinue)
}

function Assert-CodexSyncNoLockingProcess {
    param([Parameter(Mandatory)][string]$Action)

    $processes = @(Get-CodexSyncLockingProcesses)
    if ($processes.Count -eq 0) { return }

    $summary = ($processes | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
    throw "执行 $Action 前请关闭 Chrome、Microsoft Edge 并完全退出 Codex Desktop。仍在运行：$summary"
}

# ---------------------------------------------------------------------------
# 组件交换（sync 的 commit 步骤）
#   顺序固定为：记 digest → 写 journal(commit-started) → live→backup → stage→live
# ---------------------------------------------------------------------------

function Invoke-CodexSyncComponentSwap {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)]$Journal,
        [Parameter(Mandatory)][string]$Component,
        [Parameter(Mandatory)][string]$LivePath,
        [Parameter(Mandatory)][string]$StagePath,
        [Parameter(Mandatory)][string]$BackupPath,
        [Parameter(Mandatory)][bool]$ExistedBefore
    )

    $oldDigest = $null
    if ($ExistedBefore) { $oldDigest = Get-CodexSyncTreeDigest -Path $LivePath }
    $newDigest = Get-CodexSyncTreeDigest -Path $StagePath

    $entry = @{
        component     = $Component
        livePath      = $LivePath
        stagePath     = $StagePath
        backupPath    = $BackupPath
        existedBefore = $ExistedBefore
        oldDigest     = $oldDigest
        newDigest     = $newDigest
        swappedOut    = $false
        swappedIn     = $false
    }
    $Journal.targets = @($Journal.targets) + @($entry)

    $Journal.phase = 'commit-started'
    Write-CodexSyncJournal -RootPath $RootPath -Journal $Journal

    if ($ExistedBefore) {
        Move-Item -LiteralPath $LivePath -Destination $BackupPath
        $entry.swappedOut = $true
        Write-CodexSyncJournal -RootPath $RootPath -Journal $Journal
    }

    try {
        Move-Item -LiteralPath $StagePath -Destination $LivePath
    } catch {
        # 只有"新的还没就位"才允许退回；已就位就不再自动折腾。
        if ($ExistedBefore -and
            (Test-Path -LiteralPath $BackupPath) -and
            -not (Test-Path -LiteralPath $LivePath)) {
            Move-Item -LiteralPath $BackupPath -Destination $LivePath
            $entry.swappedOut = $false
            Write-CodexSyncJournal -RootPath $RootPath -Journal $Journal
        }
        throw
    }

    $entry.swappedIn = $true
    Write-CodexSyncJournal -RootPath $RootPath -Journal $Journal
    return $entry
}

# ---------------------------------------------------------------------------
# 崩溃恢复（§6.6）：矩阵驱动。journal 只是提示，文件系统 + 树摘要才是事实源。
#   两种目标：
#     Recover 启动恢复，严格按 §6.6 矩阵（含"已提交则继续收尾"的行）
#     Revert  进程内失败处理，目标是把已切换组件退回本次操作前状态
# ---------------------------------------------------------------------------

function Get-CodexSyncTargetField {
    param(
        $Target,
        [Parameter(Mandatory)][string]$Name,
        $Default = $null
    )

    if ($null -eq $Target) { return $Default }
    if ($Target -is [System.Collections.IDictionary]) {
        if ($Target.Contains($Name)) { return $Target[$Name] }
        return $Default
    }
    $property = $Target.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $Default
}

function Get-CodexSyncTargetSnapshot {
    <#
        把矩阵输入折算成"存在性 + 树摘要"。摘要取不到（读盘失败）记为 $null，
        在矩阵里自然落进"内容不可识别"行，不会被当成匹配。
    #>
    param([Parameter(Mandatory)]$Target)

    $livePath = [string](Get-CodexSyncTargetField -Target $Target -Name 'livePath' -Default '')
    $backupPath = [string](Get-CodexSyncTargetField -Target $Target -Name 'backupPath' -Default '')
    $stagePath = [string](Get-CodexSyncTargetField -Target $Target -Name 'stagePath' -Default '')
    $safetyPath = [string](Get-CodexSyncTargetField -Target $Target -Name 'safetyPath' -Default '')

    $snapshot = [ordered]@{
        livePath      = $livePath
        backupPath    = $backupPath
        stagePath     = $stagePath
        safetyPath    = $safetyPath
        liveExists    = (-not [string]::IsNullOrWhiteSpace($livePath)) -and (Test-Path -LiteralPath $livePath)
        liveDigest    = $null
        backupExists  = (-not [string]::IsNullOrWhiteSpace($backupPath)) -and (Test-Path -LiteralPath $backupPath)
        backupDigest  = $null
        stageExists   = (-not [string]::IsNullOrWhiteSpace($stagePath)) -and (Test-Path -LiteralPath $stagePath)
        stageDigest   = $null
        safetyExists  = (-not [string]::IsNullOrWhiteSpace($safetyPath)) -and (Test-Path -LiteralPath $safetyPath)
        safetyDigest  = $null
        oldDigest     = (Get-CodexSyncTargetField -Target $Target -Name 'oldDigest')
        newDigest     = (Get-CodexSyncTargetField -Target $Target -Name 'newDigest')
        existedBefore = [bool](Get-CodexSyncTargetField -Target $Target -Name 'existedBefore' -Default $true)
    }

    foreach ($pair in @(
        @{ Flag = 'liveExists'; Digest = 'liveDigest'; Path = $livePath },
        @{ Flag = 'backupExists'; Digest = 'backupDigest'; Path = $backupPath },
        @{ Flag = 'stageExists'; Digest = 'stageDigest'; Path = $stagePath },
        @{ Flag = 'safetyExists'; Digest = 'safetyDigest'; Path = $safetyPath }
    )) {
        if (-not $snapshot[$pair.Flag]) { continue }
        try {
            $snapshot[$pair.Digest] = Get-CodexSyncTreeDigest -Path $pair.Path
        } catch {
            $snapshot[$pair.Digest] = $null
        }
    }

    return [pscustomobject]$snapshot
}

function New-CodexSyncTargetRow {
    param(
        [Parameter(Mandatory)][int]$Row,
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)][string]$Reason,
        [Parameter(Mandatory)][string]$Outcome,
        [string]$Note = $null
    )

    return [pscustomobject]@{
        Row     = $Row
        Action  = $Action
        Reason  = $Reason
        Outcome = $Outcome
        Note    = $Note
    }
}

function Resolve-CodexSyncTargetRow {
    <#
        按 docs/DESIGN.md §6.6 的 11 行矩阵给出唯一动作。
        Action: none | delete-stage | complete-swap | restore-backup | restore-safety | attention
        Outcome: aborted | recovered | committed | attention
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('sync', 'rollback')][string]$OperationKind,
        [Parameter(Mandatory)]$Snapshot
    )

    $isOld = $Snapshot.liveExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.liveDigest -Right $Snapshot.oldDigest)
    $isNew = $Snapshot.liveExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.liveDigest -Right $Snapshot.newDigest)
    $bOld = $Snapshot.backupExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.backupDigest -Right $Snapshot.oldDigest)
    $sNew = $Snapshot.stageExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.stageDigest -Right $Snapshot.newDigest)
    $sfOld = $Snapshot.safetyExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.safetyDigest -Right $Snapshot.oldDigest)
    # §6.6 的 S 列指的是"完整的新树"。stage 存在但摘要与 newDigest 不一致（只拷了一半、
    # 或读不出来）不属于矩阵的任何一行，落进行 11；attention 必须保留 stage 作为现场。
    if ($Snapshot.stageExists -and -not $sNew) {
        return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'stage 内容不完整或无法识别，矩阵不覆盖该组合'
    }

    if ($OperationKind -eq 'rollback') {
        if (-not $Snapshot.liveExists) {
            if ($sfOld) { return New-CodexSyncTargetRow -Row 3 -Action 'restore-safety' -Outcome 'recovered' -Reason 'live 缺失，安全快照内容完整' }
            if (-not $Snapshot.safetyExists) { return New-CodexSyncTargetRow -Row 8 -Action 'attention' -Outcome 'attention' -Reason 'live 与安全快照都不存在' }
            return New-CodexSyncTargetRow -Row 10 -Action 'attention' -Outcome 'attention' -Reason '安全快照内容与 oldDigest 不一致'
        }
        if ($isOld) {
            if ($Snapshot.safetyExists -and -not $sfOld) {
                return New-CodexSyncTargetRow -Row 10 -Action 'attention' -Outcome 'attention' -Reason 'live 未动但安全快照内容不可识别'
            }
            if ($Snapshot.stageExists) { return New-CodexSyncTargetRow -Row 1 -Action 'delete-stage' -Outcome 'aborted' -Reason 'live 仍是回滚前内容，只清理 stage' }
            return New-CodexSyncTargetRow -Row 2 -Action 'none' -Outcome 'aborted' -Reason 'live 未动、无 stage'
        }
        if ($isNew) {
            if ($sfOld) {
                if ($Snapshot.stageExists) { return New-CodexSyncTargetRow -Row 6 -Action 'restore-safety' -Outcome 'recovered' -Reason '回滚已生效，退回安全快照并清理 stage' }
                return New-CodexSyncTargetRow -Row 5 -Action 'restore-safety' -Outcome 'recovered' -Reason '回滚已生效，退回安全快照'
            }
            return New-CodexSyncTargetRow -Row 7 -Action 'attention' -Outcome 'attention' -Reason '回滚已生效但安全快照不可用，无法退回'
        }
        return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'live 摘要既不匹配同步前内容也不匹配回滚内容'
    }

    if (-not $Snapshot.liveExists) {
        if ($bOld) {
            if ($sNew) { return New-CodexSyncTargetRow -Row 4 -Action 'complete-swap' -Outcome 'committed' -Reason '交换第 1 步完成、stage 完整，继续前滚' }
            return New-CodexSyncTargetRow -Row 8 -Action 'restore-backup' -Outcome 'recovered' -Reason 'live 缺失、stage 丢失，退回备份内容'
        }
        if (-not $Snapshot.backupExists -and -not $Snapshot.stageExists) {
            return New-CodexSyncTargetRow -Row 9 -Action 'attention' -Outcome 'attention' -Reason 'live/backup/stage 三份都不存在'
        }
        return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason '备份存在但内容与 oldDigest 不一致'
    }

    if ($isOld) {
        if ($bOld) {
            if ($Snapshot.stageExists) { return New-CodexSyncTargetRow -Row 10 -Action 'delete-stage' -Outcome 'aborted' -Reason '三份齐全且未交换' }
            return New-CodexSyncTargetRow -Row 3 -Action 'none' -Outcome 'recovered' -Reason '备份已生成但 live 未动'
        }
        if (-not $Snapshot.backupExists) {
            if ($Snapshot.stageExists) { return New-CodexSyncTargetRow -Row 2 -Action 'delete-stage' -Outcome 'aborted' -Reason '已 staged 未 commit' }
            return New-CodexSyncTargetRow -Row 1 -Action 'none' -Outcome 'aborted' -Reason '尚未备份、未交换'
        }
        return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason '备份内容与 oldDigest 不一致'
    }

    if ($isNew) {
        if ($bOld) {
            if ($Snapshot.stageExists) { return New-CodexSyncTargetRow -Row 6 -Action 'delete-stage' -Outcome 'committed' -Reason '交换完成但 stage 未清' }
            return New-CodexSyncTargetRow -Row 5 -Action 'none' -Outcome 'committed' -Reason '交换完成'
        }
        if (-not $Snapshot.backupExists) {
            if (-not $Snapshot.existedBefore) { return New-CodexSyncTargetRow -Row 7 -Action 'none' -Outcome 'committed' -Reason 'existedBefore=false 的新建组件' }
            return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'existedBefore=true 但备份不存在'
        }
        return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason '备份内容与 oldDigest 不一致'
    }

    return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'live 摘要既不匹配 oldDigest 也不匹配 newDigest'
}

function Move-CodexSyncVerified {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        $ExpectedDigest,
        [Parameter(Mandatory)][string]$Component
    )

    Move-Item -LiteralPath $Source -Destination $Destination
    $digest = Get-CodexSyncTreeDigest -Path $Destination
    if ($null -eq $ExpectedDigest) { return $true }
    if (Test-CodexSyncDigestEqual -Left $digest -Right $ExpectedDigest) { return $true }
    throw "$Component：移动后内容校验失败（$Destination）"
}

function Resolve-CodexSyncRevertRow {
    <#
        -Goal Revert 的行判定：目标是把 live 退回本次操作前内容。
        这里的 Outcome 统一为 'reverted'（“已处于目标状态”），不再用 aborted/recovered——
        那是矩阵终态语义，混用会让一致性校验把正常撤回误判为混合终态。
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('sync', 'rollback')][string]$OperationKind,
        [Parameter(Mandatory)]$Snapshot
    )

    $isOld = $Snapshot.liveExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.liveDigest -Right $Snapshot.oldDigest)
    $isNew = $Snapshot.liveExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.liveDigest -Right $Snapshot.newDigest)
    $bOld = $Snapshot.backupExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.backupDigest -Right $Snapshot.oldDigest)
    $sfOld = $Snapshot.safetyExists -and (Test-CodexSyncDigestEqual -Left $Snapshot.safetyDigest -Right $Snapshot.oldDigest)
    $hasOldCopy = $null -ne $Snapshot.oldDigest

    if ($isOld) {
        return New-CodexSyncTargetRow -Row 1 -Action 'none' -Outcome 'reverted' -Reason 'live 已是本次操作前内容'
    }

    if ($isNew -or -not $Snapshot.liveExists) {
        if ($OperationKind -eq 'rollback') {
            if ($sfOld) { return New-CodexSyncTargetRow -Row 5 -Action 'restore-safety' -Outcome 'reverted' -Reason '退回安全快照' }
            if ($Snapshot.safetyExists) { return New-CodexSyncTargetRow -Row 10 -Action 'attention' -Outcome 'attention' -Reason '安全快照内容与 oldDigest 不一致' }
            return New-CodexSyncTargetRow -Row 7 -Action 'attention' -Outcome 'attention' -Reason '安全快照不存在'
        }
        if ($bOld) {
            if (-not $hasOldCopy) { return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'journal 没有记录 oldDigest' }
            return New-CodexSyncTargetRow -Row 5 -Action 'revert-new' -Outcome 'reverted' -Reason '退回备份内容'
        }
        if ($Snapshot.backupExists) { return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason '备份内容与 oldDigest 不一致' }
        if (-not $Snapshot.existedBefore) { return New-CodexSyncTargetRow -Row 7 -Action 'delete-live' -Outcome 'reverted' -Reason '本次新建的组件，撤回即移出' }
        return New-CodexSyncTargetRow -Row 9 -Action 'attention' -Outcome 'attention' -Reason '备份不存在且组件本该存在'
    }

    return New-CodexSyncTargetRow -Row 11 -Action 'attention' -Outcome 'attention' -Reason 'live 摘要无法识别'
}

function Get-CodexSyncRowForTarget {
    param(
        [Parameter(Mandatory)][string]$OperationKind,
        [Parameter(Mandatory)][string]$Goal,
        [Parameter(Mandatory)]$Snapshot
    )

    if ($Goal -eq 'Revert') {
        return Resolve-CodexSyncRevertRow -OperationKind $OperationKind -Snapshot $Snapshot
    }
    return Resolve-CodexSyncTargetRow -OperationKind $OperationKind -Snapshot $Snapshot
}

function Repair-CodexSyncOperation {
    <#
        执行矩阵给出的动作。返回：
          Status = none | aborted | recovered | committed | attention
          Actions / NeedsAttention / Rows 供日志与对账
        attention 时保留 journal（与备份、stage、安全快照），绝不删。
    #>
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)]$Journal,
        [Parameter(Mandatory)][ValidateSet('Recover', 'Revert')][string]$Goal
    )

    $actions = New-Object 'System.Collections.Generic.List[string]'
    $attention = New-Object 'System.Collections.Generic.List[string]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $stamp = Get-CodexSyncTimestamp -Unique
    $operationKind = [string](Get-CodexSyncTargetField -Target $Journal -Name 'operationKind' -Default 'sync')
    if ([string]::IsNullOrWhiteSpace($operationKind)) { $operationKind = 'sync' }
    if ($operationKind -ne 'sync' -and $operationKind -ne 'rollback') { $operationKind = 'sync' }
    $targets = @($Journal.targets)

    if ($targets.Count -eq 0) {
        # 还没记录任何组件就被打断：live 一定没动（journal 会在第一次交换前先落 targets）。
        Remove-CodexSyncJournal -RootPath $RootPath
        return [pscustomobject]@{ Status = 'aborted'; Actions = @('无 target 记录，live 未动'); NeedsAttention = @(); Rows = @(); Journal = $Journal }
    }

    # ---- 一致性预检（只读，不动任何东西）------------------------------------
    # 整个操作必须落在同一个终态上：committed / recovered / aborted 任意混合都意味着
    # "一部分组件已进入下一步"，不能自动收尾。若不一致，直接返回 attention，
    # 从而保证“发现混合状态时一行 live 都未被动过”。
    # 两种 Goal 都走这条预检：Revert 的逐组件 Outcome 统一为 'reverted'，
    # 所以正常撤回不会误报；真出现 attention/其他混合时同样拦下。
    $preOutcomes = New-Object 'System.Collections.Generic.List[string]'
    $preDetail = New-Object 'System.Collections.Generic.List[string]'
    foreach ($preTarget in $targets) {
        $preComponent = [string](Get-CodexSyncTargetField -Target $preTarget -Name 'component' -Default '?')
        $preSnapshot = Get-CodexSyncTargetSnapshot -Target $preTarget
        $preRow = Get-CodexSyncRowForTarget -OperationKind $operationKind -Goal $Goal -Snapshot $preSnapshot
        $preOutcomes.Add([string]$preRow.Outcome)
        $preDetail.Add("$preComponent：$($preRow.Outcome)（row=$($preRow.Row)）")
    }
    $distinctOutcomes = @($preOutcomes.ToArray() | Sort-Object -Unique)
    if ($distinctOutcomes.Count -gt 1) {
        $preAttention = @("组件终态不一致，不自动收尾：$($preDetail.ToArray() -join '；')")
        return [pscustomobject]@{
            Status         = 'attention'
            Actions        = @('未执行任何恢复动作（先做一致性预检）')
            NeedsAttention = $preAttention
            Rows           = @()
            Journal        = $Journal
        }
    }

    for ($i = $targets.Count - 1; $i -ge 0; $i--) {
        $target = $targets[$i]
        $component = [string](Get-CodexSyncTargetField -Target $target -Name 'component' -Default '?')
        $safeName = Get-CodexSyncSafeName -Name $component
        $snapshot = Get-CodexSyncTargetSnapshot -Target $target

        # 行判定统一收在 Get-CodexSyncRowForTarget 里（Recover 走 §6.6 矩阵，Revert 走撤回规则），
        # 预检与本轮执行必须用同一个函数，否则两处口径会漂。
        $row = Get-CodexSyncRowForTarget -OperationKind $operationKind -Goal $Goal -Snapshot $snapshot

        $rows.Add([pscustomobject]@{ Component = $component; Row = $row.Row; Action = $row.Action; Outcome = $row.Outcome; Reason = $row.Reason })

        try {
            switch ($row.Action) {
                'none' { }
                'delete-stage' {
                    if ($snapshot.stageExists) {
                        Remove-CodexSyncTree -Path $snapshot.stagePath
                        $actions.Add("${component}：清理 stage（row=$($row.Row)）")
                    }
                }
                'complete-swap' {
                    $null = Move-CodexSyncVerified -Source $snapshot.stagePath -Destination $snapshot.livePath -ExpectedDigest $snapshot.newDigest -Component $component
                    $target.swappedIn = $true
                    $actions.Add("${component}：stage 就位并校验通过（row=4）")
                }
                'restore-backup' {
                    $null = Move-CodexSyncVerified -Source $snapshot.backupPath -Destination $snapshot.livePath -ExpectedDigest $snapshot.oldDigest -Component $component
                    $target.swappedOut = $false
                    $target.swappedIn = $false
                    $actions.Add("${component}：备份内容已回填并校验通过（row=$($row.Row)）")
                }
                'restore-safety' {
                    if ($snapshot.liveExists) {
                        $rejected = Join-Path (Split-Path -Parent $snapshot.safetyPath) ('rejected-{0}-{1}' -f $safeName, $stamp)
                        Move-Item -LiteralPath $snapshot.livePath -Destination $rejected
                        $actions.Add("${component}：当前 live 已移到 $rejected")
                    }
                    $null = Move-CodexSyncVerified -Source $snapshot.safetyPath -Destination $snapshot.livePath -ExpectedDigest $snapshot.oldDigest -Component $component
                    $target.swappedOut = $false
                    $target.swappedIn = $false
                    $actions.Add("${component}：安全快照已回填并校验通过（row=$($row.Row)）")
                }
                'revert-new' {
                    $rejected = Join-Path (Split-Path -Parent $snapshot.backupPath) ('rejected-{0}-{1}' -f $safeName, $stamp)
                    if ($snapshot.liveExists) { Move-Item -LiteralPath $snapshot.livePath -Destination $rejected }
                    $null = Move-CodexSyncVerified -Source $snapshot.backupPath -Destination $snapshot.livePath -ExpectedDigest $snapshot.oldDigest -Component $component
                    $target.swappedOut = $false
                    $target.swappedIn = $false
                    $actions.Add("${component}：已退回操作前内容（本次新内容保留在 $rejected）")
                }
                'delete-live' {
                    $rejected = Join-Path $RootPath ('backups\rejected-{0}-{1}' -f $safeName, $stamp)
                    Move-Item -LiteralPath $snapshot.livePath -Destination $rejected
                    $target.swappedOut = $false
                    $target.swappedIn = $false
                    $actions.Add("${component}：本次新建内容已移出到 $rejected")
                }
                'attention' {
                    $attention.Add("${component}：$($row.Reason)（row=$($row.Row)）")
                }
                default {
                    $attention.Add("${component}：矩阵返回未知动作 $($row.Action)")
                }
            }
        } catch {
            $attention.Add("${component}：恢复动作失败 —— $($_.Exception.Message)")
        }

        Write-CodexSyncJournal -RootPath $RootPath -Journal $Journal
    }

    if ($attention.Count -gt 0) {
        return [pscustomobject]@{ Status = 'attention'; Actions = $actions.ToArray(); NeedsAttention = $attention.ToArray(); Rows = $rows.ToArray(); Journal = $Journal }
    }

    # 走到这里说明：没有 attention 行，而且（Recover 模式下）所有组件终态一致。
    Remove-CodexSyncJournal -RootPath $RootPath
    $status = 'aborted'
    foreach ($row in $rows.ToArray()) {
        if ($row.Outcome -eq 'committed') { $status = 'committed'; break }
    }
    if ($status -ne 'committed') {
        foreach ($row in $rows.ToArray()) {
            if ($row.Outcome -eq 'recovered') { $status = 'recovered'; break }
        }
    }
    if ($status -eq 'aborted') {
        foreach ($row in $rows.ToArray()) {
            if ($row.Outcome -eq 'reverted') { $status = 'reverted'; break }
        }
    }
    return [pscustomobject]@{ Status = $status; Actions = $actions.ToArray(); NeedsAttention = @(); Rows = $rows.ToArray(); Journal = $Journal }
}

function Resolve-CodexSyncPendingOperation {
    <# 启动时调用：读 journal → 跑矩阵。兼容旧调用点。 #>
    param([Parameter(Mandatory)][string]$RootPath)

    $journal = Read-CodexSyncJournal -RootPath $RootPath
    if ($null -eq $journal) {
        return [pscustomobject]@{ Status = 'none'; Actions = @(); NeedsAttention = @(); Rows = @() }
    }
    return Repair-CodexSyncOperation -RootPath $RootPath -Journal $journal -Goal Recover
}


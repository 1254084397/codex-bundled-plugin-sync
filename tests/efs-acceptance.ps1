#Requires -Version 5.1
<#
  EFS 主动开启 / 撤销的真实验收（§8 的 cipher 路径）。

  安全边界（硬编码，不靠调用方自觉）：
    - 只在工作区内的临时目录上跑，拒绝任何位于 CodexHome 下的路径；
    - 目标目录必须未加密、文件系统必须是 NTFS，否则立即停止（不扩大测试范围）；
    - 任一断言失败立即停止并保留现场，不做“继续试”；
    - finally 一定尝试 cipher /d + 删除目录，并报告清理结果。

  用法：
    powershell -ExecutionPolicy Bypass -File tests\efs-acceptance.ps1
    powershell -ExecutionPolicy Bypass -File tests\efs-acceptance.ps1 -KeepWork

  退出码：0 = 全部断言通过且清理干净；1 = 失败或中止；2 = 断言通过但清理未完成。
#>
[CmdletBinding()]
param(
    [switch]$KeepWork,
    [string]$Scratch = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts\lib\CodexPluginSync.Common.ps1')

$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' }
if ([string]::IsNullOrWhiteSpace($Scratch)) {
    $Scratch = Join-Path $PSScriptRoot '.work\efs-acceptance'
}

$failures = New-Object 'System.Collections.Generic.List[string]'
$cleanupOk = $true
$created = $false

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if ($Condition) {
        Write-Host ("  ok   {0}" -f $Message) -ForegroundColor Green
    } else {
        $script:failures.Add($Message)
        Write-Host ("  FAIL {0}" -f $Message) -ForegroundColor Red
    }
}

Write-Host '=== EFS 主动开启 / 撤销验收 ==='
Write-Host ("scratch = {0}" -f $Scratch)
Write-Host ("CODEX_HOME（禁止触碰） = {0}" -f $codexHome)

try {
    # ---- 边界检查 --------------------------------------------------------
    $resolvedScratch = [System.IO.Path]::GetFullPath($Scratch)
    if (Test-CodexSyncPathUnder -Path $resolvedScratch -Parent $repoRoot) {
        Write-Host '  ok   目标位于工作区内' -ForegroundColor Green
    } else {
        Write-Host '  ABORT 目标不在工作区内，拒绝执行' -ForegroundColor Red
        exit 1
    }
    if (Test-Path -LiteralPath $codexHome) {
        $resolvedCodexHome = (Resolve-Path -LiteralPath $codexHome).Path
        if (Test-CodexSyncPathUnder -Path $resolvedScratch -Parent $resolvedCodexHome) {
            Write-Host '  ABORT 目标位于 CodexHome 下，拒绝执行' -ForegroundColor Red
            exit 1
        }
    }

    if (Test-Path -LiteralPath $resolvedScratch) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedScratch -Force | Out-Null
    $created = $true
    Set-Content -LiteralPath (Join-Path $resolvedScratch 'plain.txt') -Value 'plain' -Encoding ascii

    $drive = (Get-Item -LiteralPath $resolvedScratch).PSDrive.Name
    $volume = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue
    $fileSystem = if ($null -ne $volume) { [string]$volume.FileSystem } else { 'unknown' }
    Write-Host ("  volume={0} fs={1}" -f $drive, $fileSystem)
    if ($fileSystem -ne 'NTFS') {
        Write-Host '  ABORT 目标卷不是 NTFS，EFS 不适用' -ForegroundColor Red
        exit 1
    }

    $preEncrypted = Get-CodexSyncPathEncrypted -Path $resolvedScratch
    Write-Host ("  preEncrypted = {0}" -f $preEncrypted)
    if ($preEncrypted) {
        Write-Host '  ABORT 目标已加密，不在其上测试“主动开启”' -ForegroundColor Red
        exit 1
    }

    # ---- 1. 真实 cipher /e ----------------------------------------------
    Write-Host '--- 1. cipher /e（真实开启）'
    $output = & cipher /e $resolvedScratch 2>&1
    $exitEncrypt = $LASTEXITCODE
    $afterEncrypt = Get-CodexSyncPathEncrypted -Path $resolvedScratch
    Write-Host ("  cipher /e exit={0} encrypted={1}" -f $exitEncrypt, $afterEncrypt)
    Assert-True ($exitEncrypt -eq 0) 'cipher /e 退出码为 0'
    Assert-True $afterEncrypt 'cipher /e 后目录 Encrypted 属性为真'

    # ---- 2. 继承与可读性 --------------------------------------------------
    Write-Host '--- 2. 继承与读写'
    $childPath = Join-Path $resolvedScratch 'after-encrypt.txt'
    Set-Content -LiteralPath $childPath -Value 'x' -Encoding ascii
    $childEncrypted = [bool]((Get-Item -LiteralPath $childPath -Force).Attributes -band [System.IO.FileAttributes]::Encrypted)
    Assert-True $childEncrypted '新建文件继承加密'
    Assert-True ((Get-Content -LiteralPath $childPath -Raw).Trim() -eq 'x') '加密后本用户仍可正常读写'

    # ---- 3. Restore（撤销本次开启）---------------------------------------
    Write-Host '--- 3. Restore-CodexSyncEfsState（enabledByThisRun=true）'
    $restore = Restore-CodexSyncEfsState -Path $resolvedScratch -PreEncrypted $false -EnabledByThisRun $true
    $afterRestore = Get-CodexSyncPathEncrypted -Path $resolvedScratch
    Write-Host ("  restored={0} nowEncrypted={1} message={2}" -f $restore.Restored, $afterRestore, $restore.Message)
    Assert-True $restore.Restored '撤销报告成功'
    Assert-True (-not $afterRestore) '撤销后目录回到未加密'

    # ---- 4. 既有加密状态不得被误解除 --------------------------------------
    Write-Host '--- 4. enabledByThisRun=false 时不得改变既有加密状态'
    $null = & cipher /e $resolvedScratch 2>&1
    $keep = Restore-CodexSyncEfsState -Path $resolvedScratch -PreEncrypted $true -EnabledByThisRun $false
    $afterKeep = Get-CodexSyncPathEncrypted -Path $resolvedScratch
    Write-Host ("  restored={0} stillEncrypted={1} message={2}" -f $keep.Restored, $afterKeep, $keep.Message)
    Assert-True $keep.Restored '未开启时不报错'
    Assert-True $afterKeep '既有加密状态保持（未被误解除）'

    # ---- 5. 失败路径必须如实上报 -----------------------------------------
    Write-Host '--- 5. 撤销失败必须如实上报（用已删除的路径触发 cipher 失败）'
    $missing = Join-Path $resolvedScratch 'not-there'
    $failed = Restore-CodexSyncEfsState -Path $missing -PreEncrypted $false -EnabledByThisRun $true
    Write-Host ("  restored={0} message={1}" -f $failed.Restored, $failed.Message)
    Assert-True (-not $failed.Restored) '撤销失败时 Restored=false'
    Assert-True (-not [string]::IsNullOrWhiteSpace($failed.Message)) '撤销失败时带出具体原因'
}
catch {
    $failures.Add("异常中止：" + $_.Exception.Message)
    Write-Host ("  ABORT {0}" -f $_.Exception.Message) -ForegroundColor Red
}
finally {
    Write-Host '--- 清理'
    if ($created -and (Test-Path -LiteralPath $Scratch)) {
        try {
            $still = Get-CodexSyncPathEncrypted -Path $Scratch
            if ($still) { $null = & cipher /d $Scratch 2>&1 }
            $finalEnc = Get-CodexSyncPathEncrypted -Path $Scratch
            if ($finalEnc) { $cleanupOk = $false; Write-Host '  清理警告：目录仍处于加密状态' -ForegroundColor Yellow }
            if ($KeepWork) {
                Write-Host ("  保留现场：{0}" -f $Scratch)
            } else {
                Remove-Item -LiteralPath $Scratch -Recurse -Force
                if (Test-Path -LiteralPath $Scratch) { $cleanupOk = $false; Write-Host '  清理警告：目录未能删除' -ForegroundColor Yellow }
                else { Write-Host '  已解密并删除临时目录' -ForegroundColor Green }
            }
        } catch {
            $cleanupOk = $false
            Write-Host ("  清理失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    if ($cleanupOk) {
        Write-Host 'EFS 验收通过（cipher /e 开启、继承、cipher /d 撤销、既有状态保持、失败上报），临时目录已清理。' -ForegroundColor Green
        exit 0
    }
    Write-Host 'EFS 断言通过，但清理未完成，请手查临时目录。' -ForegroundColor Yellow
    exit 2
}

Write-Host ("EFS 验收失败：{0} 项" -f $failures.Count) -ForegroundColor Red
foreach ($failure in $failures) { Write-Host ("  - {0}" -f $failure) -ForegroundColor Red }
exit 1

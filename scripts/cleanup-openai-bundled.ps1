[CmdletBinding()]
param(
    [string]$Root = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME 'CodexHome' }),
    [ValidateRange(0, 100)][int]$KeepBackups = 1,
    [ValidateRange(1, 720)][int]$StaleStageHours = 24,
    [ValidateRange(1, 3650)][int]$KeepLogDays = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rootPath = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
$backupDir = Join-Path $rootPath 'backups'
$stageDir = Join-Path $rootPath '.tmp\bundled-marketplaces'
$logDir = Join-Path $rootPath 'logs'
$removedItems = 0
$freedBytes = [int64]0

function Assert-UnderRoot {
    param([Parameter(Mandatory)][string]$Path)
    # Use the lexical path here. Resolve-Path follows junctions and can turn a
    # path inside a backup into the live target of a latest junction.
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside CodexHome: $fullPath"
    }
    return $fullPath
}

function Remove-DirectoryTreeSafely {
    param([Parameter(Mandatory)][string]$Path)
    $fullPath = Assert-UnderRoot -Path $Path
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop

    # Remove a junction/symlink itself; never recurse into its target.
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($item.PSIsContainer) {
            [IO.Directory]::Delete($fullPath, $false)
        } else {
            [IO.File]::Delete($fullPath)
        }
        return [int64]0
    }

    if (-not $item.PSIsContainer) {
        $size = [int64]$item.Length
        [IO.File]::Delete($fullPath)
        return $size
    }

    $bytes = [int64]0
    foreach ($child in Get-ChildItem -LiteralPath $fullPath -Force -ErrorAction Stop) {
        if ($child.PSIsContainer) {
            $bytes += Remove-DirectoryTreeSafely -Path $child.FullName
        } else {
            if (-not ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                $bytes += [int64]$child.Length
            }
            [IO.File]::Delete($child.FullName)
        }
    }

    [IO.Directory]::Delete($fullPath, $false)
    return $bytes
}

function Remove-SafeDirectory {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = Assert-UnderRoot -Path $Path
    $script:freedBytes += Remove-DirectoryTreeSafely -Path $resolved
    $script:removedItems++
    Write-Host "Removed directory: $resolved"
}

function Remove-SafeFile {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = Assert-UnderRoot -Path $Path
    $item = Get-Item -LiteralPath $resolved -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        $script:freedBytes += [int64]$item.Length
    }
    [IO.File]::Delete($resolved)
    $script:removedItems++
    Write-Host "Removed file: $resolved"
}

try {
    # Remove empty backup folders first, including folders left by failed runs.
    if (Test-Path -LiteralPath $backupDir -PathType Container) {
        $backupFolders = @(Get-ChildItem -LiteralPath $backupDir -Directory -Filter 'openai-bundled-sync-*' -Force)
        foreach ($folder in $backupFolders) {
            $fileCount = @(Get-ChildItem -LiteralPath $folder.FullName -Force -Recurse -File -ErrorAction SilentlyContinue).Count
            if ($fileCount -eq 0) {
                Remove-SafeDirectory -Path $folder.FullName
            }
        }

        # Keep the newest completed rollback backup; older ones are redundant.
        $backupFolders = @(Get-ChildItem -LiteralPath $backupDir -Directory -Filter 'openai-bundled-sync-*' -Force |
            Sort-Object LastWriteTime -Descending)
        if ($backupFolders.Count -gt $KeepBackups) {
            $backupFolders | Select-Object -Skip $KeepBackups | ForEach-Object {
                Remove-SafeDirectory -Path $_.FullName
            }
        }
    }

    # Keep only the newest rollback safety snapshot as well.
    $rollbackFolders = @(Get-ChildItem -LiteralPath $backupDir -Directory -Filter 'rollback-before-*' -Force -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($rollbackFolders.Count -gt 1) {
        $rollbackFolders | Select-Object -Skip 1 | ForEach-Object {
            Remove-SafeDirectory -Path $_.FullName
        }
    }

    # Remove abandoned marketplace staging directories older than the grace period.
    if (Test-Path -LiteralPath $stageDir -PathType Container) {
        $stageCutoff = (Get-Date).AddHours(-$StaleStageHours)
        Get-ChildItem -LiteralPath $stageDir -Directory -Filter 'openai-bundled.stage-*' -Force |
            Where-Object { $_.LastWriteTime -lt $stageCutoff } |
            ForEach-Object { Remove-SafeDirectory -Path $_.FullName }
    }

    # Keep recent logs for diagnosis, but rotate old sync logs.
    if (Test-Path -LiteralPath $logDir -PathType Container) {
        $logCutoff = (Get-Date).AddDays(-$KeepLogDays)
        Get-ChildItem -LiteralPath $logDir -File -Filter 'sync-openai-bundled-*.log' -Force |
            Where-Object { $_.LastWriteTime -lt $logCutoff } |
            ForEach-Object { Remove-SafeFile -Path $_.FullName }
    }

    Write-Host ('Cleanup completed: removed {0} item(s), freed {1:N1} MB. Kept newest {2} sync backup(s) and newest rollback safety snapshot; logs retained for {3} day(s).' -f `
        $removedItems, ($freedBytes / 1MB), $KeepBackups, $KeepLogDays)
    exit 0
}
catch {
    Write-Error "Cleanup failed: $($_.Exception.Message)"
    exit 1
}

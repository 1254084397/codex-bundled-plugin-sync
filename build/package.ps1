# 打一个可以发给别人的发布包：单个 exe + README + LICENSE，压缩成 zip。
# 用法：powershell -ExecutionPolicy Bypass -File .\build\package.ps1
# 产物：dist\CodexPluginSync-<版本>.zip
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'CodexPluginSync.csproj'
$cliProject = Join-Path $PSScriptRoot 'CodexPluginSync.Cli.csproj'
$output = Join-Path $PSScriptRoot "bin\$Configuration"
$exe = Join-Path $output 'CodexPluginSync.exe'
$cliExe = Join-Path $output 'CodexPluginSync.Cli.exe'

Push-Location $repoRoot
try {
    Write-Host "== 构建 GUI（$Configuration）=="
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" $project "-p:Configuration=$Configuration" -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "GUI 构建失败（exit $LASTEXITCODE）" }
    if (-not (Test-Path -LiteralPath $exe)) { throw "找不到产物：$exe" }

    Write-Host "== 构建 CLI（$Configuration）=="
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" $cliProject "-p:Configuration=$Configuration" -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "CLI 构建失败（exit $LASTEXITCODE）" }
    if (-not (Test-Path -LiteralPath $cliExe)) { throw "找不到产物：$cliExe" }

    $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.0.0' }

    Write-Host '== 自检（必须全绿，否则不打包）=='
    & $cliExe --selftest | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "自检未通过（exit $LASTEXITCODE），拒绝打包" }

    $dist = Join-Path $repoRoot 'dist'
    $stage = Join-Path $dist ("CodexPluginSync-$version")
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Copy-Item -LiteralPath $exe -Destination $stage
    Copy-Item -LiteralPath $cliExe -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $repoRoot 'sync-codex-bundled.cmd') -Destination $stage -ErrorAction SilentlyContinue

    $zip = Join-Path $dist ("CodexPluginSync-$version.zip")
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Host ''
    Write-Host "== 完成 =="
    Write-Host ("目录：" + $stage)
    Write-Host ("压缩包：" + $zip + "（" + [math]::Round((Get-Item -LiteralPath $zip).Length / 1KB) + " KB）")
    Write-Host '发给人时只需要这个 zip：解压后双击 exe 就是界面。'
}
finally {
    Pop-Location
}

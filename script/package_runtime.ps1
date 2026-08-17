# package_runtime.ps1 — 把固定版本的 Node.js 和官方 dsh Runtime 打包进 Resources/runtime
# 对应 macOS 项目的 script/package_runtime.sh。
#
# 用法：
#   $env:HARNESS_RUNTIME_SOURCE = "C:\path\to\runtime-source"   # 含 node_modules\.bin\dsh.cmd
#   $env:HARNESS_NODE_PATH     = "C:\path\to\node.exe"          # 可选，默认用 PATH 里的 node
#   .\script\package_runtime.ps1
[CmdletBinding()]
param(
    [string]$SourceRoot = $env:HARNESS_RUNTIME_SOURCE,
    [string]$NodePath   = $env:HARNESS_NODE_PATH
)
$ErrorActionPreference = 'Stop'

$RootDir     = Split-Path -Parent $PSScriptRoot
$Destination = Join-Path $RootDir 'Resources\runtime'

if (-not $SourceRoot -or -not (Test-Path $SourceRoot -PathType Container)) {
    Write-Error '请设置 HARNESS_RUNTIME_SOURCE 指向包含 node_modules\.bin\dsh.cmd 的 Runtime 源码目录。'
    exit 2
}
if (-not $NodePath) {
    $NodePath = (Get-Command node.exe -ErrorAction SilentlyContinue)?.Source
}
if (-not $NodePath -or -not (Test-Path $NodePath)) {
    Write-Error '没有找到可执行 Node；请设置 HARNESS_NODE_PATH。'
    exit 2
}

if (Test-Path (Join-Path $SourceRoot 'node_modules\.bin\dsh.cmd')) {
    $RuntimeSource = $SourceRoot
    $pnpmShim = Join-Path $SourceRoot 'node_modules\.bin\pnpm.cmd'
} else {
    Write-Error 'Runtime source 中没有 node_modules\.bin\dsh.cmd。'
    exit 2
}
if (-not (Test-Path $pnpmShim)) {
    Write-Error 'Runtime source 中没有 node_modules\.bin\pnpm.cmd；请把固定版本 pnpm 一起安装到 Runtime。'
    exit 2
}

# Single-writer lock（对应 macOS 的 .runtime-lock 目录锁）
$LockDir = Join-Path $RootDir 'Resources\.runtime-lock'
try {
    New-Item -ItemType Directory -Path $LockDir -ErrorAction Stop | Out-Null
} catch {
    Write-Error "另一个 Runtime 打包正在运行（或存在陈旧锁目录 $LockDir）。确认没有并发运行后可手动删除该目录。"
    exit 2
}

$StagingRoot = Join-Path $RootDir "Resources\.runtime-staging-$PID"
$Backup = $null
try {
    New-Item -ItemType Directory -Path (Join-Path $StagingRoot 'runtime\node\bin') -Force | Out-Null

    # robocopy 的 /MIR 复制；返回码 0-7 都是成功
    robocopy $RuntimeSource (Join-Path $StagingRoot 'runtime') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy 复制 Runtime 失败（$LASTEXITCODE）。" }
    Copy-Item $NodePath (Join-Path $StagingRoot 'runtime\node\bin\node.exe')

    # 内置 Node 必须实际能跑才能发布
    $nodeVersionOutput = & (Join-Path $StagingRoot 'runtime\node\bin\node.exe') --version 2>&1
    if ($LASTEXITCODE -ne 0 -or $nodeVersionOutput -notmatch '^v\d+') {
        throw "内置 Node 探针失败：$nodeVersionOutput"
    }
    Write-Host "内置 Node 探针通过：$nodeVersionOutput"

    if (Test-Path $Destination) {
        $Backup = "$Destination.backup.$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
        Move-Item $Destination $Backup
        Write-Host "已有 Runtime 已保留到：$Backup"
    }

    try {
        Move-Item (Join-Path $StagingRoot 'runtime') $Destination
    } catch {
        Write-Error '新 Runtime 落位失败，正在恢复旧 Runtime。'
        if ($Backup -and (Test-Path $Backup)) {
            Move-Item $Backup $Destination -ErrorAction SilentlyContinue
        }
        exit 1
    }
    $Backup = $null
    Write-Host "Runtime 已写入：$Destination"
}
finally {
    if (Test-Path $StagingRoot) { Remove-Item $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue }
    if ($Backup -and -not (Test-Path $Destination) -and (Test-Path $Backup)) {
        Move-Item $Backup $Destination -ErrorAction SilentlyContinue
    }
    Remove-Item $LockDir -Force -ErrorAction SilentlyContinue
}

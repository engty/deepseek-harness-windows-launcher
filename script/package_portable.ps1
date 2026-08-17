# package_portable.ps1 — 发布免安装、免管理员的便携包（单文件 exe + zip）
#
# 用法：
#   .\script\package_portable.ps1 [-Version 0.1.0]
#
# 产物：
#   artifacts\DeepSeekHarness.exe                      （单文件，可直接分发）
#   artifacts\DeepSeek-Harness-v<版本>-windows-x64.zip  （压缩包形态）
[CmdletBinding()]
param(
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'

$RootDir   = Split-Path -Parent $PSScriptRoot
$Project   = Join-Path $RootDir 'src\HarnessLauncher\HarnessLauncher.csproj'
$Artifacts = Join-Path $RootDir 'artifacts'
$Publish   = Join-Path $RootDir 'publish'

# PS5.1 的 Remove-Item 不支持超长路径，用 robocopy 空镜像法清除目录
function Remove-DirectoryRobust([string] $Path) {
    if (-not (Test-Path $Path)) { return }
    $empty = Join-Path $env:TEMP "purge-empty-$PID"
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS | Out-Null
    Remove-Item $empty -Force -ErrorAction SilentlyContinue
    Remove-Item $Path -Force -ErrorAction SilentlyContinue
}

Remove-DirectoryRobust $Publish
Remove-DirectoryRobust $Artifacts

# 1. 单元测试
dotnet test (Join-Path $RootDir 'tests\HarnessLauncher.Tests\HarnessLauncher.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. 自包含单文件发布（不依赖目标机器安装 .NET，也不需要管理员权限）
dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true -p:Version=$Version `
    -o $Publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. 组装 artifacts
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') $Artifacts

# 如果 Resources\runtime 存在（先用 package_runtime.ps1 打包过），
# 以「文件夹形态」再出一个包含 Runtime 的完整压缩包。
$zipName = "DeepSeek-Harness-v$Version-windows-x64.zip"
Compress-Archive -Path (Join-Path $Artifacts 'DeepSeekHarness.exe') `
    -DestinationPath (Join-Path $Artifacts $zipName)

if (Test-Path (Join-Path $RootDir 'Resources\runtime')) {
    $fullDir = Join-Path $Artifacts 'DeepSeek Harness'
    New-Item -ItemType Directory -Path $fullDir -Force | Out-Null
    Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') $fullDir
    # 用 robocopy 代替 Copy-Item：PS5.1 的 Copy-Item 不支持超长路径（深层 node_modules 会失败）
    robocopy (Join-Path $RootDir 'Resources\runtime') (Join-Path $fullDir 'runtime') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { Write-Error "复制 Runtime 失败（robocopy $LASTEXITCODE）。"; exit 1 }
    # 用系统自带 bsdtar 打 zip：Compress-Archive 同样不支持超长路径
    $fullZip = Join-Path $Artifacts "DeepSeek-Harness-v$Version-windows-x64-full.zip"
    if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
    & "$env:SystemRoot\System32\tar.exe" -a -cf $fullZip -C $Artifacts 'DeepSeek Harness'
    if ($LASTEXITCODE -ne 0) { Write-Error "打包 full.zip 失败（tar $LASTEXITCODE）。"; exit 1 }
    # 超长路径目录用 robocopy 空镜像法清除
    $emptyDir = Join-Path $Artifacts '.empty-purge'
    New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
    robocopy $emptyDir $fullDir /MIR /NFL /NDL /NJH /NJS | Out-Null
    Remove-Item $emptyDir -Force -ErrorAction SilentlyContinue
    Remove-Item $fullDir -Force -ErrorAction SilentlyContinue
    Write-Host '已生成含内置 Runtime 的完整压缩包。'
}

Get-ChildItem $Artifacts | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
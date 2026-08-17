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

if (Test-Path $Publish)   { Remove-Item $Publish   -Recurse -Force }
if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }

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
    Copy-Item (Join-Path $RootDir 'Resources\runtime') (Join-Path $fullDir 'runtime') -Recurse
    Compress-Archive -Path $fullDir `
        -DestinationPath (Join-Path $Artifacts "DeepSeek-Harness-v$Version-windows-x64-full.zip")
    Remove-Item $fullDir -Recurse -Force
    Write-Host '已生成含内置 Runtime 的完整压缩包。'
}

Get-ChildItem $Artifacts | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}

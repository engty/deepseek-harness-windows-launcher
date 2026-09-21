# 生成无需管理员权限的完整 Windows x64 便携包。
# 产物：完整 ZIP、7-Zip 自解压 EXE，以及可直接运行的启动器 EXE。
[CmdletBinding()]
param(
    [string]$Version = '0.2.0',
    [switch]$AllowShellOnly,
    [string]$DotnetPath = 'dotnet'
)
$ErrorActionPreference = 'Stop'

$RootDir = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RootDir 'src\HarnessLauncher\HarnessLauncher.csproj'
$Tests = Join-Path $RootDir 'tests\HarnessLauncher.Tests\HarnessLauncher.Tests.csproj'
$Runtime = Join-Path $RootDir 'Resources\runtime'
$WebView2Runtime = Join-Path $RootDir 'Resources\webview2'
$Artifacts = Join-Path $RootDir 'artifacts'
$Publish = Join-Path $RootDir 'publish'
$PackageRoot = Join-Path $RootDir '.portable-staging'
$SevenZip = 'C:\Program Files\7-Zip\7z.exe'
$Sfx = 'C:\Program Files\7-Zip\7z.sfx'

function Remove-DirectoryRobust([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
}

function Quote-NativeArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

if (-not $AllowShellOnly -and -not (Test-Path (Join-Path $Runtime 'node\bin\node.exe'))) {
    throw '完整发布必须先准备 Resources\runtime；如只需验证启动器，请显式传入 -AllowShellOnly。'
}
if (-not $AllowShellOnly -and -not (Test-Path (Join-Path $WebView2Runtime 'msedgewebview2.exe'))) {
    throw '完整发布必须先准备 Resources\webview2（x64 Fixed Version WebView2 Runtime）；如只需验证启动器，请显式传入 -AllowShellOnly。'
}
if (-not (Test-Path $SevenZip) -or -not (Test-Path $Sfx)) {
    throw '未找到 7-Zip 自解压组件（7z.exe/7z.sfx）。'
}

Remove-DirectoryRobust $Publish
Remove-DirectoryRobust $Artifacts
Remove-DirectoryRobust $PackageRoot

& $DotnetPath test $Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "单元测试失败：$LASTEXITCODE" }

& $DotnetPath publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $Publish
if ($LASTEXITCODE -ne 0) { throw "发布失败：$LASTEXITCODE" }

New-Item -ItemType Directory -Path $Artifacts, $PackageRoot -Force | Out-Null
$packageName = 'DeepSeek Harness'
$packageDir = Join-Path $PackageRoot $packageName
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') (Join-Path $packageDir 'DeepSeekHarness.exe') -Force

if (Test-Path $Runtime) {
    robocopy $Runtime (Join-Path $packageDir 'runtime') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制 Runtime 失败：$LASTEXITCODE" }
}
robocopy (Join-Path $RootDir 'Resources\dsh1024-launcher') (Join-Path $packageDir 'Resources\dsh1024-launcher') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "复制 1024 Store 适配资源失败：$LASTEXITCODE" }
robocopy (Join-Path $RootDir 'Resources\session-repair') (Join-Path $packageDir 'Resources\session-repair') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "复制 Session 修复资源失败：$LASTEXITCODE" }
if (Test-Path $WebView2Runtime) {
    robocopy $WebView2Runtime (Join-Path $packageDir 'Resources\webview2') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制 WebView2 固定运行时失败：$LASTEXITCODE" }
}

$zipName = "DeepSeek-Harness-v$Version-windows-x64-full.zip"
$zipPath = Join-Path $Artifacts $zipName
$tarProcess = Start-Process -FilePath "$env:SystemRoot\System32\tar.exe" `
    -ArgumentList @('-a', '-c', '-f', (Quote-NativeArgument $zipPath), '-C', (Quote-NativeArgument $PackageRoot), (Quote-NativeArgument $packageName)) `
    -Wait -PassThru -NoNewWindow
if ($tarProcess.ExitCode -ne 0) { throw "完整 ZIP 打包失败：$($tarProcess.ExitCode)" }

$payload = Join-Path $PackageRoot 'payload.7z'
$config = Join-Path $PackageRoot 'sfx-config.txt'
@'
;!@Install@!UTF-8!
RunProgram="DeepSeek Harness\DeepSeekHarness.exe"
GUIMode="2"
;!@InstallEnd@!
'@ | Set-Content -LiteralPath $config -Encoding UTF8
$sevenZipProcess = Start-Process -FilePath $SevenZip `
    -ArgumentList @('a', '-t7z', '-mx=7', (Quote-NativeArgument $payload), (Quote-NativeArgument (Join-Path $PackageRoot $packageName))) `
    -Wait -PassThru -NoNewWindow
if ($sevenZipProcess.ExitCode -ne 0) { throw "7z payload 打包失败：$($sevenZipProcess.ExitCode)" }

$sfxName = "DeepSeek-Harness-v$Version-windows-x64.exe"
$sfxPath = Join-Path $Artifacts $sfxName
$output = [System.IO.File]::Open($sfxPath, [System.IO.FileMode]::Create)
try {
    foreach ($source in @($Sfx, $config, $payload)) {
        $input = [System.IO.File]::OpenRead($source)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
    }
} finally { $output.Dispose() }

Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') (Join-Path $Artifacts 'DeepSeekHarness.exe') -Force
Get-ChildItem $Artifacts | Select-Object Name, @{N='MB';E={[math]::Round($_.Length / 1MB, 1)}}

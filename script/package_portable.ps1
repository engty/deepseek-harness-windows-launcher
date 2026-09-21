# 生成无需管理员权限的 Windows x64 便携包。
# 产物与原 Windows 项目保持一致：启动器 EXE、普通 ZIP、full ZIP。
# 另外生成一个包含完整 Runtime 的 7-Zip 自解压 EXE。
[CmdletBinding()]
param(
    [string]$Version = '0.2.0',
    [switch]$AllowShellOnly,
    [switch]$SkipSfx,
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

$runtimeReady =
    (Test-Path (Join-Path $Runtime 'node\bin\node.exe')) -and
    (Test-Path (Join-Path $Runtime 'node_modules\.bin\dsh.cmd')) -and
    (Test-Path (Join-Path $Runtime 'node_modules\.bin\pnpm.cmd')) -and
    (Test-Path (Join-Path $Runtime 'node_modules\@deepseek-ai\dsh\package.json'))

if (-not $AllowShellOnly -and -not $runtimeReady) {
    throw '完整发布必须先准备 Resources\runtime（Node、pnpm 和官方 @deepseek-ai/dsh）；如只需验证启动器，请显式传入 -AllowShellOnly。'
}

# Fixed WebView2 是增强隔离的可选输入。没有该目录时仍保持旧项目的
# Evergreen WebView2 兼容行为；如果目录存在但不完整，则立即失败。
$webView2Present = Test-Path $WebView2Runtime
if ($webView2Present -and -not (Test-Path (Join-Path $WebView2Runtime 'msedgewebview2.exe'))) {
    throw 'Resources\webview2 已存在但缺少 msedgewebview2.exe；请重新准备 Fixed Version WebView2 Runtime。'
}

if (-not $SkipSfx -and ((-not (Test-Path $SevenZip)) -or (-not (Test-Path $Sfx)))) {
    throw '未找到 7-Zip 自解压组件（7z.exe/7z.sfx）；CI 可使用 setup-7zip，验证构建可传入 -SkipSfx。'
}

Remove-DirectoryRobust $Publish
Remove-DirectoryRobust $Artifacts
Remove-DirectoryRobust $PackageRoot

& $DotnetPath test $Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "单元测试失败：$LASTEXITCODE" }

& $DotnetPath publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:Version=$Version -o $Publish
if ($LASTEXITCODE -ne 0) { throw "发布失败：$LASTEXITCODE" }

New-Item -ItemType Directory -Path $Artifacts, $PackageRoot -Force | Out-Null
$packageName = 'DeepSeek Harness'
$packageDir = Join-Path $PackageRoot $packageName
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') (Join-Path $packageDir 'DeepSeekHarness.exe') -Force

if ($runtimeReady) {
    robocopy $Runtime (Join-Path $packageDir 'runtime') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制 Runtime 失败：$LASTEXITCODE" }
}
robocopy (Join-Path $RootDir 'Resources\dsh1024-launcher') (Join-Path $packageDir 'Resources\dsh1024-launcher') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "复制 1024 Store 适配资源失败：$LASTEXITCODE" }
robocopy (Join-Path $RootDir 'Resources\session-repair') (Join-Path $packageDir 'Resources\session-repair') /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "复制 Session 修复资源失败：$LASTEXITCODE" }
if ($webView2Present) {
    robocopy $WebView2Runtime (Join-Path $packageDir 'Resources\webview2') /MIR /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "复制 WebView2 固定运行时失败：$LASTEXITCODE" }
}

# 与原 Windows 项目一致：普通 ZIP 只提供单文件启动器，适合快速下载。
$singleZipPath = Join-Path $Artifacts "DeepSeek-Harness-v$Version-windows-x64.zip"
Compress-Archive -LiteralPath (Join-Path $Publish 'DeepSeekHarness.exe') -DestinationPath $singleZipPath -Force

if ($runtimeReady) {
    $zipName = "DeepSeek-Harness-v$Version-windows-x64-full.zip"
    $zipPath = Join-Path $Artifacts $zipName
    $tarArguments = @('-a', '-c', '-f', (Quote-NativeArgument $zipPath), '-C', (Quote-NativeArgument $PackageRoot), (Quote-NativeArgument $packageName))
    $tarProcess = Start-Process -FilePath "$env:SystemRoot\System32\tar.exe" -ArgumentList $tarArguments -Wait -PassThru -NoNewWindow
    if ($tarProcess.ExitCode -ne 0) { throw "完整 ZIP 打包失败：$($tarProcess.ExitCode)" }

    if (-not $SkipSfx) {
        $payload = Join-Path $PackageRoot 'payload.7z'
        $config = Join-Path $PackageRoot 'sfx-config.txt'
@'
;!@Install@!UTF-8!
RunProgram="DeepSeek Harness\DeepSeekHarness.exe"
GUIMode="2"
;!@InstallEnd@!
'@ | Set-Content -LiteralPath $config -Encoding UTF8
        $sevenZipArguments = @('a', '-t7z', '-mx=7', (Quote-NativeArgument $payload), (Quote-NativeArgument (Join-Path $PackageRoot $packageName)))
        $sevenZipProcess = Start-Process -FilePath $SevenZip -ArgumentList $sevenZipArguments -Wait -PassThru -NoNewWindow
        if ($sevenZipProcess.ExitCode -ne 0) { throw "7z payload 打包失败：$($sevenZipProcess.ExitCode)" }

        $sfxName = "DeepSeek-Harness-v$Version-windows-x64-full.exe"
        $sfxPath = Join-Path $Artifacts $sfxName
        $output = [System.IO.File]::Open($sfxPath, [System.IO.FileMode]::Create)
        try {
            foreach ($source in @($Sfx, $config, $payload)) {
                $input = [System.IO.File]::OpenRead($source)
                try { $input.CopyTo($output) } finally { $input.Dispose() }
            }
        } finally { $output.Dispose() }
    }
} elseif ($AllowShellOnly) {
    Write-Warning '当前为 shell-only 验证包：未生成 full ZIP/SFX。正式发布必须提供 Resources\runtime。'
}

Copy-Item (Join-Path $Publish 'DeepSeekHarness.exe') (Join-Path $Artifacts 'DeepSeekHarness.exe') -Force
Get-ChildItem $Artifacts | Select-Object Name, @{N='MB';E={[math]::Round($_.Length / 1MB, 1)}}

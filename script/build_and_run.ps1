# build_and_run.ps1 — 本地构建并运行（对应 macOS 的 script/build_and_run.sh）
# 如果 Resources\runtime 存在，自动通过 HARNESS_RUNTIME_ROOT 指向它；
# 否则也可以先设 HARNESS_DSH_PATH 指向任意 dsh。
$ErrorActionPreference = 'Stop'
$RootDir = Split-Path -Parent $PSScriptRoot

$runtime = Join-Path $RootDir 'Resources\runtime'
if ((Test-Path $runtime) -and -not $env:HARNESS_RUNTIME_ROOT -and -not $env:HARNESS_DSH_PATH) {
    $env:HARNESS_RUNTIME_ROOT = $runtime
    Write-Host "使用内置 Runtime：$runtime"
}

dotnet build (Join-Path $RootDir 'src\HarnessLauncher\HarnessLauncher.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $RootDir 'src\HarnessLauncher\bin\Debug\net8.0-windows\DeepSeekHarness.exe'
if (Test-Path (Join-Path $RootDir 'Resources\runtime')) {
    # 开发形态：把 Resources\runtime 复制到 exe 旁边，模拟发布布局
    Copy-Item $runtime (Join-Path (Split-Path $exe) 'runtime') -Recurse -Force
}
& $exe
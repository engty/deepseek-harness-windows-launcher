# 下载并准备 x64 Fixed Version WebView2 Runtime 到 Resources\webview2。
# 该目录随完整便携包一起分发，不需要用户安装或管理员权限。
[CmdletBinding()]
param(
    [string]$Version = '139.0.3405.111'
)
$ErrorActionPreference = 'Stop'

$RootDir = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $RootDir 'Resources\webview2'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("dsh-webview2-" + [Guid]::NewGuid().ToString('N'))
$Package = "webview2.runtime.x64.$Version.nupkg"
$Url = "https://api.nuget.org/v3-flatcontainer/webview2.runtime.x64/$Version/$Package"

try {
    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    $archive = Join-Path $TempRoot $Package
    Invoke-WebRequest -Uri $Url -OutFile $archive
    $extract = Join-Path $TempRoot 'extract'
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    $executable = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'msedgewebview2.exe' | Select-Object -First 1
    if ($null -eq $executable) { throw '下载的固定 WebView2 包中没有 msedgewebview2.exe。' }

    $runtimeSource = $executable.Directory.FullName
    if (Test-Path $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Copy-Item (Join-Path $runtimeSource '*') $OutputDir -Recurse -Force
    Write-Output "WebView2 Fixed Version $Version 已准备到 $OutputDir"
} finally {
    if (Test-Path $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

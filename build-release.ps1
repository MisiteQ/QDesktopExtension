# 一键产出可直接运行的发布目录（publish 目录本身就是运行时目录）
# 用法: powershell -ExecutionPolicy Bypass -File .\build-release.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $root

function Has-Sdk([string]$exe) {
    try {
        $sdks = & $exe --list-sdks 2>$null
        return (($sdks -join '').Trim().Length -gt 0)
    } catch { return $false }
}

function Find-Dotnet {
    # 先看当前用户目录里的 SDK：机器级 C:\Program Files\dotnet 常常只装了运行时，
    # 直接按 PATH 取会拿到没有 SDK 的那份，publish 就会报 "No .NET SDKs were found"。
    $cands = @()
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { $cands += $local }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $cands += $cmd.Source }
    foreach ($c in $cands) { if (Has-Sdk $c) { return $c } }
    throw '找不到带 SDK 的 dotnet：请安装 .NET 8 SDK（仅运行时无法编译）'
}

$dotnet = Find-Dotnet
Write-Host "[1/4] dotnet publish -c Release" -ForegroundColor Cyan
& $dotnet publish -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$publish = Join-Path $root 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
if (-not (Test-Path -LiteralPath $publish)) { throw "找不到发布目录: $publish" }

Write-Host "[2/4] 复制 FFmpeg 动态库到 publish\ffmpeg" -ForegroundColor Cyan
$ffmpegOut = Join-Path $publish 'ffmpeg'
if (-not (Test-Path -LiteralPath $ffmpegOut)) { New-Item -ItemType Directory -Path $ffmpegOut | Out-Null }

# 优先用项目根目录的 ffmpeg 目录（方便自己维护版本），否则从 Debug 输出里取
$candidates = @(
    (Join-Path $root 'ffmpeg'),
    (Join-Path $root 'bin\Debug\net8.0-windows10.0.19041.0\win-x64\ffmpeg')
)
$source = $null
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) {
        $dlls = Get-ChildItem -LiteralPath $c -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue
        if ($dlls.Count -gt 0) { $source = $c; break }
    }
}
if ($source) {
    Get-ChildItem -LiteralPath $source -Filter '*.dll' | Copy-Item -Destination $ffmpegOut -Force
    Write-Host "      来源: $source"
} else {
    Write-Warning '没找到 FFmpeg DLL，请把 avcodec-62.dll / avformat-62.dll / avutil-60.dll / swscale-9.dll 等放进 publish\ffmpeg'
}

Write-Host "[3/4] 写入安装/卸载/停机脚本" -ForegroundColor Cyan
foreach ($name in @('install.ps1', 'uninstall.ps1', 'install-vdd.ps1', 'install-parsec-vdd.ps1', 'vdd-addmode.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $publish -Force
}

# 虚拟显示器驱动（可选组件）：连同驱动文件一起放进发布目录，
# 用户想要"系统里真的多一块显示器"时运行 install-vdd.ps1 即可
$drvSrc = Join-Path $root 'driver'
if (Test-Path -LiteralPath $drvSrc) {
    $drvDst = Join-Path $publish 'driver'
    if (-not (Test-Path -LiteralPath $drvDst)) { New-Item -ItemType Directory -Path $drvDst | Out-Null }
    Copy-Item -LiteralPath $drvSrc -Destination $publish -Recurse -Force
    Write-Host "      已复制虚拟显示驱动: $drvDst"
} else {
    Write-Warning '没找到 driver 目录，安装包里将不含虚拟显示驱动（扩展屏只能用软件画布模式）'
}

# 命令行兜底停机（不想动鼠标时用）：读 appconfig.json 的端口与密码，调用 /shutdown
$stopPs1 = @'
# 本机优雅停机：读 appconfig.json 的端口与密码，调用 /shutdown
$cfg = Get-Content (Join-Path $PSScriptRoot 'appconfig.json') -Raw | ConvertFrom-Json
$url = "http://127.0.0.1:$($cfg.HttpPort)/shutdown?token=$([uri]::EscapeDataString($cfg.Password))"
try {
    Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5 | Out-Null
    "已发送停机请求，服务正在退出。"
} catch {
    "停机失败（服务未运行，或端口/密码不对）：$($_.Exception.Message)"
}
'@
[IO.File]::WriteAllText((Join-Path $publish 'stop.ps1'), $stopPs1, (New-Object Text.UTF8Encoding $true))

if (-not (Test-Path -LiteralPath (Join-Path $publish 'appconfig.json'))) {
    Copy-Item -LiteralPath (Join-Path $root 'appconfig.json') -Destination $publish -Force
}

# 旧的 START.bat / STOP.bat 已废弃：双击图标即可运行，不需要控制台窗口
foreach ($legacy in @('START.bat', 'STOP.bat')) {
    $p = Join-Path $publish $legacy
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}

Write-Host "[4/4] 完成" -ForegroundColor Green
Write-Host "运行目录: $publish"
Write-Host "安装: 右键 install.ps1 用 PowerShell 运行（会建桌面快捷方式）"
Write-Host "启动: 双击桌面图标，或直接运行 QDesktopExtension.exe"
Write-Host "停止: 面板里点【停止服务】/ 关闭窗口 / 托盘菜单【退出】"


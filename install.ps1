# QDesktopExtension 安装脚本
# 用法：右键"使用 PowerShell 运行"，或在 PowerShell 里执行
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -AutoStart        # 开机自启
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Scope Machine    # 装到 Program Files（需管理员）

param(
    [ValidateSet('User', 'Machine')]
    [string]$Scope = 'User',
    [switch]$AutoStart,
    [switch]$NoLaunch,
    # 安装目标目录（安装器内部用；一般不用手填）
    [string]$Target = ''
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot

if (-not (Test-Path -LiteralPath (Join-Path $src 'QDesktopExtension.exe'))) {
    throw "没找到 QDesktopExtension.exe。请把 install.ps1 放在发布目录里运行。"
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Target) {
    $target = $Target
} elseif ($Scope -eq 'Machine') {
    if (-not $isAdmin) { throw '装机范围选了 Machine（Program Files），需要以管理员身份运行 PowerShell。' }
    $target = Join-Path ${env:ProgramFiles} 'QDesktopExtension'
} else {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\QDesktopExtension'
}

Write-Host "安装到: $target" -ForegroundColor Cyan

# 覆盖安装前先停掉正在跑的实例，否则 exe 被占用无法替换
Get-Process -Name 'QDesktopExtension' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch {}
}
Start-Sleep -Milliseconds 800
Get-Process -Name 'QDesktopExtension' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (-not (Test-Path -LiteralPath $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

# 升级时保留用户已经改过的配置
$userCfg = Join-Path $target 'appconfig.json'
$backup = $null
if (Test-Path -LiteralPath $userCfg) {
    $backup = Get-Content -LiteralPath $userCfg -Raw
}

Copy-Item -LiteralPath (Join-Path $src 'QDesktopExtension.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $src 'appconfig.json') -Destination $target -Force
if ($backup) { [IO.File]::WriteAllText($userCfg, $backup, (New-Object Text.UTF8Encoding $false)) }

# 驱动文件一并放进安装目录：以后想重装 / 修复驱动，直接跑安装目录里的 install-vdd.ps1
$drvSrc = Join-Path $src 'driver'
if (Test-Path -LiteralPath $drvSrc) {
    Copy-Item -LiteralPath $drvSrc -Destination $target -Recurse -Force
}

$ffmpegSrc = Join-Path $src 'ffmpeg'
if (Test-Path -LiteralPath $ffmpegSrc) {
    $ffmpegDst = Join-Path $target 'ffmpeg'
    if (-not (Test-Path -LiteralPath $ffmpegDst)) { New-Item -ItemType Directory -Path $ffmpegDst | Out-Null }
    Copy-Item -Path (Join-Path $ffmpegSrc '*') -Destination $ffmpegDst -Force -Recurse
}

foreach ($f in @('install.ps1', 'uninstall.ps1', 'stop.ps1', 'install-vdd.ps1', 'install-parsec-vdd.ps1', 'vdd-addmode.ps1')) {
    $p = Join-Path $src $f
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $target -Force }
}

# 快捷方式：桌面 + 开始菜单
$shell = New-Object -ComObject WScript.Shell
$exe = Join-Path $target 'QDesktopExtension.exe'
$shortcutTargets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'QDesktopExtension.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'QDesktopExtension.lnk')
)
foreach ($lnkPath in $shortcutTargets) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = '远程桌面串流服务（托盘 + 设置面板）'
    $lnk.Save()
    Write-Host "  快捷方式: $lnkPath"
}

# 卸载项（控制面板可见）
$uninstallKey = if ($Scope -eq 'Machine') {
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QDesktopExtension'
} else {
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QDesktopExtension'
}
New-Item -Path $uninstallKey -Force | Out-Null
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName' -Value 'QDesktopExtension 远程桌面服务'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion' -Value '1.0'
Set-ItemProperty -Path $uninstallKey -Name 'Publisher' -Value 'QDesktopExtension'
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $target
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon' -Value "$exe,0"
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`""
Set-ItemProperty -Path $uninstallKey -Name 'QuietUninstallString' -Value "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`" -Quiet"
Set-ItemProperty -Path $uninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Value 1 -Type DWord
try { Set-ItemProperty -Path $uninstallKey -Name 'EstimatedSize' -Value ([int]((Get-Item -LiteralPath $exe).Length / 1024)) -Type DWord } catch {}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($AutoStart) {
    Set-ItemProperty -Path $runKey -Name 'QDesktopExtension' -Value "`"$exe`""
    Write-Host '  已设置为开机自启' -ForegroundColor Green
} else {
    Remove-ItemProperty -Path $runKey -Name 'QDesktopExtension' -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host "  程序: $exe"
Write-Host "  配置: $userCfg"
Write-Host '  用法: 双击桌面图标 → 设置面板里【启动服务】；关窗口默认退出（勾选"最小化到托盘"则留在托盘继续串流）。'
if ($AutoStart) { Write-Host '  开机自启: 已开启（托盘菜单【退出】可关掉本次运行）' }

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -WorkingDirectory $target
    Write-Host '  已启动一次。' -ForegroundColor Green
}

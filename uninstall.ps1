# QDesktopExtension 卸载脚本（由安装脚本放到安装目录里）
# 用法：powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 [-Quiet]
param([switch]$Quiet)

$ErrorActionPreference = 'SilentlyContinue'
$dir = $PSScriptRoot

if (-not $Quiet) {
    $ans = Read-Host '确定卸载 QDesktopExtension 吗？配置文件和日志会一起删除 (Y/N)'
    if ($ans -ne 'Y' -and $ans -ne 'y') {
        Write-Host '已取消。'
        return
    }
}

# 先停掉运行中的实例
Get-Process -Name 'QDesktopExtension' | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch {}
}
Start-Sleep -Milliseconds 1000
Get-Process -Name 'QDesktopExtension' | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 快捷方式
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'QDesktopExtension.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'QDesktopExtension.lnk')
)
foreach ($t in $targets) { if (Test-Path -LiteralPath $t) { Remove-Item -LiteralPath $t -Force } }

# 开机自启
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'QDesktopExtension'

# 卸载项
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QDesktopExtension' -Recurse -Force
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QDesktopExtension' -Recurse -Force

# 目录本身：脚本在目录里跑，先退到父目录再删
$parent = Split-Path -Parent $dir
Push-Location $parent
Start-Sleep -Milliseconds 200
Remove-Item -LiteralPath $dir -Recurse -Force
Pop-Location

if (-not $Quiet) {
    Write-Host '已卸载。'
    if (-not $Quiet) { Read-Host '按回车关闭' | Out-Null }
}

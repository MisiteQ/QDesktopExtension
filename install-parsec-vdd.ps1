# 安装 Parsec Virtual Display Driver（每台客户端一块独立扩展屏用）
# 驱动安装包来自官方开源仓库 nomi-san/parsec-vdd v0.45.1，带 Parsec Cloud 官方签名。
# 用法：右键本文件 → 用 PowerShell 运行（自动 UAC 提权）。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $root 'driver\ParsecVDD\parsec-vdd-0.45.0.0.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw "找不到驱动安装包: $installer" }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '需要管理员权限，正在请求提权...'
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"" -Verb RunAs -Wait
    exit
}

Write-Host '安装 Parsec VDD 0.45...' -ForegroundColor Cyan
$p = Start-Process -FilePath $installer -ArgumentList '/S' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "驱动安装失败，退出码 $($p.ExitCode)" }
Start-Sleep -Seconds 2

$drv = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.DeviceName -eq 'Parsec Virtual Display Adapter' } | Select-Object -First 1
if ($drv) {
    Write-Host "安装成功：$($drv.DeviceName)，版本 $($drv.DriverVersion)，$($drv.InfName)" -ForegroundColor Green
    Write-Host '重新启动 QDesktopExtension 后，日志应显示「Parsec VDD 已就绪」。'
} else {
    Write-Warning '安装器已退出，但没有枚举到 Parsec Virtual Display Adapter；请重启后再检查。'
}

# 安装「虚拟显示器」驱动（一次性，之后不用再重启）
#
# 依据 GitHub 官方仓库 VirtualDrivers/Virtual-Display-Driver 的说明与社区脚本：
#   1) 驱动会去读 C:\VirtualDisplayDriver\vdd_settings.xml（路径写死），文件必须放在这里；
#   2) 根设备要用 nefconw.exe 或 SetupAPI 建立，pnputil 只会把驱动包放进驱动库、不会建设备；
#   3) 一般无需重启；只有 HAGS（硬件加速 GPU 计划）开着时才会出现"设备正常但没有显示器"，
#      这种情况把 HAGS 关掉重启一次即可（脚本会顺便告诉你当前的 HAGS 状态）。
#
# 用法：右键本文件 → 用 PowerShell 运行（自动 UAC 提权，结束前窗口不会关）。
# 卸载：pnputil /delete-driver oem<xx>.inf /uninstall，或设备管理器里删掉该显示器。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'driver\VirtualDisplayDriver'
$inf = Join-Path $src 'MttVDD.inf'
$nefcon = Join-Path $root 'driver\tools\x64\nefconw.exe'
if (-not (Test-Path -LiteralPath $inf)) { throw "找不到驱动文件: $inf" }

$logPath = Join-Path $root 'install-vdd.log'
try { Start-Transcript -LiteralPath $logPath -Force | Out-Null } catch { }

# 自提升
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '需要管理员权限，正在请求提权...'
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile', '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"" -Verb RunAs
    exit
}

Write-Host ("[install-vdd] {0}  {1}" -f $PSCommandPath, (Get-Date -Format o))

# ---------------------------------------------------------------- 1/5 固定路径
Write-Host '[1/5] 把驱动文件放到 C:\VirtualDisplayDriver（驱动写死了这个路径）' -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath 'C:\VirtualDisplayDriver')) { New-Item -ItemType Directory -Path 'C:\VirtualDisplayDriver' | Out-Null }
Get-ChildItem -LiteralPath $src | Copy-Item -Destination 'C:\VirtualDisplayDriver' -Force
Get-ChildItem -LiteralPath 'C:\VirtualDisplayDriver' | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host

# ---------------------------------------------------- 2/5 证书（签名链信任）
Write-Host '[2/5] 导入驱动签名证书到「受信任的发布者」' -ForegroundColor Cyan
try {
    $cat = Join-Path $src 'mttvdd.cat'
    $certs = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certs.Import([System.IO.File]::ReadAllBytes($cat))
    $tmp = Join-Path $env:TEMP 'vddcerts'
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    foreach ($c in $certs) {
        $cerPath = Join-Path $tmp ("{0}.cer" -f $c.Thumbprint)
        [System.IO.File]::WriteAllBytes($cerPath, $c.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
        Write-Host ("      已导入: {0}" -f $c.Subject)
    }
} catch {
    Write-Warning ("证书导入有问题（不影响后续，驱动若已签名通常可正常加载）: {0}" -f $_.Exception.Message)
}

# ------------------------------------------------------------ 3/5 驱动包注册
Write-Host '[3/5] 注册驱动包' -ForegroundColor Cyan
& "$env:SystemRoot\System32\pnputil.exe" /add-driver $inf /install | Out-Host

# ----------------------------------------------------- 4/5 建立根设备并绑定
Write-Host '[4/5] 创建虚拟显示器设备并绑定驱动' -ForegroundColor Cyan
if (Test-Path -LiteralPath $nefcon) {
    # 官方社区脚本用的就是 nefcon，最稳
    Push-Location $src
    try { & $nefcon install $inf 'Root\MttVDD' | Out-Host } finally { Pop-Location }
} else {
    $code = @'
using System;
using System.Runtime.InteropServices;

public struct GuidEx
{
    public uint Data1; public ushort Data2; public ushort Data3;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data4;
}

[StructLayout(LayoutKind.Sequential)]
public struct DevInfoData
{
    public uint cbSize;
    public GuidEx ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;
}

public static class VddSetup
{
    const uint DIF_REGISTERDEVICE = 0x19;
    const uint SPDRP_HARDWAREID = 0x00000001;
    const uint INSTALLFLAG_FORCE = 0x00000001;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref GuidEx classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName, ref GuidEx classGuid,
        string deviceDescription, IntPtr hwndParent, uint creationFlags, ref DevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet, ref DevInfoData deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref DevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId,
        string fullInfPath, uint installFlags, out bool bRebootRequired);

    public static string Install(string infPath)
    {
        var display = new GuidEx
        {
            Data1 = 0x4D36E968, Data2 = 0xE325, Data3 = 0x11CE,
            Data4 = new byte[] { 0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18 }
        };
        IntPtr set = SetupDiCreateDeviceInfoList(ref display, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return "创建设备列表失败(错误码 " + Marshal.GetLastWin32Error() + ")";

        var did = new DevInfoData();
        did.cbSize = (uint)Marshal.SizeOf(typeof(DevInfoData));
        const string hwid = @"Root\MttVDD";

        if (!SetupDiCreateDeviceInfo(set, hwid, ref display, "Virtual Display", IntPtr.Zero, 0, ref did))
            return "创建设备节点失败(错误码 " + Marshal.GetLastWin32Error() + ")";

        var chars = new byte[(hwid.Length + 2) * 2];
        for (int i = 0; i < hwid.Length; i++)
        {
            chars[i * 2] = (byte)hwid[i];
            chars[i * 2 + 1] = 0;
        }
        if (!SetupDiSetDeviceRegistryProperty(set, ref did, SPDRP_HARDWAREID, chars, (uint)chars.Length))
            return "写入硬件 ID 失败(错误码 " + Marshal.GetLastWin32Error() + ")";

        if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref did))
            return "注册设备失败(错误码 " + Marshal.GetLastWin32Error() + ")";

        SetupDiDestroyDeviceInfoList(set);

        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hwid, infPath, INSTALLFLAG_FORCE, out reboot))
            return "绑定驱动失败(错误码 " + Marshal.GetLastWin32Error() + ")";

        return reboot ? "已安装（系统建议重启，一般刷新设备即可生效）" : "已安装并生效";
    }
}
'@
    Add-Type -TypeDefinition $code -Language CSharp
    [VddSetup]::Install($inf) | Out-Host
}

# 已有设备就重启它一次（等价于设备管理器里的"启用/重启设备"），不必重启系统
Start-Sleep -Seconds 5
& "$env:SystemRoot\System32\pnputil.exe" /scan-devices | Out-Null
try { & "$env:SystemRoot\System32\pnputil.exe" /restart-device 'ROOT\MttVDD' 2>&1 | Out-Host } catch { }
Start-Sleep -Seconds 5

# ------------------------------------------------------------- 5/5 检查结果
Write-Host '[5/5] 检查结果' -ForegroundColor Cyan
Get-PnpDevice -InstanceId 'ROOT\MttVDD*' -ErrorAction SilentlyContinue |
    Select-Object Status, Problem, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String | Write-Host

Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens
Write-Host ("当前系统显示器数量: {0}" -f $screens.Count)
foreach ($s in $screens) { Write-Host ("  - {0} {1}x{2} @({3},{4})" -f $s.DeviceName, $s.Bounds.Width, $s.Bounds.Height, $s.Bounds.X, $s.Bounds.Y) }

$hags = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode
$hagsText = if ($hags -eq 2) { '已开启（已知会挡住虚拟显示器，建议关掉）' } elseif ($hags -eq 1) { '已关闭（正常）' } else { '未设置（等同关闭，正常）' }
Write-Host ("HAGS（硬件加速 GPU 计划）: {0}" -f $hagsText)

if ($screens.Count -lt 2) {
    Write-Host ''
    Write-Host '还没看到第二块屏。按顺序试：' -ForegroundColor Yellow
    Write-Host '  1) 设备管理器 → 显示适配器 → 找到 "Virtual Display" → 右键「启用设备」；'
    Write-Host '  2) 设置 → 显示 → 点「检测」按钮；'
    Write-Host '  3) 若 HAGS 开着，关掉它并重启一次（这是唯一确实需要重启的情况）。'
} else {
    Write-Host ''
    Write-Host '成功：系统里已经多了一块显示器，可以在显示设置里拖动它的位置、单独设分辨率。' -ForegroundColor Green
}

try { Stop-Transcript | Out-Null } catch { }
Write-Host ''
Write-Host '（本窗口不会自动关闭，方便你看结果；按任意键结束）' -ForegroundColor DarkGray
try { $null = [Console]::ReadKey($true) } catch { Start-Sleep -Seconds 10 }

# 把"客户端屏幕分辨率"变成一个真实的虚拟显示器模式，并立刻切换过去。
# 由主程序在需要时以管理员身份调用（会弹一次 UAC），也可以手动运行排查问题。
#   用法: powershell -File vdd-addmode.ps1 -Width 2400 -Height 1080
param(
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [int]$Refresh = 60
)

$ErrorActionPreference = 'Stop'
$xmlPath = 'C:\VirtualDisplayDriver\vdd_settings.xml'

function Write-Result([string]$text, [int]$code) {
    # 主程序会读这个文件判断结果（脚本被单独拉起来跑，stdout 拿不到）
    try {
        [IO.File]::WriteAllText((Join-Path $env:TEMP 'qde-vdd-mode.result'),
            '{"ok":' + $(if ($code -eq 0) { 'true' } else { 'false' }) + ',"code":' + $code + ',"msg":"' + $text + '"}',
            (New-Object Text.UTF8Encoding $false))
    }
    catch { }
}

try {
    # 1) 把分辨率写进驱动配置（驱动只在设备重载时读这个文件）
    if (Test-Path -LiteralPath $xmlPath) {
        $txt = Get-Content -LiteralPath $xmlPath -Raw
        if ($txt -match "<width>$Width</width>\s*<height>$Height</height>") {
            Write-Host "模式 $Width x $Height 已存在"
        }
        else {
            $entry = "        <resolution>`r`n            <width>$Width</width>`r`n            <height>$Height</height>`r`n            <refresh_rate>$Refresh</refresh_rate>`r`n        </resolution>`r`n"
            $txt = $txt -replace '(<resolutions>)', "`$1`r`n$entry"
            [IO.File]::WriteAllText($xmlPath, $txt, (New-Object Text.UTF8Encoding $false))
            Write-Host "已把 $Width x $Height 写入驱动配置"
        }
    }
    else {
        Write-Host "找不到 $xmlPath，跳过写配置"
    }

    # 2) 重载虚拟显示设备（不需要重启电脑），让新分辨率生效
    $inst = (Get-PnpDevice -ErrorAction SilentlyContinue |
             Where-Object { $_.InstanceId -like 'ROOT\DISPLAY\*' -and $_.FriendlyName -like '*Virtual Display*' } |
             Select-Object -First 1 -ExpandProperty InstanceId)
    if (-not $inst) { throw '没有找到虚拟显示设备（Virtual Display Driver）' }

    Disable-PnpDevice -InstanceId $inst -Confirm:$false
    Start-Sleep -Seconds 2
    Enable-PnpDevice -InstanceId $inst -Confirm:$false
    Start-Sleep -Seconds 6

    # 3) 找到重载后的虚拟屏并切到目标分辨率
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class QdeDisp {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] public static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr lparam);
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] public static extern bool EnumDisplayDevices(string dev, uint num, ref DISPLAY_DEVICE dd, uint flags);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
  public struct DISPLAY_DEVICE {
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceKey;
  }
  public static string FindVirtual() {
    // 逐个显示适配器看名字
    for (uint i = 0; i < 16; i++) {
      DISPLAY_DEVICE tmp = new DISPLAY_DEVICE(); tmp.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
      if (!EnumDisplayDevices(null, i, ref tmp, 0)) break;
      var n = tmp.DeviceString ?? "";
      if (n.Contains("Virtual Display") || n.Contains("MttVDD")) return tmp.DeviceName;
    }
    return null;
  }
  public static bool IsVirtualDevice(string dev) {
    DISPLAY_DEVICE dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
    if (!EnumDisplayDevices(dev, 0, ref dd, 0)) return false;
    var s = (dd.DeviceString ?? "") + "|" + (dd.DeviceID ?? "");
    // 虚拟显示器的 EDID 是 MTT1337；驱动名可能显示成 "Generic PnP Monitor"，所以两个都认
    return s.Contains("MTT1337") || s.Contains("Virtual Display") || s.Contains("MttVDD") || s.Contains("VDD");
  }
  public static bool HasMode(string dev, int w, int h) {
    for (int i = 0; i < 400; i++) {
      DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
      if (!EnumDisplaySettings(dev, i, ref dm)) break;
      if (dm.dmPelsWidth == w && dm.dmPelsHeight == h) return true;
    }
    return false;
  }
  public static int SetMode(string dev, int w, int h) {
    DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    EnumDisplaySettings(dev, -1, ref dm);
    dm.dmPelsWidth = w; dm.dmPelsHeight = h;
    dm.dmFields = 0x40000 | 0x80000;   // DM_PELSWIDTH | DM_PELSHEIGHT
    return ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, 0x1, IntPtr.Zero); // CDS_UPDATEREGISTRY
  }
}
'@

    Add-Type -AssemblyName System.Windows.Forms
    $dev = [QdeDisp]::FindVirtual()
    if (-not $dev) {
        foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
            if ([QdeDisp]::IsVirtualDevice($s.DeviceName)) { $dev = $s.DeviceName; break }
        }
    }
    if (-not $dev) { throw '重载后没找到虚拟显示器' }
    if (-not [QdeDisp]::HasMode($dev, $Width, $Height)) { throw "驱动没有注册 $Width x $Height 这个模式" }
    $rc = [QdeDisp]::SetMode($dev, $Width, $Height)
    if ($rc -ne 0) { throw "切换分辨率失败，ChangeDisplaySettingsEx 返回 $rc" }

    Write-Host "虚拟屏 $dev 已切到 $Width x $Height"
    Write-Result "虚拟屏已切到 $Width x $Height" 0
    exit 0
}
catch {
    Write-Host "失败: $($_.Exception.Message)"
    Write-Result $_.Exception.Message 1
    exit 1
}

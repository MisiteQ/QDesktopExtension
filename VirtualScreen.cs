using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>虚拟扩展屏里的一个成员窗口。</summary>
public sealed class VirtualWindow
{
    public long Hwnd { get; set; }
    public string Title { get; set; } = "";

    public IntPtr Handle => new(Hwnd);

    public override string ToString() => Title;
}

/// <summary>
/// 软件实现的"扩展屏"：不依赖任何虚拟显示器驱动。
/// 把用户选中的若干窗口按网格合成到一块虚拟画布（默认 1920×1080）上，
/// 手机看到的是这块画布；触摸/点击再按网格算回目标窗口的客户区坐标。
/// 这样等于多出一块"只放你关心的程序"的屏幕，而不是把主屏镜像一遍。
/// </summary>
public sealed class VirtualScreen : IDisposable
{
    private const uint SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;
    private const int WHITE_BRUSH = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                          IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader pbmi, uint usage,
                                                  out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>画布上的一个格子：属于哪个窗口、占画布哪块、窗口客户区在桌面上的位置。</summary>
    public sealed record Tile(IntPtr Hwnd, string Title, int X, int Y, int Width, int Height,
                              ScreenRect ClientRect);

    private readonly int _width;
    private readonly int _height;
    private readonly List<VirtualWindow> _windows;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _scratchDc;
    private readonly IntPtr[] _bmpDc = new IntPtr[4];
    private readonly IntPtr[] _bmp = new IntPtr[4];
    private readonly IntPtr[] _oldBmp = new IntPtr[4];
    private readonly int[] _bmpW = new int[4];
    private readonly int[] _bmpH = new int[4];

    // 一整块"桌面抓图"缓存：每帧只从屏幕拷一次，再按格子缩放（直接从屏幕 DC 缩放读像素非常慢）
    private IntPtr _deskDc;
    private IntPtr _deskBmp;
    private IntPtr _deskOld;
    private int _deskW;
    private int _deskH;
    private ScreenRect _deskRect = new(0, 0, 0, 0);

    private List<Tile> _tiles = new();
    private bool _disposed;
    private long _lastTileCheck;
    private readonly Logger? _logger;

    // 壁纸缓存：每帧 Image.FromFile 等于每帧读盘 + 解码，会把一个核吃满
    private System.Drawing.Image? _wallpaper;
    private string _wallpaperPath = "";
    private DateTime _wallpaperStamp;
    private long _wallpaperCheckedAt;

    public VirtualScreen(int width, int height, IEnumerable<VirtualWindow> windows, Logger? logger = null)
    {
        _width = Math.Max(320, width % 2 == 0 ? width : width - 1);
        _height = Math.Max(180, height % 2 == 0 ? height : height - 1);
        _windows = windows.Where(w => w.Hwnd != 0).Take(4).ToList();
        _logger = logger;
        _screenDc = GetDC(IntPtr.Zero);
        _scratchDc = CreateCompatibleDC(_screenDc);
        RecomputeTiles();
    }

    public int Width => _width;
    public int Height => _height;
    public IReadOnlyList<Tile> Tiles => _tiles;
    public IReadOnlyList<VirtualWindow> Windows => _windows;
    public ScreenRect Rect => new(0, 0, _width, _height);

    public int WindowCount => _windows.Count;

    /// <summary>按配置重建虚拟扩展屏；句柄失效的窗口按标题找回（找不到就跳过）。</summary>
    public static VirtualScreen Build(ConfigModel cfg, Logger? logger = null)
    {
        var wins = new List<VirtualWindow>();
        foreach (var w in cfg.VirtualWindows)
        {
            var hwnd = new IntPtr(w.Hwnd);
            // 句柄还活着就直接用：浏览器等程序的标题会随内容变化，不能拿标题当唯一凭据
            var live = WindowSource.GetTitle(hwnd);
            if (live != null && WindowSource.GetClientScreenRect(hwnd) != null)
            {
                wins.Add(new VirtualWindow { Hwnd = w.Hwnd, Title = live });
                continue;
            }
            var found = WindowSource.List().FirstOrDefault(x => x.Title == w.Title);
            if (found != null)
                wins.Add(new VirtualWindow { Hwnd = found.Handle, Title = found.Title });
            else
                logger?.Warn($"虚拟扩展屏: 窗口「{w.Title}」已不存在，跳过");
        }
        return new VirtualScreen(cfg.VirtualScreenWidth, cfg.VirtualScreenHeight, wins, logger);
    }

    public string Describe() => _windows.Count == 0
        ? "虚拟扩展屏（未选窗口）"
        : $"虚拟扩展屏（{string.Join(" / ", _windows.Select(w => w.Title))}）";

    /// <summary>网格排版：1 个铺满；2 个左右分；3~4 个 2×2（3 个时最后一个占右半整列）。</summary>
    private void RecomputeTiles()
    {
        var list = new List<Tile>();
        var n = _windows.Count;
        for (var i = 0; i < n; i++)
        {
            var w = _windows[i];
            var rect = WindowSource.GetClientScreenRect(w.Handle);
            if (rect == null) continue;

            int x, y, tw, th;
            switch (n)
            {
                case 1:
                    x = y = 0; tw = _width; th = _height;
                    break;
                case 2:
                    tw = _width / 2; th = _height;
                    x = i == 0 ? 0 : tw; y = 0;
                    break;
                case 3 when i == 0:
                    // 左半整列 + 右半上下两块
                    x = 0; y = 0; tw = _width / 2; th = _height;
                    break;
                case 3:
                    tw = _width / 2; th = _height / 2;
                    x = _width / 2; y = i == 1 ? 0 : _height / 2;
                    break;
                default:
                    tw = _width / 2; th = _height / 2;
                    x = i % 2 == 0 ? 0 : _width / 2;
                    y = i < 2 ? 0 : _height / 2;
                    break;
            }
            list.Add(new Tile(w.Handle, w.Title, x, y, tw, th, rect.Value));
        }
        _tiles = list;
        _lastTileCheck = Environment.TickCount64;
    }

    private void EnsureScratch(int index, int w, int h)
    {
        if (_bmpW[index] == w && _bmpH[index] == h && _bmpDc[index] != IntPtr.Zero) return;
        if (_bmp[index] != IntPtr.Zero)
        {
            SelectObject(_bmpDc[index], _oldBmp[index]);
            DeleteObject(_bmp[index]);
            _bmp[index] = IntPtr.Zero;
        }
        if (_bmpDc[index] == IntPtr.Zero) _bmpDc[index] = CreateCompatibleDC(_screenDc);

        var bmi = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = (uint)(w * h * 4)
        };
        _bmp[index] = CreateDIBSection(_screenDc, ref bmi, 0, out _, IntPtr.Zero, 0);
        if (_bmp[index] == IntPtr.Zero) return;
        _oldBmp[index] = SelectObject(_bmpDc[index], _bmp[index]);
        _bmpW[index] = w;
        _bmpH[index] = h;
    }

    /// <summary>桌面抓图缓冲：一个 DIB 段，每帧只做一次屏幕→内存拷贝。</summary>
    private bool EnsureDesk(int w, int h)
    {
        if (_deskW == w && _deskH == h && _deskBmp != IntPtr.Zero) return true;
        if (_deskBmp != IntPtr.Zero)
        {
            SelectObject(_deskDc, _deskOld);
            DeleteObject(_deskBmp);
            _deskBmp = IntPtr.Zero;
        }
        if (_deskDc == IntPtr.Zero) _deskDc = CreateCompatibleDC(_screenDc);

        var bmi = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biSizeImage = (uint)(w * h * 4)
        };
        _deskBmp = CreateDIBSection(_screenDc, ref bmi, 0, out _, IntPtr.Zero, 0);
        if (_deskBmp == IntPtr.Zero) return false;
        _deskOld = SelectObject(_deskDc, _deskBmp);
        _deskW = w;
        _deskH = h;
        return true;
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    /// <summary>把各格子用到的桌面区域一次拷进内存（覆盖所有窗口客户区的并集）。</summary>
    private bool GrabDesktop()
    {
        if (_tiles.Count == 0) return false;
        var r = _tiles[0].ClientRect;
        foreach (var t in _tiles)
        {
            var c = t.ClientRect;
            var left = Math.Min(r.X, c.X);
            var top = Math.Min(r.Y, c.Y);
            var right = Math.Max(r.X + r.Width, c.X + c.Width);
            var bottom = Math.Max(r.Y + r.Height, c.Y + c.Height);
            r = new ScreenRect(left, top, right - left, bottom - top);
        }
        _deskRect = r;
        if (!EnsureDesk(Math.Max(2, r.Width), Math.Max(2, r.Height))) return false;
        SetStretchBltMode(_deskDc, 1);   // COLORONCOLOR：只做采样，比 HALFTONE 快得多
        return BitBlt(_deskDc, 0, 0, r.Width, r.Height, _screenDc, r.X, r.Y, SRCCOPY);
    }

    /// <summary>
    /// 把这块扩展屏画到 dstDc 上。没有成员窗口时画一块"空桌面"（不是黑屏、也不是停帧）——
    /// 真实的第二块显示器没放窗口时也是这样：屏是有的，内容可以随时往上放。
    /// 返回 false 只表示连底图都没画成。
    /// </summary>
    public bool Render(IntPtr dstDc, int dstW, int dstH)
    {
        if (_disposed) return false;

        // 每秒核对一次几何：窗口被拖动/缩放/关掉时重新排版
        if (Environment.TickCount64 - _lastTileCheck > 1000) RecomputeTiles();

        if (_tiles.Count == 0)
        {
            DrawEmptyHint(dstDc, dstW, dstH);
            return true;
        }

        // 有窗口时也是"桌面 + 窗口"，不是一块纯色画布
        DrawDesktop(dstDc, dstW, dstH);

        var grabbed = GrabDesktop();
        for (var i = 0; i < _tiles.Count && i < 4; i++)
        {
            var t = _tiles[i];
            var cw = Math.Max(2, t.ClientRect.Width);
            var ch = Math.Max(2, t.ClientRect.Height);

            // 快路径：从刚抓好的桌面内存块里按格子裁剪+缩放（每次约 1ms）
            if (grabbed && !IsIconic(t.Hwnd))
            {
                var sx = t.ClientRect.X - _deskRect.X;
                var sy = t.ClientRect.Y - _deskRect.Y;
                SetStretchBltMode(dstDc, HALFTONE);
                if (StretchBlt(dstDc, t.X, t.Y, t.Width, t.Height, _deskDc, sx, sy, cw, ch, SRCCOPY))
                {
                    continue;
                }
            }

            // 慢路径：窗口最小化或在别的虚拟屏上时，单独 PrintWindow 抓一次
            EnsureScratch(i, cw, ch);
            if (_bmpDc[i] == IntPtr.Zero) continue;
            if (!WindowSource.CaptureTo(t.Hwnd, _bmpDc[i], cw, ch)) continue;
            SetStretchBltMode(dstDc, HALFTONE);
            StretchBlt(dstDc, t.X, t.Y, t.Width, t.Height, _bmpDc[i], 0, 0, cw, ch, SRCCOPY);
        }
        // 桌面已经画上去了，即使所有窗口都抓取失败也算成功
        return true;
    }

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int len);

    /// <summary>铺桌面：壁纸 + 底部任务栏条。一块显示器不管放没放窗口，看到的都是这张桌面。</summary>
    private void DrawDesktop(IntPtr dstDc, int dstW, int dstH)
    {
        var wallTop = Math.Max(0, dstH - 48);

        var painted = false;
        try
        {
            var img = GetWallpaperImage();
            if (img != null)
            {
                using var g = System.Drawing.Graphics.FromHdc(dstDc);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                // cover：按比例撑满，多余部分居中裁掉
                var scale = Math.Max((float)dstW / img.Width, (float)wallTop / img.Height);
                var w = (int)(img.Width * scale);
                var h = (int)(img.Height * scale);
                g.DrawImage(img, new System.Drawing.Rectangle((dstW - w) / 2, (wallTop - h) / 2, w, h));
                painted = true;
            }
        }
        catch { /* 壁纸读不出来就退回纯色 */ }

        if (!painted)
        {
            var wall = CreateSolidBrush(0x00342A22);
            var area = new RECT { Left = 0, Top = 0, Right = dstW, Bottom = wallTop };
            FillRect(dstDc, ref area, wall);
            DeleteObject(wall);
        }

        var bar = CreateSolidBrush(0x00201A14);
        var barRect = new RECT { Left = 0, Top = wallTop, Right = dstW, Bottom = dstH };
        FillRect(dstDc, ref barRect, bar);
        DeleteObject(bar);
    }

    /// <summary>空扩展屏：桌面 + 一行提示。</summary>
    private void DrawEmptyHint(IntPtr dstDc, int dstW, int dstH)
    {
        DrawDesktop(dstDc, dstW, dstH);

        // 提示文字带底衬，避免压在壁纸上看不清
        var boxW = Math.Min(dstW - 24, 620);
        var boxH = 108;
        var bx = (dstW - boxW) / 2;
        var by = dstH / 2 - boxH / 2;
        var shade = CreateSolidBrush(0x00121012);
        var box = new RECT { Left = bx, Top = by, Right = bx + boxW, Bottom = by + boxH };
        FillRect(dstDc, ref box, shade);
        DeleteObject(shade);

        SetBkMode(dstDc, 1);                 // TRANSPARENT
        SetTextColor(dstDc, 0x00E8E8EE);
        var lines = new[]
        {
            "扩展屏已就绪（这块屏就是你的第二块显示器）",
            $"{_width} × {_height}",
            "在电脑的设置面板「配置扩展屏…」里把程序放上来；不放就是一张空桌面"
        };
        var y = by + 20;
        foreach (var line in lines)
        {
            TextOutW(dstDc, bx + 24, y, line, line.Length);
            y += 26;
        }
    }

    private const int SPI_GETDESKWALLPAPER = 0x0073;

    /// <summary>取壁纸图片；5 秒检查一次壁纸是否被换过，其余时间直接用缓存。</summary>
    private System.Drawing.Image? GetWallpaperImage()
    {
        var now = Environment.TickCount64;
        var due = now - _wallpaperCheckedAt > 5000;
        if (due) _wallpaperCheckedAt = now;

        if (due)
        {
            try
            {
                var path = GetWallpaperPath();
                var stamp = path.Length > 0 && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (!string.Equals(path, _wallpaperPath, StringComparison.OrdinalIgnoreCase) || stamp != _wallpaperStamp)
                {
                    _wallpaper?.Dispose();
                    _wallpaper = null;
                    _wallpaperPath = path;
                    _wallpaperStamp = stamp;
                    if (stamp != DateTime.MinValue) _wallpaper = System.Drawing.Image.FromFile(path);
                }
            }
            catch
            {
                _wallpaper?.Dispose();
                _wallpaper = null;
                _wallpaperStamp = DateTime.MinValue;
            }
        }
        return _wallpaper;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(int action, int param, System.Text.StringBuilder buffer, int winIni);

    private static string GetWallpaperPath()
    {
        var sb = new System.Text.StringBuilder(1024);
        return SystemParametersInfoW(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0) ? sb.ToString() : "";
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// 把画布上的归一化坐标换算成目标窗口的桌面坐标。
    /// 返回 false 表示点在格子之间的空白处（不注入，避免误点别的应用）。
    /// </summary>
    public bool HitTest(float rx, float ry, out int screenX, out int screenY, out IntPtr hwnd, out string title)
    {
        screenX = screenY = 0;
        hwnd = IntPtr.Zero;
        title = "";
        var px = (int)Math.Round(Math.Clamp(rx, 0f, 1f) * (_width - 1));
        var py = (int)Math.Round(Math.Clamp(ry, 0f, 1f) * (_height - 1));

        foreach (var t in _tiles)
        {
            if (px < t.X || px >= t.X + t.Width || py < t.Y || py >= t.Y + t.Height) continue;
            var fx = (px - t.X) / (double)Math.Max(1, t.Width);
            var fy = (py - t.Y) / (double)Math.Max(1, t.Height);
            screenX = t.ClientRect.X + (int)Math.Round(fx * (t.ClientRect.Width - 1));
            screenY = t.ClientRect.Y + (int)Math.Round(fy * (t.ClientRect.Height - 1));
            hwnd = t.Hwnd;
            title = t.Title;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _wallpaper?.Dispose();
            _wallpaper = null;
            for (var i = 0; i < 4; i++)
            {
                if (_bmp[i] != IntPtr.Zero)
                {
                    if (_oldBmp[i] != IntPtr.Zero) SelectObject(_bmpDc[i], _oldBmp[i]);
                    DeleteObject(_bmp[i]);
                }
                if (_bmpDc[i] != IntPtr.Zero) DeleteDC(_bmpDc[i]);
            }
            if (_scratchDc != IntPtr.Zero) DeleteDC(_scratchDc);
            if (_deskBmp != IntPtr.Zero)
            {
                if (_deskOld != IntPtr.Zero) SelectObject(_deskDc, _deskOld);
                DeleteObject(_deskBmp);
            }
            if (_deskDc != IntPtr.Zero) DeleteDC(_deskDc);
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
        }
        catch
        {
        }
    }
}

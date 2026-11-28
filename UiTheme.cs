using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>两套配色：深色「控制台 / 导播台」与浅色（同一套琥珀主色，换底不换品牌）。</summary>
public enum UiThemeMode
{
    Dark,
    Light
}

/// <summary>
/// 主题调色板与自绘控件。颜色都是静态属性，切换主题时整体替换，
/// 再由 <see cref="ApplyTheme"/> 按控件原本的配色角色重新上色。
/// </summary>
internal static class UiTheme
{
    public static UiThemeMode Mode { get; private set; } = UiThemeMode.Dark;

    public static Color Background { get; private set; }        // 窗口底
    public static Color Surface { get; private set; }           // 卡片
    public static Color SurfaceAlt { get; private set; }        // 输入框 / 次级面
    public static Color SurfaceHi { get; private set; }         // 悬停面
    public static Color Border { get; private set; }
    public static Color BorderSoft { get; private set; }
    public static Color BorderStrong { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Dim { get; private set; }
    public static Color Accent { get; private set; }            // 琥珀：主操作 / 选中
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    public static Color AccentInk { get; private set; }         // 琥珀底上的字
    public static Color AccentSoft { get; private set; }        // 琥珀浅底
    public static Color Ok { get; private set; }
    public static Color Bad { get; private set; }
    public static Color BadSoft { get; private set; }
    public static Color BadLine { get; private set; }

    static UiTheme() => ApplyPalette(UiThemeMode.Dark);

    /// <summary>切换配色。只改调色板，界面上的控件由调用方再走 <see cref="ApplyTheme"/>。</summary>
    public static void SetMode(UiThemeMode mode)
    {
        Mode = mode;
        ApplyPalette(mode);
        EnableProcessDarkMode(mode == UiThemeMode.Dark);
    }

    /// <summary>让系统的滚动条 / 菜单等原生部分跟上主题（Win10 1809+ 的 uxtheme 未公开导出）。</summary>
    private static void EnableProcessDarkMode(bool dark)
    {
        try { SetPreferredAppMode(dark ? 2 : 3); } catch { }   // 2=ForceDark 3=ForceLight
        try { FlushMenuThemes(); } catch { }
    }

    private static void ApplyPalette(UiThemeMode mode)
    {
        if (mode == UiThemeMode.Light)
        {
            Background = Color.FromArgb(243, 245, 248);
            Surface = Color.FromArgb(255, 255, 255);
            SurfaceAlt = Color.FromArgb(241, 243, 246);
            SurfaceHi = Color.FromArgb(231, 235, 240);
            Border = Color.FromArgb(215, 221, 228);
            BorderSoft = Color.FromArgb(230, 234, 239);
            BorderStrong = Color.FromArgb(185, 194, 204);
            Text = Color.FromArgb(26, 30, 36);
            Muted = Color.FromArgb(93, 104, 116);
            Dim = Color.FromArgb(150, 161, 172);
            Accent = Color.FromArgb(192, 118, 0);
            AccentHover = Color.FromArgb(217, 140, 20);
            AccentPressed = Color.FromArgb(158, 97, 0);
            AccentInk = Color.FromArgb(255, 255, 255);
            AccentSoft = Color.FromArgb(251, 239, 216);
            Ok = Color.FromArgb(23, 138, 70);
            Bad = Color.FromArgb(210, 60, 74);
            BadSoft = Color.FromArgb(253, 236, 238);
            BadLine = Color.FromArgb(231, 179, 184);
            return;
        }

        Background = Color.FromArgb(12, 14, 16);
        Surface = Color.FromArgb(18, 21, 25);
        SurfaceAlt = Color.FromArgb(25, 29, 34);
        SurfaceHi = Color.FromArgb(31, 36, 42);
        Border = Color.FromArgb(38, 44, 51);
        BorderSoft = Color.FromArgb(30, 36, 41);
        BorderStrong = Color.FromArgb(52, 60, 69);
        Text = Color.FromArgb(231, 234, 238);
        Muted = Color.FromArgb(154, 164, 175);
        Dim = Color.FromArgb(107, 118, 129);
        Accent = Color.FromArgb(240, 163, 42);
        AccentHover = Color.FromArgb(255, 184, 77);
        AccentPressed = Color.FromArgb(217, 143, 28);
        AccentInk = Color.FromArgb(20, 16, 8);
        AccentSoft = Color.FromArgb(42, 33, 20);
        Ok = Color.FromArgb(70, 196, 107);
        Bad = Color.FromArgb(240, 97, 109);
        BadSoft = Color.FromArgb(42, 23, 25);
        BadLine = Color.FromArgb(90, 42, 46);
    }

    /// <summary>正文：Segoe UI（中文由系统自动回退到微软雅黑 UI）。</summary>
    public static Font Body(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static void Apply(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        ApplyFormColors(form);
        form.Font = Body();
        form.HandleCreated += (_, _) => ApplyWindowChrome(form.Handle);
        if (form.IsHandleCreated) ApplyWindowChrome(form.Handle);
    }

    /// <summary>顶层窗口：深色标题栏 + 允许深色主题，子窗口的滚动条才会跟着变暗。</summary>
    public static void ApplyWindowChrome(IntPtr handle)
    {
        var dark = Mode == UiThemeMode.Dark;
        SetDarkTitleBar(handle, dark);
        try { AllowDarkModeForWindow(handle, dark); } catch { }
        try { SetWindowTheme(handle, dark ? "DarkMode_Explorer" : null, null); } catch { }
    }

    /// <summary>窗口底色 / 字色跟随当前主题（切主题后单独调用）。</summary>
    public static void ApplyFormColors(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
    }

    /// <summary>标题栏深/浅：只设 DWM 的沉浸式深色模式属性，不动窗体主题类。</summary>
    public static void SetDarkTitleBar(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        try
        {
            AllowDarkModeForWindow(handle, dark);
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch
        {
        }
    }

    private static readonly PropertyInfo? DoubleBufferedProperty =
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// 递归给面板类容器开双缓冲。设置面板是 TableLayoutPanel 嵌卡片的多层结构，
    /// 每一层都会先擦自己的背景再画子控件，不缓冲时整块界面会跟着刷新一跳一跳地闪。
    /// </summary>
    public static void EnableDoubleBuffering(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Form or Panel)
            {
                try { DoubleBufferedProperty?.SetValue(child, true); } catch { }
            }
            EnableDoubleBuffering(child);
        }
    }

    // ---------- 滚动条深色 ----------

    private static readonly List<WeakReference<Control>> ChromeTargets = new();

    /// <summary>让控件里的系统滚动条跟随主题（深色主题下不要出现亮色滚动条）。</summary>
    public static void EnableDarkChrome(Control control)
    {
        ChromeTargets.Add(new WeakReference<Control>(control));
        control.HandleCreated += (_, _) => DeferChrome(control);
        if (control.IsHandleCreated) DeferChrome(control);
    }

    /// <summary>切主题后重新套用滚动条配色。</summary>
    public static void RefreshChrome()
    {
        for (var i = ChromeTargets.Count - 1; i >= 0; i--)
        {
            if (!ChromeTargets[i].TryGetTarget(out var control))
            {
                ChromeTargets.RemoveAt(i);
                continue;
            }
            ApplyChrome(control);
        }
    }

    private static void DeferChrome(Control control)
    {
        // 句柄刚创建时正是父窗口创建子窗口的阶段，延后到消息循环再设主题更稳
        try { control.BeginInvoke(new Action(() => ApplyChrome(control))); }
        catch { }
    }

    private static void ApplyChrome(Control control)
    {
        try
        {
            if (!control.IsHandleCreated) return;
            var dark = Mode == UiThemeMode.Dark;
            AllowDarkModeForWindow(control.Handle, dark);
            SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null);
            // 让非客户区（滚动条）立刻重画，否则切主题后滚动条还停在上一套配色
            SetWindowPos(control.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
        }
    }

    // ---------- 主题切换：按配色角色重新上色 ----------

    private enum ColorRole
    {
        None,
        Background, Surface, SurfaceAlt, SurfaceHi, Border, BorderSoft, BorderStrong,
        Text, Muted, Dim, Accent, AccentHover, AccentPressed, AccentInk, AccentSoft,
        Ok, Bad
    }

    private sealed class ThemeBinding
    {
        public ColorRole Back = ColorRole.None;
        public ColorRole Fore = ColorRole.None;
        public ColorRole Link = ColorRole.None;
        public ColorRole ActiveLink = ColorRole.None;
        public ColorRole VisitedLink = ColorRole.None;
        public ColorRole FlatBorder = ColorRole.None;
        public ColorRole FlatChecked = ColorRole.None;
        public ColorRole FlatHover = ColorRole.None;
    }

    private static readonly ConditionalWeakTable<Control, ThemeBinding> Bindings = new();

    /// <summary>
    /// 在界面按初始主题构建完成后调用一次：把每个控件的颜色记成"角色"，
    /// 之后切主题就能按角色换成另一套配色，而不用重建控件树。
    /// </summary>
    public static void CaptureTheme(Control root)
    {
        foreach (var control in Enumerate(root))
        {
            var binding = new ThemeBinding
            {
                Back = RoleOf(control.BackColor),
                Fore = RoleOf(control.ForeColor)
            };

            if (control is LinkLabel link)
            {
                binding.Link = RoleOf(link.LinkColor);
                binding.ActiveLink = RoleOf(link.ActiveLinkColor);
                binding.VisitedLink = RoleOf(link.VisitedLinkColor);
            }

            if (control is CheckBox or RadioButton)
            {
                var flat = control is CheckBox cb ? cb.FlatAppearance : ((RadioButton)control).FlatAppearance;
                binding.FlatBorder = RoleOf(flat.BorderColor);
                binding.FlatChecked = RoleOf(flat.CheckedBackColor);
                binding.FlatHover = RoleOf(flat.MouseOverBackColor);
            }

            Bindings.AddOrUpdate(control, binding);
        }
    }

    /// <summary>切主题后调用：按捕获的角色把整棵控件树重刷成当前配色。</summary>
    public static void ApplyTheme(Control root)
    {
        foreach (var control in Enumerate(root))
        {
            if (!Bindings.TryGetValue(control, out var binding)) continue;

            if (binding.Back != ColorRole.None) control.BackColor = Resolve(binding.Back);
            if (binding.Fore != ColorRole.None) control.ForeColor = Resolve(binding.Fore);

            if (control is LinkLabel link)
            {
                if (binding.Link != ColorRole.None) link.LinkColor = Resolve(binding.Link);
                if (binding.ActiveLink != ColorRole.None) link.ActiveLinkColor = Resolve(binding.ActiveLink);
                if (binding.VisitedLink != ColorRole.None) link.VisitedLinkColor = Resolve(binding.VisitedLink);
            }

            if (control is CheckBox or RadioButton)
            {
                var flat = control is CheckBox cb ? cb.FlatAppearance : ((RadioButton)control).FlatAppearance;
                if (binding.FlatBorder != ColorRole.None) flat.BorderColor = Resolve(binding.FlatBorder);
                if (binding.FlatChecked != ColorRole.None) flat.CheckedBackColor = Resolve(binding.FlatChecked);
                if (binding.FlatHover != ColorRole.None) flat.MouseOverBackColor = Resolve(binding.FlatHover);
            }

            if (control is TabControl tabs) tabs.Invalidate(true);
            else control.Invalidate();
        }
    }

    private static IEnumerable<Control> Enumerate(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var item in Enumerate(child))
                yield return item;
    }

    private static ColorRole RoleOf(Color color)
    {
        if (color.IsEmpty) return ColorRole.None;
        var argb = color.ToArgb();
        if (argb == Background.ToArgb()) return ColorRole.Background;
        if (argb == Surface.ToArgb()) return ColorRole.Surface;
        if (argb == SurfaceAlt.ToArgb()) return ColorRole.SurfaceAlt;
        if (argb == SurfaceHi.ToArgb()) return ColorRole.SurfaceHi;
        if (argb == Border.ToArgb()) return ColorRole.Border;
        if (argb == BorderSoft.ToArgb()) return ColorRole.BorderSoft;
        if (argb == BorderStrong.ToArgb()) return ColorRole.BorderStrong;
        if (argb == Text.ToArgb()) return ColorRole.Text;
        if (argb == Muted.ToArgb()) return ColorRole.Muted;
        if (argb == Dim.ToArgb()) return ColorRole.Dim;
        if (argb == Accent.ToArgb()) return ColorRole.Accent;
        if (argb == AccentHover.ToArgb()) return ColorRole.AccentHover;
        if (argb == AccentPressed.ToArgb()) return ColorRole.AccentPressed;
        if (argb == AccentInk.ToArgb()) return ColorRole.AccentInk;
        if (argb == AccentSoft.ToArgb()) return ColorRole.AccentSoft;
        if (argb == Ok.ToArgb()) return ColorRole.Ok;
        if (argb == Bad.ToArgb()) return ColorRole.Bad;
        return ColorRole.None;
    }

    private static Color Resolve(ColorRole role) => role switch
    {
        ColorRole.Background => Background,
        ColorRole.Surface => Surface,
        ColorRole.SurfaceAlt => SurfaceAlt,
        ColorRole.SurfaceHi => SurfaceHi,
        ColorRole.Border => Border,
        ColorRole.BorderSoft => BorderSoft,
        ColorRole.BorderStrong => BorderStrong,
        ColorRole.Text => Text,
        ColorRole.Muted => Muted,
        ColorRole.Dim => Dim,
        ColorRole.Accent => Accent,
        ColorRole.AccentHover => AccentHover,
        ColorRole.AccentPressed => AccentPressed,
        ColorRole.AccentInk => AccentInk,
        ColorRole.AccentSoft => AccentSoft,
        ColorRole.Ok => Ok,
        ColorRole.Bad => Bad,
        _ => Color.Empty
    };

    /// <summary>输入控件跟随主题：下拉框整行自绘，展开列表也是当前配色。</summary>
    public static void StyleInput(Control control)
    {
        switch (control)
        {
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = SurfaceAlt;
                combo.ForeColor = Text;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.ItemHeight = Math.Max(18, combo.Font.Height + 6);
                if (!Equals(combo.Tag, "qde-dark"))
                {
                    combo.Tag = "qde-dark";
                    combo.DrawItem += DrawComboItem;
                }
                break;
            case NumericUpDown numeric:
                numeric.BackColor = SurfaceAlt;
                numeric.ForeColor = Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;
            case TextBox text:
                text.BackColor = SurfaceAlt;
                text.ForeColor = Text;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckedListBox list:
                list.BackColor = SurfaceAlt;
                list.ForeColor = Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox check:
                check.BackColor = Surface;
                check.ForeColor = Text;
                check.FlatStyle = FlatStyle.Flat;
                check.FlatAppearance.BorderColor = Border;
                check.FlatAppearance.CheckedBackColor = AccentSoft;
                check.FlatAppearance.MouseOverBackColor = SurfaceHi;
                break;
            case RadioButton radio:
                radio.BackColor = Surface;
                radio.ForeColor = Text;
                radio.FlatStyle = FlatStyle.Flat;
                radio.FlatAppearance.BorderColor = Border;
                radio.FlatAppearance.CheckedBackColor = AccentSoft;
                radio.FlatAppearance.MouseOverBackColor = SurfaceHi;
                break;
        }
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? AccentSoft : SurfaceAlt))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Index < 0) return;
        var text = combo.Items[e.Index]?.ToString() ?? "";
        TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -6, 0),
            selected ? Accent : Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

public enum UiButtonRole
{
    Secondary,
    Primary,
    Outline,
    Danger
}

/// <summary>
/// 下拉框：系统自带的组合框按钮和边框在深色底上会露出亮灰色方块，
/// 所以整块表面由自己重绘（底色 / 边框 / 文字 / 右侧箭头），
/// 交互仍是原生 ComboBox，展开列表走 <see cref="UiTheme.StyleInput"/> 的自绘项。
/// </summary>
public sealed class UiComboBox : ComboBox
{
    public UiComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_PAINT = 0x000F;
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT) PaintFace();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private void PaintFace()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = UiTheme.RoundedRectangle(bounds, Math.Max(3, DeviceDpi * 5 / 96)))
            {
                using (var brush = new SolidBrush(UiTheme.SurfaceAlt)) g.FillPath(brush, path);
                using (var pen = new Pen(Focused ? UiTheme.Accent : UiTheme.Border)) g.DrawPath(pen, path);
            }

            var arrow = Math.Max(10, DeviceDpi * 12 / 96);
            var textRect = new Rectangle(Math.Max(6, DeviceDpi * 8 / 96), 0,
                Math.Max(0, Width - arrow - Math.Max(10, DeviceDpi * 12 / 96)), Height);
            var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : string.Empty;
            TextRenderer.DrawText(g, text, Font, textRect,
                Enabled ? UiTheme.Text : UiTheme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            var cx = Width - arrow / 2 - Math.Max(4, DeviceDpi * 5 / 96);
            var cy = Height / 2;
            var w = Math.Max(3, DeviceDpi * 4 / 96);
            using var chevron = new Pen(UiTheme.Muted, Math.Max(1f, DeviceDpi / 96f));
            g.DrawLines(chevron, new[]
            {
                new Point(cx - w, cy - w / 2),
                new Point(cx, cy + w / 2 + 1),
                new Point(cx + w, cy - w / 2)
            });
        }
        catch
        {
        }
    }
}

/// <summary>自绘按钮：深色底 + 细边框，Primary 用琥珀实底，悬停有轻微提亮。</summary>
public sealed class UiButton : Button
{
    private bool _hover;
    private bool _pressed;
    private UiButtonRole _role = UiButtonRole.Secondary;

    public UiButtonRole Role
    {
        get => _role;
        set { _role = value; Invalidate(); }
    }

    public UiButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = UiTheme.Body(9.5f, FontStyle.Bold);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.Background))
            e.Graphics.FillRectangle(back, ClientRectangle);

        var fill = UiTheme.SurfaceAlt;
        var border = UiTheme.Border;
        var foreground = UiTheme.Text;

        switch (Role)
        {
            case UiButtonRole.Primary:
                fill = _pressed ? UiTheme.AccentPressed : _hover ? UiTheme.AccentHover : UiTheme.Accent;
                border = fill;
                foreground = UiTheme.AccentInk;
                break;
            case UiButtonRole.Outline:
                fill = _hover || _pressed ? UiTheme.SurfaceHi : UiTheme.Surface;
                border = _hover || _pressed ? UiTheme.Accent : UiTheme.Border;
                foreground = UiTheme.Text;
                break;
            case UiButtonRole.Danger:
                fill = _hover || _pressed ? UiTheme.BadSoft : UiTheme.Surface;
                border = _hover || _pressed ? UiTheme.Bad : UiTheme.BadLine;
                foreground = UiTheme.Bad;
                break;
            default:
                fill = _hover || _pressed ? UiTheme.SurfaceHi : UiTheme.SurfaceAlt;
                border = _hover || _pressed ? UiTheme.BorderStrong : UiTheme.Border;
                break;
        }
        if (!Enabled)
        {
            fill = UiTheme.Surface;
            border = UiTheme.BorderSoft;
            foreground = UiTheme.Dim;
        }

        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var radius = Math.Max(4, DeviceDpi * 6 / 96);
        using (var path = UiTheme.RoundedRectangle(bounds, radius))
        {
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            using (var pen = new Pen(border)) e.Graphics.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, flags);

        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -4, -4);
            using var focusPen = new Pen(Role == UiButtonRole.Primary ? UiTheme.AccentInk : UiTheme.Accent)
            { DashStyle = DashStyle.Dot };
            e.Graphics.DrawRectangle(focusPen, focus);
        }
    }
}

/// <summary>圆角卡片：1 像素边框；Brand=true 时整块填充琥珀色（顶栏徽标用）。</summary>
public class UiCardPanel : Panel
{
    public bool Brand { get; set; }

    public UiCardPanel()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(14, 10, 14, 10);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.Background))
            e.Graphics.FillRectangle(back, ClientRectangle);
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.RoundedRectangle(bounds, Math.Max(4, DeviceDpi * 10 / 96));
        using var brush = new SolidBrush(Brand ? UiTheme.Accent : BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.RoundedRectangle(bounds, Math.Max(4, DeviceDpi * 10 / 96));
        using var pen = new Pen(Brand ? UiTheme.AccentHover : UiTheme.Border);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>标签页：去掉系统标签条，自己画纯文字标签，选中项用琥珀下划线标记。</summary>
public sealed class UiTabControl : TabControl
{
    public UiTabControl()
    {
        DrawMode = TabDrawMode.Normal;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(120, 34);
        Font = UiTheme.Body(9.5f);
        Padding = Point.Empty;
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 句柄创建过程中改 ItemSize / Padding 会让 TabControl 重建句柄；
        // 若此时父窗口还在 WmShowWindow 里创建子窗口，就会抛 1400（句柄无效）。
        // 延后到消息循环里再设，尺寸也只影响外观。
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsHandleCreated) return;
                var scale = DeviceDpi / 96f;
                ItemSize = new Size((int)Math.Round(120 * scale), (int)Math.Round(34 * scale));
                Padding = new Point((int)Math.Round(10 * scale), (int)Math.Round(4 * scale));
                Invalidate();
            }));
        }
        catch
        {
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        if (m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero)
        {
            // 只擦标签条那一条，页面内容交给各自的面板，避免整片闪
            try
            {
                using var graphics = Graphics.FromHdc(m.WParam);
                using var brush = new SolidBrush(UiTheme.Background);
                graphics.FillRectangle(brush, new Rectangle(0, 0, ClientRectangle.Width, Math.Max(0, DisplayRectangle.Top)));
                m.Result = new IntPtr(1);
                return;
            }
            catch
            {
                // 句柄还没完全就绪时放弃擦除，交给系统处理
            }
        }
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;
        PaintChrome();
    }

    /// <summary>盖掉系统标签条和页面外沿的亮色 3D 边框，再自己画标签文字。</summary>
    private void PaintChrome()
    {
        if (!IsHandleCreated || TabCount == 0) return;
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var page = DisplayRectangle;
            using (var background = new SolidBrush(UiTheme.Background))
            {
                // 子控件绘制在父窗口之上，所以盖住页面区域也不会遮住内容
                g.FillRectangle(background, new Rectangle(0, 0, Width, Math.Max(0, page.Top)));
                g.FillRectangle(background, new Rectangle(0, page.Top - 3, Width, 6));
                g.FillRectangle(background, new Rectangle(0, 0, 3, Height));
                g.FillRectangle(background, new Rectangle(Width - 3, 0, 3, Height));
                g.FillRectangle(background, new Rectangle(0, Height - 3, Width, 3));
            }

            for (var i = 0; i < TabCount; i++)
            {
                var bounds = GetTabRect(i);
                var selected = i == SelectedIndex;
                using var font = UiTheme.Body(9.5f, selected ? FontStyle.Bold : FontStyle.Regular);
                TextRenderer.DrawText(g, TabPages[i].Text, font, bounds,
                    selected ? UiTheme.Text : UiTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            var lineY = Math.Max(0, page.Top - 1);
            using (var border = new Pen(UiTheme.Border))
                g.DrawLine(border, 0, lineY, Width, lineY);

            if (SelectedIndex >= 0)
            {
                var bounds = GetTabRect(SelectedIndex);
                var thickness = Math.Max(2, DeviceDpi * 2 / 96);
                var inset = Math.Max(8, DeviceDpi * 10 / 96);
                using var accent = new Pen(UiTheme.Accent, thickness);
                g.DrawLine(accent, bounds.Left + inset, lineY, bounds.Right - inset, lineY);
            }
        }
        catch
        {
            // 重绘失败不能影响交互，下一次 WM_PAINT 会再来
        }
    }
}

/// <summary>托盘菜单的深色渲染。</summary>
public sealed class UiToolStripRenderer : ToolStripProfessionalRenderer
{
    public UiToolStripRenderer() : base(new DarkColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? UiTheme.Text : UiTheme.Dim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = UiTheme.Text;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuBorder => UiTheme.Border;
        public override Color MenuItemBorder => UiTheme.Border;
        public override Color MenuItemSelected => UiTheme.SurfaceHi;
        public override Color MenuItemSelectedGradientBegin => UiTheme.SurfaceHi;
        public override Color MenuItemSelectedGradientEnd => UiTheme.SurfaceHi;
        public override Color MenuStripGradientBegin => UiTheme.Surface;
        public override Color MenuStripGradientEnd => UiTheme.Surface;
        public override Color ToolStripDropDownBackground => UiTheme.Surface;
        public override Color ToolStripBorder => UiTheme.Border;
        public override Color SeparatorDark => UiTheme.Border;
        public override Color SeparatorLight => UiTheme.SurfaceAlt;
        public override Color ImageMarginGradientBegin => UiTheme.Surface;
        public override Color ImageMarginGradientMiddle => UiTheme.Surface;
        public override Color ImageMarginGradientEnd => UiTheme.Surface;
        public override Color OverflowButtonGradientBegin => UiTheme.Surface;
        public override Color OverflowButtonGradientMiddle => UiTheme.Surface;
        public override Color OverflowButtonGradientEnd => UiTheme.Surface;
    }
}

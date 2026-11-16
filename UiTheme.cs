using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>桌面端统一的深色琥珀主题与轻量自绘控件。</summary>
internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(7, 8, 10);
    public static readonly Color Surface = Color.FromArgb(17, 19, 24);
    public static readonly Color SurfaceAlt = Color.FromArgb(25, 28, 35);
    public static readonly Color Border = Color.FromArgb(43, 47, 56);
    public static readonly Color Text = Color.FromArgb(236, 238, 243);
    public static readonly Color Muted = Color.FromArgb(139, 144, 156);
    public static readonly Color Dim = Color.FromArgb(90, 95, 106);
    public static readonly Color Accent = Color.FromArgb(255, 180, 84);
    public static readonly Color AccentHover = Color.FromArgb(255, 198, 114);
    public static readonly Color AccentPressed = Color.FromArgb(232, 155, 56);
    public static readonly Color AccentInk = Color.FromArgb(26, 18, 6);
    public static readonly Color Ok = Color.FromArgb(110, 231, 168);
    public static readonly Color Bad = Color.FromArgb(255, 128, 128);

    public static Font Body(float size = 9.5f, FontStyle style = FontStyle.Regular) =>
        new("Microsoft YaHei UI", size, style);

    public static Font Display(float size, FontStyle style = FontStyle.Regular) =>
        new("Bahnschrift", size, style, GraphicsUnit.Point);

    public static Font Mono(float size = 11f, FontStyle style = FontStyle.Regular) =>
        new("Cascadia Mono", size, style, GraphicsUnit.Point);

    public static void Apply(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body();
        form.HandleCreated += (_, _) => TryDarkTitleBar(form.Handle);
        if (form.IsHandleCreated) TryDarkTitleBar(form.Handle);
    }

    public static void EnableDarkChrome(Control control, string theme = "DarkMode_Explorer")
    {
        if (!OperatingSystem.IsWindows()) return;
        control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, theme, null);
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, theme, null);
    }

    public static void StyleInput(Control control)
    {
        if (control is ComboBox) EnableDarkChrome(control, "DarkMode_CFD");
        else if (control is NumericUpDown or TextBox or CheckedListBox) EnableDarkChrome(control);

        switch (control)
        {
            case ComboBox combo:
                combo.BackColor = SurfaceAlt;
                combo.ForeColor = Text;
                combo.FlatStyle = FlatStyle.Standard;
                break;
            case NumericUpDown numeric:
                numeric.BackColor = SurfaceAlt;
                numeric.ForeColor = Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                foreach (Control child in numeric.Controls) EnableDarkChrome(child);
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
                check.FlatAppearance.CheckedBackColor = SurfaceAlt;
                check.FlatAppearance.MouseOverBackColor = SurfaceAlt;
                break;
            case RadioButton radio:
                radio.BackColor = Surface;
                radio.ForeColor = Text;
                radio.FlatStyle = FlatStyle.Flat;
                radio.FlatAppearance.BorderColor = Border;
                radio.FlatAppearance.CheckedBackColor = SurfaceAlt;
                radio.FlatAppearance.MouseOverBackColor = SurfaceAlt;
                break;
        }
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

    private static void TryDarkTitleBar(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        try
        {
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string? subIdList);
}

public enum UiButtonRole
{
    Secondary,
    Primary,
    Outline,
    Danger
}

/// <summary>保留 Button 键盘与对话框行为，只接管视觉状态。</summary>
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
        Cursor = Cursors.Hand;
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
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false; Invalidate(); base.OnMouseUp(e);
    }
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

        if (SystemInformation.HighContrast)
        {
            fill = SystemColors.Control;
            border = SystemColors.ControlText;
            foreground = SystemColors.ControlText;
        }
        else
        {
            switch (Role)
            {
                case UiButtonRole.Primary:
                    fill = _pressed ? UiTheme.AccentPressed : _hover ? UiTheme.AccentHover : UiTheme.Accent;
                    border = fill;
                    foreground = UiTheme.AccentInk;
                    break;
                case UiButtonRole.Outline:
                    border = UiTheme.Accent;
                    foreground = UiTheme.Accent;
                    if (_hover || _pressed) fill = Color.FromArgb(42, 35, 24);
                    break;
                case UiButtonRole.Danger:
                    border = _hover || _pressed ? UiTheme.Bad : Color.FromArgb(112, 62, 62);
                    foreground = UiTheme.Bad;
                    if (_hover || _pressed) fill = Color.FromArgb(48, 27, 29);
                    break;
                default:
                    if (_hover || _pressed) fill = Color.FromArgb(34, 38, 47);
                    break;
            }
            if (!Enabled)
            {
                fill = UiTheme.SurfaceAlt;
                border = UiTheme.Border;
                foreground = UiTheme.Dim;
            }
        }

        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var radius = Math.Max(4, DeviceDpi * 8 / 96);
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

/// <summary>不使用透明 BackColor 的圆角卡片，避免子控件闪烁。</summary>
public class UiCardPanel : Panel
{
    public bool AccentBorder { get; set; }

    public UiCardPanel()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(18, 14, 18, 14);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? UiTheme.Background))
            e.Graphics.FillRectangle(back, ClientRectangle);
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.RoundedRectangle(bounds, Math.Max(4, DeviceDpi * 12 / 96));
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = UiTheme.RoundedRectangle(bounds, Math.Max(4, DeviceDpi * 12 / 96));
        using var pen = new Pen(AccentBorder ? UiTheme.Accent : UiTheme.Border);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>保留原生 TabControl 行为，仅自绘深色分段标签。</summary>
public sealed class UiTabControl : TabControl
{
    public UiTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(144, 42);
        Padding = new Point(12, 8);
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Text;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowTheme(Handle, "", "");
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        if (m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero)
        {
            // 原生控件只给标签本身发 OwnerDraw 消息，标签条右侧仍由系统擦成亮色。
            // 在擦底阶段处理可避免覆盖 TabPage，也不引入双缓冲造成的旧页面残影。
            using var graphics = Graphics.FromHdc(m.WParam);
            using var brush = new SolidBrush(UiTheme.Background);
            graphics.FillRectangle(brush, ClientRectangle);
            m.Result = new IntPtr(1);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;

        // 即使禁用 uxtheme，原生 TabControl 仍会在页面外沿画出亮色 3D 边。
        // 原生绘制结束后只覆盖这两圈边缘，不接管标签或页面内容。
        using var g = Graphics.FromHwnd(Handle);
        var page = DisplayRectangle;
        using (var background = new Pen(UiTheme.Background, 3))
            g.DrawRectangle(background, page.Left - 2, page.Top - 2, page.Width + 3, page.Height + 3);
        using var border = new Pen(UiTheme.Border);
        g.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = e.Index == SelectedIndex;
        var bounds = GetTabRect(e.Index);
        using var back = new SolidBrush(selected ? UiTheme.Surface : UiTheme.Background);
        e.Graphics.FillRectangle(back, bounds);
        if (selected)
            using (var accent = new SolidBrush(UiTheme.Accent))
                e.Graphics.FillRectangle(accent, bounds.Left + 12, bounds.Bottom - 3, bounds.Width - 24, 3);
        var color = selected ? UiTheme.Text : UiTheme.Muted;
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), UiTheme.Accent, UiTheme.Surface);
    }
}

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
        public override Color MenuItemSelected => UiTheme.SurfaceAlt;
        public override Color MenuItemSelectedGradientBegin => UiTheme.SurfaceAlt;
        public override Color MenuItemSelectedGradientEnd => UiTheme.SurfaceAlt;
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

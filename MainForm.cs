using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>
/// 设置 / 控制面板。设计目标：
///  - 一眼看到"服务在不在跑"和"手机该访问哪个地址"；
///  - 只暴露用户真正需要改的东西（复制 / 扩展、画质、端口密码、关闭行为），
///    引擎参数（UDP、x264 预设、单客户端带宽、线程数……）不再放到界面上；
///  - 深色琥珀控制台：常驻运行状态与访问地址，四个设置分区，固定保存操作栏。
/// </summary>
public sealed class MainForm : Form
{
    public const string CloseAsk = "ask";
    public const string CloseTray = "tray";
    public const string CloseExit = "exit";

    private readonly AppHost _host;
    private readonly UiSettings _ui;

    // 顶部状态
    private readonly Label _lblStatus = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold) };
    private readonly UiButton _btnStart = new() { Text = "启动服务", AutoSize = false, Size = new Size(104, 34) };
    private readonly UiButton _btnStop = new() { Text = "停止服务", AutoSize = false, Size = new Size(104, 34) };

    // 访问地址
    private readonly Label _lblUrl = new() { AutoSize = true, Font = UiTheme.Mono(11.5f, FontStyle.Bold) };
    private readonly Label _lblStats = new() { AutoSize = true, ForeColor = UiTheme.Muted };
    private readonly UiButton _btnCopy = new() { Text = "复制", AutoSize = false, Size = new Size(64, 30) };
    private readonly UiButton _btnOpen = new() { Text = "打开网页", AutoSize = false, Size = new Size(96, 30) };
    private readonly UiButton _btnLogs = new() { Text = "日志", AutoSize = false, Size = new Size(64, 30) };

    // 显示
    private readonly RadioButton _rbCopy = new() { Text = "复制屏幕", AutoSize = true, Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold) };
    private readonly RadioButton _rbLinkLan = new() { Text = "局域网地址", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f) };
    private readonly RadioButton _rbLinkUsb = new() { Text = "USB 数据线", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f) };
    private readonly RadioButton _rbLinkBoth = new() { Text = "两种都允许", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f) };
    private readonly Label _lblUsbStatus = new() { AutoSize = true, MaximumSize = new Size(700, 0) };
    private readonly UiButton _btnUsbRefresh = new() { Text = "重新检测 USB" };
    private readonly RadioButton _rbExtend = new() { Text = "扩展屏", AutoSize = true, Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold) };
    private readonly ComboBox _cmbSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    private readonly UiButton _btnVirtualCfg = new() { Text = "配置扩展屏…", AutoSize = true };
    private readonly Label _lblSourceHint = new() { AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = UiTheme.Muted };
    private readonly Label _lblVdd = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, Font = UiTheme.Body(8.5f) };
    private readonly CheckBox _chkCursor = new() { Text = "把本机鼠标指针画进画面", AutoSize = true };
    private readonly CheckBox _chkInput = new() { Text = "允许在浏览器里用鼠标 / 触摸操作电脑", AutoSize = true };

    // 画质
    private readonly ComboBox _cmbFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly NumericUpDown _numBitrate = new() { Minimum = 1, Maximum = 50, DecimalPlaces = 1, Increment = 0.5m, Width = 140 };
    private readonly ComboBox _cmbMaxRes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _chkAdaptive = new() { Text = "网络差时自动降码率 / 降分辨率", AutoSize = true };
    private readonly ComboBox _cmbJpegFps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _cmbJpegWidth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };

    // 其他
    private readonly NumericUpDown _numHttpPort = new() { Minimum = 1, Maximum = 65535, Width = 140 };
    private readonly NumericUpDown _numWsPort = new() { Minimum = 1, Maximum = 65535, Width = 140 };
    private readonly TextBox _txtPassword = new() { Width = 200, UseSystemPasswordChar = false };
    private readonly CheckBox _chkLoopbackOnly = new() { Text = "只允许本机访问（默认允许局域网）", AutoSize = true };
    private readonly CheckBox _chkAutoStart = new() { Text = "开机自动启动（登录后在托盘串流）", AutoSize = true };
    private readonly ComboBox _cmbCloseAction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly ComboBox _cmbLogLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };

    private readonly UiButton _btnSave = new() { Text = "保存配置", AutoSize = false, Size = new Size(110, 36) };
    private readonly UiButton _btnSaveRestart = new() { Text = "保存并重启服务", AutoSize = false, Size = new Size(150, 36) };

    private readonly Label _lblMsg = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, AutoEllipsis = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private bool _loading;
    private bool _restartPending;
    private DateTime _restartAt;
    private string _lastUsbText = "";
    private System.Windows.Forms.Timer? _copyResetTimer;

    public bool ExitRequested { get; private set; }
    public AppHost Host => _host;

    public MainForm(AppHost host)
    {
        _host = host;
        _ui = UiSettings.Load(Path.Combine(AppContext.BaseDirectory, "uisettings.json"));

        Text = "QDesktopExtension";
        // 在设置尺寸与布局前启用 DPI 自动缩放和深色标题栏。
        UiTheme.Apply(this);
        Font = UiTheme.Body();
        // 默认居中到系统主屏：不这么写的话，接了扩展屏 / 虚拟显示器后
        // 面板会被摆到那块屏上，用户在主屏上看不到窗口（实测过）
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        ClientSize = new Size(Math.Min(960, wa.Width - 60), Math.Min(740, wa.Height - 60));
        MinimumSize = new Size(840, 620);
        Location = new Point(wa.X + Math.Max(0, (wa.Width - Width) / 2), wa.Y + Math.Max(0, (wa.Height - Height) / 2));
        DoubleBuffered = false;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _cmbFps.Items.AddRange(new object[] { "15 帧", "20 帧", "25 帧", "30 帧", "45 帧", "60 帧" });
        _cmbMaxRes.Items.AddRange(new object[] { "跟随显示器分辨率", "1920 × 1080", "2560 × 1440", "1280 × 720" });
        _cmbJpegFps.Items.AddRange(new object[] { "15 帧", "20 帧", "25 帧", "30 帧" });
        _cmbJpegWidth.Items.AddRange(new object[] { "640 像素", "960 像素", "1280 像素", "1600 像素" });
        _cmbCloseAction.Items.AddRange(new object[] { "每次询问我", "只关闭面板（后台继续串流）", "退出程序并停止服务" });
        _cmbLogLevel.Items.AddRange(new object[] { "只记错误", "普通", "详细（排查问题用）" });

        StyleButtons();
        BuildLayout();
        LoadFromConfig(_host.Config);
        _chkAutoStart.Checked = IsAutoStart();
        SelectCloseAction(_ui.CloseAction);

        _btnStart.Click += (_, _) => StartService();
        _btnStop.Click += (_, _) => StopService();
        _btnOpen.Click += (_, _) => OpenWeb();
        _btnLogs.Click += (_, _) => OpenLogs();
        _btnCopy.Click += (_, _) => CopyUrl();
        _btnSave.Click += (_, _) => SaveConfig(false);
        _btnSaveRestart.Click += (_, _) => SaveConfig(true);
        _rbCopy.CheckedChanged += (_, _) => OnModeChanged(_rbCopy);
        _rbExtend.CheckedChanged += (_, _) => OnModeChanged(_rbExtend);
        _cmbSource.SelectedIndexChanged += (_, _) => UpdateSourceHint();

        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => { _timer.Start(); Tick(); };
        FormClosing += OnFormClosing;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // WinForms 会随 DPI 同步缩放 MinimumSize，跨屏后可能缩到无法操作的大小。
        MinimumSize = new Size(840, 620);
        if (Width < MinimumSize.Width || Height < MinimumSize.Height)
            Size = new Size(Math.Max(Width, MinimumSize.Width), Math.Max(Height, MinimumSize.Height));
    }

    // ================= 外观 =================
    private void StyleButtons()
    {
        _btnStart.Role = UiButtonRole.Primary;
        _btnCopy.Role = UiButtonRole.Primary;
        _btnSaveRestart.Role = UiButtonRole.Primary;
        _btnSave.Role = UiButtonRole.Outline;

        _btnStart.Size = _btnStop.Size = new Size(112, 36);
        _btnCopy.Size = new Size(96, 36);
        _btnCopy.Text = "复制地址";
        _btnOpen.Size = new Size(104, 36);
        _btnLogs.Size = new Size(76, 34);
        _btnUsbRefresh.Size = new Size(128, 32);
        _btnVirtualCfg.Size = new Size(128, 32);
        _btnSave.Size = new Size(112, 36);
        _btnSaveRestart.Size = new Size(158, 36);

        foreach (var input in new Control[]
        {
            _cmbSource, _cmbFps, _numBitrate, _cmbMaxRes, _cmbJpegFps, _cmbJpegWidth,
            _numHttpPort, _numWsPort, _txtPassword, _cmbCloseAction, _cmbLogLevel,
            _rbCopy, _rbExtend, _rbLinkLan, _rbLinkUsb, _rbLinkBoth,
            _chkCursor, _chkInput, _chkAdaptive, _chkLoopbackOnly, _chkAutoStart
        }) UiTheme.StyleInput(input);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, H(84)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, H(62)));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(24, 12, 24, 10),
            BackColor = UiTheme.Background
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var mark = new UiCardPanel { Size = new Size(52, 46), Margin = new Padding(0, 0, 14, 0), Padding = Padding.Empty, AccentBorder = true };
        mark.Controls.Add(new Label
        {
            Text = "QD",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiTheme.Accent,
            Font = UiTheme.Display(13f, FontStyle.Bold)
        });

        var identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Background };
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, H(30)));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, H(24)));
        identity.Controls.Add(new Label
        {
            Text = "QDesktopExtension",
            AutoSize = true,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Display(16f, FontStyle.Bold)
        }, 0, 0);
        identity.Controls.Add(new Label
        {
            Text = "远程桌面控制台  /  REMOTE DESKTOP",
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Body(8.5f)
        }, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.Background,
            Margin = Padding.Empty
        };
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.BackColor = UiTheme.Background;
        _lblStatus.Font = UiTheme.Body(9.5f, FontStyle.Bold);
        _lblStatus.Margin = new Padding(0, 9, 14, 0);
        _btnLogs.Margin = new Padding(0, 0, 10, 0);
        _btnStop.Margin = new Padding(0);
        _btnStart.Margin = new Padding(0);
        actions.Controls.Add(_lblStatus);
        actions.Controls.Add(_btnLogs);
        actions.Controls.Add(_btnStop);
        actions.Controls.Add(_btnStart);

        layout.Controls.Add(mark, 0, 0);
        layout.Controls.Add(identity, 1, 0);
        layout.Controls.Add(actions, 2, 0);
        header.Controls.Add(layout);
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        return header;
    }

    private Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = HPad(18, 14, 18, 8),
            BackColor = UiTheme.Background
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, H(124)));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildAddressCard(), 0, 0);
        body.Controls.Add(BuildTabs(), 0, 1);
        return body;
    }

    private Control BuildAddressCard()
    {
        var card = new UiCardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), AccentBorder = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Surface };
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, H(26)));
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, H(34)));
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, H(26)));
        text.Controls.Add(new Label { Text = "手机 / 平板 / 另一台电脑访问", AutoSize = true, ForeColor = UiTheme.Muted }, 0, 0);
        _lblUrl.ForeColor = UiTheme.Text;
        _lblUrl.AutoSize = false;
        _lblUrl.Dock = DockStyle.Fill;
        _lblUrl.AutoEllipsis = true;
        _lblUrl.TextAlign = ContentAlignment.MiddleLeft;
        _lblUrl.Cursor = Cursors.Hand;
        _lblUrl.Click += (_, _) => OpenWeb();
        text.Controls.Add(_lblUrl, 0, 1);
        _lblStats.AutoSize = false;
        _lblStats.Dock = DockStyle.Fill;
        _lblStats.AutoEllipsis = true;
        _lblStats.Font = UiTheme.Body(8.5f);
        text.Controls.Add(_lblStats, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Anchor = AnchorStyles.None,
            Margin = new Padding(16, 0, 0, 0)
        };
        _btnCopy.Margin = new Padding(0, 0, 10, 0);
        buttons.Controls.Add(_btnCopy);
        buttons.Controls.Add(_btnOpen);
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildTabs()
    {
        var tabs = new UiTabControl { Dock = DockStyle.Fill, Font = UiTheme.Body(9.5f) };
        tabs.TabPages.Add(BuildDisplayTab());
        tabs.TabPages.Add(BuildLinkTab());
        tabs.TabPages.Add(BuildQualityTab());
        tabs.TabPages.Add(BuildOtherTab());
        return tabs;
    }

    private TabPage BuildDisplayTab()
    {
        var page = NewPage("显示采集");
        var layout = PageGrid(page);

        var modes = AddCard(layout, 152, "显示模式");
        AddWideRow(modes, BuildModeChoices(), 82);

        // 多客户端独立屏（DESIGN-multi-client 第 4 步）：让用户能看到"屏建成没有、被谁占着"
        var vdd = AddCard(layout, 132, "虚拟扩展屏（每台设备一块）");
        _lblVdd.MaximumSize = Size.Empty;
        AddWideRow(vdd, _lblVdd, 92);

        var source = AddCard(layout, 224, "采集与交互");
        var srcRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbSource.Dock = DockStyle.Fill;
        _cmbSource.Margin = new Padding(0, 3, 12, 3);
        _btnVirtualCfg.AutoSize = false;
        _btnVirtualCfg.Margin = new Padding(0, 2, 0, 2);
        _btnVirtualCfg.Click += (_, _) => ConfigureVirtualScreen();
        srcRow.Controls.Add(_cmbSource, 0, 0);
        srcRow.Controls.Add(_btnVirtualCfg, 1, 0);
        AddRow(source, "采集来源", srcRow, 42);

        _lblSourceHint.AutoSize = false;
        _lblSourceHint.Dock = DockStyle.Fill;
        _lblSourceHint.MaximumSize = Size.Empty;
        _lblSourceHint.Font = UiTheme.Body(8.5f);
        AddWideRow(source, _lblSourceHint, 46);
        AddRow(source, "", _chkCursor, 32);
        AddRow(source, "", _chkInput, 32);
        return page;
    }

    private Control BuildModeChoices()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        UiCardPanel Choice(RadioButton radio, string description, bool first)
        {
            var card = new UiCardPanel
            {
                Dock = DockStyle.Fill,
                BackColor = UiTheme.SurfaceAlt,
                Padding = new Padding(12, 7, 12, 7),
                Margin = first ? new Padding(0, 0, 12, 0) : new Padding(0),
                Cursor = Cursors.Hand
            };
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.SurfaceAlt };
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, H(32)));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            radio.AutoSize = false;
            radio.Dock = DockStyle.Fill;
            radio.BackColor = UiTheme.SurfaceAlt;
            radio.Font = UiTheme.Body(10f, FontStyle.Bold);
            radio.Margin = Padding.Empty;
            var hint = new Label
            {
                Text = description,
                AutoSize = false,
                Dock = DockStyle.Fill,
                BackColor = UiTheme.SurfaceAlt,
                ForeColor = UiTheme.Muted,
                Font = UiTheme.Body(8.5f),
                Cursor = Cursors.Hand
            };
            stack.Controls.Add(radio, 0, 0);
            stack.Controls.Add(hint, 0, 1);
            card.Controls.Add(stack);
            card.Click += (_, _) => radio.Checked = true;
            hint.Click += (_, _) => radio.Checked = true;
            return card;
        }

        var copy = Choice(_rbCopy, "浏览器显示电脑当前屏幕的画面。", true);
        var extend = Choice(_rbExtend, "作为独立显示器，可摆放窗口并单独设置分辨率。", false);
        table.Controls.Add(copy, 0, 0);
        table.Controls.Add(extend, 1, 0);

        void SyncChoice()
        {
            copy.AccentBorder = _rbCopy.Checked;
            extend.AccentBorder = _rbExtend.Checked;
            copy.Invalidate();
            extend.Invalidate();
        }
        _rbCopy.CheckedChanged += (_, _) => SyncChoice();
        _rbExtend.CheckedChanged += (_, _) => SyncChoice();
        SyncChoice();
        return table;
    }

    private TabPage BuildLinkTab()
    {
        var page = NewPage("连接访问");
        var layout = PageGrid(page);

        var connection = AddCard(layout, 218, "连接方式");
        var modes = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty
        };
        _rbLinkLan.Margin = new Padding(0, 4, 28, 0);
        _rbLinkUsb.Margin = new Padding(0, 4, 28, 0);
        _rbLinkBoth.Margin = new Padding(0, 4, 0, 0);
        modes.Controls.Add(_rbLinkLan);
        modes.Controls.Add(_rbLinkUsb);
        modes.Controls.Add(_rbLinkBoth);
        AddWideRow(connection, modes, 38);
        AddWideRow(connection, Hint("局域网用于同一网络下的浏览器访问；USB 需要手机开启调试并允许本机，拔掉数据线后可回退到局域网。"), 36);

        _lblUsbStatus.AutoSize = false;
        _lblUsbStatus.Dock = DockStyle.Fill;
        _lblUsbStatus.MaximumSize = Size.Empty;
        _lblUsbStatus.AutoEllipsis = true;
        _lblUsbStatus.ForeColor = UiTheme.Muted;
        AddRow(connection, "USB 状态", _lblUsbStatus, 38);

        _btnUsbRefresh.Text = "重新检测 USB";
        _btnUsbRefresh.AutoSize = false;
        _btnUsbRefresh.Click += (_, _) =>
        {
            // Refresh 内部要 Join adb 进程（最长 1.5 秒），同步调用会把整个界面卡住
            _btnUsbRefresh.Enabled = false;
            SetMsg("正在重新检测 USB 设备…");
            var usb = _host.Usb;
            Task.Run(() => { try { usb?.Refresh(); } catch { } })
                .ContinueWith(_ =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _btnUsbRefresh.Enabled = _host.Running;
                            SetMsg("USB 设备检测完成");
                        }));
                    }
                    catch
                    {
                    }
                });
        };
        AddRow(connection, "", _btnUsbRefresh, 36);

        var security = AddCard(layout, 260, "访问安全");
        AddRow(security, "网页端口", _numHttpPort, 40);
        AddRow(security, "推流端口", _numWsPort, 40);
        AddRow(security, "访问密码", _txtPassword, 40);
        AddRow(security, "", _chkLoopbackOnly, 32);
        AddWideRow(security, Hint("端口被占用时可修改；端口、密码和采集来源改动需重启服务后生效。"), 38);
        return page;
    }

    private TabPage BuildQualityTab()
    {
        var page = NewPage("画质性能");
        var layout = PageGrid(page);

        var basic = AddCard(layout, 260, "基础画质");
        AddRow(basic, "帧率", _cmbFps, 40);
        AddRow(basic, "码率（Mbps）", _numBitrate, 40);
        AddRow(basic, "画面尺寸上限", _cmbMaxRes, 40);
        AddRow(basic, "", _chkAdaptive, 32);
        AddWideRow(basic, Hint("码率越高越清晰、越占带宽；局域网通常 8 Mbps 即可，弱网络建议 3～5 Mbps。"), 38);

        var jpeg = AddCard(layout, 188, "浏览器兼容模式");
        AddRow(jpeg, "JPEG 帧率", _cmbJpegFps, 40);
        AddRow(jpeg, "JPEG 画面宽度", _cmbJpegWidth, 40);
        AddWideRow(jpeg, Hint("浏览器无法解码 H.264 时自动使用此通道；降低宽度可减少卡顿。"), 38);
        return page;
    }

    private TabPage BuildOtherTab()
    {
        var page = NewPage("系统维护");
        var layout = PageGrid(page);

        var startup = AddCard(layout, 142, "启动与关闭");
        AddRow(startup, "", _chkAutoStart, 32);
        AddRow(startup, "关闭窗口时", _cmbCloseAction, 40);

        var diagnostics = AddCard(layout, 148, "诊断与日志");
        AddRow(diagnostics, "日志详细程度", _cmbLogLevel, 40);
        AddWideRow(diagnostics, Hint("排查问题时可选“详细”；日志目录可通过顶部“日志”按钮打开。"), 38);
        return page;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(20, 10, 20, 10) };
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = UiTheme.Surface,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty
        };
        _btnSave.Margin = new Padding(0, 0, 10, 0);
        _btnSaveRestart.Margin = Padding.Empty;
        buttons.Controls.Add(_btnSave);
        buttons.Controls.Add(_btnSaveRestart);

        _lblMsg.Margin = new Padding(0, 0, 16, 0);
        row.Controls.Add(_lblMsg, 0, 0);
        row.Controls.Add(buttons, 1, 0);
        footer.Controls.Add(row);
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        return footer;
    }

    // ---------- 小工具 ----------
    /// <summary>
    /// 设计像素（96 DPI 基准）→ 当前 DPI 像素。字体随 DPI 变大，但代码里写死的行高、内边距不会自己缩放，
    /// 不换算就会出现"字比行高、说明文字被拦腰裁掉"（140% 缩放下实测复现）。
    /// </summary>
    private int H(int designPx) => (int)Math.Round(designPx * DeviceDpi / 96f);
    private Padding HPad(int left, int top, int right, int bottom) => new(H(left), H(top), H(right), H(bottom));
    private TabPage NewPage(string text) => new(text)
    {
        BackColor = UiTheme.Background,
        Padding = Padding.Empty
    };

    private TableLayoutPanel PageGrid(TabPage page)
    {
        // AutoScroll 与 Dock=Fill 放在同一个 TableLayoutPanel 上时，较高页面出现滚动条
        // 会反复改变可用宽度并触发布局循环。外层只负责滚动，内层按固定行高向下增长。
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Background,
            AutoScroll = true
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            BackColor = UiTheme.Background,
            Padding = HPad(16, 16, 16, 16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(layout);
        page.Controls.Add(scroll);
        return layout;
    }

    private TableLayoutPanel AddCard(TableLayoutPanel page, int height, string title, string? description = null)
    {
        var row = page.RowCount++;
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, H(height)));
        var card = new UiCardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, H(12)),
            Padding = HPad(18, 14, 18, 14)
        };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = description == null ? 2 : 3,
            BackColor = UiTheme.Surface
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, H(32)));
        stack.Controls.Add(SectionTitle(title), 0, 0);
        var formRow = 1;
        if (description != null)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, H(36)));
            stack.Controls.Add(new Label
            {
                Text = description,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Muted,
                Font = UiTheme.Body(8.5f),
                Margin = Padding.Empty
            }, 0, 1);
            formRow = 2;
        }
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, H(150)));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.Controls.Add(form, 0, formRow);
        card.Controls.Add(stack);
        page.Controls.Add(card, 0, row);
        return form;
    }

    private Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = UiTheme.Body(10f, FontStyle.Bold),
        ForeColor = UiTheme.Text,
        Margin = Padding.Empty
    };

    private Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        ForeColor = UiTheme.Muted,
        Font = UiTheme.Body(8.5f),
        Margin = Padding.Empty
    };

    private void AddRow(TableLayoutPanel table, string label, Control control, int height = 34)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, H(height)));
        if (label.Length > 0)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.Text,
                Margin = Padding.Empty
            }, 0, row);
        }

        control.Margin = new Padding(0, 3, 0, 3);
        if (control is ComboBox or TextBox || control.Dock == DockStyle.Fill)
            control.Dock = DockStyle.Fill;
        else
            control.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        if (control is NumericUpDown) control.Width = 180;
        table.Controls.Add(control, 1, row);
    }

    private void AddWideRow(TableLayoutPanel table, Control control, int height)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, H(height)));
        control.Dock = DockStyle.Fill;
        control.Margin = Padding.Empty;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    // ================= 配置读写 =================
    public void LoadFromConfig(ConfigModel cfg)
    {
        _loading = true;
        try
        {
            _cmbFps.SelectedIndex = FpsToIndex(cfg.Fps);
            _numBitrate.Value = Math.Clamp((decimal)cfg.BitRate / 1_000_000m, _numBitrate.Minimum, _numBitrate.Maximum);
            _cmbMaxRes.SelectedIndex = MaxResToIndex(cfg.MaxWidth, cfg.MaxHeight);
            _chkAdaptive.Checked = cfg.EnableAdaptive;
            _cmbJpegFps.SelectedIndex = JpegFpsToIndex(cfg.JpegFps);
            _cmbJpegWidth.SelectedIndex = JpegWidthToIndex(cfg.JpegWidth);

            _numHttpPort.Value = Math.Clamp((decimal)cfg.HttpPort, _numHttpPort.Minimum, _numHttpPort.Maximum);
            _numWsPort.Value = Math.Clamp((decimal)cfg.WsPort, _numWsPort.Minimum, _numWsPort.Maximum);
            _txtPassword.Text = cfg.Password;
            _chkLoopbackOnly.Checked = cfg.IsLoopbackOnly();
            _cmbLogLevel.SelectedIndex = cfg.LogLevel switch
            {
                LogLevel.Debug => 2,
                LogLevel.Warn or LogLevel.Error => 0,
                _ => 1
            };
            _chkCursor.Checked = cfg.DrawCursor;
            _chkInput.Checked = cfg.AllowRemoteInput;

            _rbLinkLan.Checked = cfg.LinkMode == LinkMode.Lan;
            _rbLinkUsb.Checked = cfg.LinkMode == LinkMode.Usb;
            _rbLinkBoth.Checked = cfg.LinkMode == LinkMode.Both;

            var source = CaptureSource.FromConfig(cfg);
            _rbExtend.Checked = source.Virtual || source.MonitorIndex > 0;
            _rbCopy.Checked = !_rbExtend.Checked;
            RebuildSourceList(source.Virtual ? null : (source.IsWindow ? null : source.MonitorIndex), source.Virtual);
        }
        finally
        {
            _loading = false;
        }
        UpdateSourceHint();
    }

    private static int FpsToIndex(int fps) => fps switch
    {
        <= 15 => 0, <= 20 => 1, <= 25 => 2, <= 30 => 3, <= 45 => 4, _ => 5
    };

    private static readonly int[] FpsValues = { 15, 20, 25, 30, 45, 60 };

    private static int MaxResToIndex(int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        if (w >= 7680 && h >= 4320) return 0;   // 旧版"跟随显示器"写法
        if (w >= 2560 && h >= 1440) return 2;
        if (w >= 1920 && h >= 1080) return 1;
        if (w >= 1280 && h >= 720) return 3;
        return 0;
    }

    private static int JpegFpsToIndex(int fps) => fps switch { <= 15 => 0, <= 20 => 1, <= 25 => 2, _ => 3 };
    private static readonly int[] JpegFpsValues = { 15, 20, 25, 30 };

    private static int JpegWidthToIndex(int w) => w switch { <= 640 => 0, <= 960 => 1, <= 1280 => 2, _ => 3 };
    private static readonly int[] JpegWidthValues = { 640, 960, 1280, 1600 };

    private void SelectSource(CaptureSource source)
    {
        for (var i = 0; i < _cmbSource.Items.Count; i++)
        {
            if (_cmbSource.Items[i] is SourceChoice sc)
            {
                if (source.Virtual && sc.Source.Virtual) { _cmbSource.SelectedIndex = i; return; }
                if (!source.Virtual && !sc.Source.Virtual && sc.Source.MonitorIndex == source.MonitorIndex)
                { _cmbSource.SelectedIndex = i; return; }
            }
        }
        if (_cmbSource.Items.Count > 0) _cmbSource.SelectedIndex = 0;
    }

    /// <summary>
    /// 按当前模式重建"采集哪块屏"列表。
    /// 复制：所有显示器（多屏时补一项"全部显示器"）；扩展：主屏之外的真显示器 + 无驱动时的软件扩展屏。
    /// </summary>
    private void RebuildSourceList(int? keepMonitor, bool keepVirtual)
    {
        _cmbSource.BeginUpdate();
        try
        {
            _cmbSource.Items.Clear();
            var monitors = DisplayCapture.GetMonitors();
            if (_rbCopy.Checked)
            {
                for (var i = 0; i < monitors.Count; i++)
                    _cmbSource.Items.Add(new SourceChoice(CaptureSource.Monitor(i), $"显示器 {i + 1}（{monitors[i].Width}×{monitors[i].Height}）"));
                if (monitors.Count > 1)
                    _cmbSource.Items.Add(new SourceChoice(CaptureSource.Monitor(-1), "全部显示器（拼成一整块）"));
                if (_cmbSource.Items.Count == 0)
                    _cmbSource.Items.Add(new SourceChoice(CaptureSource.Monitor(0), "显示器 1"));
            }
            else
            {
                for (var i = 1; i < monitors.Count; i++)
                    _cmbSource.Items.Add(new SourceChoice(CaptureSource.Monitor(i), $"系统里的第 {i + 1} 块屏（{monitors[i].Width}×{monitors[i].Height}）"));
                _cmbSource.Items.Add(new SourceChoice(CaptureSource.VirtualSource(), "软件扩展屏（没装虚拟显示器驱动时用这个）"));
            }
        }
        finally
        {
            _cmbSource.EndUpdate();
        }

        var want = keepVirtual ? CaptureSource.VirtualSource() : CaptureSource.Monitor(keepMonitor ?? 0);
        SelectSource(want);
        if (_cmbSource.SelectedIndex < 0 && _cmbSource.Items.Count > 0) _cmbSource.SelectedIndex = 0;
    }

    private void OnModeChanged(RadioButton changed)
    {
        if (_loading || !changed.Checked) return;
        if (changed == _rbCopy) _rbExtend.Checked = false;
        else _rbCopy.Checked = false;

        var keepVirtual = _cmbSource.SelectedItem is SourceChoice sc && sc.Source.Virtual;
        var keepMonitor = (_cmbSource.SelectedItem as SourceChoice)?.Source.MonitorIndex ?? 0;
        RebuildSourceList(keepVirtual ? null : keepMonitor, _rbExtend.Checked && keepVirtual);
        UpdateSourceHint();
    }

    private void UpdateSourceHint()
    {
        if (_rbCopy.Checked)
        {
            _lblSourceHint.Text = "浏览器看到的是哪块屏的画面。多显示器时可以只复制主屏，也可以整块拼起来。";
            return;
        }

        var monitors = DisplayCapture.GetMonitors().Count;
        var selected = (_cmbSource.SelectedItem as SourceChoice)?.Source;
        _lblSourceHint.Text = selected is { Virtual: true }
            ? "软件扩展屏：不装驱动也能用，但它是一块由程序画出来的画布，系统显示设置里看不到它。"
            : monitors > 1
                ? "这块屏在系统里已经作为真实显示器存在：可以拖动摆位、单独设分辨率，鼠标移过去就能用，空桌面也正常显示。"
                : "系统里目前只有一块屏。要真的“多一块显示器”，请在本机安装虚拟显示驱动（面板 → 日志同目录里的 install-vdd.ps1，装完无需重启）。";
    }

    private ConfigModel ReadFromUi()
    {
        var cfg = _host.Config;
        cfg.Password = string.IsNullOrWhiteSpace(_txtPassword.Text) ? "123456" : _txtPassword.Text.Trim();
        cfg.BindAddress = _chkLoopbackOnly.Checked ? "127.0.0.1" : "0.0.0.0";

        cfg.Fps = FpsValues[Math.Max(0, _cmbFps.SelectedIndex)];
        cfg.BitRate = (long)(_numBitrate.Value * 1_000_000m);

        switch (_cmbMaxRes.SelectedIndex)
        {
            case 2: cfg.MaxWidth = 2560; cfg.MaxHeight = 1440; break;
            case 1: cfg.MaxWidth = 1920; cfg.MaxHeight = 1080; break;
            case 3: cfg.MaxWidth = 1280; cfg.MaxHeight = 720; break;
            default: cfg.MaxWidth = 0; cfg.MaxHeight = 0; break;
        }
        cfg.EnableAdaptive = _chkAdaptive.Checked;
        cfg.JpegFps = JpegFpsValues[Math.Max(0, _cmbJpegFps.SelectedIndex)];
        cfg.JpegWidth = JpegWidthValues[Math.Max(0, _cmbJpegWidth.SelectedIndex)];

        cfg.HttpPort = (int)_numHttpPort.Value;
        cfg.WsPort = (int)_numWsPort.Value;
        cfg.LogLevel = _cmbLogLevel.SelectedIndex switch
        {
            2 => LogLevel.Debug,
            0 => LogLevel.Warn,
            _ => LogLevel.Info
        };
        cfg.DrawCursor = _chkCursor.Checked;
        cfg.AllowRemoteInput = _chkInput.Checked;

        cfg.LinkMode = _rbLinkLan.Checked ? LinkMode.Lan : _rbLinkUsb.Checked ? LinkMode.Usb : LinkMode.Both;

        var chosen = (_cmbSource.SelectedItem as SourceChoice)?.Source ?? CaptureSource.Monitor(0);
        cfg.CaptureWindow = 0;
        cfg.CaptureWindowTitle = "";
        if (chosen.Virtual)
        {
            cfg.UseVirtualScreen = true;
        }
        else
        {
            cfg.UseVirtualScreen = false;
            cfg.CaptureMonitor = chosen.MonitorIndex;
        }

        cfg.Normalize();
        return cfg;
    }

    // ================= 操作 =================
    private void StartService()
    {
        if (_host.Running) return;
        _host.Start(ReadFromUi());
        SetMsg("正在启动服务…");
        Task.Delay(1200).ContinueWith(_ => BeginInvoke(new Action(() =>
        {
            if (_host.LastError != null) SetMsg("启动失败: " + _host.LastError, true);
            else if (_host.Running) SetMsg("服务已启动，浏览器打开上面的地址即可");
        })));
    }

    private void StopService()
    {
        if (!_host.Running) { SetMsg("服务当前没有运行"); return; }
        SetMsg("正在停止服务…");
        _host.RequestStop();
        Task.Delay(1500).ContinueWith(_ => BeginInvoke(new Action(() => SetMsg(_host.Running ? "停止中…" : "已停止"))));
    }

    private void SaveConfig(bool restart)
    {
        try
        {
            var cfg = ReadFromUi();
            ConfigModel.Save(cfg, _host.ConfigPath);
            _host.ApplySavedConfig(cfg);
            SetAutoStart(_chkAutoStart.Checked, Application.ExecutablePath);
            _ui.CloseAction = SelectedCloseAction();
            _ui.Save();
            LoadFromConfig(cfg);
            if (restart)
            {
                // Restart 内部是后台线程：先停旧服务再起新服务，界面不阻塞
                _host.Restart(cfg);
                _restartPending = true;
                _restartAt = DateTime.Now;
                SetMsg("配置已保存，正在按新配置重启…");
            }
            else
            {
                SetMsg("配置已保存（端口 / 密码 / 采集源改动需要重启服务才生效）");
            }
        }
        catch (Exception ex)
        {
            SetMsg("保存失败: " + ex.Message, true);
        }
    }

    private string CurrentUrl()
    {
        var port = _host.Config.HttpPort;
        if (_host.Config.IsLoopbackOnly()) return $"http://127.0.0.1:{port}/";
        var ips = AppHost.LocalAddresses();
        return ips.Count > 0 ? $"http://{ips[0]}:{port}/" : $"http://localhost:{port}/";
    }

    private void OpenWeb()
    {
        try { Process.Start(new ProcessStartInfo(CurrentUrl()) { UseShellExecute = true }); }
        catch (Exception ex) { SetMsg("打开浏览器失败: " + ex.Message, true); }
    }

    private void CopyUrl()
    {
        try
        {
            Clipboard.SetText(CurrentUrl());
            SetMsg("访问地址已复制：" + CurrentUrl());
            _btnCopy.Text = "已复制";
            _copyResetTimer?.Stop();
            _copyResetTimer?.Dispose();
            _copyResetTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _copyResetTimer.Tick += (_, _) =>
            {
                _btnCopy.Text = "复制地址";
                _copyResetTimer?.Stop();
                _copyResetTimer?.Dispose();
                _copyResetTimer = null;
            };
            _copyResetTimer.Start();
        }
        catch (Exception ex) { SetMsg("复制失败: " + ex.Message, true); }
    }

    private void OpenLogs()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, _host.Config.LogPath ?? "./logs");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { SetMsg("打开日志目录失败: " + ex.Message, true); }
    }

    /// <summary>配置软件扩展屏：选窗口 + 画布尺寸。保存配置后由管线按这份清单合成画面。</summary>
    private void ConfigureVirtualScreen()
    {
        var cfg = _host.Config;
        using var dlg = new VirtualScreenForm(cfg.VirtualWindows, cfg.VirtualScreenWidth, cfg.VirtualScreenHeight);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // 记下窗口现在的位置，客户端"取消投放"时才好还原
        foreach (var w in dlg.SelectedWindows)
        {
            var rc = WindowSource.GetWindowRectOf(new IntPtr(w.Hwnd));
            if (rc == null) continue;
            w.X = rc.Value.X;
            w.Y = rc.Value.Y;
            w.W = rc.Value.Width;
            w.H = rc.Value.Height;
        }

        cfg.VirtualWindows = dlg.SelectedWindows;
        cfg.VirtualScreenWidth = dlg.CanvasWidth;
        cfg.VirtualScreenHeight = dlg.CanvasHeight;
        cfg.UseVirtualScreen = true;

        // 直接切到"扩展屏 + 软件扩展屏"，用户点保存就能用
        _rbExtend.Checked = true;
        RebuildSourceList(null, true);
        SelectSource(CaptureSource.VirtualSource());
        UpdateSourceHint();
        SetMsg($"扩展屏已配置 {cfg.VirtualWindows.Count} 个窗口（{cfg.VirtualScreenWidth}×{cfg.VirtualScreenHeight}）；点【保存并重启服务】后生效");
    }

    private void Tick()
    {
        var stats = _host.Stats;
        if (_host.Running && stats != null)
        {
            _lblStatus.Text = "● 服务运行中";
            _lblStatus.ForeColor = UiTheme.Ok;
            _lblStats.Text = $"{stats.Fps} fps  ·  {stats.Kbps} kbps  ·  {(_host.Server?.AuthedClients ?? 0)} 台客户端  ·  运行 {stats.UptimeSeconds}s";
        }
        else
        {
            var err = _host.LastError ?? "";
            _lblStatus.Text = err.Length > 0 ? "● 服务异常" : "○ 服务已停止";
            _lblStatus.ForeColor = err.Length > 0 ? UiTheme.Bad : UiTheme.Muted;
            _lblStats.Text = err.Length > 0 ? err : "启动服务后，即可从浏览器远程访问这台电脑";
        }

        var url = CurrentUrl();
        if (_lblUrl.Text != url) _lblUrl.Text = url;

        RefreshVddStatus();

        var usb = _host.Usb;
        var usbText = usb == null
            ? (_host.Running ? "USB：未启用" : "USB：服务未启动")
            : "USB：" + usb.Status;
        if (_lastUsbText != usbText)
        {
            _lblUsbStatus.Text = usbText;
            _lastUsbText = usbText;
        }

        _btnUsbRefresh.Enabled = _host.Running;

        _btnStart.Enabled = !_host.Running;
        _btnStop.Enabled = _host.Running;
        _btnStart.Visible = !_host.Running;
        _btnStop.Visible = _host.Running;

        // 重启是后台异步做的，这里把结果回显出来（否则用户只会看到"正在重启…"不动）
        if (_restartPending && (DateTime.Now - _restartAt).TotalSeconds > 1.2)
        {
            if (_host.LastError != null)
            {
                _restartPending = false;
                SetMsg("重启失败: " + _host.LastError, true);
            }
            else if (_host.Running)
            {
                _restartPending = false;
                SetMsg("已按新配置重启完成");
            }
            else if ((DateTime.Now - _restartAt).TotalSeconds > 12)
            {
                _restartPending = false;
                SetMsg("重启超时：请点【停止服务】后再点【启动服务】", true);
            }
        }
    }

    private void SetMsg(string text, bool error = false)
    {
        _lblMsg.Text = text;
        _lblMsg.ForeColor = error ? UiTheme.Bad : UiTheme.Muted;
    }

    // ================= 虚拟扩展屏状态 =================
    private string _lastVddText = "";

    private void RefreshVddStatus()
    {
        var screens = _host.ClientScreens;
        string text;
        if (!_host.Running || screens == null)
        {
            text = _host.Running
                ? "Parsec VDD 不可用，所有设备共享主屏画面"
                : "服务未启动。启动后，每台接入的设备会自动获得一块自己的扩展屏（Parsec VDD）";
        }
        else if (!screens.DriverReady)
        {
            text = "Parsec VDD 不可用（未安装驱动或驱动打不开），所有设备共享主屏画面";
        }
        else
        {
            var list = screens.SnapshotScreens();
            var head = $"驱动就绪 · 已建 {list.Count}/{ParsecVdd.MaxClientScreens} 块";
            if (list.Count == 0)
            {
                text = head + "。有设备接入时会自动插一块新显示器并推给它独立画面";
            }
            else
            {
                text = head + "：" + string.Join("；", list.Select(s =>
                    $"{s.Device} ({s.Size}) ← {s.Ip}{(s.Orphan ? "（已断开，等待重连）" : "")}"));
            }
        }
        if (text != _lastVddText)
        {
            _lblVdd.Text = text;
            _lastVddText = text;
        }
    }

    // ================= 关闭行为 =================
    private void SelectCloseAction(string action)
    {
        _cmbCloseAction.SelectedIndex = action switch { CloseTray => 1, CloseExit => 2, _ => 0 };
    }

    private string SelectedCloseAction() => _cmbCloseAction.SelectedIndex switch
    {
        1 => CloseTray,
        2 => CloseExit,
        _ => CloseAsk
    };

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "QDesktopExtension";

    private static bool IsAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool on, string exePath)
    {
        try
        {
            if (on)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key?.SetValue(RunValueName, "\"" + exePath + "\"");
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private bool _asking;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            ExitRequested = true;
            _host.RequestStop();
            return;
        }

        if (ExitRequested)
        {
            _host.Stop(8000);
            return;
        }

        var action = _ui.CloseAction;
        if (action == CloseAsk)
        {
            e.Cancel = true;
            if (_asking) return;
            _asking = true;
            var t = new System.Windows.Forms.Timer { Interval = 50 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); AskThenAct(); };
            t.Start();
            return;
        }

        if (action == CloseTray)
        {
            e.Cancel = true;
            Hide();
            SetMsg("已收起到托盘，串流继续；双击托盘图标可再打开面板");
            return;
        }

        ExitRequested = true;
        _host.Stop(8000);
    }

    private void AskThenAct()
    {
        try
        {
            string? chosen;
            bool remember;
            using (var dlg = new CloseChoiceForm())
            {
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.ShowDialog(this);
                chosen = dlg.Action;
                remember = dlg.Remember;
            }

            if (chosen == null) { SetMsg("已取消关闭"); return; }

            if (remember)
            {
                _ui.CloseAction = chosen;
                _ui.Save();
                SelectCloseAction(chosen);
            }

            if (chosen == CloseTray)
            {
                Hide();
                SetMsg("已收起到托盘，串流继续；双击托盘图标可再打开面板");
                return;
            }

            ExitRequested = true;
            _host.Stop(8000);
            Close();
        }
        finally
        {
            _asking = false;
        }
    }

    public sealed record SourceChoice(CaptureSource Source, string Label)
    {
        public override string ToString() => Label;
    }
}

namespace QDesktopExtension;

/// <summary>点窗口右上角关闭时弹的三选一：只关面板 / 退出并停服务 / 取消。</summary>
public sealed class CloseChoiceForm : Form
{
    /// <summary>返回值："tray" 只关面板、"exit" 退出程序、null 取消关闭。</summary>
    public string? Action { get; private set; }
    public bool Remember { get; private set; }

    public CloseChoiceForm()
    {
        Text = "关闭控制面板";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(500, 330);
        UiTheme.Apply(this);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(22),
            BackColor = UiTheme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        root.Controls.Add(new Label
        {
            Text = "关闭面板后要做什么？",
            AutoSize = true,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Body(12f, FontStyle.Bold)
        }, 0, 0);
        root.Controls.Add(new Label
        {
            Text = "服务可以在后台继续运行，也可以随程序一起退出。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted
        }, 0, 1);

        var btnTray = new UiButton
        {
            Text = "只关闭面板\r\n后台继续串流，双击托盘图标可重新打开",
            Role = UiButtonRole.Primary,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };
        var btnExit = new UiButton
        {
            Text = "退出程序\r\n停止串流并释放端口",
            Role = UiButtonRole.Danger,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };
        var chkRemember = new CheckBox
        {
            Text = "记住我的选择（之后可在“系统维护”中修改）",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = UiTheme.Background
        };
        UiTheme.StyleInput(chkRemember);
        chkRemember.BackColor = UiTheme.Background;
        var btnCancel = new UiButton { Text = "取消", Size = new Size(92, 34), DialogResult = DialogResult.Cancel };
        var cancelBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = UiTheme.Background,
            Margin = Padding.Empty
        };
        cancelBar.Controls.Add(btnCancel);

        btnTray.Click += (_, _) => { Action = "tray"; Remember = chkRemember.Checked; Close(); };
        btnExit.Click += (_, _) => { Action = "exit"; Remember = chkRemember.Checked; Close(); };

        root.Controls.Add(btnTray, 0, 2);
        root.Controls.Add(btnExit, 0, 3);
        root.Controls.Add(chkRemember, 0, 4);
        root.Controls.Add(cancelBar, 0, 5);
        Controls.Add(root);
        AcceptButton = btnTray;
        CancelButton = btnCancel;
    }
}

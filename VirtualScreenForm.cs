namespace QDesktopExtension;

/// <summary>
/// 虚拟扩展屏配置：勾选要放到这块"屏幕"上的窗口，设定画布尺寸。
/// 完全不依赖虚拟显示器驱动，所以任何 Windows 电脑都能用。
/// </summary>
public sealed class VirtualScreenForm : Form
{
    private readonly CheckedListBox _list = new();
    private readonly NumericUpDown _numW = new();
    private readonly NumericUpDown _numH = new();
    private readonly List<string> _titles = new();
    private readonly Label _lblCount = new() { Text = "已选择 0 / 4", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = UiTheme.Muted };

    /// <summary>确定后回传选中的窗口（按标题 + 当前句柄）。</summary>
    public List<VirtualWindowCfg> SelectedWindows { get; } = new();
    public int CanvasWidth => (int)_numW.Value;
    public int CanvasHeight => (int)_numH.Value;

    public VirtualScreenForm(List<VirtualWindowCfg> current, int width, int height)
    {
        Text = "配置扩展屏（手机当第二块屏）";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 540);
        UiTheme.Apply(this);
        Font = UiTheme.Body(9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = UiTheme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = UiTheme.Background };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Background };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label
        {
            Text = "配置软件扩展屏",
            AutoSize = true,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Body(11f, FontStyle.Bold)
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Text = "勾选要投放的程序，最多 4 个，自动按 1 / 2 / 4 宫格排版。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted
        }, 0, 1);
        var btnRefresh = new UiButton { Text = "刷新列表", Size = new Size(108, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnRefresh.Click += (_, _) => Fill(current);
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(btnRefresh, 1, 0);

        _list.Dock = DockStyle.Fill;
        _list.CheckOnClick = true;
        _list.IntegralHeight = false;
        _list.Font = UiTheme.Body(9f);
        UiTheme.StyleInput(_list);

        _numW.Minimum = 320; _numW.Maximum = 3840; _numW.Increment = 16;
        _numW.Value = Math.Max(320, Math.Min(3840, width));
        _numH.Minimum = 180; _numH.Maximum = 2160; _numH.Increment = 16;
        _numH.Value = Math.Max(180, Math.Min(2160, height));
        UiTheme.StyleInput(_numW);
        UiTheme.StyleInput(_numH);

        var dimensionsCard = new UiCardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 12), Padding = new Padding(14, 8, 14, 8) };
        var dimensions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, BackColor = UiTheme.Surface };
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dimensions.Controls.Add(new Label { Text = "画布尺寸", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = UiTheme.Text }, 0, 0);
        _numW.Dock = DockStyle.Fill;
        _numH.Dock = DockStyle.Fill;
        dimensions.Controls.Add(_numW, 1, 0);
        dimensions.Controls.Add(new Label { Text = "×", AutoSize = true, Anchor = AnchorStyles.None, ForeColor = UiTheme.Muted, Margin = new Padding(10, 0, 10, 0) }, 2, 0);
        dimensions.Controls.Add(_numH, 3, 0);
        dimensions.Controls.Add(_lblCount, 4, 0);
        dimensionsCard.Controls.Add(dimensions);

        var ok = new UiButton { Text = "确定", DialogResult = DialogResult.OK, Size = new Size(96, 34), Role = UiButtonRole.Primary };
        var cancel = new UiButton { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(96, 34) };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = UiTheme.Background,
            Margin = Padding.Empty
        };
        footer.Controls.Add(cancel);
        footer.Controls.Add(ok);

        _list.ItemCheck += (_, e) =>
        {
            if (e.NewValue == CheckState.Checked && _list.CheckedItems.Count + 1 > 4)
            {
                e.NewValue = CheckState.Unchecked;
                MessageBox.Show(this, "最多只能放 4 个窗口", "扩展屏",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (IsHandleCreated) BeginInvoke(new Action(UpdateCount));
        };

        ok.Click += (_, _) =>
        {
            SelectedWindows.Clear();
            foreach (var item in _list.CheckedItems)
            {
                var idx = _list.Items.IndexOf(item);
                SelectedWindows.Add(new VirtualWindowCfg
                {
                    Title = _titles[idx],
                    Hwnd = new IntPtr(_handles[idx]).ToInt64()
                });
            }
            if (SelectedWindows.Count == 0)
            {
                MessageBox.Show(this, "至少勾选一个窗口", "扩展屏",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(dimensionsCard, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        Fill(current);
    }

    private long[] _handles = Array.Empty<long>();

    private void Fill(List<VirtualWindowCfg> current)
    {
        var wanted = current.Select(w => w.Title).ToHashSet();
        _list.Items.Clear();
        _titles.Clear();
        var wins = WindowSource.List((uint)Environment.ProcessId);
        _handles = new long[wins.Count];
        var index = 0;
        foreach (var w in wins)
        {
            _list.Items.Add($"{Trunc(w.Title, 46)}  （{w.Process}，{w.Width}x{w.Height}）");
            _titles.Add(w.Title);
            _handles[index] = w.Handle;
            if (wanted.Contains(w.Title)) _list.SetItemChecked(index, true);
            index++;
        }
        UpdateCount();
    }

    private void UpdateCount() => _lblCount.Text = $"已选择 {_list.CheckedItems.Count} / 4";

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}


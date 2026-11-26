using System.Drawing;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>"显示采集"页签的构建逻辑（从 MainForm 拆出）。</summary>
public sealed partial class MainForm
{
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
}

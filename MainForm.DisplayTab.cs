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

        var modes = AddCard(layout, 112, "显示模式");
        AddWideRow(modes, BuildModeChoices(), 62);

        // 多客户端独立屏（DESIGN-multi-client 第 4 步）：让用户能看到"屏建成没有、被谁占着"
        var vdd = AddCard(layout, 96, "虚拟扩展屏（每台设备一块）");
        _lblVdd.MaximumSize = Size.Empty;
        AddWideRow(vdd, _lblVdd, 46);

        var source = AddCard(layout, 202, "采集与交互");
        _cmbSource.Dock = DockStyle.Fill;
        _cmbSource.Margin = Padding.Empty;
        AddRow(source, "采集来源", _cmbSource, 42);

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
        // 原生写法：一行两个单选项，说明文字放在下一行，不用自绘卡片
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = UiTheme.Surface };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, H(30)));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        void Place(RadioButton radio, string description, int column)
        {
            radio.AutoSize = false;
            radio.Dock = DockStyle.Fill;
            radio.TextAlign = ContentAlignment.MiddleLeft;
            radio.Margin = Padding.Empty;

            var hint = new Label
            {
                Text = description,
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Muted,
                Margin = new Padding(column == 0 ? 0 : H(8), 0, 0, 0)
            };
            table.Controls.Add(radio, column, 0);
            table.Controls.Add(hint, column, 1);
        }

        Place(_rbCopy, "浏览器显示电脑当前屏幕的画面。", 0);
        Place(_rbExtend, "作为独立显示器，可摆放窗口并单独设置分辨率。", 1);
        return table;
    }
}

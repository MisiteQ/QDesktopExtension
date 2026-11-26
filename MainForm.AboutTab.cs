using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>"关于"页签的构建逻辑（从 MainForm 拆出）。</summary>
public sealed partial class MainForm
{
    private TabPage BuildAboutTab()
    {
        var page = NewPage(AboutTabText);
        var layout = PageGrid(page);

        // 品牌头：沿用卡片语言，左侧应用标记，右侧名称 / 版本 / 一句话定位
        var hero = AddBareCard(layout, 140, HPad(20, 16, 20, 16));
        var heroGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Surface };
        heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, H(80)));
        heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var mark = new UiCardPanel
        {
            Size = new Size(H(64), H(64)),
            Margin = new Padding(0, H(4), H(16), 0),
            Padding = Padding.Empty,
            AccentBorder = true
        };
        mark.Controls.Add(new Label
        {
            Text = "QD",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiTheme.Accent,
            Font = UiTheme.Display(15f, FontStyle.Bold)
        });

        var identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Surface };
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, H(34)));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, H(24)));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        identity.Controls.Add(new Label
        {
            Text = "QDesktopExtension",
            AutoSize = true,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Display(16f, FontStyle.Bold)
        }, 0, 0);
        identity.Controls.Add(new Label
        {
            Text = $"版本 {AppVersion()}　·　桌面扩展平台",
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Body(9f)
        }, 0, 1);
        identity.Controls.Add(new Label
        {
            Text = "把电脑画面实时投到手机、平板或另一台电脑的浏览器，并可用触摸或鼠标键盘反向操作。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.Body(9f)
        }, 0, 2);

        heroGrid.Controls.Add(mark, 0, 0);
        heroGrid.Controls.Add(identity, 1, 0);
        hero.Controls.Add(heroGrid);

        var author = AddCard(layout, 174, "作者与项目");
        AddRow(author, "作者", ValueLabel(AuthorName), 36);
        AddRow(author, "GitHub", ValueLink(AuthorUrl), 36);
        AddWideRow(author, Hint("个人项目，欢迎在 GitHub 上交流或反馈问题。"), 38);

        var runtime = AddCard(layout, 248, "运行环境");
        AddRow(runtime, "程序版本", ValueLabel("v" + AppVersion()), 36);
        AddRow(runtime, "运行时", ValueLabel($".NET {Environment.Version}"), 36);
        AddRow(runtime, "操作系统", ValueLabel(RuntimeInformation.OSDescription), 36);
        AddRow(runtime, "程序目录", ValueLabel(AppContext.BaseDirectory), 36);
        var copy = new UiButton { Text = "复制版本信息", Role = UiButtonRole.Outline, AutoSize = false, Size = new Size(132, 32) };
        copy.Margin = new Padding(0, 4, 0, 4);
        copy.Click += (_, _) => CopyVersionInfo();
        AddRow(runtime, "", copy, 40);

        var credits = AddCard(layout, 202, "组件与致谢");
        AddRow(credits, "Fleck", ValueLabel("WebSocket 服务端"), 32);
        AddRow(credits, "FFmpeg", ValueLabel("H.264 编码与 MP4 封装"), 32);
        AddRow(credits, "Parsec VDD", ValueLabel("虚拟扩展屏驱动"), 32);
        AddWideRow(credits, Hint($"© {DateTime.Now.Year} {AuthorName}　本程序按现状提供；远程访问存在安全风险，开放到公网前请先设置强密码。"), 44);
        return page;
    }
}

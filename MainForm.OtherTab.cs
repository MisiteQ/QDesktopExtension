using System.Drawing;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>"系统维护"页签的构建逻辑（从 MainForm 拆出）。</summary>
public sealed partial class MainForm
{
    private TabPage BuildOtherTab()
    {
        var page = NewPage("系统维护");
        var layout = PageGrid(page);

        var startup = AddCard(layout, 122, "启动与关闭");
        AddRow(startup, "", _chkAutoStart, 32);
        AddRow(startup, "关闭窗口时", _cmbCloseAction, 40);

        var diagnostics = AddCard(layout, 128, "诊断与日志");
        AddRow(diagnostics, "日志详细程度", _cmbLogLevel, 40);
        AddWideRow(diagnostics, Hint("排查问题时可选“详细”；日志目录可通过顶部“日志”按钮打开。"), 38);
        return page;
    }
}

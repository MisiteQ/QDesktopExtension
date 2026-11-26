using System.Drawing;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>"画质性能"页签的构建逻辑（从 MainForm 拆出）。</summary>
public sealed partial class MainForm
{
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
}

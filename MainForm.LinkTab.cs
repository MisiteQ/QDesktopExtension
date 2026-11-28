using System.Drawing;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>"连接访问"页签的构建逻辑（从 MainForm 拆出）。</summary>
public sealed partial class MainForm
{
    private TabPage BuildLinkTab()
    {
        var page = NewPage("连接访问");
        var layout = PageGrid(page);

        var connection = AddCard(layout, 198, "连接方式");
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

        var security = AddCard(layout, 240, "访问安全");
        AddRow(security, "网页端口", _numHttpPort, 40);
        AddRow(security, "推流端口", _numWsPort, 40);
        AddRow(security, "访问密码", _txtPassword, 40);
        AddRow(security, "", _chkLoopbackOnly, 32);
        AddWideRow(security, Hint("端口被占用时可修改；端口、密码和采集来源改动需重启服务后生效。"), 38);
        return page;
    }
}

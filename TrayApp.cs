using System.Drawing;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>托盘常驻 + 设置面板的组合：启动即开始串流，退出时优雅停流。</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppHost _host;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miToggle = new("停止服务");
    private readonly System.Windows.Forms.Timer _sync = new() { Interval = 1000 };
    private bool _balloonShown;

    public TrayApp(AppHost host)
    {
        _host = host;
        _form = new MainForm(host);
        _form.FormClosed += (_, _) => { if (_form.ExitRequested) ExitApp(); };

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "QDesktopExtension 远程桌面",
            Visible = true
        };

        var menu = new ContextMenuStrip
        {
            Renderer = new UiToolStripRenderer(),
            BackColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            Font = UiTheme.Body(9f)
        };
        menu.Items.Add(new ToolStripMenuItem("打开设置面板", null, (_, _) => ShowForm()));
        menu.Items.Add(new ToolStripMenuItem("打开网页", null, (_, _) => OpenWeb()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowForm();
        _miToggle.Click += (_, _) =>
        {
            if (_host.Running) _host.RequestStop();
            else _host.Start();
        };

        _sync.Tick += (_, _) => SyncTray();
        _sync.Start();

        _host.Start();      // 双击图标就开始串流，不用再点 START.bat
        _form.Show();
        SyncTray();
    }

    private void SyncTray()
    {
        _miToggle.Text = _host.Running ? "停止服务" : "启动服务";
        var tip = _host.Running ? "远程桌面服务正在运行" : "远程桌面服务已停止";
        _tray.Text = _host.Running ? "QDesktopExtension · 服务运行中" : "QDesktopExtension · 服务已停止";
        if (!_balloonShown)
        {
            _balloonShown = true;
            _tray.ShowBalloonTip(3000, "QDesktopExtension", tip + "，双击托盘图标打开设置面板。", ToolTipIcon.Info);
        }
    }

    private void ShowForm()
    {
        if (_form.Visible)
        {
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            return;
        }
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void OpenWeb()
    {
        var port = _host.Config.HttpPort;
        var address = _host.Config.IsLoopbackOnly()
            ? "127.0.0.1"
            : AppHost.LocalAddresses().FirstOrDefault() ?? "localhost";
        var url = $"http://{address}:{port}/";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void ExitApp()
    {
        try { _sync.Stop(); } catch { }
        try { _host.Stop(8000); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        ExitThread();
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico");
            if (s != null)
            {
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                using var ms = new MemoryStream(bytes);
                return new Icon(ms);
            }
        }
        catch { }
        try
        {
            var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon != null) return exeIcon;
        }
        catch { }
        return SystemIcons.Application;
    }
}

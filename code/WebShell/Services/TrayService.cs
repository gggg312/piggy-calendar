using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 系统托盘服务：常驻 NotifyIcon。
    /// 左键 / 右键都弹出同一套菜单；菜单首项按当前显隐状态显示「隐藏小组件」/「显示小组件」（v0.1.6.57）。
    /// v0.1.6.57 同时取消了「菜单一弹出就强制唤出小组件」的旧行为：否则点过「隐藏」后再右键，
    /// 窗口又被叫回来，显隐切换形同失效。现在显隐只由菜单首项与组件右上角隐藏按钮控制。
    /// </summary>
    internal sealed class TrayService : IDisposable
    {
        private NotifyIcon _notify;
        private ContextMenuStrip _menu;
        // 图标流必须在托盘生命周期内长期持有：System.Drawing.Icon 内部引用该流，
        // 流一旦 Dispose，图标句柄即失效，NotifyIcon 将不显示图标。
        private MemoryStream _iconStream;

        private ToolStripMenuItem _toggleItem;
        private readonly Func<bool> _isWidgetVisible;
        private readonly Action _toggleWidget;
        private readonly Action _openSettings;
        private readonly Action _openAbout;
        private readonly Action _resetWidgetPos;
        private readonly Action _exitApp;

        public TrayService(Func<bool> isWidgetVisible, Action toggleWidget, Action openSettings, Action openAbout, Action resetWidgetPos, Action exitApp)
        {
            _isWidgetVisible = isWidgetVisible;
            _toggleWidget = toggleWidget;
            _openSettings = openSettings;
            _openAbout = openAbout;
            _resetWidgetPos = resetWidgetPos;
            _exitApp = exitApp;
        }

        public void Initialize()
        {
            _notify = new NotifyIcon
            {
                Text = "小猪日历",
                Visible = true,
                Icon = LoadAppIcon()
            };

            _menu = new ContextMenuStrip();

            // v0.1.6.57：首项「隐藏/显示小组件」，文案在菜单弹出时按当前状态刷新（不在这里写死）
            _toggleItem = new ToolStripMenuItem("隐藏小组件");
            _toggleItem.Click += (s, e) => _toggleWidget?.Invoke();
            _menu.Items.Add(_toggleItem);
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add("设置", null, (s, e) => _openSettings?.Invoke());
            _menu.Items.Add("关于", null, (s, e) => _openAbout?.Invoke());
            _menu.Items.Add("重置窗口位置", null, (s, e) => _resetWidgetPos?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, (s, e) => _exitApp?.Invoke());

            // v0.1.6.57：菜单弹出只刷新首项文案，不再强制唤出小组件（旧行为会让「隐藏」形同失效）
            _menu.Opening += (s, e) =>
            {
                bool visible = true;
                try { visible = _isWidgetVisible?.Invoke() ?? true; } catch { }
                if (_toggleItem != null) _toggleItem.Text = visible ? "隐藏小组件" : "显示小组件";
            };
            _notify.ContextMenuStrip = _menu;

            // 左键单击：同样在鼠标位置弹出菜单（v0.1.6.57 起不再顺带唤出小组件）
            _notify.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveRight);
                }
            };

            AppLog.Write("[TRAY] 托盘已初始化，图标=" + (_notify.Icon != null));
        }

        private Icon LoadAppIcon()
        {
            // 从 WPF 资源 Assets/app.ico（多尺寸 16/24/32/48/64/128/256）加载托盘图标。
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
                var resource = System.Windows.Application.GetResourceStream(uri);
                if (resource != null)
                {
                    using (var stream = resource.Stream)
                    {
                        _iconStream = new MemoryStream();
                        stream.CopyTo(_iconStream);
                    }
                    _iconStream.Position = 0;
                    var icon = new Icon(_iconStream, new Size(32, 32));
                    AppLog.Write("[TRAY] 应用图标加载成功（32×32）");
                    return icon;
                }
                AppLog.Write("[TRAY] 未取到 Assets/app.ico 资源流，回退系统图标");
            }
            catch (Exception ex)
            {
                AppLog.Write("[TRAY] 加载应用图标失败，回退系统图标：" + ex.Message);
            }
            return SystemIcons.Application;
        }

        /// <summary>托盘气泡提示（M0 后续按需使用）。</summary>
        public void ShowTip(string title, string text, int timeoutMs = 1500)
        {
            try { _notify?.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.Info); } catch { }
        }

        public void Dispose()
        {
            try { if (_notify != null) { _notify.Visible = false; _notify.Dispose(); } } catch { }
            _notify = null;
            try { _menu?.Dispose(); } catch { }
            _menu = null;
            try { _iconStream?.Dispose(); } catch { }
            _iconStream = null;
        }
    }
}

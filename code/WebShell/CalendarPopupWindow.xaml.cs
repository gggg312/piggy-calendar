using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PiggyCalendar.WebShell.Services;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// 桌面常驻悬浮小组件（M0-4R，原日历弹出浮窗改造）。
    /// 普通窗口层级（不置顶、可被遮挡、不进任务栏）；整窗可拖动并记忆位置；失焦不收起，仅向前端推送焦点状态。
    /// WebView2 承载 Vue3 前端；时间/日期/星期/日历网格/底部按钮栏由前端 M0-5 起落地。
    /// </summary>
    public partial class CalendarPopupWindow : Window
    {
        private string _webRoot;
        private CSharpBridge _bridge;
        private bool _initialized;
        private readonly Action<CSharpBridge> _onBridgeReady;
        private readonly DispatcherTimer _resizeSaveTimer;

        // M0-8a 手动拖动：鼠标按下发生在 WebView2 子 HWND 内，WPF 不持有鼠标捕获，
        // DragMove() 无法生效；改为定时器轮询全局光标位置 + 左键状态，按位移差移动窗口。
        private readonly DispatcherTimer _moveTimer;
        private bool _moveActive;
        private System.Drawing.Point _moveAnchorMouse;
        private double _moveAnchorLeft;
        private double _moveAnchorTop;
        private double _moveAnchorScale = 1.0;   // v0.1.6.89：拖动开始时窗口所在屏的缩放（物理↔DIP 换算用）

        // M0-8a 手动缩放：Thumb 内置 DragDelta 同样依赖鼠标捕获，在子 HWND 下不触发；
        // 改为 Border 热区按下后启动定时器，基于锚点快照按位移差计算新尺寸与位置。
        private readonly DispatcherTimer _resizeTimer;
        private bool _resizeActive;
        private string _resizeDir = "";
        private System.Drawing.Point _resizeAnchorMouse;
        private double _resizeAnchorLeft;
        private double _resizeAnchorTop;
        private double _resizeAnchorWidth;
        private double _userBaseWidth;   // v0.1.6.33：用户设定宽（日历自适应加宽不回写它）
        private double _resizeAnchorHeight;

        /// <summary>桥对象（WebView2 初始化完成后可用）。</summary>
        public CSharpBridge Bridge => _bridge;

        public CalendarPopupWindow(Action<CSharpBridge> onBridgeReady = null)
        {
            InitializeComponent();
            _onBridgeReady = onBridgeReady;
            Loaded += OnLoadedAsync;

            // M0-8a：缩放过程中节流记忆宽高；停止拖拽约 300ms 后落盘一次，避免拖动中高频写 INI。
            _resizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _resizeSaveTimer.Tick += (s, e) =>
            {
                _resizeSaveTimer.Stop();
                SaveLayout();
            };
            SizeChanged += (s, e) => _resizeSaveTimer.Stop();
            SizeChanged += (s, e) => _resizeSaveTimer.Start();
            SizeChanged += (s, e) => ApplyRoundRegion();   // v0.1.6.29 缩放后同步圆角 Region
            Closing += (s, e) => SaveLayout();

            // M0-8a 手动拖动定时器：约 16ms 轮询一次，跟随光标位移；左键松开即结束并落盘。
            _moveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _moveTimer.Tick += MoveTimer_Tick;

            // M0-8a 手动缩放定时器：与移动同频，按下边/角热区后跟随光标调整尺寸。
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _resizeTimer.Tick += ResizeTimer_Tick;

            // 焦点状态：仅通知前端暂停/恢复秒刷新，不收起窗口（v0.3 1.2）
            Activated += (s, e) => PostFocusState(true);
            Deactivated += (s, e) => PostFocusState(false);
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            try
            {
                // v0.1.6.58：先定位，再初始化 WebView2 —— 否则窗口会先在默认位置显示、稍后才跳到记忆位置
                ApplyStoredPosition();
                _webRoot = LocateWebRoot();
                if (_webRoot == null)
                {
                    AppLog.Write("[WIDGET] 未找到 WebUI\\prototype 目录");
                    MessageBox.Show("未找到 WebUI\\prototype 目录（含 index.html 与 lib\\）。",
                        "小猪日历", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                WebView2LoaderBootstrap.EnsureWebView2LoaderReady();

                string wv2Dir = AppConfig.WebView2CacheDir;
                CoreWebView2Environment wv2Env = await CoreWebView2Environment.CreateAsync(null, wv2Dir);
                await webView.EnsureCoreWebView2Async(wv2Env);

                try { webView.DefaultBackgroundColor = System.Drawing.Color.Transparent; } catch { }

                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app", _webRoot, CoreWebView2HostResourceAccessKind.Allow);

                _bridge = new CSharpBridge(json =>
                {
                    try { webView.Dispatcher.Invoke(() => webView.CoreWebView2.PostWebMessageAsJson(json)); }
                    catch { }
                });
                webView.CoreWebView2.AddHostObjectToScript("CSharpBridge", _bridge);
                _onBridgeReady?.Invoke(_bridge);

                webView.CoreWebView2.NavigationStarting += (s, nav) =>
                {
                    try
                    {
                        if (IsLocalAppUrl(nav.Uri)) return;
                        nav.Cancel = true;
                        OpenInSystemBrowser(nav.Uri);
                    }
                    catch { }
                };
                webView.CoreWebView2.NewWindowRequested += (s, ev) =>
                {
                    try { ev.Handled = true; OpenInSystemBrowser(ev.Uri); } catch { }
                };
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                // v0.1.6.57：关掉 WebView2 默认右键菜单（刷新/另存为/打印对桌面挂件无意义，
                // 且刷新会重载页面、重置缩放与倒计时条内部状态）—— 用户定案「直接取消」
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                webView.CoreWebView2.NavigationCompleted += (s2, e2) =>
                {
                    if (!e2.IsSuccess) return;
                    webView.CoreWebView2.PostWebMessageAsJson(
                        "{\"kind\":\"shell-ready\",\"payload\":{\"version\":\"" + _bridge.GetVersion() + "\"}}");
                };

                // v0.1.6.76：改走 file:// 直连 —— 虚拟主机 scheme 每个新文档约有 1 秒固定开销
                // 实测同一 WebView2 内 https://app/ 1030ms、file:// 35ms（空页），完整页 343ms
                webView.CoreWebView2.Navigate(new System.Uri(System.IO.Path.Combine(_webRoot, "index.html")).AbsoluteUri);
                _initialized = true;
                InitAcrylic();        // v0.1.6.30 先建 Acrylic 通道（合成背景透明 + 扩展 DWM 客户区）
                ApplyAppearance();
                ApplyRoundRegion();   // v0.1.6.29 首次显示即裁剪圆角，避免圆角外露出窗口底色方块
                AppLog.Write("[WIDGET] WebView2 初始化完成，WebRoot=" + _webRoot);
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 初始化异常：" + ex);
            }
        }

        // ---------- 位置：记忆 + 默认落点 + 越界回退 ----------

        private void ApplyStoredPosition()
        {
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;

                // M0-8a：先恢复记忆尺寸（读取时已钳制最小宽高；这里再限制不超出工作区）
                double w = Math.Max(AppConfig.MinWidgetWidth, AppConfig.WidgetWidth);
                double h = Math.Max(AppConfig.MinWidgetHeight, AppConfig.WidgetHeight);
                if (w > screen.Width) w = screen.Width;
                if (h > screen.Height) h = screen.Height;
                Width = w;
                Height = h;
                _userBaseWidth = w;   // v0.1.6.33：这是用户设定宽，日历自适应加宽不回写它

                // v0.1.6.58：判定改用真正的哨兵 -1（原来是 `>= 0`，副屏坐标本身是负数，会被当成「无记忆」
                // 从而回落主屏默认落点 —— 这是「隐藏后恢复跑到初始位置」的根因之一）
                if (AppConfig.WidgetPosX != -1 && AppConfig.WidgetPosY != -1)
                {
                    // v0.1.6.58：clamp 按「目标点所在屏」工作区（与拖动/缩放一致），副屏负坐标不再被主屏工作区拉回
                    double rawX = AppConfig.WidgetPosX;
                    double rawY = AppConfig.WidgetPosY;
                    var targetScreen = System.Windows.Forms.Screen.FromPoint(
                        new System.Drawing.Point((int)(rawX + Width / 2), (int)(rawY + Height / 2))).WorkingArea;
                    Left = ClampX(AppConfig.WidgetPosX, targetScreen);
                    Top = ClampY(AppConfig.WidgetPosY, targetScreen);
                    AppLog.Write("[WIDGET] 恢复记忆布局 Left=" + Left.ToString("F0") + " Top=" + Top.ToString("F0")
                        + " Size=" + Width.ToString("F0") + "x" + Height.ToString("F0"));
                    return;
                }
                // 默认落点：主显示器工作区右上角内缩 24px
                Left = screen.Right - Width - 24;
                Top = screen.Top + 24;
                Left = ClampX((int)Left, screen);
                Top = ClampY((int)Top, screen);
                AppLog.Write("[WIDGET] 默认落点（无位置记忆） Left=" + Left.ToString("F0") + " Top=" + Top.ToString("F0")
                    + " Size=" + Width.ToString("F0") + "x" + Height.ToString("F0"));
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 定位失败：" + ex.Message);
            }
            finally
            {
                // v0.1.6.58：定位结束才允许落盘（此前 SizeChanged 的节流保存会把记忆覆盖成默认位置）
                _layoutReady = true;
            }
        }

        private double ClampX(int x, System.Drawing.Rectangle screen)
        {
            double max = screen.Right - Width;
            if (x > max) x = (int)max;
            if (x < screen.Left) x = screen.Left;
            return x;
        }

        private double ClampY(int y, System.Drawing.Rectangle screen)
        {
            double max = screen.Bottom - Height;
            if (y > max) y = (int)max;
            if (y < screen.Top) y = screen.Top;
            return y;
        }

        // v0.1.6.58：启动定位（ApplyStoredPosition）完成前禁止落盘。
        // 原因：窗口首次显示时 SizeChanged 的 300ms 节流保存会先把 WPF 默认位置写进 config，
        // 抢在读取记忆之前覆盖掉记忆值 —— 于是「恢复记忆布局」读到的永远是自己刚写进去的默认位置。
        private bool _layoutReady;

        private void SaveLayout()
        {
            if (!_layoutReady) return;   // v0.1.6.58：启动阶段不落盘，避免覆盖记忆位置
            try
            {
                AppConfig.WidgetPosX = (int)Math.Round(Left);
                AppConfig.WidgetPosY = (int)Math.Round(Top);
                // v0.1.6.33：宽度只记用户设定值——日历自适应加宽后的宽不写回，否则翻到节日少的月份就缩不回去了
                AppConfig.WidgetWidth = (int)Math.Round(_userBaseWidth > 0 ? _userBaseWidth : Width);
                AppConfig.WidgetHeight = (int)Math.Round(Height);
                AppConfig.Save();
                AppLog.Write("[WIDGET] 布局已记忆 x=" + AppConfig.WidgetPosX + " y=" + AppConfig.WidgetPosY
                    + " w=" + AppConfig.WidgetWidth + " h=" + AppConfig.WidgetHeight);
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 保存布局失败：" + ex.Message);
            }
        }

        /// <summary>重置窗口位置：放回默认落点（主屏工作区右上角内缩 24px）并记忆。托盘菜单「重置窗口位置」调用。</summary>
        public void ResetToDefaultPos()
        {
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                Left = ClampX((int)(screen.Right - Width - 24), screen);
                Top = ClampY((int)(screen.Top + 24), screen);
                SaveLayout();
                AppLog.Write("[WIDGET] 已重置窗口位置 Left=" + Left.ToString("F0") + " Top=" + Top.ToString("F0"));
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 重置窗口位置失败：" + ex.Message);
            }
        }

        // ---------- v0.1.6.33：日历列宽自适应 → 按内容需要宽加宽/缩回 ----------

        /// <summary>
        /// 前端量出「7 列各容纳最长副标题」所需的窗口宽后调用。
        /// 目标宽 = clamp(需要宽, 用户设定宽, min(设定宽 + 560, 屏幕工作区宽))：内容变多时加宽，
        /// 翻到节日少的月份时缩回设定宽；加宽时若窗口贴着屏幕右边界则保持右边界不动向左长，
        /// 否则保持左边界向右长；最后按窗口所在屏 clamp 一次，保证不被推出屏外。
        /// </summary>
        public void FitWidgetWidth(double needWidth)
        {
            try
            {
                if (_resizeActive) return;   // 手动缩放会话进行中不插手宽度
                if (double.IsNaN(needWidth) || double.IsInfinity(needWidth) || needWidth <= 0) return;

                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + Height / 2))).WorkingArea;
                double baseW = _userBaseWidth > 0
                    ? Math.Max(AppConfig.MinWidgetWidth, _userBaseWidth)
                    : Math.Max(AppConfig.MinWidgetWidth, AppConfig.WidgetWidth);
                double cap = Math.Min(baseW + 560, screen.Width);
                double target = Math.Min(Math.Max(needWidth, baseW), cap);
                if (Math.Abs(target - Width) < 1.5) return;

                double oldRight = Left + Width;
                bool stickRight = Math.Abs(oldRight - screen.Right) <= 12
                    || (Left + Width / 2) > (screen.Left + screen.Width / 2);

                Width = target;
                Left = stickRight ? oldRight - Width : Left;
                if (Left < screen.Left) Left = screen.Left;
                if (Left + Width > screen.Right) Left = screen.Right - Width;

                AppLog.Write("[WIDGET] 列宽自适应 need=" + needWidth.ToString("F1") + " base=" + baseW.ToString("F0")
                    + " cap=" + cap.ToString("F0") + " -> W=" + Width.ToString("F0") + " L=" + Left.ToString("F0")
                    + (stickRight ? "（贴右向左长）" : "（贴左向右长）"));
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 列宽自适应失败：" + ex.Message);
            }
        }

        // ---------- 手动缩放（前端内部手柄经桥 BeginResize 启动；复用 16ms 定时器，不依赖鼠标捕获） ----------

        /// <summary>供前端缩放手柄按下时调用，按方向（L/R/T/B 组合）启动等比缩放会话。锁定状态下不缩放并提示。</summary>
        public void BeginResize(string dir)
        {
            try
            {
                if (AppConfig.WidgetLocked) { NotifyLockHit(); return; }
                if (_resizeActive) return;
                _resizeDir = dir ?? "";
                _resizeActive = true;
                _resizeAnchorMouse = System.Windows.Forms.Cursor.Position;
                _resizeAnchorLeft = Left;
                _resizeAnchorTop = Top;
                _resizeAnchorWidth = Width;
                _resizeAnchorHeight = Height;
                _resizeTimer.Start();
                AppLog.Write("[WIDGET] 等比缩放开始 dir=" + _resizeDir
                    + " anchor=" + _resizeAnchorMouse.X + "," + _resizeAnchorMouse.Y);
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 缩放起始失败：" + ex.Message);
            }
        }

        private void ResizeTimer_Tick(object sender, EventArgs e)
        {
            // 左键松开：结束缩放并记忆尺寸位置。
            if (System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.Left)
            {
                if (_resizeActive)
                {
                    _resizeActive = false;
                    _resizeTimer.Stop();
                    _userBaseWidth = Width;   // v0.1.6.33：用户手动缩放后的宽度即新的基准宽（列宽自适应以它为下限）
                    SaveLayout();
                    AppLog.Write("[WIDGET] 等比缩放结束 Size=" + Width.ToString("F0") + "x" + Height.ToString("F0"));
                }
                return;
            }

            try
            {
                bool left = _resizeDir.Contains("L");
                bool right = _resizeDir.Contains("R");
                bool top = _resizeDir.Contains("T");
                bool bottom = _resizeDir.Contains("B");

                // v0.1.6.90：缩放的单位统一到 DIP。Cursor.Position 与 Screen.WorkingArea 是物理像素，
                // Width/Height/Left/Top 是 DIP；原来把物理位移直接当 DIP 用（125% 屏缩放比鼠标快 1.25 倍），
                // 又用物理工作区去夹 DIP 的锚点（top 方向会算出负的 maxK → 直接缩不动）。
                // 现取鼠标所在屏的缩放，把工作区折成 DIP 再比较，位移也先除以该屏缩放。
                var cur = System.Windows.Forms.Cursor.Position;
                double scaleNow = ScaleAtPoint(cur);
                if (scaleNow < 0.5 || scaleNow > 4.0) scaleNow = 1.0;
                var waPx = System.Windows.Forms.Screen.FromPoint(cur).WorkingArea;
                double scL = waPx.Left / scaleNow;
                double scT = waPx.Top / scaleNow;
                double scR = waPx.Right / scaleNow;
                double scB = waPx.Bottom / scaleNow;
                double baseW = AppConfig.BaseWidgetWidth;
                double baseH = AppConfig.BaseWidgetHeight;

                double dx = (cur.X - _resizeAnchorMouse.X) / scaleNow;   // 物理位移 → DIP
                double dy = (cur.Y - _resizeAnchorMouse.Y) / scaleNow;

                // 快照系数（宽度基准；等比模型下宽高比恒定）。
                double k0 = _resizeAnchorWidth / baseW;

                // 各方向位移换算成系数增量；L/T 为反向锚点，位移方向取反。
                double dkW = 0, dkH = 0;
                if (right) dkW = dx / baseW;
                if (left) dkW = -dx / baseW;
                if (bottom) dkH = dy / baseH;
                if (top) dkH = -dy / baseH;

                double dk;
                if (dkW != 0 && dkH != 0) dk = Math.Abs(dkW) >= Math.Abs(dkH) ? dkW : dkH;
                else dk = dkW != 0 ? dkW : dkH;

                double k = k0 + dk;
                k = Math.Max(AppConfig.MinScale, Math.Min(AppConfig.MaxScale, k));

                // R/B 方向固定左上角锚点；L/T 方向反向扩展。四边 clamp 保证窗口任一边不越出工作区（顶部横条始终可见可抓）。
                double maxK = AppConfig.MaxScale;
                if (right) maxK = Math.Min(maxK, (scR - _resizeAnchorLeft) / baseW);
                if (left) maxK = Math.Min(maxK, (_resizeAnchorLeft + _resizeAnchorWidth - scL) / baseW);
                if (bottom) maxK = Math.Min(maxK, (scB - _resizeAnchorTop) / baseH);
                if (top) maxK = Math.Min(maxK, (_resizeAnchorTop + _resizeAnchorHeight - scT) / baseH);
                k = Math.Min(k, maxK);

                double newW = baseW * k;
                double newH = baseH * k;
                // L/T 方向锚点跟随：新窗口向反方向延展，左上角随之移动。
                double newL = left ? _resizeAnchorLeft + (_resizeAnchorWidth - newW) : _resizeAnchorLeft;
                double newT = top ? _resizeAnchorTop + (_resizeAnchorHeight - newH) : _resizeAnchorTop;

                Width = newW;
                Height = newH;
                Left = newL;
                Top = newT;
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 缩放过程失败：" + ex.Message);
            }
        }

        // ---------- 手动拖动（不依赖 WPF 鼠标捕获；由前端顶部把手横条经桥 BeginDrag 触发） ----------

        /// <summary>供前端把手横条鼠标按下时调用，启动手动拖动会话。锁定状态下不拖动并提示。</summary>
        public void StartDrag()
        {
            try
            {
                if (AppConfig.WidgetLocked) { NotifyLockHit(); return; }
                BeginManualMove();
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] StartDrag 失败：" + ex.Message);
            }
        }

        /// <summary>锁定时拖动/缩放被拦截，向前端推送命中事件（触发锁定按钮闪烁 5 次）。</summary>
        private void NotifyLockHit()
        {
            try
            {
                AppLog.Write("[WIDGET] 锁定状态下尝试拖动/缩放，已拦截");
                _bridge?.PostEvent("{\"kind\":\"widget-locked-hit\",\"payload\":{}}");
            }
            catch { }
        }

        /// <summary>应用小组件外观：窗口不透明度固定 1.0；透明度改由前端面板背景 --panel-alpha 控制（v0.1.6.8）。
        /// v0.1.6.30：毛玻璃不再用 CSS 半透明，改由系统 Acrylic 提供真模糊，这里同步 accent 参数。</summary>
        public void ApplyAppearance()
        {
            try
            {
                Opacity = 1.0;
                ApplyAcrylic();
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 应用外观失败：" + ex.Message);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(MonitorPt pt, int flags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorPt { public int X; public int Y; }

        /// <summary>该点所在显示器的缩放倍数。PerMonitorV2 下 Cursor.Position 与 Screen.WorkingArea 都是物理像素，
        /// 而 WPF 的 Left/Top/Width 是 DIP，两者之间必须用这个比例换算。</summary>
        private static double ScaleAtPoint(System.Drawing.Point pt)
        {
            try
            {
                var p = new MonitorPt { X = pt.X, Y = pt.Y };
                IntPtr h = MonitorFromPoint(p, 2);          // MONITOR_DEFAULTTONEAREST
                uint dx = 96, dy = 96;
                if (GetDpiForMonitor(h, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            return 1.0;
        }

        private void BeginManualMove()
        {
            if (_moveActive) return;
            _moveActive = true;
            SetDragBlank(true);   // v0.1.6.30：拖动期间撤掉系统模糊，改由前端补实心底色（否则画面严重滞后）
            _moveAnchorMouse = System.Windows.Forms.Cursor.Position;
            _moveAnchorLeft = Left;
            _moveAnchorTop = Top;
            _moveAnchorScale = ScaleAtPoint(_moveAnchorMouse);
            _moveTimer.Start();
            AppLog.Write("[WIDGET] 手动拖动开始 anchor=" + _moveAnchorMouse.X + "," + _moveAnchorMouse.Y);
        }

        private void MoveTimer_Tick(object sender, EventArgs e)
        {
            // 左键已松开：结束拖动并记忆位置。
            if (System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.Left)
            {
                if (_moveActive)
                {
                    _moveActive = false;
                    _moveTimer.Stop();
                    SetDragBlank(false);   // v0.1.6.30：松手即恢复系统模糊
                    SaveLayout();
                    AppLog.Write("[WIDGET] 手动拖动结束");
                }
                return;
            }

            var cur = System.Windows.Forms.Cursor.Position;

            /* v0.1.6.89：统一在「物理像素」里算，最后按鼠标所在屏的缩放换算回 DIP。
               原实现把物理位移直接加到 DIP 的 Left/Top 上、又用物理工作区去夹 DIP 的 Top，
               在 125% 缩放的副屏上会同时出现「拖动比鼠标快 1.25 倍」和「边界错位」——
               用户实测：离屏顶还有一大截就拖不上去，再往上窗口跑到别的屏幕。 */
            double scaleA = _moveAnchorScale > 0.1 ? _moveAnchorScale : 1.0;
            double scaleNow = ScaleAtPoint(cur);
            if (!(scaleNow > 0.1)) scaleNow = 1.0;

            double wPx = Width * scaleNow;
            double hPx = Height * scaleNow;
            double pxLeft = _moveAnchorLeft * scaleA + (cur.X - _moveAnchorMouse.X);
            double pxTop = _moveAnchorTop * scaleA + (cur.Y - _moveAnchorMouse.Y);

            // 按「鼠标所在屏」的工作区（物理像素）夹取：整窗始终留在当前这块屏里
            var wa = System.Windows.Forms.Screen.FromPoint(cur).WorkingArea;
            if (pxLeft > wa.Right - wPx) pxLeft = wa.Right - wPx;
            if (pxLeft < wa.Left) pxLeft = wa.Left;
            if (pxTop > wa.Bottom - hPx) pxTop = wa.Bottom - hPx;
            if (pxTop < wa.Top) pxTop = wa.Top;

            Left = pxLeft / scaleNow;
            Top = pxTop / scaleNow;
        }

        // ---------- 显隐（托盘左键 / 设置开关） ----------

        /// <summary>显示小组件并定位；唤出后聚焦到前台（普通层级，不强制置顶）。</summary>
        public void ShowWidget()
        {
            try
            {
                // v0.1.6.58：不再重跑 ApplyStoredPosition —— Hide() 只是隐藏窗口，Left/Top 与尺寸都还保留着，
                // 显隐必须「原地回来」。重新定位只发生在启动加载与托盘「重置窗口位置」。
                Show();
                Activate();
                AppLog.Write("[WIDGET] 小组件已显示");
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 显示异常：" + ex);
            }
        }

        public void HideWidget()
        {
            try { Hide(); AppLog.Write("[WIDGET] 小组件已隐藏"); }
            catch (Exception ex) { AppLog.Write("[WIDGET] 隐藏异常：" + ex.Message); }
        }

        public void ToggleWidget()
        {
            AppLog.Write("[WIDGET] ToggleWidget 当前 IsVisible=" + IsVisible);
            if (IsVisible) HideWidget();
            else ShowWidget();
        }

        /// <summary>设置开关联动：按显隐状态显示/隐藏小组件。</summary>
        public void ApplyVisibility(bool visible)
        {
            if (visible) ShowWidget();
            else HideWidget();
        }

        /// <summary>桥接口：显示小组件。</summary>
        public void ShowPanel() => ShowWidget();

        /// <summary>桥接口：隐藏小组件。</summary>
        public void HidePanel() => HideWidget();

        private void PostFocusState(bool focused)
        {
            try
            {
                _bridge?.PostEvent("{\"kind\":\"widget-focus-state\",\"payload\":{\"focused\":" + (focused ? "true" : "false") + "}}");
            }
            catch { }
        }

        // ---------- 窗口圆角区域（v0.1.6.29） ----------
        // WebView2 由独立的子 HWND 绘制，WPF 的 Border CornerRadius 裁不到它，
        // 于是面板圆角之外（窗口矩形四角）会露出窗口底色的浅色方块——金属材质对比强，最容易被看到。
        // 这里用 SetWindowRgn 把窗口本体裁成圆角，四角不再有任何像素，透出的就只剩桌面。
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        /// <summary>
        /// 按窗口实际像素尺寸重设圆角 Region。
        /// 半径 = 前端 .panel-bg 的 border-radius 14（CSS px，作用于未被 scale 的 #app 层）。
        /// 前端 Border Margin 已改为 0（v0.1.6.29），窗口边缘与面板边缘重合，两者圆角同心同半径。
        /// </summary>
        public void ApplyRoundRegion()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                double dpi = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
                int w = (int)Math.Round(ActualWidth * dpi);
                int h = (int)Math.Round(ActualHeight * dpi);
                if (w <= 0 || h <= 0) return;
                int r = (int)Math.Round(14.0 * dpi);
                IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
                SetWindowRgn(hwnd, rgn, true);
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] ApplyRoundRegion 异常：" + ex.Message);
            }
        }

        // ---------- 系统 Acrylic 毛玻璃（v0.1.6.30） ----------
        // 三条前提缺一不可（AcrylicProbe 实测）：AllowsTransparency=False
        // + CompositionTarget.BackgroundColor=Transparent + DwmExtendFrameIntoClientArea(-1,-1,-1,-1)，
        // 再 SetWindowCompositionAttribute(Attribute=19, AccentState=3/4, AccentFlags=2) 才出真模糊。
        // 分层窗口（AllowsTransparency=True）拿不到 DWM blur-behind，这就是之前 CSS backdrop-filter 对桌面无效的根因。
        // 拖动期间必须撤掉 accent 并由前端补实心底色：DWM 每帧重算整窗模糊 30~50ms，留着模糊就必然拖不动。
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private struct MARGINS
        {
            public int LeftWidth;
            public int RightWidth;
            public int TopWidth;
            public int BottomWidth;
        }

        private bool _acrylicReady;
        private bool _dragBlank;

        /// <summary>初始化系统 Acrylic：合成背景全透明 + 扩展 DWM 客户区，随后按外观配置上 accent。</summary>
        public void InitAcrylic()
        {
            if (_acrylicReady) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                var src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null)
                {
                    src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
                }
                MARGINS m = new MARGINS();
                m.LeftWidth = -1; m.RightWidth = -1; m.TopWidth = -1; m.BottomWidth = -1;
                int hr = DwmExtendFrameIntoClientArea(hwnd, ref m);
                _acrylicReady = true;
                AppLog.Write("[WIDGET] Acrylic 初始化 hr=" + hr);
                ApplyAcrylic();
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] Acrylic 初始化异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 按当前材质/色系/质感/浓度应用 accent。
        /// 只有毛玻璃需要模糊；纯色与金属不透明，直接 AccentState=0 关掉，省掉 DWM 每帧重算的开销。
        /// </summary>
        public void ApplyAcrylic()
        {
            if (!_acrylicReady) { InitAcrylic(); return; }
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int state = 0;
                int color = 0;
                if (!_dragBlank && AppConfig.WidgetTexture == "glass")
                {
                    state = (AppConfig.GlassFinish == "frosted") ? 4 : 3;   // 默认清亮 = AccentState 3
                    bool light = AppConfig.WidgetTheme == "light";
                    int a = (int)Math.Round(Math.Max(0, Math.Min(100, AppConfig.GlassTintPercent)) * 0.8 * 2.55);
                    int r = light ? 0xF2 : 0x1F;   // 与测试程序同基色：深 #1F2329 / 浅 #F2F4F7
                    int g = light ? 0xF4 : 0x23;
                    int b = light ? 0xF7 : 0x29;
                    color = (a << 24) | (b << 16) | (g << 8) | r;
                }

                AccentPolicy policy = new AccentPolicy();
                policy.AccentState = state;
                policy.AccentFlags = 2;
                policy.GradientColor = color;
                int size = Marshal.SizeOf(typeof(AccentPolicy));
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, ptr, false);
                    WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                    data.Attribute = 19;   // WCA_ACCENT_POLICY
                    data.Data = ptr;
                    data.SizeOfData = size;
                    int ret = SetWindowCompositionAttribute(hwnd, ref data);
                    AppLog.Write("[WIDGET] accent state=" + state + " color=0x" + color.ToString("X8")
                        + " blank=" + _dragBlank + " ret=" + ret);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] ApplyAcrylic 异常：" + ex.Message);
            }
        }

        /// <summary>拖动期间撤掉 accent，并通知前端补实心底色；松手后恢复模糊。</summary>
        public void SetDragBlank(bool blank)
        {
            if (_dragBlank == blank) return;
            _dragBlank = blank;
            ApplyAcrylic();
            try
            {
                _bridge?.PostEvent("{\"kind\":\"widget-drag-state\",\"payload\":{\"dragging\":" + (blank ? "true" : "false") + "}}");
            }
            catch { }
        }

        // ---------- 静态工具 ----------

        /// <summary>定位前端目录：① 开发态源码 code\WebUI\prototype；② 交付态 EXE 同目录 WebUI\prototype。</summary>
        /// <summary>定位前端目录：磁盘源码目录优先，无则用内嵌资源解压目录（v0.1.6.34 单 EXE）。</summary>
        private static string LocateWebRoot()
        {
            return WebRootProvider.Resolve();
        }

        /// <summary>
        /// v0.1.6.76：页面改走 file://，白名单相应放开 file —— 但只放行 webRoot 目录内的文件，
        /// 避免任意本地文件被当成应用页渲染；https://app/ 保留兼容（回退期仍可用）。
        /// </summary>
        private bool IsLocalAppUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var local = new System.Uri(url).LocalPath;
                    var root = System.IO.Path.GetFullPath(_webRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                             + System.IO.Path.DirectorySeparatorChar;
                    return System.IO.Path.GetFullPath(local).StartsWith(root, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
            return url.StartsWith("https://app/", StringComparison.OrdinalIgnoreCase);
        }

        private static void OpenInSystemBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            bool allowed = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            if (!allowed) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}

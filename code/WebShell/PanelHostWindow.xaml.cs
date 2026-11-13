using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PiggyCalendar.WebShell.Services;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// M0-9~M0-11 通用独立弹窗宿主：承载设置（左导航主从）、工具中心（四列网格）、关于（上下同屏）三类面板。
    /// 复用小组件创建的同一个 <see cref="CSharpBridge"/>（配置读写、开关、同步状态/触发），不新建桥。
    /// 窗口单例由 App 管理：重复打开同一面板时聚焦已有窗口，不重复创建。
    /// </summary>
    public partial class PanelHostWindow : Window
    {
        private string _webRoot;
        private readonly string _view;
        private readonly CSharpBridge _bridge;
        private readonly Action<string> _onClosed;
        private bool _initialized;
        private string _prefillDate;   // v0.1.6.60：右键日期「添加提醒」预填日期（yyyy-MM-dd），空=不预填
        private string _manageIds;     // v0.1.6.63：右键「管理提醒」当日提醒 id 列表（逗号分隔），空=不过滤

        /// <summary>
        /// 构造面板弹窗。
        /// </summary>
        /// <param name="view">面板标识：settings / tools / about，决定 panel.html 渲染哪个视图。</param>
        /// <param name="width">窗口宽度（固定尺寸）。</param>
        /// <param name="height">窗口高度（固定尺寸）。</param>
        /// <param name="title">窗口标题。</param>
        /// <param name="bridge">复用小组件桥实例。</param>
        /// <param name="onClosed">关闭回调，参数为 view，供 App 从单例字典移除。</param>
        public PanelHostWindow(string view, double width, double height, string title,
            CSharpBridge bridge, Action<string> onClosed, string prefillDate = null, string manageIds = null)
        {
            InitializeComponent();
            _view = view ?? "settings";
            _bridge = bridge;
            _onClosed = onClosed;
            _prefillDate = prefillDate;   // v0.1.6.60：日期右键「添加提醒」→ 提醒窗按该日期预填
            _manageIds = manageIds;       // v0.1.6.63：右键「管理提醒」→ 提醒窗列表过滤到当日
            Width = width;
            Height = height;
            Title = title;
            Loaded += OnLoadedAsync;
            Closed += (s, e) => { try { _onClosed?.Invoke(_view); } catch { } };
        }

        /// <summary>v0.1.6.60：面板已打开时由「日期右键 → 添加提醒」重新导航到带预填日期的 URL。</summary>
        public void OpenWithDate(string ymd, string manageIds = null)
        {
            _prefillDate = ymd;
            _manageIds = manageIds;   // v0.1.6.63：同一入口兼作「管理提醒」重导航（ids 空则退回不过滤）
            try
            {
                if (_initialized && webView?.CoreWebView2 != null)
                {
                    AppLog.Write("[PANEL] 带日期重新导航 view=" + _view + " date=" + ymd);
                    webView.CoreWebView2.Navigate(BuildUrl());
                }
            }
            catch (Exception ex) { AppLog.Write("[PANEL] 带日期重新导航失败：" + ex.Message); }
        }

        private string BuildUrl()
        {
            // v0.1.6.76：改走 file:// 直连（虚拟主机 scheme 每文档约 1 秒开销）；query 仍照旧携带
            var url = new Uri(Path.Combine(_webRoot, "panel.html")).AbsoluteUri
                    + "?view=" + Uri.EscapeDataString(_view);
            if (!string.IsNullOrWhiteSpace(_prefillDate))
                url += "&date=" + Uri.EscapeDataString(_prefillDate);
            /* v0.1.6.63：右键「管理提醒」——把当日会触发的提醒 id 带给面板，由面板过滤「已添加提醒」列表 */
            if (!string.IsNullOrWhiteSpace(_manageIds))
                url += "&ids=" + Uri.EscapeDataString(_manageIds);
            AppLog.Write("[PANEL] URL view=" + _view
                + (string.IsNullOrWhiteSpace(_prefillDate) ? "" : " date=" + _prefillDate)
                + (string.IsNullOrWhiteSpace(_manageIds) ? "" : " ids=" + _manageIds));   // v0.1.6.63：入口参数可回溯
            return url;
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();   // v0.1.6.75：分段耗时打点
            try
            {
                AppLog.Write("[PERF] panel-load start view=" + _view);
                _webRoot = LocateWebRoot();
                AppLog.Write("[PERF] webRoot=" + sw.ElapsedMilliseconds + "ms");
                if (_webRoot == null)
                {
                    AppLog.Write("[PANEL] 未找到 WebUI\\prototype 目录");
                    return;
                }

                WebView2LoaderBootstrap.EnsureWebView2LoaderReady();
                AppLog.Write("[PERF] loaderReady=" + sw.ElapsedMilliseconds + "ms");

                // 弹窗与小组件并行存活，WebView2 userDataFolder 必须隔离为独立目录，避免目录占用冲突。
                // v0.1.6.76：三个面板共用同一个 Environment —— 少起一组浏览器进程（实测 6 进程 / 337MB），
                // 且第二个面板起不再付 CreateAsync + EnsureCore 的冷启动（375ms → 约 107ms）。
                CoreWebView2Environment wv2Env = await SharedEnvTask();
                AppLog.Write("[PERF] createEnv=" + sw.ElapsedMilliseconds + "ms");
                await webView.EnsureCoreWebView2Async(wv2Env);
                AppLog.Write("[PERF] ensureCore=" + sw.ElapsedMilliseconds + "ms");
                try { webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1E, 0x24); } catch { }

                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app", _webRoot, CoreWebView2HostResourceAccessKind.Allow);

                if (_bridge != null)
                    webView.CoreWebView2.AddHostObjectToScript("CSharpBridge", _bridge);

                // 导航白名单：仅本地 app / about:blank / data；外链交系统浏览器（与小组件同一口径）。
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
                // v0.1.6.57：面板窗一并关掉默认右键菜单，与小组件窗口保持一致
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                webView.CoreWebView2.NavigationCompleted += (s2, e2) =>
                {
                    if (!e2.IsSuccess) AppLog.Write("[PANEL] 导航失败 view=" + _view + "：" + e2.WebErrorStatus);
                    else AppLog.Write("[PERF] navCompleted=" + sw.ElapsedMilliseconds + "ms view=" + _view);
                };

                webView.CoreWebView2.Navigate(BuildUrl());
                _initialized = true;
                AppLog.Write("[PERF] navigate=" + sw.ElapsedMilliseconds + "ms");
                AppLog.Write("[PANEL] WebView2 初始化完成 view=" + _view
                    + (string.IsNullOrWhiteSpace(_prefillDate) ? "" : " prefill=" + _prefillDate));
            }
            catch (Exception ex)
            {
                AppLog.Write("[PANEL] 初始化异常 view=" + _view + "：" + ex);
            }
        }

        /// <summary>
        /// 向本面板 webview 直发事件（v0.1.6.25）。
        /// 小组件与面板共用同一 CSharpBridge 实例，但其 PostEvent 回调只绑定小组件 webview；
        /// 要让面板页收到 reminder-changed 等推送，须用面板自身 CoreWebView2.PostWebMessageAsJson 直发。
        /// </summary>
        public void PostEvent(string json)
        {
            try
            {
                if (!_initialized || webView.CoreWebView2 == null) return;
                webView.Dispatcher.Invoke(() => webView.CoreWebView2.PostWebMessageAsJson(json));
            }
            catch (Exception ex) { AppLog.Write("[PANEL] 面板推送失败 view=" + _view + "：" + ex.Message); }
        }

        // v0.1.6.76：面板级单例 Environment，并发打开多面板时共用同一次创建
        private static Task<CoreWebView2Environment> _envTask;
        private static readonly object _envLock = new object();

        private static Task<CoreWebView2Environment> SharedEnvTask()
        {
            lock (_envLock)
            {
                if (_envTask == null)
                {
                    _envTask = CoreWebView2Environment.CreateAsync(null, AppConfig.PanelWebView2CacheDir)
                        .ContinueWith(t2 =>
                        {
                            if (t2.IsFaulted || t2.IsCanceled)
                            {
                                lock (_envLock) { _envTask = null; }   // 失败不缓存，下次重试
                                throw t2.Exception ?? (Exception)new TaskCanceledException();
                            }
                            return t2.Result;
                        }, TaskScheduler.Default);
                }
                return _envTask;
            }
        }

        /// <summary>定位前端目录（与小组件共用 WebRootProvider：磁盘优先，无则内嵌解压）。</summary>
        private static string LocateWebRoot()
        {
            return WebRootProvider.Resolve();
        }

        /// <summary>
        /// v0.1.6.76：panel.html 改走 file://，白名单放开 file 但限定在 webRoot 目录内；
        /// https://app/ 保留兼容，回退期仍可用。
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
                    var local = new Uri(url).LocalPath;
                    var root = Path.GetFullPath(_webRoot).TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
                    return Path.GetFullPath(local).StartsWith(root, StringComparison.OrdinalIgnoreCase);
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

        // ---------- 自绘深色标题栏交互 ----------

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CloseBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b)
                b.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x52, 0x4D));
        }

        private void CloseBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b)
                b.Background = System.Windows.Media.Brushes.Transparent;
        }
    }
}

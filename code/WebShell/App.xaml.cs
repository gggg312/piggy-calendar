using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PiggyCalendar.WebShell.Services;
using PiggyCalendar.WebShell.Services.HolidaySync;

namespace PiggyCalendar.WebShell
{
    /// <summary>
    /// 应用宿主：单实例 Mutex → 日志/配置初始化 → 托盘常驻 + 桌面悬浮小组件。
    /// ShutdownMode=OnExplicitShutdown：小组件隐藏不退出，仅托盘「退出」结束进程。
    /// </summary>
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private TrayService _tray;
        // v0.1.6.59：隐藏提示每进程只弹一次（进程内标记，不写 config.ini；重启后重新计一次）
        private bool _hideTipShown;
        private CalendarPopupWindow _widget;
        private CSharpBridge _shellBridge;
        // 面板弹窗单例：同一视图只开一个，重复点击聚焦已有窗口；关闭后从字典移除。
        private readonly System.Collections.Generic.Dictionary<string, PanelHostWindow> _panels =
            new System.Collections.Generic.Dictionary<string, PanelHostWindow>();
        private ShutdownRemindWindow _remindWindow; // 关机前提醒弹窗单例（v0.1.6.10）
        private ReminderRemindWindow _reminderRemindWindow; // 提醒到点弹窗单例（v0.1.6.23）

        private static string SingleInstanceMutexName
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return "PiggyCalendar_SingleInstance_" + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 单实例互斥体（名称带版本号，避免旧版本残留进程持锁阻塞新版本启动）
            _singleInstance = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // 已有实例运行：OnExplicitShutdown 模式下早期 Shutdown() 不会立即终止启动中的 WPF 状态机，
                // 必须用 Environment.Exit 强制结束第二实例（托盘常驻程序单实例拦截的可靠做法）。
                AppLog.Init();
                AppLog.Write("[APP] 检测到已有实例，第二实例退出");
                Environment.Exit(0);
            }

            // 2. 日志与配置
            AppLog.Init();
            AppLog.Write("[APP] 小猪日历启动，版本=" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            AppConfig.LoadFromIni();

            // 2.5 WebView2 Runtime 检测（v0.1.6.95）：缺失时明确提示并退出，避免白屏/无响应
            if (!EnsureWebView2Runtime())
            {
                Environment.Exit(0);
                return;
            }

            // 3. 全局异常捕获（写日志，不崩溃退出）
            RegisterGlobalExceptionHandlers();

            // 4. 同步开机自启注册表状态（v0.1.6.99：尊重用户在系统里禁用自启的意图，不强行写回）
            try
            {
                if (AppConfig.AutoStartEnabled)
                {
                    if (!AutoStartService.IsRegistered())
                    {
                        // 注册表项已不存在：用户可能在任务管理器「启动」里禁用，或被安全软件清理。
                        // 此时不强行写回（否则用户以为关了、下次启动又冒出来），把开关同步为关闭。
                        AppConfig.AutoStartEnabled = false;
                        try { AppConfig.Save(); } catch { }
                        AppLog.Write("[AUTOSTART] 注册表中已无自启项（可能被系统或用户关闭），开关同步为关闭");
                    }
                    else
                    {
                        AutoStartService.ApplyAutoStart(true);   // 路径未变化时内部直接跳过，不重复写
                    }
                }
                else
                {
                    AutoStartService.ApplyAutoStart(false);
                }
            }
            catch (Exception ex) { AppLog.Write("[AUTOSTART] 同步失败：" + ex.Message); }

            // ② 恢复自动关机调度（重启后未到点则继续，已过期清除）
            try { AutoShutdownService.Instance.RestoreFromConfig(); } catch (Exception ex) { AppLog.Write("[SHUTDOWN] 恢复调度失败：" + ex.Message); }

            // ④ 启动提醒服务（后台每秒调度，独立于小组件窗口：窗口关闭/最小化仍到点弹窗）
            try
            {
                ReminderService.Instance.OnFire = (id, text, sound) => SafeInvoke(() => ShowReminderRemind(id, text, sound));
                ReminderService.Instance.Changed += () => BroadcastReminderChanged();
                ReminderService.Instance.Start();
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 启动提醒服务失败：" + ex.Message); }

            // 5. 桌面悬浮小组件；桥就绪后绑定壳层动作回调
            _widget = new CalendarPopupWindow(bridge =>
            {
                _shellBridge = bridge;
                // D 方案：手动 CRUD（经此桥调用）成功后广播 manual-updated，驱动小组件窗口日历刷新（index 订阅）
                bridge.BindManualChanged(() => bridge.PostEvent("{\"kind\":\"manual-updated\",\"payload\":{}}"));
                bridge.BindShellActions(
                    showCalendar: () => SafeInvoke(() => ShowWidget()),
                    hideCalendar: () => SafeInvoke(() => HideWidget()),
                    openSettings: () => SafeInvoke(OpenSettings),
                    openAbout: () => SafeInvoke(OpenAbout),
                    // v0.1.6.59：桥也必须走统一入口 SetWidgetVisible（原直连 ApplyWidgetVisibility 会绕过
                    // 显隐日志与一次性隐藏提示，造成「两条通道逻辑不完全相同」）
                    setWidgetVisible: visible => SafeInvoke(() => SetWidgetVisible(visible)),
                    beginDrag: () => SafeInvoke(() => _widget.StartDrag()),
                    beginResize: dir => SafeInvoke(() => _widget.BeginResize(dir)),
                    openAutoShutdown: () => SafeInvoke(OpenAutoShutdown),
                    closeAutoShutdown: () => SafeInvoke(CloseAutoShutdownPanel),
                    openReminder: () => SafeInvoke(OpenReminder),
                    openReminderAt: ymd => SafeInvoke(() => OpenReminderAt(ymd)),
                    openReminderManage: (ymd, ids) => SafeInvoke(() => OpenReminderManage(ymd, ids)),
                    closeReminder: () => SafeInvoke(CloseReminderPanel),
                    fitWidgetWidth: px => SafeInvoke(() => _widget.FitWidgetWidth(px)));
                bridge.BindAppearance(() => SafeInvoke(() => _widget.ApplyAppearance()));

                // M0-8c：绑定同步引擎（复用桥的 remote 读写与事件出口），启动后按状态机静默同步。
                // 引擎自身异常不得阻断小组件桥绑定与渲染。
                try
                {
                    HolidaySyncService.Instance.Attach(bridge, bridge.PostEvent);
                    HolidaySyncService.Instance.Start();
                }
                catch (Exception ex)
                {
                    AppLog.Write("[SYNC] 引擎初始化失败（不影响小组件本体）：" + ex.Message);
                }
            });

            // 6. 托盘：左键 / 右键都弹菜单；首项「隐藏/显示小组件」（v0.1.6.57 起菜单不再自动唤出小组件）
            _tray = new TrayService(
                isWidgetVisible: () => AppConfig.WidgetVisible,
                toggleWidget: () => SafeInvoke(ToggleWidgetVisibility),
                openSettings: () => SafeInvoke(OpenSettings),
                openAbout: () => SafeInvoke(OpenAbout),
                resetWidgetPos: () => SafeInvoke(() =>
                {
                    // 重置位置必须有可见反馈：窗口处于隐藏态时先显示出来
                    if (!AppConfig.WidgetVisible) SetWidgetVisible(true);
                    _widget.ResetToDefaultPos();
                }),
                exitApp: () => SafeInvoke(ExitApplication));
            _tray.Initialize();
            // 自动关机：关机前提前提醒 → 置顶弹窗（v0.1.6.10，小组件所在屏幕居中 + 实时倒计时 + 取消本次）
            AutoShutdownService.Instance.OnAdvanceNotify = target => SafeInvoke(() => ShowShutdownRemind(target));

            // 7. 按配置决定小组件初始显隐
            ApplyWidgetVisibility(AppConfig.WidgetVisible);

            AppLog.Write("[APP] 启动完成：托盘 + 桌面小组件就绪");
        }

        private void ShowWidget()
        {
            _widget.ShowWidget();
        }

        private void HideWidget()
        {
            _widget.HideWidget();
        }

        private void ApplyWidgetVisibility(bool visible)
        {
            _widget.ApplyVisibility(visible);
        }

        /// <summary>v0.1.6.57：托盘首项「隐藏/显示小组件」切换。</summary>
        private void ToggleWidgetVisibility()
        {
            SetWidgetVisible(!AppConfig.WidgetVisible);
        }

        /// <summary>
        /// v0.1.6.57：统一显隐入口（托盘菜单、托盘「重置窗口位置」共用）。
        /// 先写 config.ini 再即时应用：重启后仍保持隐藏态；组件右上角隐藏按钮走桥的
        /// SetWidgetVisible(bool)，逻辑完全相同，两条通道写的是同一个 widgetVisible 键。
        /// </summary>
        private void SetWidgetVisible(bool visible)
        {
            // v0.1.6.59：原状态取窗口真实可见性，不能取 AppConfig —— 桥在回调前已把 widgetVisible 写成新值，读 config 必失真
            bool wasVisible = _widget != null && _widget.IsVisible;
            try
            {
                AppConfig.WidgetVisible = visible;
                AppConfig.Save();
            }
            catch (Exception ex)
            {
                AppLog.Write("[WIDGET] 显隐配置写入失败：" + ex.Message);
            }
            ApplyWidgetVisibility(visible);
            AppLog.Write("[WIDGET] 小组件显隐 → " + (visible ? "显示" : "隐藏"));
            // v0.1.6.59：由显示转为隐藏时给一次性托盘气泡，让用户知道窗口没丢、只是进了托盘。
            // 只在托盘首项与组件右上角隐藏按钮这两条用户主动隐藏的通道上触发；每进程一次，不写 config。
            if (!visible && wasVisible && !_hideTipShown)
            {
                _hideTipShown = true;
                try { _tray?.ShowTip("小猪日历", "已隐藏，托盘菜单可重新显示", 2500); } catch { }
                AppLog.Write("[WIDGET] 已弹出一次性隐藏提示（本进程仅此一次）");
            }
        }

        /// <summary>
        /// 检查 WebView2 Runtime 是否可用；缺失时先尝试用 EXE 同目录的官方安装器静默安装，
        /// 仍不可用则弹窗说明并给出下载入口。返回 false 表示无法继续启动。
        /// v0.1.6.95：Win10 1809~2004 等系统常未内置 Runtime，缺它只会白屏，必须给出可读提示。
        /// v0.1.6.96：若同目录放了官方安装器（绿色离线包场景），直接静默装，装完再检测一次。
        /// 判定用 SDK 官方接口 GetAvailableBrowserVersionString（注册表三类安装位置都能识别）。
        /// </summary>
        private static bool EnsureWebView2Runtime()
        {
            // 关键：GetAvailableBrowserVersionString 这个静态 API 本身要加载同目录的原生 WebView2Loader.dll。
            // 全新目录（尤其 Release 包）里 loader 还没释放，过早调用会抛 DllNotFoundException 被误判成"运行时缺失"。
            // 所以先释放 loader，再做检测。Debug 目录因历史运行残留 loader 会掩盖此问题，必须按空目录验证。
            try { WebView2LoaderBootstrap.EnsureWebView2LoaderReady(); }
            catch (Exception ex) { AppLog.Write("[WEBVIEW2] 释放 loader 失败：" + ex.Message); }

            string version = TryGetRuntimeVersion();
            if (!string.IsNullOrWhiteSpace(version))
            {
                AppLog.Write("[WEBVIEW2] Runtime 版本：" + version);
                return true;
            }

            AppLog.Write("[WEBVIEW2] 未检测到 WebView2 Runtime，尝试使用同目录安装器");

            // 同目录若带官方安装器（引导程序 2MB 或离线独立安装程序），静默安装后再检测
            string installer = FindRuntimeInstaller();
            if (!string.IsNullOrEmpty(installer))
            {
                try
                {
                    AppLog.Write("[WEBVIEW2] 找到安装器：" + installer + "，开始静默安装");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = installer,
                        Arguments = "/silent /install",
                        UseShellExecute = true
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        // 离线安装程序较大，最多等 8 分钟；超时不阻塞启动流程
                        if (!proc.WaitForExit(8 * 60 * 1000))
                            AppLog.Write("[WEBVIEW2] 安装器等待超时，继续检测");
                    }
                    version = TryGetRuntimeVersion();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        AppLog.Write("[WEBVIEW2] 安装成功，Runtime 版本：" + version);
                        return true;
                    }
                    AppLog.Write("[WEBVIEW2] 安装器已执行但运行时仍不可用");
                }
                catch (Exception ex)
                {
                    AppLog.Write("[WEBVIEW2] 调用安装器失败：" + ex.GetType().Name + " " + ex.Message);
                }
            }

            const string downloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            string msg = "小猪日历需要 Microsoft Edge WebView2 Runtime 才能运行。\n\n"
                       + "当前系统未检测到该运行时。点击「是」打开微软下载页面，安装后重新启动小猪日历即可。\n\n"
                       + "（Win11 与多数 Win10 已自带；若确认已安装仍提示此信息，请重启系统后再试。）";
            try
            {
                var r = MessageBox.Show(msg, "缺少 WebView2 Runtime",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(downloadUrl); }
                    catch (Exception ex) { AppLog.Write("[WEBVIEW2] 打开下载页失败：" + ex.Message); }
                }
            }
            catch { }
            return false;
        }

        /// <summary>取 WebView2 Runtime 版本号；SDK API 失败时回退读注册表（不依赖原生 loader）。</summary>
        private static string TryGetRuntimeVersion()
        {
            try
            {
                string v = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
                AppLog.Write("[WEBVIEW2] SDK 接口返回空版本号，回退注册表检测");
            }
            catch (Exception ex)
            {
                AppLog.Write("[WEBVIEW2] Runtime 检测异常：" + ex.GetType().Name + " " + ex.Message);
            }
            string reg = TryGetRuntimeVersionFromRegistry();
            if (!string.IsNullOrWhiteSpace(reg))
                AppLog.Write("[WEBVIEW2] Runtime 版本（注册表）：" + reg);
            return reg;
        }

        /// <summary>
        /// 读注册表判定 Runtime 是否安装（官方文档方法 1）。WebView2 Runtime 的 EdgeUpdate 客户端 GUID 固定，
        /// 机器级在 WOW6432Node 下，用户级在 HKCU 下，两处都要查。
        /// </summary>
        private static string TryGetRuntimeVersionFromRegistry()
        {
            const string runtimeKey = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            string[] clientPaths = new[]
            {
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + runtimeKey,
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + runtimeKey
            };
            var hives = new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser };
            foreach (var hive in hives)
            {
                foreach (string sub in clientPaths)
                {
                    try
                    {
                        using (var key = hive.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            object pv = key.GetValue("pv");
                            string s = pv as string;
                            if (!string.IsNullOrEmpty(s) && s != "0.0.0.0") return s;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 在 EXE 同目录查找微软官方 WebView2 安装器：在线引导程序（MicrosoftEdgeWebview2Setup.exe）
        /// 或离线独立安装程序（MicrosoftEdgeWebView2RuntimeInstaller*.exe）。找不到返回 null。
        /// </summary>
        private static string FindRuntimeInstaller()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidates = new[]
                {
                    "MicrosoftEdgeWebview2Setup.exe",
                    "MicrosoftEdgeWebView2Setup.exe",
                    "MicrosoftEdgeWebView2RuntimeInstallerX64.exe",
                    "MicrosoftEdgeWebView2RuntimeInstallerX86.exe",
                    "MicrosoftEdgeWebView2RuntimeInstallerARM64.exe"
                };
                foreach (string name in candidates)
                {
                    string path = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(path)) return path;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[WEBVIEW2] 查找安装器异常：" + ex.Message);
            }
            return null;
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (s, args) =>
            {
                AppLog.Write("[APP] UI 线程未捕获异常：" + args.Exception);
                args.Handled = true; // 记录后继续运行，避免托盘常驻进程崩溃
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                AppLog.Write("[APP] 非 UI 线程未捕获异常：" + args.ExceptionObject);
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                AppLog.Write("[APP] 任务未观测异常：" + args.Exception);
                args.SetObserved();
            };
        }

        /// <summary>自动关机设置窗口（独立窗口，取代原工具中心，小组件底部原位按钮触发）。</summary>
        private void OpenAutoShutdown()
        {
            OpenPanel("autoShutdown", 800, 680, "自动关机");
        }

        /// <summary>关闭自动关机面板窗（启动成功后前端调用，实现「启动后窗口关闭」）。</summary>
        private void CloseAutoShutdownPanel()
        {
            try
            {
                PanelHostWindow w;
                lock (_panels) { if (!_panels.TryGetValue("autoShutdown", out w) || w == null) return; _panels.Remove("autoShutdown"); }
                w.Close();
            }
            catch (Exception ex) { AppLog.Write("[APP] 关闭自动关机面板失败：" + ex.Message); }
        }

        /// <summary>提醒设置窗口（④ v0.1.6.23，独立窗口，小组件底部提醒按钮原位触发）。</summary>
        private void OpenReminder()
        {
            OpenPanel("reminder", 800, 680, "提醒");
        }

        /// <summary>v0.1.6.60：日历日期右键「添加提醒」→ 打开提醒窗并按该日期预填（yyyy-MM-dd）。</summary>
        private void OpenReminderAt(string ymd)
        {
            if (string.IsNullOrWhiteSpace(ymd)) { OpenReminder(); return; }
            OpenPanel("reminder", 800, 680, "提醒", ymd);
        }

        /// <summary>v0.1.6.63：日历日期右键「管理提醒」→ 提醒窗 + 列表过滤到当日的 ids。</summary>
        private void OpenReminderManage(string ymd, string ids)
        {
            if (string.IsNullOrWhiteSpace(ymd)) { OpenReminder(); return; }
            OpenPanel("reminder", 800, 680, "提醒", ymd, ids);
        }

        /// <summary>关闭提醒面板窗（保存成功后前端调用）。</summary>
        private void CloseReminderPanel()
        {
            try
            {
                PanelHostWindow w;
                lock (_panels) { if (!_panels.TryGetValue("reminder", out w) || w == null) return; _panels.Remove("reminder"); }
                w.Close();
            }
            catch (Exception ex) { AppLog.Write("[APP] 关闭提醒面板失败：" + ex.Message); }
        }

        /// <summary>提醒到点弹窗（v0.1.6.23）：置顶、小组件所在屏幕居中；按钮「稍后 5 分钟」/「我知道了」；窗口关闭/最小化仍弹（调度在 ReminderService）。</summary>
        private void ShowReminderRemind(string id, string text, bool sound)
        {
            try
            {
                if (_reminderRemindWindow != null) { _reminderRemindWindow.Activate(); return; }
                var w = new ReminderRemindWindow(
                    id, string.IsNullOrWhiteSpace(text) ? "提醒" : text, sound,
                    onPostpone: () => SafeInvoke(() => ReminderService.Instance.Postpone(id)),
                    onAcknowledge: () => SafeInvoke(() => ReminderService.Instance.Acknowledge(id)),
                    onClosed: () => { _reminderRemindWindow = null; });
                _reminderRemindWindow = w;
                PositionToWidgetScreen(w);
                w.Show();
                w.Activate();
                /* v0.1.6.98：触发成功也记一条，便于用户报"到点没弹"时对照时间线；此前只有失败才写日志 */
                AppLog.Write("[APP] 提醒弹窗已显示 id=" + id);
            }
            catch (Exception ex) { AppLog.Write("[APP] 提醒弹窗失败：" + ex.Message); }
        }

        /// <summary>提醒数据变更 → 广播 reminder-changed 到小组件（刷新入口选中态）与所有已打开面板（实时刷新清单）。</summary>
        private void BroadcastReminderChanged()
        {
            try
            {
                var h = ReminderService.Instance.GetHasToday();
                var json = "{\"kind\":\"reminder-changed\",\"payload\":" + h + "}";
                _shellBridge?.PostEvent(json);
                // v0.1.6.25：面板开着也要实时收到提醒变更（触发后清单立即删掉该单次提醒）
                lock (_panels)
                {
                    foreach (var p in _panels.Values)
                    {
                        try { p.PostEvent(json); } catch { }
                    }
                }
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 广播失败：" + ex.Message); }
        }

        /// <summary>设置弹窗（DeskBox 式钻入布局）：左导航 + 右侧内容区层级钻入，800×680 固定不可调。</summary>
        private void OpenSettings()
        {
            OpenPanel("settings", 800, 680, "设置");
        }        /// <summary>关于弹窗（M0-11）：上下同屏，简介/双仓库/版本号/赞赏码。</summary>
        private void OpenAbout()
        {
            OpenPanel("about", 480, 560, "关于");
        }

        /// <summary>打开指定面板单例：已存在则聚焦，否则新建；关闭后从字典移除。</summary>
        private void OpenPanel(string view, double width, double height, string title, string prefillDate = null, string manageIds = null)
        {
            try
            {
                PanelHostWindow existing;
                if (_panels.TryGetValue(view, out existing) && existing != null)
                {
                    // v0.1.6.60：已开着同一面板时，若带预填日期则重新导航（否则停在上一次的旧表单状态）
                    if (!string.IsNullOrWhiteSpace(prefillDate)) existing.OpenWithDate(prefillDate, manageIds);
                    existing.Show();
                    existing.Activate();
                    return;
                }
                var panel = new PanelHostWindow(view, width, height, title, _shellBridge, closedView =>
                {
                    lock (_panels) { _panels.Remove(closedView); }
                }, prefillDate, manageIds);
                lock (_panels) { _panels[view] = panel; }
                // v0.1.6.77：设置/提醒/关于面板跟随小组件所在屏幕居中，不再跟随鼠标所在屏幕
                PositionToWidgetScreen(panel);
                panel.Show();
                panel.Activate();
            }
            catch (Exception ex)
            {
                AppLog.Write("[APP] 打开面板 view=" + view + " 失败：" + ex.Message);
            }
        }

        private void ShowShutdownRemind(DateTime target)
        {
            try
            {
                if (_remindWindow != null) { _remindWindow.Activate(); return; }
                // v0.1.6.17：弹窗从调度服务实时读当前目标，用户改时间后自动跟随新目标倒计时
                var w = new ShutdownRemindWindow(
                    targetProvider: () => AutoShutdownService.Instance.CurrentTarget,
                    onCancelOnce: () => SafeInvoke(() => AutoShutdownService.Instance.SkipOnce()),
                    onClosed: () => { _remindWindow = null; });
                _remindWindow = w;
                PositionToWidgetScreen(w);
                w.Show();
                w.Activate();
            }
            catch (Exception ex) { AppLog.Write("[APP] 关机提醒弹窗失败：" + ex.Message); }
        }

        /// <summary>把弹窗定位到小组件所在屏幕的工作区居中（多屏时跟随小组件那块屏）。</summary>
        private void PositionToWidgetScreen(Window w)
        {
            try
            {
                var sc = System.Windows.Forms.Screen.FromRectangle(
                    new System.Drawing.Rectangle((int)_widget.Left, (int)_widget.Top,
                        (int)_widget.ActualWidth, (int)_widget.ActualHeight));
                var wa = sc.WorkingArea;
                w.Left = wa.Left + (wa.Width - w.Width) / 2.0;
                w.Top = wa.Top + (wa.Height - w.Height) / 2.0;
            }
            catch { w.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen; }
        }

        private void SafeInvoke(Action action)
        {
            try
            {
                if (Dispatcher.CheckAccess()) action();
                else Dispatcher.Invoke(action);
            }
            catch (Exception ex)
            {
                AppLog.Write("[APP] 回调异常：" + ex.Message);
            }
        }

        private void ExitApplication()
        {
            AppLog.Write("[APP] 用户从托盘退出，开始清理");
            try { _tray?.Dispose(); } catch { }
            try { _widget?.Close(); } catch { }
            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}

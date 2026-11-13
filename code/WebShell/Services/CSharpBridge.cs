using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// C# → JS 桥对象（AddHostObjectToScript 注入，JS 端 window.CSharpBridge）。
    /// 方法返回简单类型或 JSON 字符串；JS 端经 bridge.js 得到 Promise。
    /// 按业务分 partial：本文件为基座（版本/通道/配置/日志/小组件显隐与位置）。
    /// </summary>
    [ComVisible(true)]
    public partial class CSharpBridge
    {
        /// <summary>C# 主动推事件给 JS 的出口（由主窗口注入 PostWebMessageAsJson）。</summary>
        private readonly Action<string> _postEvent;

        /// <summary>手动节假日变更回调（App 绑定为广播 manual-updated 到小组件窗口；D 方案）。</summary>
        private Action _manualChanged;

        // 壳层动作回调（由 App 初始化后绑定；避免桥对象反向持有窗口引用）
        private Action _showCalendar;
        private Action _hideCalendar;
        private Action _openSettings;
        private Action _openAbout;
        private Action _openAutoShutdown;
        private Action _closeAutoShutdown;
        private Action _openReminder;    // ④ v0.1.6.23 提醒
        private Action _closeReminder;   // ④ v0.1.6.23 提醒
        private Action<string> _openReminderAt;  // v0.1.6.60 日期右键「添加提醒」带预填日期
        private Action<string, string> _openReminderManage;  // v0.1.6.63 日期右键「管理提醒」（ymd + 当日提醒 id 列表）
        private Action<bool> _setWidgetVisible;
        private Action _beginDrag;
        private Action<string> _beginResize;
        private Action<double> _fitWidgetWidth;   // v0.1.6.33 日历列宽自适应 → 窗口加宽

        public CSharpBridge(Action<string> postEvent)
        {
            _postEvent = postEvent;
        }

        /// <summary>绑定壳层窗口动作回调。</summary>
        public void BindShellActions(Action showCalendar, Action hideCalendar,
            Action openSettings, Action openAbout, Action<bool> setWidgetVisible,
            Action beginDrag, Action<string> beginResize, Action openAutoShutdown = null,
            Action closeAutoShutdown = null, Action openReminder = null, Action closeReminder = null,
            Action<double> fitWidgetWidth = null, Action<string> openReminderAt = null,
            Action<string, string> openReminderManage = null)
        {
            _showCalendar = showCalendar;
            _hideCalendar = hideCalendar;
            _openSettings = openSettings;
            _openAbout = openAbout;
            _openAutoShutdown = openAutoShutdown;
            _closeAutoShutdown = closeAutoShutdown;
            _openReminder = openReminder;
            _closeReminder = closeReminder;
            _openReminderAt = openReminderAt;
            _openReminderManage = openReminderManage;   // v0.1.6.63
            _setWidgetVisible = setWidgetVisible;
            _beginDrag = beginDrag;
            _beginResize = beginResize;
            _fitWidgetWidth = fitWidgetWidth;
        }

        /// <summary>关闭自动关机面板窗（启动成功后前端调用）。</summary>
        public void CloseAutoShutdownPanel()
        {
            try { _closeAutoShutdown?.Invoke(); } catch { }
        }

        /// <summary>壳层主动向前端推送消息（widget-focus-state 等）。</summary>
        public void PostEvent(string json)
        {
            try { _postEvent?.Invoke(json); } catch { }
        }

        /// <summary>绑定手动节假日变更回调（App 绑定为广播 manual-updated 到小组件窗口）。</summary>
        public void BindManualChanged(Action a) => _manualChanged = a;

        /// <summary>手动 CRUD 成功后通知（Holidays partial 调用）。</summary>
        public void NotifyManualChanged()
        {
            try { _manualChanged?.Invoke(); } catch { }
        }

        // ---------- 基座 ----------

        /// <summary>壳/软件版本号（与 AssemblyInfo 一致）。</summary>
        public string GetVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.1.0.0" : v.ToString(4);
        }

        /// <summary>程序所在目录（EXE 同目录）。</summary>
        public string GetAppPath()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>通道连通性自检：返回 pong + 时间戳。</summary>
        public string Ping()
        {
            return "pong:" + DateTime.Now.ToString("HH:mm:ss.fff");
        }

        /// <summary>前端写运行日志到 app_log.log。</summary>
        public void LogLine(string msg)
        {
            AppLog.Write("[JS] " + (msg ?? ""));
        }

        /// <summary>壳环境信息（系统版本、主显示器参数、版本号）。</summary>
        public string GetShellInfo()
        {
            var d = new Dictionary<string, object>
            {
                ["version"] = GetVersion(),
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["is64Bit"] = Environment.Is64BitProcess,
                ["widgetVisible"] = AppConfig.WidgetVisible,
                ["lunarEnabled"] = AppConfig.LunarEnabled,
                ["autoStartEnabled"] = AppConfig.AutoStartEnabled
            };
            return Json(d);
        }

        /// <summary>全局配置 JSON（前端启动拉取；字段 camelCase）。</summary>
        public string GetSettings()
        {
            var d = new Dictionary<string, object>
            {
                ["widgetVisible"] = AppConfig.WidgetVisible,
                ["lunarEnabled"] = AppConfig.LunarEnabled,
                ["autoStartEnabled"] = AppConfig.AutoStartEnabled,
                ["version"] = GetVersion()
            };
            return Json(d);
        }

        /// <summary>更新配置（部分键即可），写 config.ini。返回 "ok" 或 "err:..."。</summary>
        public string SaveSettings(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空配置";
                var ser = new JavaScriptSerializer();
                var d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:配置解析失败";

                if (d.ContainsKey("widgetVisible"))
                    AppConfig.WidgetVisible = GetBool(d, "widgetVisible", AppConfig.WidgetVisible);
                if (d.ContainsKey("lunarEnabled"))
                    AppConfig.LunarEnabled = GetBool(d, "lunarEnabled", AppConfig.LunarEnabled);
                if (d.ContainsKey("autoStartEnabled"))
                    AppConfig.AutoStartEnabled = GetBool(d, "autoStartEnabled", AppConfig.AutoStartEnabled);

                AppConfig.Save();
                AutoStartService.ApplyAutoStart(AppConfig.AutoStartEnabled);
                _setWidgetVisible?.Invoke(AppConfig.WidgetVisible);
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        // ---------- 单项开关读写（方案 v0.3 接口） ----------

        public bool GetWidgetVisible() => AppConfig.WidgetVisible;

        public string SetWidgetVisible(bool visible)
        {
            AppConfig.WidgetVisible = visible;
            AppConfig.Save();
            _setWidgetVisible?.Invoke(visible);
            return "ok";
        }

        /// <summary>读取已记忆的小组件位置；未记忆返回 x=-1,y=-1。</summary>
        public string GetWidgetPosition()
        {
            return Json(new Dictionary<string, object>
            {
                ["x"] = AppConfig.WidgetPosX,
                ["y"] = AppConfig.WidgetPosY
            });
        }

        /// <summary>拖动结束上报小组件左上角坐标，壳层写 config.ini。</summary>
        public string SetWidgetPosition(int x, int y)
        {
            try
            {
                AppConfig.WidgetPosX = x;
                AppConfig.WidgetPosY = y;
                AppConfig.Save();
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        public bool GetLunarEnabled() => AppConfig.LunarEnabled;

        public string SetLunarEnabled(bool enabled)
        {
            AppConfig.LunarEnabled = enabled;
            AppConfig.Save();
            // 小组件 WebView2 复用同一事件出口，收到后刷新农历副标题显隐
            PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"lunarEnabled\":" + (enabled ? "true" : "false") + "}}");
            return "ok";
        }

        public bool GetAutostartEnabled() => AppConfig.AutoStartEnabled;

        public string SetAutostartEnabled(bool enabled)
        {
            AppConfig.AutoStartEnabled = enabled;
            // v0.1.6.99：写注册表失败时不要留下"开关开着但实际没生效"的假状态，回滚并记日志
            if (!AutoStartService.ApplyAutoStart(enabled))
            {
                AppConfig.AutoStartEnabled = !enabled;
                AppConfig.Save();
                AppLog.Write("[AUTOSTART] 切换失败，开关已回滚为 " + (!enabled));
                return "err";
            }
            AppConfig.Save();
            return "ok";
        }

        // ---------- 时钟显示模式（实时秒级 / 按分钟） ----------
        public bool GetClockRealTime() => AppConfig.ClockRealTime;

        public string SetClockRealTime(bool realTime)
        {
            AppConfig.ClockRealTime = realTime;
            AppConfig.Save();
            // 小组件 WebView2 收到后切换后台/聚焦刷新策略
            PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"clockRealTime\":" + (realTime ? "true" : "false") + "}}");
            return "ok";
        }

        // ---------- 窗口动作（前端导航） ----------

        public void ShowCalendarPanel() => _showCalendar?.Invoke();

        public void HideCalendarPanel() => _hideCalendar?.Invoke();

        public void OpenSettings() => _openSettings?.Invoke();

        public void OpenAbout() => _openAbout?.Invoke();

        /// <summary>打开自动关机设置窗口（独立窗口，取代原工具中心，原位按钮触发）。</summary>
        public void OpenAutoShutdown() => _openAutoShutdown?.Invoke();

        /// <summary>前端时间区按下鼠标后请求壳层发起窗口拖动。</summary>
        public void BeginDrag() => _beginDrag?.Invoke();

        /// <summary>前端缩放手柄按下后请求壳层按方向（L/R/T/B 组合）调整窗口尺寸。</summary>
        public void BeginResize(string dir) => _beginResize?.Invoke(dir);

        /// <summary>v0.1.6.33：前端量出日历需要多宽后请求壳层加宽/缩回窗口（单位：设备像素）。</summary>
        public string FitWidgetWidth(double needWidth)
        {
            try
            {
                _fitWidgetWidth?.Invoke(needWidth);
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        // ---------- 序列化与参数辅助 ----------

        private static string Json(object obj)
        {
            return new JavaScriptSerializer().Serialize(obj);
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (!d.TryGetValue(key, out object v) || v == null) return def;
            if (v is bool b) return b;
            return v.ToString() == "1" || string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            if (!d.TryGetValue(key, out object v) || v == null) return def;
            if (v is int i) return i;
            return int.TryParse(v.ToString(), out int r) ? r : def;
        }

        private static string GetStr(Dictionary<string, object> d, string key, string def)
        {
            if (!d.TryGetValue(key, out object v) || v == null) return def ?? "";
            return v.ToString();
        }
    }
}

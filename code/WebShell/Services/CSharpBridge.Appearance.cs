using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：小组件外观（色系 / 材质 / 按材质独立参数 / 位置锁定）。
    /// v0.1.6.30：取消全局透明度，改为「色系共用 + 每材质独立记忆」：
    ///   plain  → plainOpacity 0~100（默认 100）
    ///   glass  → glassFinish(clear|frosted) + glassTint 0~100（默认 20，内部 100 → alpha 204）
    ///   metal  → 只有色系，无参数
    /// </summary>
    public partial class CSharpBridge
    {
        private Action _applyAppearance;

        /// <summary>绑定窗口外观应用回调（App 初始化后绑定到 CalendarPopupWindow.ApplyAppearance）。</summary>
        public void BindAppearance(Action applyAppearance)
        {
            _applyAppearance = applyAppearance;
        }

        /// <summary>读取外观配置。opacityPercent 为兼容旧前端保留，值恒等于 plainOpacity。</summary>
        public string GetWidgetAppearance()
        {
            return Json(new Dictionary<string, object>
            {
                ["locked"] = AppConfig.WidgetLocked,
                ["opacityPercent"] = AppConfig.PlainOpacityPercent,
                ["plainOpacity"] = AppConfig.PlainOpacityPercent,
                ["glassFinish"] = AppConfig.GlassFinish,
                ["glassTint"] = AppConfig.GlassTintPercent,
                ["theme"] = AppConfig.WidgetTheme,
                ["texture"] = AppConfig.WidgetTexture,
                ["panelRadius"] = PanelRadius(),
                // v1.0.4：下发给前端，Win11 上隐藏「质感（清亮/磨砂）」选项——Win11 只保留一档毛玻璃
                ["isWin11"] = HostIsWindows11()
            });
        }

        /// <summary>
        /// v1.0.3：面板圆角半径（前端 --panel-radius）。
        /// 必须与窗口实际圆角同半径，否则面板弧与窗口边框弧错位——视觉上"圆角比边框大、探出边框"。
        ///   Win11：DWM 系统圆角固定 8px（DwmSetWindowAttribute 33=ROUND），前端也必须 8px 才能同心；
        ///   Win10：SetWindowRgn 按 14*dpi 裁圆角，前端保持 14px。
        /// 平台判断读注册表 CurrentBuildNumber，不用 Environment.OSVersion（未声明 manifest 时会被伪装成 9600）。
        /// </summary>
        private static int PanelRadius()
        {
            return HostIsWindows11() ? 8 : 14;
        }

        private static bool _isWin11Known;
        private static bool _isWin11;

        private static bool HostIsWindows11()
        {
            if (_isWin11Known) return _isWin11;
            _isWin11Known = true;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("CurrentBuildNumber");
                        if (v != null)
                        {
                            int build;
                            if (int.TryParse(v.ToString(), out build)) _isWin11 = build >= 22000;
                        }
                    }
                }
            }
            catch { }
            return _isWin11;
        }

        /// <summary>
        /// 更新外观配置（部分键即可）：
        /// locked(bool) / theme(dark|light) / texture(plain|glass|metal)
        /// / plainOpacity(0~100，兼容旧键 opacityPercent) / glassFinish(clear|frosted) / glassTint(0~100)
        /// </summary>
        public string SetWidgetAppearance(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空配置";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:配置解析失败";

                if (d.ContainsKey("locked"))
                    AppConfig.WidgetLocked = GetBool(d, "locked", AppConfig.WidgetLocked);

                // 兼容：旧前端只传 opacityPercent，按纯色透明度处理
                if (d.ContainsKey("plainOpacity") || d.ContainsKey("opacityPercent"))
                {
                    string key = d.ContainsKey("plainOpacity") ? "plainOpacity" : "opacityPercent";
                    int p = GetInt(d, key, AppConfig.PlainOpacityPercent);
                    AppConfig.PlainOpacityPercent = Math.Max(0, Math.Min(100, p));
                    AppConfig.WidgetOpacityPercent = AppConfig.PlainOpacityPercent;
                }

                if (d.ContainsKey("glassFinish"))
                {
                    string f = d["glassFinish"] != null ? d["glassFinish"].ToString() : AppConfig.GlassFinish;
                    AppConfig.GlassFinish = (f == "clear") ? "clear" : "frosted";
                }

                if (d.ContainsKey("glassTint"))
                {
                    int t = GetInt(d, "glassTint", AppConfig.GlassTintPercent);
                    AppConfig.GlassTintPercent = Math.Max(0, Math.Min(100, t));
                }

                if (d.ContainsKey("theme"))
                {
                    string t = d["theme"] != null ? d["theme"].ToString() : AppConfig.WidgetTheme;
                    AppConfig.WidgetTheme = t == "light" ? "light" : "dark";
                }

                if (d.ContainsKey("texture"))
                {
                    string x = d["texture"] != null ? d["texture"].ToString() : AppConfig.WidgetTexture;
                    AppConfig.WidgetTexture = (x == "glass" || x == "metal") ? x : "plain";
                    AppConfig.WidgetOpacityBackup = -1;   /* v0.1.6.30 起不再备份/还原透明度 */
                }

                AppConfig.Save();
                PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"widgetLocked\":" + (AppConfig.WidgetLocked ? "true" : "false")
                    + ",\"widgetOpacityPercent\":" + AppConfig.PlainOpacityPercent
                    + ",\"widgetTheme\":\"" + AppConfig.WidgetTheme + "\""
                    + ",\"widgetTexture\":\"" + AppConfig.WidgetTexture + "\""
                    + ",\"widgetGlassFinish\":\"" + AppConfig.GlassFinish + "\""
                    + ",\"widgetGlassTint\":" + AppConfig.GlassTintPercent + "}}");
                _applyAppearance?.Invoke();
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }
    }
}

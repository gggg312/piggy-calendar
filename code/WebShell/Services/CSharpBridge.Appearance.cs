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
                // v1.0.5 浓度改为「可读性预算」：auto=true 时按背景亮度自动求解，滑块变偏移微调
                ["glassTintAuto"] = AppConfig.GlassTintAuto,
                ["glassTintShift"] = AppConfig.GlassTintShift,
                ["theme"] = AppConfig.WidgetTheme,
                ["texture"] = AppConfig.WidgetTexture,
                // v1.0.6 皮肤预设：theme+texture 的一维组合视图，前端只显示这一排五档
                ["skin"] = AppConfig.DeriveSkin(AppConfig.WidgetTexture, AppConfig.WidgetTheme),
                ["panelRadius"] = PanelRadius(),
                // v1.0.4：下发给前端，Win11 上隐藏「质感（清亮/磨砂）」选项——Win11 只保留一档毛玻璃
                ["isWin11"] = HostIsWindows11()
            });
        }

        /// <summary>
        /// 面板外角半径（前端 --panel-radius）。小组件外角统一为直角，恒返回 0。
        /// （历史：Win11 曾用 DWM 系统圆角 8px、Win10 曾用 SetWindowRgn 14*dpi；实测 Win10 region 挂不住，
        /// 两端统一改为直角窗口 + 直角面板，不再按平台区分半径。平台判断 HostIsWindows11 仍供 isWin11 下发使用。）
        /// </summary>
        private static int PanelRadius()
        {
            return 0;
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

                /* v1.0.6 皮肤：一旦给了 skin，theme/texture 由它推导，同批传入的 theme/texture 键一律忽略，
                   避免"皮肤说一处、色系说另一处"的错位 */
                bool skinGiven = d.ContainsKey("skin");
                if (skinGiven)
                {
                    string sk = d["skin"] != null ? d["skin"].ToString() : "";
                    AppConfig.ApplySkin(sk);
                }

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

                // v1.0.5 自动浓度开关与偏移微调
                if (d.ContainsKey("glassTintAuto"))
                    AppConfig.GlassTintAuto = GetBool(d, "glassTintAuto", AppConfig.GlassTintAuto);
                if (d.ContainsKey("glassTintShift"))
                {
                    int s = GetInt(d, "glassTintShift", AppConfig.GlassTintShift);
                    AppConfig.GlassTintShift = Math.Max(-30, Math.Min(30, s));
                }

                if (!skinGiven && d.ContainsKey("theme"))
                {
                    string t = d["theme"] != null ? d["theme"].ToString() : AppConfig.WidgetTheme;
                    AppConfig.WidgetTheme = t == "light" ? "light" : "dark";
                }

                if (!skinGiven && d.ContainsKey("texture"))
                {
                    string x = d["texture"] != null ? d["texture"].ToString() : AppConfig.WidgetTexture;
                    AppConfig.WidgetTexture = (x == "glass" || x == "metal") ? x : "plain";
                    AppConfig.WidgetOpacityBackup = -1;   /* v0.1.6.30 起不再备份/还原透明度 */
                }

                /* v1.0.6：theme/texture 任何一条改动后重算皮肤名，保证下发给前端的 skin 与渲染状态同步 */
                if (!skinGiven)
                    AppConfig.WidgetSkin = AppConfig.DeriveSkin(AppConfig.WidgetTexture, AppConfig.WidgetTheme);

                AppConfig.Save();
                PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"widgetLocked\":" + (AppConfig.WidgetLocked ? "true" : "false")
                    + ",\"widgetOpacityPercent\":" + AppConfig.PlainOpacityPercent
                    + ",\"widgetTheme\":\"" + AppConfig.WidgetTheme + "\""
                    + ",\"widgetTexture\":\"" + AppConfig.WidgetTexture + "\""
                    + ",\"widgetGlassFinish\":\"" + AppConfig.GlassFinish + "\""
                    + ",\"widgetGlassTint\":" + AppConfig.GlassTintPercent
                    + ",\"widgetGlassTintAuto\":" + (AppConfig.GlassTintAuto ? "true" : "false")
                    + ",\"widgetGlassTintShift\":" + AppConfig.GlassTintShift + "}}");
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

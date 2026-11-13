using System;
using System.IO;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 程序配置：运行组件目录 + config.ini 读写。配置键与前端桥接口一一对应。
    /// M0-4R 配置项：桌面小组件显隐、小组件位置、显示农历开关、开机自启开关。
    /// </summary>
    internal static class AppConfig
    {
        private const string Section = "config";
        private const string RuntimeDirName = "运行组件";
        private const string IniFileName = "config.ini";

        // ---------- 配置字段（默认值） ----------
        public static bool WidgetVisible = true;        // 桌面悬浮小组件显隐，默认显示
        public static int WidgetPosX = -1;              // 小组件左上角 X；-1 = 未记忆位置，使用默认落点
        public static int WidgetPosY = -1;              // 小组件左上角 Y；-1 = 未记忆位置
        public static int WidgetWidth = 360;            // 小组件宽度（M0-8a 可缩放记忆；最小 320）
        public static int WidgetHeight = 560;           // 小组件高度（M0-8a 可缩放记忆；最小 510）
        public static bool LunarEnabled = true;         // 显示农历副标题，默认开启
        public static bool AutoStartEnabled = false;    // 开机自启，默认关闭
        public static bool SyncEnabled = true;          // M0-8c 在线节假日静默同步，默认开启
        public static bool ClockRealTime = true;        // 时钟显示模式：true=后台实时刷新秒数（默认）；false=按分钟更新（后台每分钟刷新，聚焦秒级）
        public static string AutoShutdownAt = "";        // 自动关机目标时刻 'yyyy-MM-dd HH:mm'；空=无任务（② 旧单次，v0.1.6.0 起改用周期字段）
        public static bool AutoShutdownEnabled = false;  // 自动关机开关（② v0.1.6.0 周期调度）
        public static string AutoShutdownTime = "";      // 时刻 'HH:mm'（②）
        public static string AutoShutdownRepeatType = "daily"; // 重复周期 daily/weekly/monthly（②）
        public static string AutoShutdownWeekdays = "";  // weekly 每周几 '1,3,5'（1=周一..7=周日）（②）
        public static int AutoShutdownMonthDay = 1;      // monthly 每月几号（②）
        public static int AutoShutdownAdvanceMinutes = 15; // 关机前提前提醒分钟（②）
        public static bool WidgetLocked = false;         // 小组件位置锁定：禁拖动+禁缩放（③）
        public static int WidgetOpacityPercent = 100;    // 【v0.1.6.30 起降级为纯色镜像】旧的全局透明度，仅保留键名兼容；真实来源见 PlainOpacityPercent
        public static string WidgetTheme = "dark";       // 小组件主题 light/dark（v0.1.6.8 浅色主题；默认深色；三材质共用）
        public static string WidgetTexture = "plain";    // 小组件质感 plain/glass/metal（v0.1.6.26 毛玻璃；v0.1.6.27 金属不锈钢；默认纯色）
        public static int WidgetOpacityBackup = -1;      // v0.1.6.28 进入金属前的透明度备份（v0.1.6.30 起不再使用，仅保留键名回退兼容）

        // ---------- v0.1.6.30 按材质独立记忆的外观参数 ----------
        public static int PlainOpacityPercent = 100;     // 纯色材质透明度 0~100（默认 100，映射 .panel-bg 的 alpha）
        public static string GlassFinish = "clear";      // 毛玻璃质感：clear=清亮（默认，对应 AccentState=3）/ frosted=磨砂（AccentState=4）
        public static int GlassTintPercent = 20;         // 毛玻璃浓度 0~100（UI 值；内部 alpha = round(v * 0.8 * 2.55)，100 → 204）

        // ---------- v0.1.6.37 「额外显示」：节日与纪念日 / 二十四节气 ----------
        public static bool FestivalEnabled = true;       // 节日与纪念日 是否显示（系统设置「额外显示」卡）
        public static string FestivalColor = "gold";    // 节日底卡颜色键（v0.1.6.42：原节日蓝改名霁蓝；旧值 blue 读取时映射到 jilan）
        public static string FestivalStyle = "outline";    // 节日底卡形态 solid=实心（默认）/ outline=只描边
        public static bool JieqiEnabled = true;          // 二十四节气 是否显示
        public static string JieqiColor = "rose";        // 节气底卡颜色键（默认青碧）
        public static string JieqiStyle = "outline";     // 节气底卡形态 outline=只描边（默认）/ solid=实心

        // ---------- v0.1.6.42 「假日样式」：公共节假日 / 周末 / 调休 的日期背景（形状·填充·颜色） ----------
        // 色板统一 7 色：jilan(霁蓝) / teal(青碧) / gold(茶金) / orange(暖橙) / rose(玫红) / gray(烟灰) / neutral(砚黑)
        public static bool HolidayBgEnabled = true;      // 公共节假日 日期背景（默认开启，沿用现状）
        public static string HolidayShape = "rect";     // round=圆形（默认）/ rect=圆角矩形（固定 8px，不对外暴露半径）
        public static string HolidayFill = "solid";      // solid=实心（默认，沿用现状）/ outline=只描边
        public static string HolidayColor = "jilan";     // 日期背景色键（默认霁蓝，替代原节日蓝）
        public static string HolidayNameMode = "follow"; // 节日名称颜色：follow=跟随日期背景色（默认）/ custom=单独指定
        public static string HolidayNameColor = "gray"; // 单独指定时的名称色键
        public static string HolidayNameFill = "outline";  // 名称底卡形态 solid=实心（默认）/ outline=只描边

        public static bool WeekendBgEnabled = true;      // 周末 日期背景（默认开启）
        public static string WeekendMode = "alone";      // follow=跟随公共节假日 / alone=单独设置（默认，沿用现状绿色）
        public static string WeekendShape = "round";     // round / rect
        public static string WeekendFill = "solid";      // solid / outline
        public static string WeekendColor = "green";      // 周末底色键（默认青碧）

        public static bool AdjustBgEnabled = true;       // 调休上班（补班日）日期背景（默认开启）
        public static string AdjustShape = "rect";      // round / rect
        public static string AdjustFill = "outline";       // solid / outline
        public static string AdjustColor = "vermilion";   // 调休底色键（默认朱红 #E0524D，沿用现状）

    // ---------- v0.1.6.63 「提醒标记色」：日期紫点 + 底部提醒图标右上角角标共用 ----------
    public static string ReminderDotColor = "iris";   // 提醒点颜色键（默认紫晶 #8B7FF5，与旧硬编码 #8b7ff5 同值）

        // ---------- v0.1.6.38 在线数据源（4 槽，用户可改地址/可单独启停） ----------
        // 槽 4 出厂为空且不启用，留给用户自己填；地址里 {year} 会在请求时替换为年份。
        public static readonly string[] DefaultSyncSourceUrl = new string[]
        {
            "https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.json",
            "https://unpkg.com/chinese-days/dist/years/{year}.json",
            "https://timor.tech/api/holiday/year/{year}",
            ""
        };
        public static readonly bool[] DefaultSyncSourceEnabled = new bool[] { true, true, true, false };
        public static readonly string[] SyncSourceUrl = new string[] { DefaultSyncSourceUrl[0], DefaultSyncSourceUrl[1], DefaultSyncSourceUrl[2], "" };
        public static readonly bool[] SyncSourceEnabled = new bool[] { true, true, true, false };

        /// <summary>基准外框尺寸（前端 stage 设计基准；窗口宽高始终保持 360:560 等比）。</summary>
        public const int BaseWidgetWidth = 360;
        public const int BaseWidgetHeight = 560;

        /// <summary>等比缩放系数上下限（模型 A：整体 transform:scale，内容随系数同步放大缩小）。</summary>
        public const double MinScale = 0.85;
        public const double MaxScale = 1.5;

        /// <summary>最小窗口尺寸（基准 × 最小系数；等比模型下不再独立钳制宽高）。</summary>
        public static int MinWidgetWidth { get { return (int)Math.Round(BaseWidgetWidth * MinScale); } }
        public static int MinWidgetHeight { get { return (int)Math.Round(BaseWidgetHeight * MinScale); } }

        private static IniFileHelper _ini;

        /// <summary>运行组件目录（EXE 同目录\运行组件）；WebView2 缓存、config.ini、手动节假日 JSON 均放此目录。</summary>
        public static string RuntimeDir
        {
            get
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RuntimeDirName);
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static string IniPath
        {
            get { return Path.Combine(RuntimeDir, IniFileName); }
        }

        public static string WebView2CacheDir
        {
            get
            {
                string dir = Path.Combine(RuntimeDir, "WebView2缓存");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>
        /// M0-9~M0-11 独立弹窗 WebView2 缓存目录。
        /// WebView2 同一 userDataFolder 同时只能被一个存活环境占用，弹窗与小组件并行存在，必须隔离为独立目录，避免目录占用冲突。
        /// </summary>
        public static string PanelWebView2CacheDir
        {
            get
            {
                string dir = Path.Combine(RuntimeDir, "WebView2缓存-panel");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>手动节假日 JSON 路径（M0-7 打通读写；M0-8 扩充批次录入与管理）。</summary>
        public static string ManualHolidayPath
        {
            get { return Path.Combine(RuntimeDir, "manual-holidays.json"); }
        }

        /// <summary>提醒 JSON 路径（④ v0.1.6.23 提醒持久化；重启保留）。</summary>
        public static string ReminderPath
        {
            get { return Path.Combine(RuntimeDir, "reminder.json"); }
        }

        private static IniFileHelper Ini
        {
            get { return _ini ?? (_ini = new IniFileHelper(IniPath)); }
        }

        /// <summary>启动时读取 config.ini；文件缺失/键缺失用默认值。</summary>
        public static void LoadFromIni()
        {
            try
            {
                WidgetVisible = Ini.GetBool(Section, "widgetVisible", true);
                WidgetPosX = Ini.GetInt(Section, "widgetPosX", -1);
                WidgetPosY = Ini.GetInt(Section, "widgetPosY", -1);
                int w = Ini.GetInt(Section, "widgetWidth", 360);
                int h = Ini.GetInt(Section, "widgetHeight", 560);
                // 等比模型：以宽度反推系数并钳制上下限，高度按基准比例重算，保证宽高比恒定。
                double k = Math.Max(MinScale, Math.Min(MaxScale, (double)w / BaseWidgetWidth));
                WidgetWidth = (int)Math.Round(BaseWidgetWidth * k);
                WidgetHeight = (int)Math.Round(BaseWidgetHeight * k);
                LunarEnabled = Ini.GetBool(Section, "lunarEnabled", true);
                AutoStartEnabled = Ini.GetBool(Section, "autoStartEnabled", false);
                SyncEnabled = Ini.GetBool(Section, "syncEnabled", true);
                ClockRealTime = Ini.GetBool(Section, "clockRealTime", true);
                AutoShutdownAt = Ini.GetString(Section, "autoShutdownAt", "");
                AutoShutdownEnabled = Ini.GetBool(Section, "autoShutdownEnabled", false);
                AutoShutdownTime = Ini.GetString(Section, "autoShutdownTime", "");
                AutoShutdownRepeatType = Ini.GetString(Section, "autoShutdownRepeatType", "daily");
                AutoShutdownWeekdays = Ini.GetString(Section, "autoShutdownWeekdays", "");
                AutoShutdownMonthDay = Math.Max(1, Math.Min(31, Ini.GetInt(Section, "autoShutdownMonthDay", 1)));
                AutoShutdownAdvanceMinutes = Math.Max(0, Math.Min(59, Ini.GetInt(Section, "autoShutdownAdvanceMinutes", 15)));
                WidgetLocked = Ini.GetBool(Section, "widgetLocked", false);
                int op = Ini.GetInt(Section, "widgetOpacityPercent", 100);
                WidgetOpacityPercent = Math.Max(0, Math.Min(100, op));
                WidgetTheme = Ini.GetString(Section, "widgetTheme", "dark") == "light" ? "light" : "dark";
                string tex = Ini.GetString(Section, "widgetTexture", "plain");
                WidgetTexture = (tex == "glass" || tex == "metal") ? tex : "plain"; /* v0.1.6.27 增加金属 */
                int bak = Ini.GetInt(Section, "widgetOpacityBackup", -1); /* v0.1.6.28 金属前的透明度备份 */
                WidgetOpacityBackup = (bak >= 0 && bak <= 100) ? bak : -1;

                /* v0.1.6.30 按材质独立参数：纯色透明度缺键时用旧全局透明度迁移一次 */
                int plain = Ini.GetInt(Section, "widgetPlainOpacity", WidgetOpacityPercent);
                PlainOpacityPercent = Math.Max(0, Math.Min(100, plain));
                string fin = Ini.GetString(Section, "widgetGlassFinish", "clear");
                GlassFinish = (fin == "clear") ? "clear" : "frosted";
                int tint = Ini.GetInt(Section, "widgetGlassTint", 20);
                GlassTintPercent = Math.Max(0, Math.Min(100, tint));
                WidgetOpacityPercent = PlainOpacityPercent;   /* 旧字段镜像纯色值，供旧版前端/回退兼容 */

                /* v0.1.6.37「额外显示」两组内容 */
                FestivalEnabled = Ini.GetBool(Section, "festivalEnabled", true);
                FestivalColor = ClampColorKey(Ini.GetString(Section, "festivalColor", "gold"), "gold");
                FestivalStyle = Ini.GetString(Section, "festivalStyle", "outline") == "outline" ? "outline" : "solid";
                JieqiEnabled = Ini.GetBool(Section, "jieqiEnabled", true);
                JieqiColor = ClampColorKey(Ini.GetString(Section, "jieqiColor", "rose"), "rose");
                JieqiStyle = Ini.GetString(Section, "jieqiStyle", "outline") == "solid" ? "solid" : "outline";

                /* v0.1.6.42「假日样式」：公共节假日 / 周末 / 调休 */
                HolidayBgEnabled = Ini.GetBool(Section, "holidayBgEnabled", true);
                HolidayShape = Ini.GetString(Section, "holidayShape", "rect") == "rect" ? "rect" : "round";
                HolidayFill = Ini.GetString(Section, "holidayFill", "solid") == "outline" ? "outline" : "solid";
                HolidayColor = ClampColorKey(Ini.GetString(Section, "holidayColor", "jilan"), "jilan");
                HolidayNameMode = Ini.GetString(Section, "holidayNameMode", "follow") == "custom" ? "custom" : "follow";
                HolidayNameColor = ClampColorKey(Ini.GetString(Section, "holidayNameColor", "gray"), "gray");
                HolidayNameFill = Ini.GetString(Section, "holidayNameFill", "outline") == "outline" ? "outline" : "solid";

                WeekendBgEnabled = Ini.GetBool(Section, "weekendBgEnabled", true);
                WeekendMode = Ini.GetString(Section, "weekendMode", "alone") == "follow" ? "follow" : "alone";
                WeekendShape = Ini.GetString(Section, "weekendShape", "round") == "rect" ? "rect" : "round";
                WeekendFill = Ini.GetString(Section, "weekendFill", "solid") == "outline" ? "outline" : "solid";
                WeekendColor = ClampColorKey(Ini.GetString(Section, "weekendColor", "green"), "green");

                AdjustBgEnabled = Ini.GetBool(Section, "adjustBgEnabled", true);
                AdjustShape = Ini.GetString(Section, "adjustShape", "rect") == "rect" ? "rect" : "round";
                AdjustFill = Ini.GetString(Section, "adjustFill", "outline") == "outline" ? "outline" : "solid";
                AdjustColor = ClampColorKey(Ini.GetString(Section, "adjustColor", "vermilion"), "vermilion");

            /* v0.1.6.63「提醒标记色」 */
            ReminderDotColor = ClampColorKey(Ini.GetString(Section, "reminderDotColor", "iris"), "iris");

                /* v0.1.6.38 在线数据源 4 槽（缺键用出厂值） */
                for (int i = 0; i < 4; i++)
                {
                    SyncSourceUrl[i] = Ini.GetString(Section, "syncSourceUrl" + (i + 1), DefaultSyncSourceUrl[i]);
                    SyncSourceEnabled[i] = Ini.GetBool(Section, "syncSourceEnabled" + (i + 1), DefaultSyncSourceEnabled[i]);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[CONFIG] LoadFromIni 失败，使用默认值：" + ex.Message);
            }
        }

        /// <summary>颜色键白名单校验：只允许 jilan/teal/green/gold/orange/rose/vermilion/gray/neutral（blue 作为旧键别名映射到 jilan）。</summary>
        public static string ClampColorKey(string key, string fallback)
        {
            switch (key)
            {
                case "blue":                /* v0.1.6.42：旧键「节日蓝」改名霁蓝 */
                    return "jilan";
                case "jilan":
                case "teal":
                case "green":               /* v0.1.6.46：补回旧周末绿 #3FA942（原色板漏项，用户找不到绿色） */
                case "gold":
                case "orange":
                case "rose":
                case "vermilion":           /* v0.1.6.42：调休红（原五类配色里的补班红 #E0524D） */
                case "gray":
                case "neutral":
                    return key;
                case "iris":                /* v0.1.6.63：新增紫晶 #8B7FF5，作「提醒标记色」默认值 */
                    return key;
                default:
                    return fallback;
            }
        }

        /// <summary>把当前配置写回 config.ini。</summary>
        public static void Save()
        {
            try
            {
                Ini.WriteBool(Section, "widgetVisible", WidgetVisible);
                Ini.WriteInt(Section, "widgetPosX", WidgetPosX);
                Ini.WriteInt(Section, "widgetPosY", WidgetPosY);
                Ini.WriteInt(Section, "widgetWidth", WidgetWidth);
                Ini.WriteInt(Section, "widgetHeight", WidgetHeight);
                Ini.WriteBool(Section, "lunarEnabled", LunarEnabled);
                Ini.WriteBool(Section, "autoStartEnabled", AutoStartEnabled);
                Ini.WriteBool(Section, "syncEnabled", SyncEnabled);
                Ini.WriteBool(Section, "clockRealTime", ClockRealTime);
                Ini.WriteString(Section, "autoShutdownAt", AutoShutdownAt);
                Ini.WriteBool(Section, "autoShutdownEnabled", AutoShutdownEnabled);
                Ini.WriteString(Section, "autoShutdownTime", AutoShutdownTime);
                Ini.WriteString(Section, "autoShutdownRepeatType", AutoShutdownRepeatType);
                Ini.WriteString(Section, "autoShutdownWeekdays", AutoShutdownWeekdays);
                Ini.WriteInt(Section, "autoShutdownMonthDay", AutoShutdownMonthDay);
                Ini.WriteInt(Section, "autoShutdownAdvanceMinutes", AutoShutdownAdvanceMinutes);
                Ini.WriteBool(Section, "widgetLocked", WidgetLocked);
                Ini.WriteInt(Section, "widgetOpacityPercent", PlainOpacityPercent);   /* 旧键：写纯色值，旧版本回退时行为一致 */
                Ini.WriteString(Section, "widgetTheme", WidgetTheme);
                Ini.WriteString(Section, "widgetTexture", WidgetTexture);
                Ini.WriteInt(Section, "widgetOpacityBackup", WidgetOpacityBackup); /* v0.1.6.28 */
                Ini.WriteInt(Section, "widgetPlainOpacity", PlainOpacityPercent);      /* v0.1.6.30 */
                Ini.WriteString(Section, "widgetGlassFinish", GlassFinish);            /* v0.1.6.30 */
                Ini.WriteInt(Section, "widgetGlassTint", GlassTintPercent);            /* v0.1.6.30 */
                Ini.WriteBool(Section, "festivalEnabled", FestivalEnabled);            /* v0.1.6.37 */
                Ini.WriteString(Section, "festivalColor", FestivalColor);
                Ini.WriteString(Section, "festivalStyle", FestivalStyle);
                Ini.WriteBool(Section, "jieqiEnabled", JieqiEnabled);
                Ini.WriteString(Section, "jieqiColor", JieqiColor);
                Ini.WriteString(Section, "jieqiStyle", JieqiStyle);
                /* v0.1.6.42 假日样式 */
                Ini.WriteBool(Section, "holidayBgEnabled", HolidayBgEnabled);
                Ini.WriteString(Section, "holidayShape", HolidayShape);
                Ini.WriteString(Section, "holidayFill", HolidayFill);
                Ini.WriteString(Section, "holidayColor", HolidayColor);
                Ini.WriteString(Section, "holidayNameMode", HolidayNameMode);
                Ini.WriteString(Section, "holidayNameColor", HolidayNameColor);
                Ini.WriteString(Section, "holidayNameFill", HolidayNameFill);
                Ini.WriteBool(Section, "weekendBgEnabled", WeekendBgEnabled);
                Ini.WriteString(Section, "weekendMode", WeekendMode);
                Ini.WriteString(Section, "weekendShape", WeekendShape);
                Ini.WriteString(Section, "weekendFill", WeekendFill);
                Ini.WriteString(Section, "weekendColor", WeekendColor);
                Ini.WriteBool(Section, "adjustBgEnabled", AdjustBgEnabled);
                Ini.WriteString(Section, "adjustShape", AdjustShape);
                Ini.WriteString(Section, "adjustFill", AdjustFill);
                Ini.WriteString(Section, "adjustColor", AdjustColor);
            Ini.WriteString(Section, "reminderDotColor", ReminderDotColor);
                /* v0.1.6.38 */
                for (int i = 0; i < 4; i++)
                {
                    Ini.WriteString(Section, "syncSourceUrl" + (i + 1), SyncSourceUrl[i]);
                    Ini.WriteBool(Section, "syncSourceEnabled" + (i + 1), SyncSourceEnabled[i]);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[CONFIG] Save 失败：" + ex.Message);
            }
        }
    }
}

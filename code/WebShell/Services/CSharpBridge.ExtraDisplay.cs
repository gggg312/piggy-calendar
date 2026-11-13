using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：v0.1.6.37「额外显示」——节日与纪念日 / 二十四节气。
    /// 两组内容各自独立：开关（是否占位）+ 颜色键（blue/teal/gold/orange/rose）+ 形态（solid/outline）。
    /// 只影响月历日期下方的副标题，不动休/班角标与放假底色。
    /// </summary>
    public partial class CSharpBridge
    {
        private Action _applyExtraDisplay;

        /// <summary>绑定配置变更回调（App 初始化后绑定到 CalendarPopupWindow 的重绘/刷新）。</summary>
        public void BindExtraDisplay(Action applyExtraDisplay)
        {
            _applyExtraDisplay = applyExtraDisplay;
        }

        /// <summary>读取「额外显示」配置。</summary>
        public string GetExtraDisplay()
        {
            return Json(new Dictionary<string, object>
            {
                ["festivalEnabled"] = AppConfig.FestivalEnabled,
                ["festivalColor"] = AppConfig.FestivalColor,
                ["festivalStyle"] = AppConfig.FestivalStyle,
                ["jieqiEnabled"] = AppConfig.JieqiEnabled,
                ["jieqiColor"] = AppConfig.JieqiColor,
                ["jieqiStyle"] = AppConfig.JieqiStyle
            });
        }

        /// <summary>
        /// 更新「额外显示」配置（可只传部分键）：
        /// festivalEnabled(bool) / festivalColor(key) / festivalStyle(solid|outline)
        /// / jieqiEnabled(bool) / jieqiColor(key) / jieqiStyle(solid|outline)
        /// </summary>
        public string SetExtraDisplay(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空配置";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:配置解析失败";

                if (d.ContainsKey("festivalEnabled"))
                    AppConfig.FestivalEnabled = GetBool(d, "festivalEnabled", AppConfig.FestivalEnabled);
                if (d.ContainsKey("festivalColor"))
                    AppConfig.FestivalColor = AppConfig.ClampColorKey(Pick(d, "festivalColor", AppConfig.FestivalColor), "blue");
                if (d.ContainsKey("festivalStyle"))
                    AppConfig.FestivalStyle = Pick(d, "festivalStyle", AppConfig.FestivalStyle) == "outline" ? "outline" : "solid";

                if (d.ContainsKey("jieqiEnabled"))
                    AppConfig.JieqiEnabled = GetBool(d, "jieqiEnabled", AppConfig.JieqiEnabled);
                if (d.ContainsKey("jieqiColor"))
                    AppConfig.JieqiColor = AppConfig.ClampColorKey(Pick(d, "jieqiColor", AppConfig.JieqiColor), "teal");
                if (d.ContainsKey("jieqiStyle"))
                    AppConfig.JieqiStyle = Pick(d, "jieqiStyle", AppConfig.JieqiStyle) == "solid" ? "solid" : "outline";

                AppConfig.Save();

                PostEvent("{\"kind\":\"config-changed\",\"payload\":{"
                    + "\"festivalEnabled\":" + (AppConfig.FestivalEnabled ? "true" : "false")
                    + ",\"festivalColor\":\"" + AppConfig.FestivalColor + "\""
                    + ",\"festivalStyle\":\"" + AppConfig.FestivalStyle + "\""
                    + ",\"jieqiEnabled\":" + (AppConfig.JieqiEnabled ? "true" : "false")
                    + ",\"jieqiColor\":\"" + AppConfig.JieqiColor + "\""
                    + ",\"jieqiStyle\":\"" + AppConfig.JieqiStyle + "\"}}");
                _applyExtraDisplay?.Invoke();
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        private static string Pick(Dictionary<string, object> d, string key, string fallback)
        {
            return d[key] != null ? d[key].ToString() : fallback;
        }

        // ================= v0.1.6.42「假日样式」：公共节假日 / 周末 / 调休 =================

        /// <summary>读取「假日样式」配置（日期背景形状/填充/颜色 + 节日名称颜色/形态）。</summary>
        public string GetHolidayStyle()
        {
            return Json(new Dictionary<string, object>
            {
                ["holidayBgEnabled"] = AppConfig.HolidayBgEnabled,
                ["holidayShape"] = AppConfig.HolidayShape,
                ["holidayFill"] = AppConfig.HolidayFill,
                ["holidayColor"] = AppConfig.HolidayColor,
                ["holidayNameMode"] = AppConfig.HolidayNameMode,
                ["holidayNameColor"] = AppConfig.HolidayNameColor,
                ["holidayNameFill"] = AppConfig.HolidayNameFill,
                ["weekendBgEnabled"] = AppConfig.WeekendBgEnabled,
                ["weekendMode"] = AppConfig.WeekendMode,
                ["weekendShape"] = AppConfig.WeekendShape,
                ["weekendFill"] = AppConfig.WeekendFill,
                ["weekendColor"] = AppConfig.WeekendColor,
                ["adjustBgEnabled"] = AppConfig.AdjustBgEnabled,
                ["adjustShape"] = AppConfig.AdjustShape,
                ["adjustFill"] = AppConfig.AdjustFill,
                ["adjustColor"] = AppConfig.AdjustColor,
                ["reminderDotColor"] = AppConfig.ReminderDotColor   /* v0.1.6.63 提醒标记色 */
            });
        }

        /// <summary>
        /// 更新「假日样式」配置（可只传部分键）：
        /// holidayBgEnabled/holidayShape/holidayFill/holidayColor/holidayNameMode/holidayNameColor/holidayNameFill
        /// / weekendBgEnabled/weekendMode/weekendShape/weekendFill/weekendColor
        /// / adjustBgEnabled/adjustShape/adjustFill/adjustColor / reminderDotColor
        /// 成功后广播 config-changed（kind=holiday-style）并触发小组件重绘。
        /// </summary>
        public string SetHolidayStyle(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空配置";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:配置解析失败";

                if (d.ContainsKey("holidayBgEnabled"))
                    AppConfig.HolidayBgEnabled = GetBool(d, "holidayBgEnabled", AppConfig.HolidayBgEnabled);
                if (d.ContainsKey("holidayShape"))
                    AppConfig.HolidayShape = Pick(d, "holidayShape", AppConfig.HolidayShape) == "rect" ? "rect" : "round";
                if (d.ContainsKey("holidayFill"))
                    AppConfig.HolidayFill = Pick(d, "holidayFill", AppConfig.HolidayFill) == "outline" ? "outline" : "solid";
                if (d.ContainsKey("holidayColor"))
                    AppConfig.HolidayColor = AppConfig.ClampColorKey(Pick(d, "holidayColor", AppConfig.HolidayColor), "jilan");
                if (d.ContainsKey("holidayNameMode"))
                    AppConfig.HolidayNameMode = Pick(d, "holidayNameMode", AppConfig.HolidayNameMode) == "custom" ? "custom" : "follow";
                if (d.ContainsKey("holidayNameColor"))
                    AppConfig.HolidayNameColor = AppConfig.ClampColorKey(Pick(d, "holidayNameColor", AppConfig.HolidayNameColor), "jilan");
                if (d.ContainsKey("holidayNameFill"))
                    AppConfig.HolidayNameFill = Pick(d, "holidayNameFill", AppConfig.HolidayNameFill) == "outline" ? "outline" : "solid";

                if (d.ContainsKey("weekendBgEnabled"))
                    AppConfig.WeekendBgEnabled = GetBool(d, "weekendBgEnabled", AppConfig.WeekendBgEnabled);
                if (d.ContainsKey("weekendMode"))
                    AppConfig.WeekendMode = Pick(d, "weekendMode", AppConfig.WeekendMode) == "follow" ? "follow" : "alone";
                if (d.ContainsKey("weekendShape"))
                    AppConfig.WeekendShape = Pick(d, "weekendShape", AppConfig.WeekendShape) == "rect" ? "rect" : "round";
                if (d.ContainsKey("weekendFill"))
                    AppConfig.WeekendFill = Pick(d, "weekendFill", AppConfig.WeekendFill) == "outline" ? "outline" : "solid";
                if (d.ContainsKey("weekendColor"))
                    AppConfig.WeekendColor = AppConfig.ClampColorKey(Pick(d, "weekendColor", AppConfig.WeekendColor), "teal");

                if (d.ContainsKey("adjustBgEnabled"))
                    AppConfig.AdjustBgEnabled = GetBool(d, "adjustBgEnabled", AppConfig.AdjustBgEnabled);
                if (d.ContainsKey("adjustShape"))
                    AppConfig.AdjustShape = Pick(d, "adjustShape", AppConfig.AdjustShape) == "rect" ? "rect" : "round";
                if (d.ContainsKey("adjustFill"))
                    AppConfig.AdjustFill = Pick(d, "adjustFill", AppConfig.AdjustFill) == "outline" ? "outline" : "solid";
                if (d.ContainsKey("adjustColor"))
                    AppConfig.AdjustColor = AppConfig.ClampColorKey(Pick(d, "adjustColor", AppConfig.AdjustColor), "vermilion");

                /* v0.1.6.63「提醒标记色」：日期紫点 + 图标角标共用；非法键回落到紫晶 */
                if (d.ContainsKey("reminderDotColor"))
                    AppConfig.ReminderDotColor = AppConfig.ClampColorKey(Pick(d, "reminderDotColor", AppConfig.ReminderDotColor), "iris");

                AppConfig.Save();

                PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"holidayStyle\":true}}");
                _applyExtraDisplay?.Invoke();
                return "ok";
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }
    }
}

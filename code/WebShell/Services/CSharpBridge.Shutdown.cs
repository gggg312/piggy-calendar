using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：② 自动关机调度与执行桥（v0.1.6.0 周期版）。
    /// SetAutoShutdown 入参 {enabled, time:'HH:mm', repeatType:'daily'|'weekly'|'monthly', weekdays:'1,3,5', monthDay:N, advanceMinutes:N}。
    /// </summary>
    public partial class CSharpBridge
    {
        /// <summary>设置自动关机（周期配置）。返回 "ok" 或 "err:..."。</summary>
        public string SetAutoShutdown(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空参数";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:参数解析失败";

                bool enabled = GetBool(d, "enabled", AppConfig.AutoShutdownEnabled);
                string time = GetStr(d, "time", AppConfig.AutoShutdownTime);
                string repeatType = GetStr(d, "repeatType", AppConfig.AutoShutdownRepeatType);
                string weekdays = GetStr(d, "weekdays", AppConfig.AutoShutdownWeekdays);
                int monthDay = GetInt(d, "monthDay", AppConfig.AutoShutdownMonthDay);
                int advanceMinutes = GetInt(d, "advanceMinutes", AppConfig.AutoShutdownAdvanceMinutes);
                advanceMinutes = Math.Max(0, Math.Min(59, advanceMinutes));

                string r = AutoShutdownService.Instance.Configure(enabled, time, repeatType, weekdays, monthDay, advanceMinutes);
                // v0.1.6.1：配置成功 → 广播到小组件刷新自动关机按钮启动状态
                if (r == "ok") PostEvent("{\"kind\":\"auto-shutdown-changed\",\"payload\":{\"enabled\":" + (enabled ? "true" : "false") + "}}");
                return r;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>取消自动关机。返回 "ok"。</summary>
        public string CancelAutoShutdown()
        {
            try {
                string r = AutoShutdownService.Instance.Cancel();
                // v0.1.6.1：取消成功 → 广播到小组件刷新自动关机按钮启动状态
                if (r == "ok") PostEvent("{\"kind\":\"auto-shutdown-changed\",\"payload\":{\"enabled\":false}}");
                return r;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>当前自动关机调度状态 JSON：{enabled,time,repeatType,weekdays,monthDay,advanceMinutes,target,remainingSeconds}。</summary>
        public string GetAutoShutdownStatus()
        {
            try { return AutoShutdownService.Instance.GetStatus(); }
            catch (Exception ex) { return "err:" + ex.Message; }
        }
    }
}

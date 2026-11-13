using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：④ 提醒调度与持久化桥（v0.1.6.23）。
    /// AddReminder 入参 {mode:'countdown'|'datetime', text, sound, repeatType:'once'|'daily'|'workday'|'weekly'|'monthly',
    ///   countdownMinutes, date:'yyyy-MM-dd', hour:'HH', minute:'mm', weekdays:'1,3,5', monthDay}。
    /// 校验：倒计时最少 1 分钟；日期+时间不能早于当前。
    /// </summary>
    public partial class CSharpBridge
    {
        private readonly JavaScriptSerializer _reminderSer = new JavaScriptSerializer();

        /// <summary>打开提醒设置窗口（独立窗口，小组件底部提醒按钮触发）。</summary>
        public void OpenReminder() => _openReminder?.Invoke();

        /// <summary>v0.1.6.60：日历日期右键「添加提醒」——打开提醒窗并按该日期预填（yyyy-MM-dd）。</summary>
        public string OpenReminderAt(string ymd)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ymd)) { _openReminder?.Invoke(); return "ok"; }
                _openReminderAt?.Invoke(ymd.Trim());
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>
        /// v0.1.6.63：日历日期右键「管理提醒」——打开提醒窗并把「已添加提醒」列表过滤到该日。
        /// ids 为该日会触发的提醒 id（逗号分隔），由小组件按「当日提醒」同一口径算好传入；
        /// 面板侧不再重算（面板没有节假日/调休数据，重算会造成两处口径不一致）。
        /// </summary>
        public string OpenReminderManage(string ymd, string ids)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ymd)) { _openReminder?.Invoke(); return "ok"; }
                _openReminderManage?.Invoke(ymd.Trim(), (ids ?? "").Trim());
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>关闭提醒面板窗（前端保存成功后调用）。</summary>
        public void CloseReminderPanel() { try { _closeReminder?.Invoke(); } catch { } }

        /// <summary>添加提醒。返回 "ok" 或 "err:..."；成功后广播 reminder-changed。</summary>
        public string AddReminder(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空参数";
                var d = _reminderSer.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:参数解析失败";
                string r = ReminderService.Instance.Add(d);
                if (r == "ok") BroadcastReminderChanged();
                return r;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>修改提醒。返回 "ok" 或 "err:..."；成功后广播 reminder-changed。</summary>
        public string UpdateReminder(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空参数";
                var d = _reminderSer.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return "err:参数解析失败";
                string r = ReminderService.Instance.Update(d);
                if (r == "ok") BroadcastReminderChanged();
                return r;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>删除提醒。返回 "ok" 或 "err:..."；成功后广播 reminder-changed。</summary>
        public string RemoveReminder(string id)
        {
            try
            {
                string r = ReminderService.Instance.Remove(id);
                if (r == "ok") BroadcastReminderChanged();
                return r;
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>全部提醒列表 JSON（每项含 triggerLabel / hasToday）。</summary>
        public string ListReminders()
        {
            try { return ReminderService.Instance.List(); }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>当天是否有待触发提醒 → {hasToday}，供小组件入口选中态。</summary>
        public string GetReminderHasToday()
        {
            try { return ReminderService.Instance.GetHasToday(); }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>到点弹窗「稍后 5 分钟」（once 与周期均生效）。</summary>
        public void PostponeReminder(string id)
        {
            try { ReminderService.Instance.Postpone(id); } catch { }
        }

        /// <summary>到点弹窗「我已知晓」（once 移除；周期保留当日标记）。</summary>
        public void AcknowledgeReminder(string id)
        {
            try { ReminderService.Instance.Acknowledge(id); BroadcastReminderChanged(); } catch { }
        }

        private void BroadcastReminderChanged()
        {
            // GetHasToday() 返回 {"hasToday":true/false}；payload 直接内插该 JSON，前端 p.hasToday 可用。
            PostEvent("{\"kind\":\"reminder-changed\",\"payload\":" + ReminderService.Instance.GetHasToday() + "}");
        }
    }
}

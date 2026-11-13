using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 自动关机调度服务（②，v0.1.6.0 周期版）：支持开启开关 + 时刻 + 重复周期（每天/每周几/每月号）+ 关机前提前提醒。
    /// 配置持久化于 config.ini（AppConfig.AutoShutdown*）；重启后未到点则恢复调度。
    /// 到点执行 shutdown /s /t 30（Windows 30 秒倒计时缓冲，期间用户可 shutdown /a 取消）。
    /// </summary>
    internal class AutoShutdownService
    {
        private static readonly AutoShutdownService _instance = new AutoShutdownService();
        public static AutoShutdownService Instance { get { return _instance; } }

        private readonly Timer _timer;
        private DateTime? _target;
        private DateTime? _advanceFiredAt;   // 已触发提前提醒的目标时刻，避免每秒重复提醒
        private DateTime? _skipTarget;       // 用户「取消本次」跳过的目标时刻（v0.1.6.10，方案A：仅跳过本次，保留周期开关）

        /// <summary>关机前提前提醒回调（v0.1.6.10 起改弹窗：传入目标时刻供倒计时）。</summary>
        public Action<DateTime> OnAdvanceNotify;

        /// <summary>当前调度目标时刻（供提醒弹窗实时跟随；null=无任务）。v0.1.6.17</summary>
        public DateTime? CurrentTarget { get { return _target; } }

        private AutoShutdownService()
        {
            _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>启动时恢复调度：读 AppConfig.AutoShutdown*；开关未开或时刻无效则不调度。</summary>
        public void RestoreFromConfig()
        {
            if (!AppConfig.AutoShutdownEnabled || string.IsNullOrWhiteSpace(AppConfig.AutoShutdownTime))
            {
                _target = null;
                _advanceFiredAt = null;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                AppLog.Write("[SHUTDOWN] 恢复调度：未启用或无时刻（enabled=" + (AppConfig.AutoShutdownEnabled ? "1" : "0")
                    + " time='" + AppConfig.AutoShutdownTime + "'），不排程");
                return;
            }
            _advanceFiredAt = null;
            ComputeAndSchedule(DateTime.Now);
            AppLog.Write("[SHUTDOWN] 恢复调度：周期=" + AppConfig.AutoShutdownRepeatType
                + " 时刻=" + AppConfig.AutoShutdownTime
                + " 提前=" + AppConfig.AutoShutdownAdvanceMinutes + "分"
                + " 目标=" + TargetText() + "，定时器已启动");
        }

        /// <summary>设置自动关机（周期配置）。返回 "ok" 或 "err:..."。</summary>
        public string Configure(bool enabled, string time, string repeatType, string weekdays, int monthDay, int advanceMinutes)
        {
            // 记录旧配置，用于判定「配置是否真变更」（v0.1.6.17 加固：前端循环保存同一配置不得重算目标）
            bool oldEnabled = AppConfig.AutoShutdownEnabled;
            string oldTime = AppConfig.AutoShutdownTime;
            string oldRepeat = AppConfig.AutoShutdownRepeatType;
            string oldWeekdays = AppConfig.AutoShutdownWeekdays;
            int oldMonthDay = AppConfig.AutoShutdownMonthDay;
            int oldAdvance = AppConfig.AutoShutdownAdvanceMinutes;

            AppConfig.AutoShutdownEnabled = enabled;
            AppConfig.AutoShutdownTime = time ?? "";
            AppConfig.AutoShutdownRepeatType = repeatType ?? "daily";
            AppConfig.AutoShutdownWeekdays = weekdays ?? "";
            AppConfig.AutoShutdownMonthDay = monthDay;
            AppConfig.AutoShutdownAdvanceMinutes = advanceMinutes;
            AppConfig.Save();

            if (!enabled || string.IsNullOrWhiteSpace(time))
            {
                _target = null;
                _advanceFiredAt = null;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                AppLog.Write("[SHUTDOWN] 自动关机已关闭");
                return "ok";
            }

            bool configChanged = enabled != oldEnabled
                || !string.Equals(time, oldTime, StringComparison.Ordinal)
                || !string.Equals(repeatType, oldRepeat, StringComparison.Ordinal)
                || !string.Equals(weekdays, oldWeekdays, StringComparison.Ordinal)
                || monthDay != oldMonthDay
                || advanceMinutes != oldAdvance;

            if (configChanged)
            {
                // 配置真变更 → 视为新设定：重算目标、作废旧跳过标记、重置提醒触发，按新目标重新走完整流程
                ComputeAndSchedule(DateTime.Now);
                _skipTarget = null;
                _advanceFiredAt = null;
            }
            // else：配置未变（前端每秒循环保存同一配置）→ 保留 _target/_skipTarget/_advanceFiredAt，
            // 不重算目标，避免整点被推到下一周期导致到点不执行（v0.1.6.17 根因加固）。
            AppLog.Write("[SHUTDOWN] 自动关机已调度 " + AppConfig.AutoShutdownRepeatType + " " + time
                + "（配置" + (configChanged ? "有变更，已重算" : "未变更，保留原排程") + "）目标=" + TargetText()
                + " 提前=" + advanceMinutes + "分");
            return "ok";
        }

        private string TargetText()
        {
            return _target.HasValue ? _target.Value.ToString("yyyy-MM-dd HH:mm") : "无";
        }

        /// <summary>取消自动关机（关闭开关并清空调度）。返回 "ok"。</summary>
        public string Cancel()
        {
            AppConfig.AutoShutdownEnabled = false;
            AppConfig.Save();
            _target = null;
            _advanceFiredAt = null;
            _skipTarget = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            AppLog.Write("[SHUTDOWN] 自动关机已取消");
            return "ok";
        }

        /// <summary>跳过本次自动关机（v0.1.6.10，方案A）：本次到点不关机，开关保持开启，下一周期照常排程。</summary>
        public string SkipOnce()
        {
            if (_target.HasValue)
            {
                _skipTarget = _target;
                AppLog.Write("[SHUTDOWN] 用户跳过本次关机 " + _target.Value.ToString("yyyy-MM-dd HH:mm") + "，下个周期照常");
            }
            return "ok";
        }

        /// <summary>当前自动关机调度状态 JSON：{enabled,time,repeatType,weekdays,monthDay,advanceMinutes,target,remainingSeconds}。</summary>
        public string GetStatus()
        {
            var d = new Dictionary<string, object>
            {
                ["enabled"] = AppConfig.AutoShutdownEnabled,
                ["time"] = AppConfig.AutoShutdownTime,
                ["repeatType"] = AppConfig.AutoShutdownRepeatType,
                ["weekdays"] = AppConfig.AutoShutdownWeekdays,
                ["monthDay"] = AppConfig.AutoShutdownMonthDay,
                ["advanceMinutes"] = AppConfig.AutoShutdownAdvanceMinutes,
                ["target"] = _target.HasValue ? _target.Value.ToString("yyyy-MM-dd HH:mm") : "",
                ["remainingSeconds"] = _target.HasValue ? (int)Math.Max(0, (_target.Value - DateTime.Now).TotalSeconds) : 0
            };
            return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(d);
        }

        private void ComputeAndSchedule(DateTime now)
        {
            var t = ComputeNextTarget(now);
            if (t == null) { _target = null; _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
            _target = t;
            _timer.Change(0, 1000);
        }

        private DateTime? ComputeNextTarget(DateTime now)
        {
            if (!TimeSpan.TryParse(AppConfig.AutoShutdownTime, out TimeSpan ts)) return null;
            string rt = AppConfig.AutoShutdownRepeatType;

            if (rt == "weekly")
            {
                var set = ParseWeekdays(AppConfig.AutoShutdownWeekdays);
                if (set.Count == 0) return null;
                for (int off = 0; off < 8; off++)
                {
                    var dt = now.Date.AddDays(off).Add(ts);
                    int wd = ((int)dt.DayOfWeek + 6) % 7 + 1; // 周一=1..周日=7
                    if (set.Contains(wd) && dt > now) return dt;
                }
                return null;
            }

            if (rt == "monthly")
            {
                int day = Math.Max(1, Math.Min(28, AppConfig.AutoShutdownMonthDay));
                int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                int targetDay = Math.Min(day, daysInMonth);
                DateTime candidate;
                if (now.Day < targetDay)
                    candidate = new DateTime(now.Year, now.Month, targetDay).Add(ts);
                else
                    candidate = new DateTime(now.Year, now.Month, targetDay).AddMonths(1).Add(ts);
                return candidate > now ? candidate : (DateTime?)null;
            }

            // daily
            var d0 = now.Date.Add(ts);
            if (d0 <= now) d0 = d0.AddDays(1);
            return d0;
        }

        private static HashSet<int> ParseWeekdays(string s)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(s)) return set;
            foreach (var p in s.Split(','))
            {
                if (int.TryParse(p.Trim(), out int v) && v >= 1 && v <= 7) set.Add(v);
            }
            return set;
        }

        private void Check()
        {
            try
            {
                if (!_target.HasValue) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
                var now = DateTime.Now;

                // 提前提醒：距目标 advanceMinutes 时触发一次（v0.1.6.10 改弹窗，传目标时刻供倒计时）
                if (_advanceFiredAt != _target.Value)
                {
                    double secs = (_target.Value - now).TotalSeconds;
                    if (secs > 0 && secs <= AppConfig.AutoShutdownAdvanceMinutes * 60)
                    {
                        _advanceFiredAt = _target.Value;
                        AppLog.Write("[SHUTDOWN] 提前提醒已触发 目标=" + TargetText() + " 剩余=" + (int)secs + "秒");
                        try
                        {
                            OnAdvanceNotify?.Invoke(_target.Value);
                        }
                        catch { }
                    }
                }

                if (now >= _target.Value)
                {
                    var t = _target.Value;
                    _target = null;
                    _advanceFiredAt = null;
                    if (_skipTarget == t)
                    {
                        // 方案A：用户已「取消本次」——不关机，保留开关，排下一周期
                        _skipTarget = null;
                        AppLog.Write("[SHUTDOWN] 已跳过本次关机 " + t.ToString("yyyy-MM-dd HH:mm"));
                        if (AppConfig.AutoShutdownEnabled)
                            ComputeAndSchedule(DateTime.Now);
                        else
                            _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    else if (AppConfig.AutoShutdownEnabled)
                    {
                        ComputeAndSchedule(DateTime.Now);
                        AppLog.Write("[SHUTDOWN] 到达目标 " + t.ToString("yyyy-MM-dd HH:mm") + "，执行关机；下个周期=" + TargetText());
                        ExecuteShutdown();
                    }
                    else
                    {
                        AppLog.Write("[SHUTDOWN] 到达目标 " + t.ToString("yyyy-MM-dd HH:mm") + "，但开关已关闭，不执行关机");
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
            }
            catch (Exception ex) { AppLog.Write("[SHUTDOWN] 调度检查异常：" + ex.Message); }
        }

        private void ExecuteShutdown()
        {
            try
            {
                // /t 30：30 秒倒计时缓冲，期间可 shutdown /a 取消
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 30") { CreateNoWindow = true, UseShellExecute = false });
                AppLog.Write("[SHUTDOWN] 已调用 shutdown /s /t 30 发起关机（30 秒缓冲，期间 shutdown /a 可取消）");
                // v0.1.6.32 审计：45 秒后本进程仍活着 → 关机没有真正发生（被取消 / 被系统策略或权限拒绝）
                var th = new Thread(() =>
                {
                    try
                    {
                        Thread.Sleep(45000);
                        AppLog.Write("[SHUTDOWN] 45 秒后进程仍在运行：关机未生效（可能被 shutdown /a 取消，或被系统策略/权限拒绝）");
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
            }
            catch (Exception ex) { AppLog.Write("[SHUTDOWN] 执行关机失败：" + ex.Message); }
        }
    }
}

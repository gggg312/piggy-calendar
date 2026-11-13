using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 提醒调度服务（④，v0.1.6.23）：独立于小组件窗口的后台每秒调度（窗口关闭/最小化不影响到点弹窗）。
    /// 数据持久化：运行组件\reminder.json（数组 [{id,mode,text,sound,repeatType,countdownMinutes,date,hour,minute,weekdays,monthDay,monthlyMode,workdayIndex,createdAt}]）。
    /// mode：countdown=倒计时（自 created 起 N 分钟后一次性）；datetime=指定日期+时刻（可配重复）。
    /// repeatType：once/daily/workday/weekly/monthly。工作日跟随项目节假日体系（manual.batches 手动录入 + remote.years[yyyy].batches 在线同步；off→非工作日、adjust→工作日；缺该年数据周一~五兜底）。
/// monthly 三种口径（v0.1.6.66）：monthlyMode=day（每月 N 号，缺省）/ workdayNth（第 N 个工作日）/ workdayLastNth（倒数第 N 个工作日），后两者用 workdayIndex(1~10)。
    /// 触发后：once 移除（我已知晓）；周期保留（当日 lastFiredKey 防重复）。到点弹窗可「稍后 5 分钟」（once 与周期均生效）。
    /// </summary>
    internal class ReminderService
    {
        private static readonly ReminderService _instance = new ReminderService();
        public static ReminderService Instance { get { return _instance; } }

        private readonly Timer _timer;
        private readonly object _lock = new object();
        private readonly List<ReminderItem> _items = new List<ReminderItem>();
        private readonly JavaScriptSerializer _ser = new JavaScriptSerializer();
        /* v0.1.6.66：工作日判定缓存——节假日 json 每次读盘代价太高，而 NextFireKey / NextFireText 会逐日扫 370 天 */
        private readonly Dictionary<string, bool> _wdCache = new Dictionary<string, bool>();
        private DateTime _holidayMtime = DateTime.MinValue;
        private DateTime _holidayCheckedAt = DateTime.MinValue;
        /* v0.1.6.98：reminder.json 载入彻底失败标记；为 true 时禁止写盘，避免空集合覆盖用户原数据 */
        private bool _loadFailed;

        /// <summary>数据/选中态变更回调（App 绑定为广播 reminder-changed 到小组件，刷新入口选中态）。</summary>
        public Action Changed;

        /// <summary>到点触发回调（id,text,sound）；App 据此开 ReminderRemindWindow（小组件所在屏居中）。</summary>
        public Action<string, string, bool> OnFire;

        private ReminderService()
        {
            _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>启动：读 reminder.json 恢复列表（清运行态），开始每秒调度。</summary>
        public void Start()
        {
            lock (_lock)
            {
                _items.Clear();
                LoadFromDisk();
                foreach (var r in _items) { r.firedOnce = false; r.lastFiredKey = null; r.lastFiredAt = null; r.postponeUntil = null; }
                _timer.Change(0, 1000);
            }
            AppLog.Write("[REMINDER] 提醒服务启动，已恢复 " + _items.Count + " 条");
        }

        private void LoadFromDisk()
        {
            try
            {
                string path = AppConfig.ReminderPath;
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                /* v0.1.6.98：逐条容错。原先整表反序列化，只要有一条脏数据（典型如 createdAt 时区串异常）
                   就会整体抛异常 → 内存 0 条 → 之后任何保存动作都用空集合覆盖磁盘，用户全部提醒永久丢失。
                   改为先按对象数组解析，再逐条转换：坏条目跳过并计数，好条目照常恢复。 */
                List<object> rawList;
                try
                {
                    rawList = _ser.Deserialize<List<object>>(text);
                }
                catch (Exception ex)
                {
                    /* 连数组结构都解析不了：保留原文件、禁止写盘，避免后续保存把原数据冲掉 */
                    _loadFailed = true;
                    AppLog.Write("[REMINDER] 整表解析失败，本次禁止写盘以保护原数据：" + ex.Message);
                    TryKeepBadCopy(path);
                    return;
                }
                if (rawList == null) return;

                int bad = 0;
                foreach (var obj in rawList)
                {
                    try
                    {
                        string one = _ser.Serialize(obj);
                        var r = _ser.Deserialize<ReminderItem>(one);
                        if (r == null || string.IsNullOrWhiteSpace(r.id)) { bad++; continue; }
                        // v0.1.6.56：JavaScriptSerializer 把 DateTime 写成 "\/Date(ms)\/"、读回是 Kind=Utc，
                        // 而下游所有触发判定都对着 DateTime.Now（本地）比较，.NET 比较只比 Ticks 不看 Kind，
                        // 于是整体差一个时区（本机 8 小时）→ 重启后 countdown 提醒被立刻判成已到点。
                        // 读入即归一化成本地时间，4 处比较（IsFireTime×2 / TodayApplies / sortKey 展示）自动正确。
                        if (r.createdAt.Kind != DateTimeKind.Local) r.createdAt = r.createdAt.ToLocalTime();
                        _items.Add(r);
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        AppLog.Write("[REMINDER] 跳过损坏条目：" + ex.Message);
                    }
                }
                if (bad > 0)
                {
                    /* 全部条目都坏：极可能是整份文件被外部工具按标准 JSON 重写过（日期转义丢掉后不再被识别），
                       此时若照常写盘会把人家的原数据冲成空表，所以按"整体失败"处理、禁止写盘。 */
                    if (_items.Count == 0) _loadFailed = true;
                    AppLog.Write("[REMINDER] 本次载入 " + _items.Count + " 条，跳过损坏 " + bad + " 条"
                        + (_loadFailed ? "（全部损坏，已禁止写盘保护原数据）" : ""));
                    TryKeepBadCopy(path);
                }
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 读取 reminder.json 失败：" + ex.Message); }
        }

        /// <summary>把当前 reminder.json 另存一份带时间戳的副本（用于人工核对损坏条目），失败不影响主流程。</summary>
        private static void TryKeepBadCopy(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                string name = "reminder.json.bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(path, Path.Combine(dir, name), true);
                AppLog.Write("[REMINDER] 已保留副本：" + name);
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 保留副本失败：" + ex.Message); }
        }

        private void SaveToDisk()
        {
            try
            {
                /* v0.1.6.98：载入彻底失败时禁止写盘——否则空集合会把用户原数据永久冲掉 */
                if (_loadFailed)
                {
                    AppLog.Write("[REMINDER] reminder.json 载入失败过，本次跳过写盘以保护原数据");
                    return;
                }
                /* v0.1.6.92：原子落盘——写到一半被打断不会留下截断 JSON（原来直接覆盖，中断会让提醒全丢） */
                AtomicFile.WriteAllText(AppConfig.ReminderPath, _ser.Serialize(_items), new UTF8Encoding(false));
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 写入 reminder.json 失败：" + ex.Message); }
        }

        private void NotifyChanged()
        {
            SaveToDisk();
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>添加提醒。cfg：{mode,text,sound,repeatType,countdownMinutes,date,hour,minute,weekdays,monthDay}。返回 "ok" 或 "err:..."。</summary>
        public string Add(Dictionary<string, object> cfg)
        {
            lock (_lock)
            {
                string err = Validate(cfg, true);
                if (err != null) return err;
                var r = new ReminderItem
                {
                    id = "r-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"),
                    mode = GetStr(cfg, "mode", "countdown"),
                    text = GetStr(cfg, "text", ""),
                    sound = GetBool(cfg, "sound", false),
                    repeatType = GetStr(cfg, "repeatType", "once"),
                    createdAt = DateTime.Now
                };
                if (r.mode == "countdown")
                {
                    r.advanceOffsets = new List<int>();   // 倒计时没有「提前」，前端也不显示该行
                    r.countdownMinutes = Math.Max(1, GetInt(cfg, "countdownMinutes", 1));
                    r.repeatType = "once";
                    // v0.1.6.33：倒计时提醒对齐整分——向上取整到下一整分（秒/毫秒归零），
                    // 既让触发时间落在整分点，又不会把「1 分钟」压成几秒后触发
                    DateTime t0 = r.createdAt;
                    DateTime t1 = new DateTime(t0.Year, t0.Month, t0.Day, t0.Hour, t0.Minute, 0, t0.Kind);
                    if (t0.Second > 0 || t0.Millisecond > 0) t1 = t1.AddMinutes(1);
                    r.createdAt = t1;
                }
                else
                {
                    r.date = GetStr(cfg, "date", "");
                    r.hour = GetStr(cfg, "hour", "00");
                    r.minute = GetStr(cfg, "minute", "00");
                    r.weekdays = GetStr(cfg, "weekdays", "");
                    r.monthDay = Math.Max(1, Math.Min(31, GetInt(cfg, "monthDay", 1)));
                    r.monthlyMode = NormalizeMonthlyMode(GetStr(cfg, "monthlyMode", "day"));
                    r.workdayIndex = Math.Max(1, Math.Min(10, GetInt(cfg, "workdayIndex", 1)));
                    r.workdayPeriod = NormalizeWorkdayPeriod(GetStr(cfg, "workdayPeriod", "every"));
                    r.workdayMode = NormalizeWorkdayMode(GetStr(cfg, "workdayMode", "nth"));
                    r.yearlyMode = GetStr(cfg, "yearlyMode", "solar");
                    r.lunarMonth = GetInt(cfg, "lunarMonth", 0);
                    r.lunarDay = GetInt(cfg, "lunarDay", 0);
                    r.advanceOffsets = GetIntList(cfg, "advanceOffsets") ?? new List<int>();
                }
                _items.Add(r);
                NotifyChanged();
                AppLog.Write("[REMINDER] 已添加提醒 id=" + r.id);
                return "ok";
            }
        }

        /// <summary>修改提醒（文字/时间/声音可改，改后重新计时；单次提醒早于当前时刻会被拒绝，见 Validate）。周期结构保留（repeatType 未传则保留原值）。返回 "ok" 或 "err:..."。</summary>
        public string Update(Dictionary<string, object> cfg)
        {
            lock (_lock)
            {
                string id = GetStr(cfg, "id", "");
                ReminderItem r = _items.Find(x => x.id == id);
                if (r == null) return "err:未找到提醒 " + id;

                // 构造待更新的 cfg（未传字段用原值），统一走 Validate（单次早于当前时刻会被拒绝）
                var merged = new Dictionary<string, object>(cfg);
                if (!merged.ContainsKey("mode")) merged["mode"] = r.mode;
                if (!merged.ContainsKey("text")) merged["text"] = r.text;
                if (!merged.ContainsKey("sound")) merged["sound"] = r.sound;
                if (r.mode == "datetime" && GetStr(merged, "mode", "datetime") == "datetime")
                {
                    if (!merged.ContainsKey("date")) merged["date"] = r.date;
                    if (!merged.ContainsKey("hour")) merged["hour"] = r.hour;
                    if (!merged.ContainsKey("minute")) merged["minute"] = r.minute;
                    // 周期结构保留原值：前端改时间不改重复时 repeatType 不传 → 用原 repeatType
                    if (!merged.ContainsKey("repeatType")) merged["repeatType"] = r.repeatType;
                    if (!merged.ContainsKey("weekdays")) merged["weekdays"] = r.weekdays;
                    if (!merged.ContainsKey("monthDay")) merged["monthDay"] = r.monthDay;
                    if (!merged.ContainsKey("monthlyMode")) merged["monthlyMode"] = r.monthlyMode;
                    if (!merged.ContainsKey("workdayIndex")) merged["workdayIndex"] = r.workdayIndex;
                    if (!merged.ContainsKey("workdayPeriod")) merged["workdayPeriod"] = r.workdayPeriod ?? "every";
                    if (!merged.ContainsKey("workdayMode")) merged["workdayMode"] = r.workdayMode ?? "nth";
                    if (!merged.ContainsKey("advanceOffsets")) merged["advanceOffsets"] = r.advanceOffsets ?? new List<int>();
                    if (!merged.ContainsKey("yearlyMode")) merged["yearlyMode"] = r.yearlyMode ?? "solar";
                    if (!merged.ContainsKey("lunarMonth")) merged["lunarMonth"] = r.lunarMonth;
                    if (!merged.ContainsKey("lunarDay")) merged["lunarDay"] = r.lunarDay;
                }
                else if (GetStr(merged, "mode", r.mode) == "countdown")
                {
                    if (!merged.ContainsKey("countdownMinutes")) merged["countdownMinutes"] = r.countdownMinutes;
                }

                string err = Validate(merged, true);
                if (err != null) return err;

                r.mode = GetStr(merged, "mode", r.mode);
                r.text = GetStr(merged, "text", r.text);
                r.sound = GetBool(merged, "sound", r.sound);
                r.repeatType = r.mode == "countdown" ? "once" : GetStr(merged, "repeatType", r.repeatType);
                if (r.mode == "countdown")
                {
                    r.countdownMinutes = Math.Max(1, GetInt(merged, "countdownMinutes", 1));
                }
                else
                {
                    r.date = GetStr(merged, "date", r.date);
                    r.hour = GetStr(merged, "hour", r.hour);
                    r.minute = GetStr(merged, "minute", r.minute);
                    r.weekdays = GetStr(merged, "weekdays", r.weekdays);
                    r.monthDay = Math.Max(1, Math.Min(31, GetInt(merged, "monthDay", r.monthDay)));
                    r.monthlyMode = NormalizeMonthlyMode(GetStr(merged, "monthlyMode", r.monthlyMode));
                    r.workdayIndex = Math.Max(1, Math.Min(10, GetInt(merged, "workdayIndex", r.workdayIndex)));
                    r.workdayPeriod = NormalizeWorkdayPeriod(GetStr(merged, "workdayPeriod", r.workdayPeriod));
                    r.workdayMode = NormalizeWorkdayMode(GetStr(merged, "workdayMode", r.workdayMode));
                    r.yearlyMode = GetStr(merged, "yearlyMode", "solar");
                    r.lunarMonth = GetInt(merged, "lunarMonth", 0);
                    r.lunarDay = GetInt(merged, "lunarDay", 0);
                    r.advanceOffsets = GetIntList(merged, "advanceOffsets") ?? new List<int>();
                }
                // 改后重新计时：清运行态
                r.firedOnce = false; r.lastFiredKey = null; r.lastFiredAt = null; r.postponeUntil = null;

                NotifyChanged();
                AppLog.Write("[REMINDER] 已修改提醒 id=" + id);
                return "ok";
            }
        }

        /// <summary>删除提醒（手动）。返回 "ok" 或 "err:..."。</summary>
        public string Remove(string id)
        {
            lock (_lock)
            {
                int n = _items.RemoveAll(x => x.id == id);
                if (n == 0) return "err:未找到提醒 " + id;
                NotifyChanged();
                AppLog.Write("[REMINDER] 已删除提醒 id=" + id);
                return "ok";
            }
        }

        /// <summary>稍后 5 分钟（弹窗按钮；once 与周期均生效）：把下次触发推迟 5 分钟，撤销已触发标记。</summary>
        public void Postpone(string id)
        {
            lock (_lock)
            {
                var r = _items.Find(x => x.id == id);
                if (r == null) return;
                r.postponeUntil = DateTime.Now.AddMinutes(5);
                r.firedOnce = false;
                r.lastFiredKey = null;
                r.lastFiredAt = null;
                AppLog.Write("[REMINDER] 提醒稍后 5 分钟 id=" + id);
            }
        }

        /// <summary>我已知晓（弹窗按钮）：once 触发完成 → 移除；周期保留当日 lastFiredKey（今日不再重复弹）。</summary>
        public void Acknowledge(string id)
        {
            lock (_lock)
            {
                var r = _items.Find(x => x.id == id);
                if (r == null) return;
                if (r.repeatType == "once")
                {
                    _items.RemoveAll(x => x.id == id);
                    NotifyChanged();
                    AppLog.Write("[REMINDER] 单次提醒已完成并移除 id=" + id);
                }
                else
                {
                    r.lastFiredKey = DateTime.Now.ToString("yyyy-MM-dd");
                    r.lastFiredAt = DateTime.Now;
                    AppLog.Write("[REMINDER] 周期提醒今日已触发 id=" + id);
                }
            }
        }

        /// <summary>全部提醒列表 JSON：每项含 triggerLabel（人类可读触发时间）与 hasToday（今天会触发，供入口选中态）。</summary>
        public string List()
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                var todayKey = now.ToString("yyyy-MM-dd");
                var list = new List<object>();
                foreach (var r in _items)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = r.id,
                        ["mode"] = r.mode,
                        ["text"] = r.text,
                        ["sound"] = r.sound,
                        ["repeatType"] = r.repeatType,
                        ["countdownMinutes"] = r.countdownMinutes,
                        ["date"] = r.date,
                        ["hour"] = r.hour,
                        ["minute"] = r.minute,
                        ["weekdays"] = r.weekdays,
                        ["monthDay"] = r.monthDay,
                        ["monthlyMode"] = r.monthlyMode,
                        ["workdayIndex"] = r.workdayIndex,
                        ["workdayPeriod"] = r.workdayPeriod ?? "every",
                        ["workdayMode"] = r.workdayMode ?? "nth",
                        ["advanceOffsets"] = r.advanceOffsets ?? new List<int>(),
                        ["yearlyMode"] = r.yearlyMode ?? "solar",
                        ["lunarMonth"] = r.lunarMonth,
                        ["lunarDay"] = r.lunarDay,
                        ["triggerLabel"] = TriggerLabel(r),
                        ["nextAt"] = NextFireText(r, now),
                        ["hasToday"] = HasToday(r, now, todayKey),
                        ["sortKey"] = NextFireKey(r, now)
                    });
                }
                return _ser.Serialize(list);
            }
        }

        /// <summary>当天是否有待触发提醒（单次日期落在今天、或周期今天会触发）→ {hasToday}，供小组件入口选中态。</summary>
        public string GetHasToday()
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                var todayKey = now.ToString("yyyy-MM-dd");
                bool any = false;
                foreach (var r in _items) if (HasToday(r, now, todayKey)) { any = true; break; }
                return "{\"hasToday\":" + (any ? "true" : "false") + "}";
            }
        }

        // ==================== 触发判定 ====================

        private void Check()
        {
            try
            {
                var now = DateTime.Now;
                var todayKey = now.ToString("yyyy-MM-dd");
                var toFire = new List<ReminderItem>();
                lock (_lock)
                {
                    for (int i = _items.Count - 1; i >= 0; i--)
                    {
                        var r = _items[i];
                        // 稍后 5 分钟：未到延后点则跳过
                        if (r.postponeUntil.HasValue)
                        {
                            if (now < r.postponeUntil.Value) continue;
                            r.postponeUntil = null;
                        }
                        if (r.repeatType == "once" && r.firedOnce) continue;
                        // v0.1.6.77：算出「落在今天的全部触发点」（准点 + 各档提前点）
                        var points = PointsOn(r, now.Date);
                        if (points.Count == 0)
                        {
                            if (r.repeatType != "once") r.lastFiredKey = todayKey;
                            continue;
                        }
                        // 取「已到达、且晚于上次触发时刻」的最后一个点（提前点更早，随时间单调递增）
                        DateTime? hit = null;
                        for (int k = 0; k < points.Count; k++)
                        {
                            if (now < points[k]) break;
                            if (r.lastFiredAt.HasValue && r.lastFiredAt.Value >= points[k]) continue;
                            hit = points[k];
                        }
                        if (hit == null) continue;
                        // 到点 → 触发弹窗（先置标记，弹窗「稍后」会撤销、周期「知道了」保留）
                        if (r.repeatType == "once") r.firedOnce = true;
                        r.lastFiredKey = todayKey;
                        r.lastFiredAt = hit.Value;
                        toFire.Add(r);
                    }
                }
                foreach (var r in toFire)
                {
                    try { OnFire?.Invoke(r.id, r.text, r.sound); } catch { }
                }
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 调度检查异常：" + ex.Message); }
        }

        /// <summary>v0.1.6.77：算出「落在 day 这一天」的全部触发点（准点 + 各档提前点）。
        /// 提前点可能落在提醒日之前（如「提前 1 天」），所以从 day 起向后扫 maxDays 天的提醒日。</summary>
        private List<DateTime> PointsOn(ReminderItem r, DateTime day)
        {
            var list = new List<DateTime>();
            try
            {
                if (r.mode == "countdown")
                {
                    var fire0 = r.createdAt.AddMinutes(r.countdownMinutes);
                    if (fire0.Date == day.Date) list.Add(fire0);
                    return list;
                }
                var offs = r.advanceOffsets ?? new List<int>();
                int maxOff = 0;
                foreach (var o in offs) if (o > maxOff) maxOff = o;
                int maxDays = (maxOff + 1439) / 1440 + 1;
                for (int d = 0; d <= maxDays; d++)
                {
                    var baseDate = day.AddDays(d);
                    if (!BaseAppliesOn(r, baseDate)) continue;
                    var baseTime = BaseTimeOn(r, baseDate);
                    if (baseTime < r.createdAt) continue;
                    if (baseTime.Date == day.Date) list.Add(baseTime);   // 准点永远响
                    foreach (var o in offs)
                    {
                        if (o <= 0) continue;
                        var p = baseTime.AddMinutes(-o);
                        if (p.Date == day.Date && p >= r.createdAt) list.Add(p);
                    }
                }
                list.Sort();
            }
            catch (Exception ex) { AppLog.Write("[REMINDER] 触发点计算异常：" + ex.Message); }
            return list;
        }

        private static readonly System.Globalization.ChineseLunisolarCalendar _lunarCal =
            new System.Globalization.ChineseLunisolarCalendar();

        /// <summary>v0.1.6.78：公历 → 农历（月份取非闰月编号 + 日；leap 表示该日落在闰月）。</summary>
        public static void SolarToLunar(int y, int m, int d, out int lm, out int ld, out bool leap)
        {
            lm = 0; ld = 0; leap = false;
            try
            {
                var dt = new DateTime(y, m, d);
                int ly = _lunarCal.GetYear(dt);
                int leapMonth = _lunarCal.GetLeapMonth(ly);
                int raw = _lunarCal.GetMonth(dt);
                if (leapMonth > 0 && raw >= leapMonth)
                {
                    leap = raw == leapMonth;
                    lm = raw - 1;
                }
                else lm = raw;
                ld = _lunarCal.GetDayOfMonth(dt);
            }
            catch { }
        }

        /// <summary>v0.1.6.78：农历月日 → 该农历年的公历日期。
        /// 闰月一律跳过（取正闰月的同名月）；「三十」在该月为小月时落到当月最后一天（廿九）。</summary>
        public static DateTime? LunarToSolar(int lunarYear, int lm, int ld)
        {
            try
            {
                if (lm < 1 || lm > 12 || ld < 1 || ld > 30) return null;
                int leapMonth = _lunarCal.GetLeapMonth(lunarYear);
                int monthIdx = lm + (leapMonth > 0 && leapMonth <= lm ? 1 : 0);
                try { return _lunarCal.ToDateTime(lunarYear, monthIdx, ld, 0, 0, 0, 0); }
                catch (ArgumentException)
                {
                    try { return _lunarCal.ToDateTime(lunarYear, monthIdx, 29, 0, 0, 0, 0); }
                    catch (ArgumentException) { return null; }
                }
            }
            catch { return null; }
        }

        private static readonly string[] LUNAR_MONTH_NAMES = { "正月", "二月", "三月", "四月", "五月", "六月",
            "七月", "八月", "九月", "十月", "冬月", "腊月" };
        private static readonly string[] LUNAR_DAY_NAMES = { "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
            "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
            "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十" };

        public static string LunarMonthName(int m)
        {
            if (m < 1 || m > 12) return "";
            return LUNAR_MONTH_NAMES[m - 1];
        }

        public static string LunarDayName(int d)
        {
            if (d < 1 || d > 30) return "";
            return LUNAR_DAY_NAMES[d - 1];
        }

        /// <summary>v0.1.6.78：「每年」的文案（公历 / 农历两种口径）。</summary>
        private string YearlyLabel(ReminderItem r)
        {
            if (r.yearlyMode == "lunar")
                return "每年农历 " + LunarMonthName(r.lunarMonth) + LunarDayName(r.lunarDay);
            DateTime d;
            if (TryParseFire(r.date, "00", "00", out d)) return "每年 " + d.Month + " 月 " + d.Day + " 日";
            return "每年";
        }

        /// <summary>v0.1.6.77：该日期是否是这条提醒的「提醒日」（once 看提醒日期；周期按重复规则）。</summary>
        private bool BaseAppliesOn(ReminderItem r, DateTime date)
        {
            if (r.repeatType == "once")
                return string.Equals(r.date, date.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
            if (r.repeatType == "yearly")
            {
                if (r.yearlyMode == "lunar")
                {
                    var s = LunarToSolar(date.Year, r.lunarMonth, r.lunarDay);
                    return s.HasValue && s.Value.Date == date.Date;
                }
                DateTime baseDate;
                if (!TryParseFire(r.date, "00", "00", out baseDate)) return false;
                return date.Month == baseDate.Month && date.Day == baseDate.Day;
            }
            return TodayApplies(r, date);
        }

        private DateTime BaseTimeOn(ReminderItem r, DateTime date)
        {
            return date.AddHours(ParseInt(r.hour)).AddMinutes(ParseInt(r.minute));
        }

        /// <summary>v0.1.6.77：下一次触发点（含提前点），列表第二行与排序共用。</summary>
        private DateTime? NextPoint(ReminderItem r, DateTime now)
        {
            // v0.1.6.91：每年提醒的下一次常在 300 天后，原来只扫 60 天 → sortKey 为空 → 日历紫点永远不显示
            int horizon = (r.repeatType == "yearly") ? 400 : 60;
            for (int i = 0; i < horizon; i++)
            {
                var pts = PointsOn(r, now.Date.AddDays(i));
                foreach (var p in pts) if (p > now) return p;
            }
            return null;
        }

        /// <summary>v0.1.6.77：列表后缀「· 提前 30 分钟、1 天」。</summary>
        private string AdvanceText(ReminderItem r)
        {
            if (r.advanceOffsets == null || r.advanceOffsets.Count == 0) return "";
            var arr = new List<string>();
            foreach (var m in r.advanceOffsets) arr.Add(AdvanceLabel(m));
            return " · 提前 " + string.Join("、", arr);
        }

        private string AdvanceLabel(int m)
        {
            if (m % 1440 == 0) return (m / 1440) + " 天";
            if (m % 60 == 0) return (m / 60) + " 小时";
            return m + " 分钟";
        }

        /// <summary>今日是否到达基础触发时刻（once 取绝对触发时刻；周期取今日 hour:minute）。</summary>
        private bool IsFireTime(ReminderItem r, DateTime now)
        {
            if (r.mode == "countdown")
            {
                var fireAt = r.createdAt.AddMinutes(r.countdownMinutes);
                return now >= fireAt;
            }
            if (r.repeatType == "once")
            {
                DateTime dt;
                if (!TryParseFire(r.date, r.hour, r.minute, out dt)) return false;
                return now >= dt;
            }
            // 周期：今日 hour:minute（v0.1.6.40：时分与当前时刻无关，允许设在已过去的时刻）
            int hh = ParseInt(r.hour), mm = ParseInt(r.minute);
            var todayFire = now.Date.AddHours(hh).AddMinutes(mm);
            // 今天新建、且建的时候今日时刻已过 → 今天不补发，等下一次（否则刚设完就立刻弹窗）
            if (r.createdAt > todayFire) return false;
            return now >= todayFire;
        }

        /// <summary>今天是否适用（once 看触发日是否今天；daily 恒适用；workday 看是否工作日；weekly 看星期；monthly 看日期）。</summary>
        private bool TodayApplies(ReminderItem r, DateTime date)
        {
            if (r.repeatType == "once")
            {
                if (r.mode == "countdown")
                    return r.createdAt.AddMinutes(r.countdownMinutes).Date == date.Date;
                return string.Equals(r.date, date.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
            }
            // v0.1.6.91：循环提醒的 date 是「起始日」——早于起始日的日期一律不适用（与前端 rmOnDay 同口径）
            if (!string.IsNullOrEmpty(r.date) && r.date.Length == 10)
            {
                DateTime sd;
                if (TryParseFire(r.date, "00", "00", out sd) && date.Date < sd.Date) return false;
            }
            switch (r.repeatType)
            {
                case "daily": return true;
                // v0.1.6.83：工作日口径 = 周期（每个工作日 / 每周 / 每月）+ 第 N 个 / 倒数第 N 个
                case "workday":
                    if (r.workdayPeriod == "week")
                    {
                        DateTime? wk = WeekWorkdayDate(date, r.workdayMode, r.workdayIndex);
                        return wk.HasValue && wk.Value.Date == date.Date;
                    }
                    if (r.workdayPeriod == "month")
                    {
                        int? md2 = MonthlyWorkdayDay(date.Year, date.Month, r.workdayMode == "lastNth" ? "workdayLastNth" : "workdayNth", r.workdayIndex);
                        return md2.HasValue && date.Day == md2.Value;
                    }
                    return IsWorkday(date);   // every（含旧数据缺字段）：每个工作日都提醒
                case "weekly":
                    int wd = ((int)date.DayOfWeek + 6) % 7 + 1;
                    foreach (var p in Split(r.weekdays)) if (p == wd) return true;
                    return false;
                case "monthly":
                    // v0.1.6.66：每月三种口径——按日期 / 第 N 个工作日 / 倒数第 N 个工作日（旧数据 monthlyMode 为空 → 按日期）
                    if (r.monthlyMode == "workdayNth" || r.monthlyMode == "workdayLastNth")
                    {
                        int? wdDay = MonthlyWorkdayDay(date.Year, date.Month, r.monthlyMode, r.workdayIndex);
                        if (!wdDay.HasValue) return false;
                        return date.Day == wdDay.Value;
                    }
                    // v0.1.6.66 修：原来 clamp 到 28，导致「每月 29/30/31 号」永远在 28 号触发
                    int target = Math.Max(1, Math.Min(31, r.monthDay));
                    int dim = DateTime.DaysInMonth(date.Year, date.Month);
                    int fireDay = Math.Min(target, dim); // 无该日的大月 → 落到月末
                    return date.Day == fireDay;
                default: return false;
            }
        }

        private bool HasToday(ReminderItem r, DateTime now, string todayKey)
        {
            // v0.1.6.77：提前点落在今天也算「今天有提醒」（不然设了提前的当天入口不会亮）
            var pts = PointsOn(r, now.Date);
            if (pts.Count == 0) return false;
            if (r.repeatType == "once") return !r.firedOnce;
            foreach (var p in pts)
                if (!(r.lastFiredAt.HasValue && r.lastFiredAt.Value >= p)) return true;
            return false;
        }

        /// <summary>某日期是否为工作日：manual.batches（手动录入）+ remote.years[yyyy].batches（在线同步）；off→非工作，adjust→工作；都没命中→周一~五。
        /// v0.1.6.66：合并手动录入（原来只读 remote，用户手填的节假日对提醒不生效），并按日期缓存结果。</summary>
        private bool IsWorkday(DateTime date)
        {
            EnsureHolidayCacheFresh();
            string key = date.ToString("yyyy-MM-dd");
            lock (_lock)
            {
                bool cached;
                if (_wdCache.TryGetValue(key, out cached)) return cached;
            }
            bool v = CalcWorkday(date);
            lock (_lock) { _wdCache[key] = v; }
            return v;
        }

        /// <summary>节假日文件变动即清缓存（按 60 秒节流检查 mtime，避免每秒 stat）。</summary>
        private void EnsureHolidayCacheFresh()
        {
            DateTime now = DateTime.Now;
            if ((now - _holidayCheckedAt).TotalSeconds < 60) return;
            _holidayCheckedAt = now;
            try
            {
                DateTime mt = File.Exists(AppConfig.ManualHolidayPath)
                    ? File.GetLastWriteTime(AppConfig.ManualHolidayPath) : DateTime.MinValue;
                if (mt != _holidayMtime)
                {
                    _holidayMtime = mt;
                    lock (_lock) { _wdCache.Clear(); }
                }
            }
            catch { }
        }

        private bool CalcWorkday(DateTime date)
        {
            try
            {
                string key = date.ToString("yyyy-MM-dd");
                bool off = false, adjust = false;
                var data = ReadHolidayData();
                object manualObj;
                if (data.TryGetValue("manual", out manualObj) && manualObj is Dictionary<string, object>)
                {
                    object mb;
                    if (((Dictionary<string, object>)manualObj).TryGetValue("batches", out mb))
                        ScanBatchList(mb as System.Collections.IList, key, ref off, ref adjust);
                }
                object remoteObj;
                if (data.TryGetValue("remote", out remoteObj) && remoteObj is Dictionary<string, object>)
                {
                    object yearsObj;
                    if (((Dictionary<string, object>)remoteObj).TryGetValue("years", out yearsObj) && yearsObj is Dictionary<string, object>)
                    {
                        object yearObj;
                        if (((Dictionary<string, object>)yearsObj).TryGetValue(date.Year.ToString(), out yearObj) && yearObj is Dictionary<string, object>)
                        {
                            object bo;
                            if (((Dictionary<string, object>)yearObj).TryGetValue("batches", out bo))
                                ScanBatchList(bo as System.Collections.IList, key, ref off, ref adjust);
                        }
                    }
                }
                if (off) return false;      // 法定休 / 手动休息日
                if (adjust) return true;    // 调休补班（即使周末也算工作日）
                return !IsWeekend(date);
            }
            catch { return !IsWeekend(date); }
        }

        /// <summary>在一组 batches 里找 key 这一天，累计 off/adjust 命中（最终 off 优先）。</summary>
        private void ScanBatchList(System.Collections.IList batches, string key, ref bool off, ref bool adjust)
        {
            if (batches == null) return;
            foreach (var b in batches)
            {
                var batch = b as Dictionary<string, object>;
                if (batch == null) continue;
                object datesObj;
                if (!batch.TryGetValue("dates", out datesObj) || !(datesObj is System.Collections.IList)) continue;
                bool contains = false;
                foreach (var dd in (System.Collections.IList)datesObj)
                    if (dd != null && dd.ToString() == key) { contains = true; break; }
                if (!contains) continue;
                string kind = GetStr(batch, "kind", "");
                if (kind == "off") off = true;
                else if (kind == "adjust") adjust = true;
            }
        }

        /// <summary>v0.1.6.66：某月的「第 N 个工作日 / 倒数第 N 个工作日」是几号。
        /// 该月工作日不足 N 时——第 N 个提前取该月最后一个工作日，倒数第 N 个靠后取该月第一个工作日（提醒宁早勿漏）。</summary>
        private int? MonthlyWorkdayDay(int year, int month, string mode, int index)
        {
            try
            {
                int dim = DateTime.DaysInMonth(year, month);
                var days = new List<int>();
                for (int d = 1; d <= dim; d++)
                    if (IsWorkday(new DateTime(year, month, d))) days.Add(d);
                if (days.Count == 0) return null;
                int idx = Math.Max(1, Math.Min(10, index));
                if (mode == "workdayLastNth")
                {
                    if (idx <= days.Count) return days[days.Count - idx];
                    return days[0];              // 不足 → 该月第一个工作日
                }
                if (idx <= days.Count) return days[idx - 1];
                return days[days.Count - 1];     // 不足 → 该月最后一个工作日
            }
            catch { return null; }
        }

        /// <summary>v0.1.6.83：某自然周（周一算起）内第 N 个 / 倒数第 N 个工作日是哪天。
        /// 不足 N 个时——第 N 个取该周最后一个工作日，倒数第 N 个取该周第一个工作日（与每月同一口径，宁早勿漏）。</summary>
        private DateTime? WeekWorkdayDate(DateTime date, string mode, int index)
        {
            try
            {
                int off = ((int)date.DayOfWeek + 6) % 7;   // 周一 = 0
                DateTime monday = date.Date.AddDays(-off);
                var days = new List<DateTime>();
                for (int i = 0; i < 7; i++)
                {
                    DateTime d = monday.AddDays(i);
                    if (IsWorkday(d)) days.Add(d);
                }
                if (days.Count == 0) return null;
                int idx = Math.Max(1, Math.Min(3, index));   // v0.1.6.85：一周最多 5 个工作日，序号按 1~3
                int at = (mode == "lastNth") ? days.Count - idx : idx - 1;
                if (at < 0) at = 0;
                if (at >= days.Count) at = days.Count - 1;
                return days[at];
            }
            catch { return null; }
        }

        private static string NormalizeMonthlyMode(string v)
        {
            if (v == "workdayNth" || v == "workdayLastNth") return v;
            return "day";
        }

        private static string NormalizeWorkdayPeriod(string v)
        {
            if (string.IsNullOrEmpty(v)) return "every";
            v = v.Trim();
            if (v == "week" || v == "month" || v == "every") return v;
            return "every";
        }

        private static string NormalizeWorkdayMode(string v)
        {
            if (string.IsNullOrEmpty(v)) return "nth";
            v = v.Trim();
            return v == "lastNth" ? "lastNth" : "nth";
        }

        private Dictionary<string, object> ReadHolidayData()
        {
            var empty = new Dictionary<string, object> { ["remote"] = new Dictionary<string, object>() };
            try
            {
                if (!File.Exists(AppConfig.ManualHolidayPath)) return empty;
                string text = File.ReadAllText(AppConfig.ManualHolidayPath).Trim();
                if (string.IsNullOrWhiteSpace(text)) return empty;
                var root = _ser.Deserialize<Dictionary<string, object>>(text);
                return root ?? empty;
            }
            catch { return empty; }
        }

        private static bool IsWeekend(DateTime d) { return d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday; }

        /// <summary>人类可读触发时间（列表展示）。</summary>
        private string TriggerLabel(ReminderItem r)
        {
            if (r.mode == "countdown")
            {
                var fire = r.createdAt.AddMinutes(r.countdownMinutes);
                return "倒计时 " + r.countdownMinutes + " 分钟 · " + fire.ToString("yyyy-MM-dd HH:mm") + " 提醒";
            }
            string time = r.hour + ":" + r.minute;
            string main;
            switch (r.repeatType)
            {
                case "daily": main = "每天 " + time; break;
                case "workday": main = WorkdayLabel(r) + " " + time; break;   // v0.1.6.83
                case "weekly":
                    var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
                    var picked = new List<string>();
                    foreach (var p in Split(r.weekdays)) if (p >= 1 && p <= 7) picked.Add("周" + names[p - 1]);
                    main = "每周" + string.Join("、", picked) + " " + time;
                    break;
                case "monthly":
                    if (r.monthlyMode == "workdayNth") main = "每月第 " + r.workdayIndex + " 个工作日 " + time;
                    else if (r.monthlyMode == "workdayLastNth") main = "每月倒数第 " + r.workdayIndex + " 个工作日 " + time;
                    else main = "每月 " + r.monthDay + " 号 " + time;
                    break;
                case "yearly": main = YearlyLabel(r) + " " + time; break;
                default: main = r.date + " " + time + "（单次）"; break;
            }
            return main + AdvanceText(r);
        }

        /// <summary>v0.1.6.83：工作日口径文案（每个工作日 / 每周第 N 个 / 每月倒数第 N 个……）。</summary>
        private string WorkdayLabel(ReminderItem r)
        {
            string period = r.workdayPeriod == "week" ? "每周" : (r.workdayPeriod == "month" ? "每月" : "");
            if (period == "") return "工作日";
            return period + (r.workdayMode == "lastNth" ? "倒数第 " : "第 ") + r.workdayIndex + " 个工作日";
        }

        /// <summary>下次触发时间戳（Unix 秒），供前端列表组内按到期时间升序排序；已触发或无下次排到最末。</summary>
        private long NextFireKey(ReminderItem r, DateTime now)
        {
            try
            {
                DateTime next;
                if (r.mode == "countdown")
                {
                    next = r.firedOnce ? now.AddYears(10) : r.createdAt.AddMinutes(r.countdownMinutes);
                }
                else
                {
                    var np = NextPoint(r, now);
                    next = np.HasValue ? np.Value : now.AddYears(10);
                }
                return new DateTimeOffset(next).ToUnixTimeSeconds();
            }
            catch { return long.MaxValue; }
        }

        /// <summary>下次触发的绝对时刻 yyyy-MM-dd HH:mm（列表第二行展示，v0.1.6.33）；已触发/已过期/无下次返回空串。</summary>
        private string NextFireText(ReminderItem r, DateTime now)
        {
            try
            {
                DateTime? next = null;
                if (r.mode == "countdown")
                {
                    if (!r.firedOnce) next = r.createdAt.AddMinutes(r.countdownMinutes);
                }
                else if (!(r.repeatType == "once" && r.firedOnce))
                {
                    next = NextPoint(r, now);
                }
                return next.HasValue ? next.Value.ToString("yyyy-MM-dd HH:mm") : "";
            }
            catch { return ""; }
        }

        // ==================== 校验 ====================

        private string Validate(Dictionary<string, object> cfg, bool checkPast)
        {
            string mode = GetStr(cfg, "mode", "countdown");
            if (mode != "countdown" && mode != "datetime") return "err:定时方式必须为 countdown 或 datetime";
            string text = GetStr(cfg, "text", "");
            if (text.Length > 200) return "err:提醒文字不能超过 200 字";

            if (mode == "countdown")
            {
                int c = GetInt(cfg, "countdownMinutes", 1);
                if (c < 1) return "err:倒计时至少 1 分钟";
                return null;
            }

            // v0.1.6.77：提前提醒档位（下拉里只有 5 分钟~1 周这些提前项，不含 0）
            var advs = GetIntList(cfg, "advanceOffsets");
            if (advs != null)
            {
                if (advs.Count > 10) return "err:提前提醒最多 10 项";
                foreach (var a in advs) if (a < 1 || a > 10080) return "err:提前提醒档位超出范围（1 分钟~1 周）";
            }

            // datetime
            string date = GetStr(cfg, "date", "");
            string hour = GetStr(cfg, "hour", "00");
            string minute = GetStr(cfg, "minute", "00");
            DateTime dt;
            if (!TryParseFire(date, hour, minute, out dt)) return "err:日期或时间格式不正确";
            string rt = GetStr(cfg, "repeatType", "once");
            if (rt == "weekly")
            {
                var set = Split(GetStr(cfg, "weekdays", ""));
                if (set.Count == 0) return "err:请至少选择一个星期";
            }
            if (rt == "workday")
            {
                // v0.1.6.83：工作日周期与序号校验
                string wp = NormalizeWorkdayPeriod(GetStr(cfg, "workdayPeriod", "every"));
                if (wp != "every")
                {
                    string wm = NormalizeWorkdayMode(GetStr(cfg, "workdayMode", "nth"));
                    if (wm != "nth" && wm != "lastNth") return "err:工作日口径只支持 第N个 / 倒数第N个";
                    int wi0 = GetInt(cfg, "workdayIndex", 1);
                    // v0.1.6.85：每周最多 5 个工作日，序号按 1~3；每月仍 1~10
                    int wiMax = wp == "week" ? 3 : 10;
                    if (wi0 < 1 || wi0 > wiMax) return wp == "week" ? "err:每周第 N 个工作日应为 1~3" : "err:第 N 个工作日应为 1~10";
                }
            }
            if (rt == "monthly")
            {
                string mm = GetStr(cfg, "monthlyMode", "day");
                if (mm == "workdayNth" || mm == "workdayLastNth")
                {
                    int wi = GetInt(cfg, "workdayIndex", 1);
                    if (wi < 1 || wi > 10) return "err:第 N 个工作日应为 1~10";
                }
                else if (mm != "" && mm != "day") return "err:每月口径只支持 day / workdayNth / workdayLastNth";
                int md = GetInt(cfg, "monthDay", 1);
                if (md < 1 || md > 31) return "err:每月日期应为 1~31";
            }
            if (rt == "yearly")
            {
                string ym = GetStr(cfg, "yearlyMode", "solar");
                if (ym != "solar" && ym != "lunar") return "err:每年口径只支持 公历 / 农历";
                if (ym == "lunar")
                {
                    int lm0 = GetInt(cfg, "lunarMonth", 0), ld0 = GetInt(cfg, "lunarDay", 0);
                    if (lm0 < 1 || lm0 > 12) return "err:农历月份应为 1~12";
                    if (ld0 < 1 || ld0 > 30) return "err:农历日期应为 1~30";
                    if (!LunarToSolar(dt.Year, lm0, ld0).HasValue) return "err:该农历日期在当年不存在（可能没有三十）";
                }
            }
            // v0.1.6.41：单次提醒不允许设在过去——早于当前时刻直接拒绝并提示；
            // 周期提醒（每天/工作日/每周/每月）的时分只表示「当天时刻」，与当前时间无关，不校验。
            if (rt == "once" && dt <= DateTime.Now) return "err:设置时间早于当前时间，无法添加提醒";
            return null;
        }

        private bool TryParseFire(string date, string hour, string minute, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out DateTime d)) return false;
            int hh = ParseInt(hour), mm = ParseInt(minute);
            if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return false;
            dt = d.AddHours(hh).AddMinutes(mm);
            return true;
        }

        private static HashSet<int> Split(string s)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(s)) return set;
            foreach (var p in s.Split(','))
                if (int.TryParse(p.Trim(), out int v) && v >= 1 && v <= 7) set.Add(v);
            return set;
        }

        private static int ParseInt(string s) { return int.TryParse(s, out int v) ? v : 0; }

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

        /// <summary>v0.1.6.77：取整数数组（前端 JSON 数组反序列化成 object[]；单值也兼容）。</summary>
        private List<int> GetIntList(Dictionary<string, object> cfg, string key)
        {
            if (cfg == null || !cfg.ContainsKey(key)) return null;
            object v = cfg[key];
            if (v == null) return new List<int>();
            var list = new List<int>();
            /* v0.1.6.92 修 BUG：桥把前端 JSON 字符串反序列化成 Dictionary<string,object> 时，
               数组是 ArrayList 而不是 object[]（CSharpBridge.Holidays 里早有同款注释）。
               原来只认 object[]，走 else 分支转 int 失败 → 返回空表，
               结果是「提前提醒」在界面上选了几档，落盘 advancOffsets 恒为 []，一项都没生效。 */
            var col = v as System.Collections.IList;
            if (col != null && !(v is string))
            {
                foreach (var o in col)
                {
                    int n = 0;
                    try { n = Convert.ToInt32(o); } catch (Exception) { continue; }
                    if (n > 0) list.Add(n);
                }
            }
            else
            {
                int n = 0;
                try { n = Convert.ToInt32(v); } catch (Exception) { n = 0; }
                if (n > 0) list.Add(n);
            }
            return list;
        }
    }

    /// <summary>提醒项：持久化字段（写 reminder.json）+ 运行态字段（ScriptIgnore，不落盘）。</summary>
    internal class ReminderItem
    {
        public string id;
        public string mode;            // countdown / datetime
        public string text;
        public bool sound;
        public string repeatType;      // once/daily/workday/weekly/monthly
        public int countdownMinutes;
        public string date;            // yyyy-MM-dd（datetime）
        public string hour;            // HH
        public string minute;          // mm
        public string weekdays;        // '1,3,5'（weekly）
        public int monthDay;           // monthly（day 口径用）
        public string monthlyMode;     // v0.1.6.66：day / workdayNth / workdayLastNth（空=day，旧数据兼容）
        public int workdayIndex;       // v0.1.6.66：第 N 个 / 倒数第 N 个工作日（v0.1.6.69 起 1~10）
        public string workdayPeriod;   // v0.1.6.83：workday 的周期——every（每个工作日，缺省）/ week（每周）/ month（每月）
        public string workdayMode;     // v0.1.6.83：nth（第 N 个）/ lastNth（倒数第 N 个）
        public List<int> advanceOffsets; // v0.1.6.77：提前提醒分钟数（多选，如 5/30/1440；空=只准点响）
        public string yearlyMode;      // v0.1.6.78：每年口径 solar=按公历月日 / lunar=按农历月日
        public int lunarMonth;         // v0.1.6.78：农历月份 1~12（yearlyMode=lunar 时有效）
        public int lunarDay;           // v0.1.6.78：农历日 1~30（yearlyMode=lunar 时有效）
        public DateTime createdAt;

        // 运行态（不持久化）
        [ScriptIgnore]
        public bool firedOnce;
        [ScriptIgnore]
        public string lastFiredKey;
        [ScriptIgnore]
        public DateTime? lastFiredAt;   // v0.1.6.77：最后一次触发的「触发点时刻」，多点提前靠它去重
        [ScriptIgnore]
        public DateTime? postponeUntil;
    }
}

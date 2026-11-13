using System;
using System.Collections.Generic;
using System.Globalization;

namespace PiggyCalendar.WebShell.Services.HolidaySync
{
    /// <summary>适配器转换结果：成功给出标准化批次列表；失败给出错误说明，保留旧缓存不覆盖。</summary>
    internal class AdapterResult
    {
        public bool Ok;
        public string Error = "";
        public List<Dictionary<string, object>> Batches = new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// 在线年份数据 → 统一 remote 批次结构（与 manual.batches 同构：kind/name/keyDay/start/end/dates）。
    /// 两个适配器只做解析与校验，不写盘、不冲突合并；冲突裁决由前端 status 先 manual 后 remote 完成。
    /// </summary>
    internal static class Adapters
    {
        // ---------- 日期辅助 ----------

        public static bool TryParseDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out d);
        }

        private static List<object> ExpandDates(DateTime s, DateTime e)
        {
            var dates = new List<object>();
            for (var cur = s; cur <= e; cur = cur.AddDays(1))
                dates.Add(cur.ToString("yyyy-MM-dd"));
            return dates;
        }

        private static string Str(object o) { return o == null ? "" : o.ToString(); }

        // ---------- 适配器一：self-years / piggy-v1（作者自建仓库，批次结构与本应用一致） ----------

        /// <summary>入参为反序列化后的 JSON 对象（Dictionary 或数组）；直接校验并标准化每个批次。</summary>
        public static AdapterResult ConvertSelfYears(object root)
        {
            var result = new AdapterResult();
            System.Collections.IList rawBatches = null;

            var dict = root as Dictionary<string, object>;
            if (dict != null && dict.ContainsKey("batches"))
                rawBatches = (dict["batches"] as System.Collections.IList);
            else
                rawBatches = (root as System.Collections.IList);

            if (rawBatches == null) { result.Error = "piggy-v1 结构缺失 batches 数组"; return result; }

            foreach (var item in rawBatches)
            {
                var b = item as Dictionary<string, object>;
                if (b == null) { result.Error = "piggy-v1 批次项不是对象"; return result; }

                string kind = Str(b.ContainsKey("kind") ? b["kind"] : null);
                if (kind != "off" && kind != "adjust") { result.Error = "piggy-v1 批次类型非法：" + kind; return result; }

                string start = Str(b.ContainsKey("start") ? b["start"] : null);
                string end = Str(b.ContainsKey("end") ? b["end"] : null);
                if (!TryParseDate(start, out DateTime s) || !TryParseDate(end, out DateTime e))
                { result.Error = "piggy-v1 批次日期非法：" + start + "~" + end; return result; }
                if (s > e) { result.Error = "piggy-v1 批次开始晚于结束：" + start; return result; }

                var outBatch = new Dictionary<string, object>
                {
                    ["id"] = "",
                    ["kind"] = kind,
                    ["name"] = Str(b.ContainsKey("name") ? b["name"] : null),
                    ["keyDay"] = Str(b.ContainsKey("keyDay") ? b["keyDay"] : null),
                    ["start"] = start,
                    ["end"] = end
                };

                var dates = new List<object>();
                if (b.ContainsKey("dates") && b["dates"] is System.Collections.IList dl)
                    foreach (var d in dl) dates.Add(Str(d));
                if (dates.Count == 0) dates = ExpandDates(s, e);
                outBatch["dates"] = dates;

                result.Batches.Add(outBatch);
            }

            result.Ok = result.Batches.Count > 0;
            if (!result.Ok) result.Error = "piggy-v1 未解析出任何批次";
            return result;
        }

        // ---------- 适配器二：chinese-days-yearly / chinese-days-v1 ----------
        // 结构：{ holidays:{date:"英文名,中文名,类型码"}, workdays:{...}, inLieuDays:{...} }
        // 仅取 holidays（聚合为连续区间 off）与 workdays（单日 adjust）；inLieuDays 按拍板忽略。

        public static AdapterResult ConvertChineseDays(object root)
        {
            var result = new AdapterResult();
            var dict = root as Dictionary<string, object>;
            if (dict == null) { result.Error = "chinese-days-v1 根节点不是对象"; return result; }

            var holidays = dict.ContainsKey("holidays") ? dict["holidays"] as Dictionary<string, object> : null;
            var workdays = dict.ContainsKey("workdays") ? dict["workdays"] as Dictionary<string, object> : null;
            // inLieuDays 故意不读取：调休替代日按 M0-8 拍板忽略，不展示、不挂标。

            if (holidays == null && workdays == null) { result.Error = "chinese-days-v1 缺少 holidays/workdays"; return result; }

            // holidays：按中文名分组，组内日期升序，连续日期聚合为一个 off 区间批次。
            if (holidays != null)
            {
                var byName = new Dictionary<string, List<DateTime>>();
                foreach (var kv in holidays)
                {
                    if (!TryParseDate(kv.Key, out DateTime d)) continue;
                    string parts = Str(kv.Value);
                    string cn = parts;
                    var comma = parts.Split(',');
                    if (comma.Length >= 2) cn = comma[1].Trim();
                    if (!byName.ContainsKey(cn)) byName[cn] = new List<DateTime>();
                    byName[cn].Add(d);
                }

                foreach (var nameGroup in byName)
                {
                    nameGroup.Value.Sort();
                    List<DateTime> segment = null;
                    foreach (var d in nameGroup.Value)
                    {
                        if (segment == null) { segment = new List<DateTime> { d }; continue; }
                        var prev = segment[segment.Count - 1];
                        if ((d - prev).TotalDays <= 1) segment.Add(d);
                        else { FlushSegment(segment, nameGroup.Key, result); segment = new List<DateTime> { d }; }
                    }
                    if (segment != null) FlushSegment(segment, nameGroup.Key, result);
                }
            }

            // workdays：每条日期一个 adjust 单日批次，名称取所属节日中文。
            if (workdays != null)
            {
                foreach (var kv in workdays)
                {
                    if (!TryParseDate(kv.Key, out DateTime d)) continue;
                    string parts = Str(kv.Value);
                    string cn = parts;
                    var comma = parts.Split(',');
                    if (comma.Length >= 2) cn = comma[1].Trim();
                    string ds = d.ToString("yyyy-MM-dd");
                    result.Batches.Add(new Dictionary<string, object>
                    {
                        ["id"] = "",
                        ["kind"] = "adjust",
                        ["name"] = cn,
                        ["keyDay"] = "",
                        ["start"] = ds,
                        ["end"] = ds,
                        ["dates"] = new List<object> { ds }
                    });
                }
            }

            result.Ok = result.Batches.Count > 0;
            if (!result.Ok) result.Error = "chinese-days-v1 未解析出任何批次";
            return result;
        }

        // ---------- 适配器三：timor-v1（timor.tech 中国节假日 API，免费无 key，含休/班/调休） ----------
        // 结构：{ code, holiday:{ "MM-DD": { holiday:bool, name, wage, date:"yyyy-MM-dd" } } }
        // holiday=true 按 name 聚合为连续 off 区间；holiday=false 单日 adjust（调休补班）。
        // 说明：与 chinese-days-v1 同构（kind/name/keyDay/start/end/dates），供前端一致消费。

        public static AdapterResult ConvertTimor(object root)
        {
            var result = new AdapterResult();
            var dict = root as Dictionary<string, object>;
            if (dict == null) { result.Error = "timor-v1 根节点不是对象"; return result; }

            var holiday = dict.ContainsKey("holiday") ? dict["holiday"] as Dictionary<string, object> : null;
            if (holiday == null) { result.Error = "timor-v1 缺少 holiday 字段"; return result; }

            var offByName = new Dictionary<string, List<DateTime>>();
            var adjust = new List<KeyValuePair<DateTime, string>>();
            foreach (var kv in holiday)
            {
                var v = kv.Value as Dictionary<string, object>;
                if (v == null) continue;
                // 键为 MM-DD，值内 date 为完整 yyyy-MM-dd，优先取值内 date。
                string dateStr = Str(v.ContainsKey("date") ? v["date"] : kv.Key);
                if (!TryParseDate(dateStr, out DateTime d)) continue;
                bool isHoliday = v.ContainsKey("holiday") && System.Convert.ToBoolean(v["holiday"]);
                string name = Str(v.ContainsKey("name") ? v["name"] : null);
                // timor 将春节逐日命名为 除夕/初一/初二…，归一为「春节」以便聚合为一个连续区间批次。
                if (name == "除夕" || name.StartsWith("初")) name = "春节";

                if (isHoliday)
                {
                    if (!offByName.ContainsKey(name)) offByName[name] = new List<DateTime>();
                    offByName[name].Add(d);
                }
                else
                {
                    adjust.Add(new KeyValuePair<DateTime, string>(d, name));
                }
            }

            // off：按节日名分组，连续日期聚合为一个 off 区间批次。
            foreach (var g in offByName)
            {
                g.Value.Sort();
                List<DateTime> segment = null;
                foreach (var d in g.Value)
                {
                    if (segment == null) { segment = new List<DateTime> { d }; continue; }
                    var prev = segment[segment.Count - 1];
                    if ((d - prev).TotalDays <= 1) segment.Add(d);
                    else { FlushSegment(segment, g.Key, result); segment = new List<DateTime> { d }; }
                }
                if (segment != null) FlushSegment(segment, g.Key, result);
            }

            // adjust：每条补班日期一个单日批次，名称取「节日+前/后补班」。
            foreach (var a in adjust)
            {
                string ds = a.Key.ToString("yyyy-MM-dd");
                string nm = string.IsNullOrEmpty(a.Value) ? "调休补班" : a.Value;
                result.Batches.Add(new Dictionary<string, object>
                {
                    ["id"] = "", ["kind"] = "adjust", ["name"] = nm, ["keyDay"] = "",
                    ["start"] = ds, ["end"] = ds, ["dates"] = new List<object> { ds }
                });
            }

            result.Ok = result.Batches.Count > 0;
            if (!result.Ok) result.Error = "timor-v1 未解析出任何批次";
            return result;
        }

        private static void FlushSegment(List<DateTime> segment, string name, AdapterResult result)
        {
            if (segment.Count == 0) return;
            DateTime s = segment[0], e = segment[segment.Count - 1];
            result.Batches.Add(new Dictionary<string, object>
            {
                ["id"] = "",
                ["kind"] = "off",
                ["name"] = name,
                ["keyDay"] = s.ToString("yyyy-MM-dd"),
                ["start"] = s.ToString("yyyy-MM-dd"),
                ["end"] = e.ToString("yyyy-MM-dd"),
                ["dates"] = ExpandDates(s, e)
            });
        }

        /// <summary>按 format 分派到对应适配器；未知 format 返回失败结果。</summary>
        public static AdapterResult Convert(string format, object root)
        {
            switch (format)
            {
                case "piggy-v1": return ConvertSelfYears(root);
                case "chinese-days-v1": return ConvertChineseDays(root);
                case "timor-v1": return ConvertTimor(root);
                default:
                    return new AdapterResult { Error = "未实现的源格式：" + format };
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：节假日本地数据层（M0-7 手动打通；M0-8b 升级 v2 manual/remote 分区 + 手动细粒度 CRUD）。
    /// 数据文件：运行组件\manual-holidays.json。
    /// v2 结构：
    /// {
    ///   "version": 2,
    ///   "manual": { "batches": [ {id,kind,name,keyDay,start,end,dates} ] },
    ///   "remote": { "sourceId":"", "fetchedAt":"", "sourceVersion":"", "etag":"",
    ///               "years": { "yyyy": { "fetchedAt":"", "batches":[] } } }
    /// }
    /// kind: off=放假, adjust=调休补班；dates 为展开后的单日 yyyy-MM-dd 数组。
    /// 分区约定：manual 由用户维护，手动 CRUD 只改 manual；remote 仅由 M0-8c 同步引擎写入，手动入口不提供写 remote 的方法。
    /// </summary>
    public partial class CSharpBridge
    {
        private static readonly JavaScriptSerializer _holidaySer = new JavaScriptSerializer();

        /// <summary>构造一份空 v2 分区结构。</summary>
        private static Dictionary<string, object> EmptyV2()
        {
            var data = new Dictionary<string, object>
            {
                ["version"] = 2,
                ["manual"] = new Dictionary<string, object> { ["batches"] = new List<object>() },
                ["remote"] = new Dictionary<string, object>
                {
                    ["sourceId"] = "",
                    ["fetchedAt"] = "",
                    ["sourceVersion"] = "",
                    ["etag"] = "",
                    ["years"] = new Dictionary<string, object>()
                }
            };
            return data;
        }

        /// <summary>
        /// 读取完整 v2 数据；v1 旧结构自动迁移（顶层 batches 全部归入 manual）；文件缺失/损坏返回空 v2。
        /// 返回的是可变 Dictionary，供 CRUD 修改后整体落盘。
        /// </summary>
        private Dictionary<string, object> ReadHolidayData()
        {
            try
            {
                string path = AppConfig.ManualHolidayPath;
                if (!File.Exists(path)) return EmptyV2();

                string text = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(text)) return EmptyV2();

                var root = _holidaySer.Deserialize<Dictionary<string, object>>(text);
                if (root == null) return EmptyV2();

                object verObj;
                int version = root.TryGetValue("version", out verObj) && verObj != null
                    ? Convert.ToInt32(verObj) : 1;

                // v1 → v2 迁移：顶层 batches 整体归入 manual；remote 置空壳；迁移后立即落盘固化，二次启动直接读 v2。
                if (version < 2)
                {
                    var v2 = EmptyV2();
                    var manual = (Dictionary<string, object>)v2["manual"];
                    var batches = ExtractBatchList(root, "batches");
                    manual["batches"] = batches;
                    WriteHolidayData(v2);
                    AppLog.Write("[HOLIDAY] v1 数据已迁移为 v2 分区（manual " + batches.Count + " 条）");
                    return v2;
                }

                // v2 规整：缺分区补空壳，保证后续访问字段存在。
                if (!root.ContainsKey("manual") || !(root["manual"] is Dictionary<string, object>))
                    root["manual"] = new Dictionary<string, object> { ["batches"] = new List<object>() };
                if (!((Dictionary<string, object>)root["manual"]).ContainsKey("batches"))
                    ((Dictionary<string, object>)root["manual"])["batches"] = new List<object>();

                if (!root.ContainsKey("remote") || !(root["remote"] is Dictionary<string, object>))
                    root["remote"] = new Dictionary<string, object>
                    {
                        ["sourceId"] = "", ["fetchedAt"] = "", ["sourceVersion"] = "",
                        ["etag"] = "", ["years"] = new Dictionary<string, object>()
                    };
                else
                {
                    var remote = (Dictionary<string, object>)root["remote"];
                    foreach (var k in new[] { "sourceId", "fetchedAt", "sourceVersion", "etag" })
                        if (!remote.ContainsKey(k)) remote[k] = "";
                    if (!remote.ContainsKey("years") || !(remote["years"] is Dictionary<string, object>))
                        remote["years"] = new Dictionary<string, object>();
                }

                // v0.1.5.0 一次性迁移：清掉旧版播种残留的内置样本（id 以 "2026-" 开头；内置样本改由前端 seed 独立维护，
                // 不再写入 manual，管理页只展示用户真实手动批次）。发生清理则落盘固化，避免二次启动仍读到残留。
                var cleanList = GetManualBatchList(root);
                int removedLegacy = 0;
                for (int i = cleanList.Count - 1; i >= 0; i--)
                {
                    var b = cleanList[i] as Dictionary<string, object>;
                    if (b != null && b.TryGetValue("id", out object idObj) && idObj != null &&
                        idObj.ToString().StartsWith("2026-", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanList.RemoveAt(i);
                        removedLegacy++;
                    }
                }
                if (removedLegacy > 0)
                {
                    WriteHolidayData(root);
                    AppLog.Write("[HOLIDAY] 清理旧版内置样本 " + removedLegacy + " 条（id 以 2026- 开头）");
                }

                root["version"] = 2;
                return root;
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] ReadHolidayData 失败，返回空 v2：" + ex.Message);
                return EmptyV2();
            }
        }

        /// <summary>把完整 v2 结构以 UTF-8 无 BOM 落盘。</summary>
        private void WriteHolidayData(Dictionary<string, object> data)
        {
            data["version"] = 2;
            string path = AppConfig.ManualHolidayPath;
            /* v0.1.6.92：原子落盘（节假日是用户手填数据，截断后整个节假日/工作日判定都会失真） */
            AtomicFile.WriteAllText(path, _holidaySer.Serialize(data), new UTF8Encoding(false));
        }

        // ======== M0-8c：供同步引擎复用的 internal 读写（不暴露给前端桥） ========

        /// <summary>读取完整 v2 结构（含迁移/规整）；供在线同步引擎操作 remote 分区。</summary>
        internal Dictionary<string, object> ReadDataForSync() { return ReadHolidayData(); }

        /// <summary>把引擎改写后的完整 v2 结构落盘（manual 保持原样，仅 remote 被同步引擎更新）。</summary>
        internal void WriteDataForSync(Dictionary<string, object> data) { WriteHolidayData(data); }

        /// <summary>从字典中取指定键的批次列表；兼容 JavaScriptSerializer 反序列化出的 object[] / ArrayList / List&lt;object&gt;。</summary>
        private static List<object> ExtractBatchList(Dictionary<string, object> dict, string key)
        {
            var list = new List<object>();
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return list;
            var col = dict[key] as System.Collections.IList;
            if (col != null)
            {
                foreach (var item in col) list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// 取 manual 分区的批次列表（规范化为 List&lt;object&gt; 并回写 data）。
        /// 磁盘 v2 反序列化后 batches 可能是 object[]/ArrayList，统一复制为 List，供 CRUD 增删改。
        /// </summary>
        private static List<object> GetManualBatchList(Dictionary<string, object> data)
        {
            var manual = (Dictionary<string, object>)data["manual"];
            if (manual["batches"] is List<object> typed) return typed;
            var list = ExtractBatchList(manual, "batches");
            manual["batches"] = list;
            return list;
        }

        // ==================== 兼容旧接口（M0-7；内部映射到 manual 分区，不触碰 remote） ====================

        /// <summary>读取手动批次（兼容旧前端）：返回 {version:1, batches: manual.batches}。</summary>
        public string GetManualHolidays()
        {
            try
            {
                var data = ReadHolidayData();
                var manual = (Dictionary<string, object>)data["manual"];
                var legacy = new Dictionary<string, object>
                {
                    ["version"] = 1,
                    ["batches"] = manual["batches"]
                };
                return _holidaySer.Serialize(legacy);
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] GetManualHolidays 失败：" + ex.Message);
                return "{\"version\":1,\"batches\":[]}";
            }
        }

        /// <summary>
        /// 兼容旧整体保存：传入旧结构 {batches:[...]}，整体替换 manual.batches；remote 分区原样保留。
        /// 返回 "ok" 或 "err:..."。
        /// </summary>
        public string SaveManualHolidays(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空数据";
                var incoming = _holidaySer.Deserialize<Dictionary<string, object>>(json);
                if (incoming == null) return "err:配置解析失败";

                var data = ReadHolidayData();
                var manual = (Dictionary<string, object>)data["manual"];
                manual["batches"] = ExtractBatchList(incoming, "batches");
                WriteHolidayData(data);
                AppLog.Write("[HOLIDAY] manual-holidays.json 已保存（兼容整体写 manual）");
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] SaveManualHolidays 失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }

        // ==================== M0-8b 完整分区读取 + 手动细粒度 CRUD ====================

        /// <summary>读取完整 v2 分区结构（manual + remote 元信息与各年缓存）。</summary>
        public string GetHolidayData()
        {
            try
            {
                return _holidaySer.Serialize(ReadHolidayData());
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] GetHolidayData 失败：" + ex.Message);
                return _holidaySer.Serialize(EmptyV2());
            }
        }

        /// <summary>
        /// 向 manual 分区追加一个批次；校验 kind/日期合法性，dates 缺失时按 start~end 展开；remote 原样保留。
        /// 入参为单个批次 JSON 对象；返回 "ok" 或 "err:..."。
        /// </summary>
        public string AddManualBatch(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:空批次";
                var batch = _holidaySer.Deserialize<Dictionary<string, object>>(json);
                if (batch == null) return "err:批次解析失败";

                string kind = GetStr(batch, "kind", "");
                if (kind != "off" && kind != "adjust") return "err:类型必须为 off 或 adjust";

                string start = GetStr(batch, "start", "");
                string end = GetStr(batch, "end", "");
                if (!TryParseDate(start, out DateTime s) || !TryParseDate(end, out DateTime e))
                    return "err:日期格式应为 yyyy-MM-dd";
                if (s > e) return "err:开始日期晚于结束日期";

                // id 缺省生成；冲突时追加序号避免覆盖。
                string id = GetStr(batch, "id", "");
                if (string.IsNullOrWhiteSpace(id))
                    id = "m-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");

                // dates 缺失则按区间展开；已有则原样保留并校验为日期数组。
                var dates = ExtractBatchList(batch, "dates");
                if (dates.Count == 0)
                    dates = ExpandDates(s, e);
                batch["dates"] = dates;
                batch["start"] = start;
                batch["end"] = end;
                batch["kind"] = kind;
                batch["id"] = id;
                if (!batch.ContainsKey("name")) batch["name"] = "";
                if (!batch.ContainsKey("keyDay")) batch["keyDay"] = "";

                var data = ReadHolidayData();
                var list = GetManualBatchList(data);

                // id 去重：已存在则替换同 id 批次，否则追加。
                bool replaced = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var old = list[i] as Dictionary<string, object>;
                    if (old != null && GetStr(old, "id", "") == id) { list[i] = batch; replaced = true; break; }
                }
                if (!replaced) list.Add(batch);

                WriteHolidayData(data);
                AppLog.Write("[HOLIDAY] 手动批次已" + (replaced ? "更新" : "新增") + " id=" + id);
                NotifyManualChanged();
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] AddManualBatch 失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }

        /// <summary>按 id 从 manual 分区删除批次；remote 不受影响。返回 "ok" / "err:..."。</summary>
        public string RemoveManualBatch(string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id)) return "err:缺少批次 id";
                var data = ReadHolidayData();
                var list = GetManualBatchList(data);

                int removed = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var b = list[i] as Dictionary<string, object>;
                    if (b != null && GetStr(b, "id", "") == id) { list.RemoveAt(i); removed++; }
                }
                if (removed == 0) return "err:未找到批次 " + id;

                WriteHolidayData(data);
                AppLog.Write("[HOLIDAY] 手动批次已删除 id=" + id + "（" + removed + " 条）");
                NotifyManualChanged();
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] RemoveManualBatch 失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }

        /// <summary>清空 manual 分区全部批次；remote 分区原样保留。返回 "ok" / "err:..."。</summary>
        public string ClearManual()
        {
            try
            {
                var data = ReadHolidayData();
                GetManualBatchList(data).Clear();
                WriteHolidayData(data);
                AppLog.Write("[HOLIDAY] 手动批次已清空（remote 保留）");
                NotifyManualChanged();
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[HOLIDAY] ClearManual 失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }

        // ==================== 日期辅助 ====================

        private static bool TryParseDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact(s, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out d);
        }

        private static List<object> ExpandDates(DateTime s, DateTime e)
        {
            var dates = new List<object>();
            for (var cur = s; cur <= e; cur = cur.AddDays(1))
                dates.Add(cur.ToString("yyyy-MM-dd"));
            return dates;
        }
    }
}

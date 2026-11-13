using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services.HolidaySync
{
    /// <summary>
    /// M0-8c 在线节假日同步引擎（纯后端，无 UI；管理页状态展示与手动触发归 M0-10）。
    /// 职责：动态清单（在线预留、失败回退内置）→ 状态机决定年份 → 按 priority 有序尝试即停 →
    /// 适配器转换校验 → 仅覆盖 remote.years[year]，manual 分区永不被改写。
    /// 失败静默降级：超时 / DNS / 4xx / 5xx / 404 / 坏 JSON / 校验不过 → 下一源；全部失败保留旧缓存，不弹窗、不崩溃。
    /// </summary>
    public class HolidaySyncService
    {
        private static readonly Lazy<HolidaySyncService> _instance =
            new Lazy<HolidaySyncService>(() => new HolidaySyncService());
        public static HolidaySyncService Instance { get { return _instance.Value; } }

        private const double SourceTimeoutSeconds = 6.0;
        private static readonly HttpClient _http = CreateClient();
        private static readonly JavaScriptSerializer _ser = new JavaScriptSerializer();

        private readonly object _lock = new object();
        private CSharpBridge _bridge;
        private Action<string> _postMessage;
        private int _running; // 单飞标志：同一时刻只允许一轮同步，避免并发读写同一 JSON 互相覆盖

        private DateTime? _lastAutoRunDate;       // 今日是否已自动跑过（进程内去重）
        private bool _autoFailedToday;            // 当日自动模式是否已失败（当日不再自动重试）
        private int _consecutiveFails;            // 连续失败次数（退避用）
        private DateTime? _lastFailAt;            // 最近一次整轮自动失败时间（连续 3 次后拉长到 24h）
        private DateTime? _lastSyncAt;            // 最近一次成功同步时间
        private string _lastError = "";
        private string _lastResult = "";   // v0.1.6.38 最近一轮结果：updated/unchanged/no-data/network/partial
        private readonly Dictionary<int, Dictionary<string, object>> _sourceHealth =
            new Dictionary<int, Dictionary<string, object>>();

        private const string UserAgent = "PiggyCalendar/0.1 (local desktop widget; static JSON only)";

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(SourceTimeoutSeconds) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            c.DefaultRequestHeaders.Accept.Clear();
            return c;
        }

        private HolidaySyncService() { }

        /// <summary>绑定桥实例（读写 remote 分区）与前端事件出口；桥就绪后调用。</summary>
        public void Attach(CSharpBridge bridge, Action<string> postMessage)
        {
            _bridge = bridge;
            _postMessage = postMessage;
        }

        /// <summary>启动：延迟一小段后按状态机静默跑一次；注册后台每日检查定时器。</summary>
        public void Start()
        {
            // 首次自动同步延迟，不阻塞小组件启动渲染（先本地缓存，成功后再刷新）。
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                try { await RunAsync(force: false); }
                catch (Exception ex) { AppLog.Write("[SYNC] 首次自动同步异常：" + ex.Message); }
            });

            // 后台每 30 分钟检查一次：今日尚未自动跑且状态机到达窗口则补跑。
            var timer = new Timer(_ => { try { _ = RunAsync(force: false); } catch (Exception ex) { AppLog.Write("[SYNC] 后台定时检查异常：" + ex.Message); } },
                null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }

        // ==================== 状态机（基准北京时间；本机即 UTC+8） ====================

        /// <summary>季节状态：A 年内静默 / B 公告季等待次年 / C 次年已就绪停止次年检查。</summary>
        public string CurrentState(DateTime now, Dictionary<string, object> remote)
        {
            int y = now.Year, next = y + 1;
            bool nextReady = remote != null && HasYear(remote, next);
            if (now.Month >= 11 && !nextReady) return "B";
            if (now.Month >= 11 && nextReady) return "C";
            return "A";
        }

        /// <summary>按状态机计算本轮需要尝试的年份集合；force 手动触发绕过状态机与就绪判定。</summary>
        private List<int> PlanYears(DateTime now, bool force, Dictionary<string, object> remote)
        {
            var years = new List<int>();
            int y = now.Year, next = y + 1;
            if (force)
            {
                years.Add(y); years.Add(next);
                return years;
            }
            if (now.Month < 11)
            {
                // A 年内静默：仅当前年缺缓存时补当前年，不发次年请求。
                if (!HasYear(remote, y)) years.Add(y);
            }
            else
            {
                if (!HasYear(remote, y)) years.Add(y);
                if (!HasYear(remote, next)) years.Add(next); // B：次年未就绪才检查；C：已就绪不再加入。
            }
            return years;
        }

        private static bool HasYear(Dictionary<string, object> remote, int year)
        {
            if (remote == null) return false;
            if (!remote.ContainsKey("years")) return false;
            var years = remote["years"] as Dictionary<string, object>;
            return years != null && years.ContainsKey(year.ToString());
        }

        // ==================== 清单加载（在线预留，失败回退内置兜底） ====================

        /// <summary>
        /// 加载同步源清单：v0.1.6.15 起不再拉取自建在线清单；v0.1.6.38 改为读 AppConfig 的 4 槽配置，
        /// 让用户能改地址、单独启停第 4 个自建槽。地址含 {year}，请求时替换。
        /// </summary>
        private Task<SyncManifest> LoadManifestAsync()
        {
            var m = new SyncManifest { Version = 3, UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss+08:00") };
            for (int i = 0; i < 4; i++)
            {
                string url = AppConfig.SyncSourceUrl[i] ?? "";
                if (string.IsNullOrEmpty(url)) continue;
                bool enabled = AppConfig.SyncSourceEnabled[i];
                bool timor = url.IndexOf("timor.tech", StringComparison.OrdinalIgnoreCase) >= 0;
                m.Sources.Add(new SyncSource
                {
                    Id = "src-" + (i + 1),
                    Type = timor ? "timor-yearly" : "chinese-days-yearly",
                    Priority = i + 1,
                    Enabled = enabled,
                    UrlTemplate = url,
                    Format = timor ? "timor-v1" : "chinese-days-v1"
                });
            }
            return Task.FromResult(m);
        }

        /// <summary>
        /// v0.1.6.38 测试单个数据源连通性：对指定槽地址发一次 GET（{year} 用当前年），
        /// 返回 {ok,code,ms,hasData,msg}。不写缓存、不影响正式同步。
        /// </summary>
        public string TestSource(int slot)
        {
            var root = new Dictionary<string, object>();
            try
            {
                if (slot < 0 || slot > 3) return "{\"ok\":false,\"msg\":\"源槽位无效\"}";
                string url = AppConfig.SyncSourceUrl[slot] ?? "";
                if (string.IsNullOrEmpty(url)) return "{\"ok\":false,\"msg\":\"地址为空，先填写地址\"}";
                if (url.IndexOf("{year}", StringComparison.Ordinal) < 0) url = url.Replace("{year}", "");
                int year = DateTime.Now.Year;
                string real = url.Replace("{year}", year.ToString());

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var req = new HttpRequestMessage(HttpMethod.Get, real))
                using (var resp = _http.SendAsync(req).GetAwaiter().GetResult())
                {
                    sw.Stop();
                    int code = (int)resp.StatusCode;
                    root["code"] = code;
                    root["ms"] = (int)sw.ElapsedMilliseconds;
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        root["ok"] = true;
                        root["hasData"] = false;
                        root["msg"] = "已连通，但该源暂无 " + year + " 年数据（404）";
                        return _ser.Serialize(root);
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        root["ok"] = false;
                        root["hasData"] = false;
                        root["msg"] = "HTTP " + code + "，源暂不可用";
                        return _ser.Serialize(root);
                    }
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    object parsed = null;
                    try { parsed = _ser.DeserializeObject(body); } catch { parsed = null; }
                    if (parsed == null)
                    {
                        root["ok"] = false;
                        root["hasData"] = false;
                        root["msg"] = "已连通，但返回内容不是可解析的 JSON";
                        return _ser.Serialize(root);
                    }
                    bool timor = real.IndexOf("timor.tech", StringComparison.OrdinalIgnoreCase) >= 0;
                    var result = Adapters.Convert(timor ? "timor-v1" : "chinese-days-v1", parsed);
                    root["ok"] = true;
                    root["hasData"] = result.Ok;
                    root["msg"] = result.Ok
                        ? ("已连通，" + year + " 年数据可解析（" + result.Batches.Count + " 个批次）")
                        : ("已连通，但 " + year + " 年数据不可用：" + result.Error);
                    return _ser.Serialize(root);
                }
            }
            catch (Exception ex)
            {
                root["ok"] = false;
                root["hasData"] = false;
                root["msg"] = "无法连接：" + ex.Message;
                return _ser.Serialize(root);
            }
        }

        // ==================== 同步主流程 ====================

        /// <summary>执行一轮同步；force=true 为手动立即同步，绕过状态机、今日去重与当日失败退避。</summary>
        public async Task RunAsync(bool force)
        {
            if (_bridge == null) { AppLog.Write("[SYNC] 桥未绑定，跳过同步"); return; }
            if (!AppConfig.SyncEnabled && !force) { AppLog.Write("[SYNC] 同步开关关闭，跳过自动同步"); return; }

            // 单飞：上一轮尚未结束（首次任务 / 30 分钟定时器 / 手动触发重叠）则本轮直接返回，避免并发覆盖。
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                AppLog.Write("[SYNC] 已有一轮同步进行中，本次触发忽略");
                return;
            }

            try
            {
                await RunCoreAsync(force);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task RunCoreAsync(bool force)
        {
            lock (_lock)
            {
                if (!force)
                {
                    DateTime today = DateTime.Now.Date;
                    if (_lastAutoRunDate == today) return;            // 今日已自动跑过
                    if (_autoFailedToday) return;                     // 当日自动已失败，退避到下次启动
                    // 连续失败 3 次后进入 24h 冷却期，期间自动模式不再重试；手动 SyncNow 不受限制。
                    if (_consecutiveFails >= 3 && _lastFailAt.HasValue &&
                        (DateTime.Now - _lastFailAt.Value) < TimeSpan.FromHours(24))
                    {
                        AppLog.Write("[SYNC] 连续失败退避中（不足 24h），跳过自动同步");
                        return;
                    }
                }
            }

            Dictionary<string, object> data;
            try { data = _bridge.ReadDataForSync(); }
            catch (Exception ex) { AppLog.Write("[SYNC] 读取本地数据失败：" + ex.Message); return; }

            Dictionary<string, object> remote = null;
            if (data != null && data.ContainsKey("remote")) remote = data["remote"] as Dictionary<string, object>;
            var now = DateTime.Now;
            var years = PlanYears(now, force, remote);
            if (years.Count == 0) { AppLog.Write("[SYNC] 状态机判定本轮无需请求（年内已就绪）"); return; }

            SyncManifest manifest;
            try { manifest = await LoadManifestAsync(); }
            catch (Exception ex) { AppLog.Write("[SYNC] 清单加载异常：" + ex.Message); return; }

            int successYears = 0, failedYears = 0;
            bool anyUpdated = false, anyNoData = false, anyNetwork = false;
            foreach (int year in years)
            {
                string outcome = await SyncYear(manifest, year, remote);
                if (outcome == "updated" || outcome == "unchanged") successYears++; else failedYears++;
                if (outcome == "updated") anyUpdated = true;
                else if (outcome == "no-data") anyNoData = true;
                else if (outcome == "network") anyNetwork = true;
            }

            lock (_lock)
            {
                _lastAutoRunDate = now.Date;
                // v0.1.6.38 区分「源连通但无该年数据」与「源连不上」，供前端给出准确文案
                if (anyUpdated) _lastResult = failedYears > 0 ? "partial" : "updated";
                else if (anyNetwork) _lastResult = "network";
                else if (anyNoData) _lastResult = "no-data";
                else _lastResult = "unchanged";

                if (failedYears > 0 && successYears == 0)
                {
                    _autoFailedToday = !force;
                    _consecutiveFails++;
                    _lastFailAt = now;
                    _lastError = (_lastResult == "no-data") ? "在线源已连通，但该年份数据暂无更新" : "在线源无法连接，已保留本地缓存";
                }
                else
                {
                    _autoFailedToday = false;
                    _consecutiveFails = 0;
                    _lastFailAt = null;
                    _lastError = "";
                    if (anyUpdated) _lastSyncAt = now;
                }
            }

            try { _bridge.WriteDataForSync(data); }
            catch (Exception ex) { AppLog.Write("[SYNC] 落盘失败：" + ex.Message); }

            PushEvent(remote, successYears, failedYears);
            AppLog.Write("[SYNC] 本轮结束：成功年份 " + successYears + "，失败年份 " + failedYears +
                "，状态=" + CurrentState(now, remote));
        }

        /// <summary>同步单个年份：按 priority 遍历 enabled 源，首个合法即停；返回本轮结果类型（updated/unchanged/no-data/network）。</summary>
        private async Task<string> SyncYear(SyncManifest manifest, int year, Dictionary<string, object> remote)
        {
            bool triedAny = false, sawNoData = false, sawNetwork = false;

            var sources = new List<SyncSource>(manifest.Sources);
            sources.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            Dictionary<string, object> years = null;
            if (remote.ContainsKey("years")) years = remote["years"] as Dictionary<string, object>;
            if (years == null) { years = new Dictionary<string, object>(); remote["years"] = years; }

            foreach (var src in sources)
            {
                if (!src.Enabled || string.IsNullOrEmpty(src.UrlTemplate)) continue;
                triedAny = true;
                string url = src.UrlTemplate.Replace("{year}", year.ToString());

                // 条件请求：携带上一轮该年（兜底到顶层）ETag；304 表示未变化，保留旧缓存。
                string cachedEtag = "";
                if (years.ContainsKey(year.ToString()))
                {
                    var old = years[year.ToString()] as Dictionary<string, object>;
                    if (old != null && old.ContainsKey("etag")) cachedEtag = old["etag"].ToString();
                }
                if (string.IsNullOrEmpty(cachedEtag) && remote.ContainsKey("etag"))
                    cachedEtag = remote["etag"].ToString();

                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!string.IsNullOrEmpty(cachedEtag))
                            req.Headers.TryAddWithoutValidation("If-None-Match", cachedEtag);

                        using (var resp = await _http.SendAsync(req))
                        {
                            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                RecordHealth(year, src.Id, "404");
                                sawNoData = true;
                                AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " 404，尝试下一源");
                                continue; // 该年暂无数据，静默跳过本源
                            }
                            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                            {
                                RecordHealth(year, src.Id, "304");
                                AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " 304 未变化，保留旧缓存");

                                return "unchanged";
                            }
                            if (!resp.IsSuccessStatusCode)
                            {
                                RecordHealth(year, src.Id, ((int)resp.StatusCode).ToString());
                                sawNetwork = true;
                                AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " HTTP " + (int)resp.StatusCode);
                                continue;
                            }

                            string body = await resp.Content.ReadAsStringAsync();
                            object root;
                            try { root = _ser.DeserializeObject(body); }
                            catch (Exception ex) { RecordHealth(year, src.Id, "bad-json"); AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " JSON 解析失败：" + ex.Message); continue; }

                            var result = Adapters.Convert(src.Format, root);
                            if (!result.Ok) { RecordHealth(year, src.Id, "validate-fail"); AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " 校验未通过：" + result.Error); continue; }

                            string etag = resp.Headers.ETag != null ? resp.Headers.ETag.Tag : "";

                            // 成功：整批替换该年 remote 缓存；旧缓存仅在成功后才被覆盖。
                            var yearEntry = new Dictionary<string, object>
                            {
                                ["fetchedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss+08:00"),
                                ["sourceId"] = src.Id,
                                ["etag"] = etag,
                                ["batches"] = result.Batches
                            };
                            years[year.ToString()] = yearEntry;

                            remote["sourceId"] = src.Id;
                            remote["fetchedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss+08:00");
                            remote["sourceVersion"] = year.ToString();
                            remote["etag"] = etag;

                            RecordHealth(year, src.Id, "ok");
                            AppLog.Write("[SYNC] " + year + " 采用源 " + src.Id + "，批次 " + result.Batches.Count + " 个");

                            return "updated";
                        }
                    }
                }
                catch (Exception ex)
                {
                    RecordHealth(year, src.Id, "error");
                    sawNetwork = true;
                    AppLog.Write("[SYNC] " + year + " 源 " + src.Id + " 请求异常：" + ex.Message);
                }
            }
            if (!triedAny) { AppLog.Write("[SYNC] " + year + " 未启用任何在线源，跳过"); return "network"; }
            string outcome = (sawNetwork || !sawNoData) ? "network" : "no-data";
            AppLog.Write("[SYNC] " + year + " 全部源失败（" + outcome + "），保留旧缓存");
            return outcome;
        }

        private void RecordHealth(int year, string sourceId, string status)
        {
            lock (_lock)
            {
                if (!_sourceHealth.ContainsKey(year)) _sourceHealth[year] = new Dictionary<string, object>();
                _sourceHealth[year][sourceId] = status;
            }
        }

        private void PushEvent(Dictionary<string, object> remote, int successYears, int failedYears)
        {
            if (_postMessage == null) return;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["state"] = CurrentState(DateTime.Now, remote),
                    ["successYears"] = successYears,
                    ["failedYears"] = failedYears,
                    ["lastSyncAt"] = _lastSyncAt.HasValue ? _lastSyncAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                    ["lastError"] = _lastError,
                    ["lastResult"] = _lastResult
                };
                var msg = new Dictionary<string, object> { ["kind"] = "sync-updated", ["payload"] = payload };
                _postMessage(_ser.Serialize(msg));
            }
            catch { }
        }

        // ==================== 状态查询（供 M0-10 管理页桥调用） ====================

        /// <summary>汇总同步状态 JSON：开关、状态、各年缓存、最近成功/失败、各源健康。</summary>
        public string GetStatus()
        {
            var root = new Dictionary<string, object>();
            Dictionary<string, object> data = null;
            try { data = _bridge != null ? _bridge.ReadDataForSync() : null; } catch { }
            Dictionary<string, object> remote = null;
            if (data != null && data.ContainsKey("remote")) remote = data["remote"] as Dictionary<string, object>;

            root["enabled"] = AppConfig.SyncEnabled;
            root["state"] = CurrentState(DateTime.Now, remote);
            root["lastSyncAt"] = _lastSyncAt.HasValue ? _lastSyncAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
            root["lastError"] = _lastError;
            root["lastResult"] = _lastResult;
            root["consecutiveFails"] = _consecutiveFails;
            root["currentYear"] = DateTime.Now.Year;
            root["nextYear"] = DateTime.Now.Year + 1;
            root["nextYearReady"] = remote != null && HasYear(remote, DateTime.Now.Year + 1);

            var yearsInfo = new Dictionary<string, object>();
            if (remote != null && remote.ContainsKey("years"))
            {
                var years = remote["years"] as Dictionary<string, object>;
                if (years != null)
                    foreach (var kv in years)
                    {
                        var entry = kv.Value as Dictionary<string, object>;
                        int count = 0;
                        string fetchedAt = "", sourceId = "";
                        if (entry != null)
                        {
                            fetchedAt = entry.ContainsKey("fetchedAt") ? entry["fetchedAt"].ToString() : "";
                            sourceId = entry.ContainsKey("sourceId") ? entry["sourceId"].ToString() : "";
                            var b = entry["batches"] as System.Collections.IList;
                            if (b != null) count = b.Count;
                        }
                        yearsInfo[kv.Key] = new Dictionary<string, object>
                        {
                            ["fetchedAt"] = fetchedAt, ["sourceId"] = sourceId, ["batches"] = count
                        };
                    }
            }
            root["years"] = yearsInfo;

            var health = new Dictionary<string, object>();
            lock (_lock)
            {
                foreach (var y in _sourceHealth) health[y.Key.ToString()] = y.Value;
            }
            root["health"] = health;
            return _ser.Serialize(root);
        }

        /// <summary>手动立即同步：绕过状态机、今日去重与当日失败退避。</summary>
        public void TriggerManualSync()
        {
            Task.Run(async () => { try { await RunAsync(force: true); } catch (Exception ex) { AppLog.Write("[SYNC] 手动同步异常：" + ex.Message); } });
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d.ContainsKey(key) && d[key] != null ? d[key].ToString() : "";
        }
    }
}

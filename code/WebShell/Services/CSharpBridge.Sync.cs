using System;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// CSharpBridge partial：在线节假日同步桥（M0-8c）。状态查询 / 手动立即同步 / 开关。
    /// 管理页 UI 归 M0-10；本轮先把桥方法与引擎接通，供后续界面直接调用。
    /// </summary>
    public partial class CSharpBridge
    {
        /// <summary>同步状态 JSON（enabled/state/各年缓存/最近成功失败/各源健康）。</summary>
        public string GetSyncStatus()
        {
            try { return HolidaySync.HolidaySyncService.Instance.GetStatus(); }
            catch (Exception ex) { return "{\"enabled\":false,\"state\":\"A\",\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        /// <summary>手动立即同步：异步执行，绕过状态机、今日去重与当日失败退避；立即返回不阻塞前端。</summary>
        public string SyncNow()
        {
            try
            {
                HolidaySync.HolidaySyncService.Instance.TriggerManualSync();
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>同步开关；写入 config.ini。</summary>
        public string SetSyncEnabled(bool enabled)
        {
            try
            {
                AppConfig.SyncEnabled = enabled;
                AppConfig.Save();
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>读取同步开关。</summary>
        public bool GetSyncEnabled() => AppConfig.SyncEnabled;

        // ==================== v0.1.6.38 在线数据源 4 槽 ====================

        /// <summary>读取 4 个源槽（地址 / 启用 / 出厂默认地址），JSON 数组。</summary>
        public string GetSyncSources()
        {
            try
            {
                var list = new System.Collections.Generic.List<object>();
                for (int i = 0; i < 4; i++)
                {
                    var item = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["slot"] = i,
                        ["url"] = AppConfig.SyncSourceUrl[i] ?? "",
                        ["enabled"] = AppConfig.SyncSourceEnabled[i],
                        ["defaultUrl"] = AppConfig.DefaultSyncSourceUrl[i] ?? ""
                    };
                    list.Add(item);
                }
                return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(list);
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>保存某个源槽的地址与启用状态（写 config.ini）。</summary>
        public string SetSyncSource(int slot, string url, bool enabled)
        {
            try
            {
                if (slot < 0 || slot > 3) return "err:槽位无效";
                AppConfig.SyncSourceUrl[slot] = (url ?? "").Trim();
                AppConfig.SyncSourceEnabled[slot] = enabled;
                AppConfig.Save();
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>恢复 4 个源槽为出厂默认（第 4 槽仍为空且不启用）。</summary>
        public string ResetSyncSources()
        {
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    AppConfig.SyncSourceUrl[i] = AppConfig.DefaultSyncSourceUrl[i];
                    AppConfig.SyncSourceEnabled[i] = AppConfig.DefaultSyncSourceEnabled[i];
                }
                AppConfig.Save();
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.Message; }
        }

        /// <summary>测试单个源槽连通性（不写缓存）。</summary>
        public string TestSyncSource(int slot)
        {
            try { return HolidaySync.HolidaySyncService.Instance.TestSource(slot); }
            catch (Exception ex) { return "{\"ok\":false,\"msg\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }
    }
}

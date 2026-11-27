using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 每日语录桥（v1.0.2，partial ⑧）：开关读写 + 语录状态持久化。
    /// 状态文件 运行组件\quote-state.json，字段：
    ///   current  当前显示序号（1—249）
    ///   shown    已显示过的序号集合
    ///   history  向后切换的访问序列（供「向前」回退）
    ///   lastAuto 最后一次自动换日的日期 yyyy-MM-dd（只比日期是否变化，关机多天不补切）
    /// 写入用「临时文件 + 替换」，避免写到一半断电留下半截 JSON。
    /// </summary>
    public partial class CSharpBridge
    {
        /// <summary>每日语录开关（系统设置页）。</summary>
        public bool GetQuoteEnabled() => AppConfig.QuoteEnabled;

        /// <summary>切换每日语录开关：落盘 + 广播 config-changed（前端据此显隐与请求改窗口高度）。</summary>
        public string SetQuoteEnabled(bool enabled)
        {
            try
            {
                AppConfig.QuoteEnabled = enabled;
                AppConfig.Save();
                PostEvent("{\"kind\":\"config-changed\",\"payload\":{\"quoteEnabled\":" + (enabled ? "true" : "false") + "}}");
                AppLog.Write("[QUOTE] 每日语录开关=" + (enabled ? "开" : "关"));
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[QUOTE] 切换开关失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }

        /// <summary>读语录状态；文件不存在/空返回字符串 "null"（前端按首装处理）。</summary>
        public string GetQuoteState()
        {
            try
            {
                string path = AppConfig.QuoteStatePath;
                if (!File.Exists(path)) return "null";
                string text = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrWhiteSpace(text) ? "null" : text;
            }
            catch (Exception ex)
            {
                AppLog.Write("[QUOTE] 读取语录状态失败：" + ex.Message);
                return "null";
            }
        }

        /// <summary>写语录状态（前端 JSON 字符串）。校验能解析且带 current，才落盘。</summary>
        public string SetQuoteState(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "err:empty";
                var ser = new JavaScriptSerializer();
                var obj = ser.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey("current")) return "err:bad-json";

                string path = AppConfig.QuoteStatePath;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return "ok";
            }
            catch (Exception ex)
            {
                AppLog.Write("[QUOTE] 写入语录状态失败：" + ex.Message);
                return "err:" + ex.Message;
            }
        }
    }
}

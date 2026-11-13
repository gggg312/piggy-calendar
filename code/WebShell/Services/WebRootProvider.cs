using System;
using System.IO;
using System.Reflection;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 前端根目录解析（v0.1.6.34）——让单个 EXE 脱离源码目录也能跑。
    /// 顺序：
    /// 1) 磁盘源码/解压目录优先（开发态改前端不必重编）；
    /// 2) 磁盘没有 → 把 EXE 内嵌的前端资源解压到「运行组件\WebUI\v版本号\」，返回该目录（版本隔离，升级自动换新）。
    /// 小组件与设置弹窗共用本解析器，避免两处 LocateWebRoot 各自为政。
    /// </summary>
    internal static class WebRootProvider
    {
        private const string ResourcePrefix = "PiggyCalendar.WebShell.webui.";

        private static readonly object syncRoot = new object();
        private static string cached;
        private static bool resolved;

        /// <summary>资源逻辑名后缀 → 解压后的相对路径（必须与 csproj 的 LogicalName 逐条对应）。</summary>
        private static readonly string[,] Map = new string[,]
        {
            { "index.html",               "index.html" },
            { "panel.html",               "panel.html" },
            { "bridge.js",                "bridge.js" },
            { "lib.vue.global.prod.js",   "lib\\vue.global.prod.js" },
            { "lib.element-plus.min.js",  "lib\\element-plus.min.js" },
            { "lib.element-plus.css",     "lib\\element-plus.css" },
            { "lib.icons.iife.min.js",    "lib\\icons.iife.min.js" },
            { "vendor.localforage.min.js","vendor\\localforage.min.js" },
            { "vendor.lunar.min.js",      "vendor\\lunar.min.js" },
            { "modules.lunar-service.js", "modules\\lunar-service.js" },
            { "modules.sample-holidays-2026.js", "modules\\sample-holidays-2026.js" },
            { "modules.sample-holidays-2027.js", "modules\\sample-holidays-2027.js" },
            { "modules.manual-store.js",  "modules\\manual-store.js" },
            { "modules.notice-parser.js", "modules\\notice-parser.js" },
            { "modules.date-model.js",    "modules\\date-model.js" },
            { "modules.panel-store.js",   "modules\\panel-store.js" },
            { "assets.app-icon.png",      "assets\\app-icon.png" },
            { "assets.donate.jpg",        "assets\\赞赏码_small.jpg" }
        };

        public static string Resolve()
        {
            if (resolved) return cached;
            lock (syncRoot)
            {
                if (resolved) return cached;
                resolved = true;
                try
                {
                    string disk = FindOnDisk();
                    if (disk != null)
                    {
                        cached = disk;
                        AppLog.Write("[WEBROOT] 磁盘前端目录：" + disk);
                        return cached;
                    }
                    string packed = ExtractEmbedded();
                    if (packed != null)
                    {
                        cached = packed;
                        AppLog.Write("[WEBROOT] 已解压内嵌前端：" + packed);
                    }
                    else
                    {
                        AppLog.Write("[WEBROOT] 内嵌前端解压失败，前端无法加载");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("[WEBROOT] 解析异常：" + ex.Message);
                }
                return cached;
            }
        }

        /// <summary>磁盘候选路径（保持历史顺序：源码目录 &gt; EXE 同级 &gt; 上两级）。</summary>
        private static string FindOnDisk()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\WebUI\prototype")),
                Path.GetFullPath(Path.Combine(baseDir, @"WebUI\prototype")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\WebUI\prototype"))
            };
            foreach (string c in candidates)
            {
                try
                {
                    if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html"))) return c;
                }
                catch { }
            }
            return null;
        }

        private static string ExtractEmbedded()
        {
            string version = "0.0.0.0";
            try { version = typeof(WebRootProvider).Assembly.GetName().Version.ToString(4); }
            catch { }

            string dir = Path.Combine(AppConfig.RuntimeDir, "WebUI", "v" + version);
            string indexPath = Path.Combine(dir, "index.html");
            string panelPath = Path.Combine(dir, "panel.html");

            // 同版本已解压且关键文件到位 → 直接复用（不每次启动重写，避免无谓 IO）
            try
            {
                if (File.Exists(indexPath) && File.Exists(panelPath) && new FileInfo(indexPath).Length > 0) return dir;
            }
            catch { }

            // 只清理自己这本版目录，再重建
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
            Directory.CreateDirectory(dir);

            int ok = 0;
            int miss = 0;
            for (int i = 0; i < Map.GetLength(0); i++)
            {
                string suffix = Map[i, 0];
                string rel = Map[i, 1];
                byte[] data = ReadResource(ResourcePrefix + suffix);
                if (data == null || data.Length == 0)
                {
                    miss++;
                    AppLog.Write("[WEBROOT] 缺嵌入资源：" + suffix);
                    continue;
                }
                try
                {
                    string target = Path.Combine(dir, rel);
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    File.WriteAllBytes(target, data);
                    ok++;
                }
                catch (Exception ex)
                {
                    miss++;
                    AppLog.Write("[WEBROOT] 写出失败：" + rel + " — " + ex.Message);
                }
            }
            AppLog.Write("[WEBROOT] 解压完成 ok=" + ok + " miss=" + miss + " 目标=" + dir);

            if (!File.Exists(indexPath) || !File.Exists(panelPath)) return null;
            return dir;
        }

        private static byte[] ReadResource(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    byte[] buffer = new byte[stream.Length];
                    int offset = 0;
                    while (offset < buffer.Length)
                    {
                        int read = stream.Read(buffer, offset, buffer.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    return offset == buffer.Length ? buffer : null;
                }
            }
            catch { return null; }
        }
    }
}

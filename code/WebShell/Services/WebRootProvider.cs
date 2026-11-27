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
            { "modules.quotes-data.js",   "modules\\quotes-data.js" },
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
                        // 中性表述：ExtractEmbedded 可能"比对一致直接复用"，也可能真解压了一遍，
                        // 细节由它内部的「跳过解压」/「解压完成 ok=」两行给出，这里不再断言"已解压"。
                        AppLog.Write("[WEBROOT] 前端目录就绪：" + packed);
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

            // v1.0.6：不再"目录存在即复用"。
            // 版本号被要求保持 1.0.0.0 不变，而同版本目录名就等于版本号 —— 只要 index.html/panel.html
            // 还在那儿，改了前端也会一直吃旧文件（表现为：EXE 换成新版，界面却还是老的一套）。
            // 改成逐字节比对：与内嵌资源完全一致才跳过，任何一处不一致就重写。
            bool upToDate = IsUpToDate(dir);
            if (upToDate)
            {
                AppLog.Write("[WEBROOT] 前端与内嵌资源一致，跳过解压：" + dir);
                return dir;
            }

            // 清理旧目录（失败也无妨，下面逐文件覆盖写）
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

        /// <summary>
        /// v1.0.6：磁盘目录与内嵌资源逐字节比对。全部一致才认为是最新，任一文件缺失/大小不符/内容不同都算过期。
        /// 只在启动时跑一次（Resolve 有缓存），18 个文件几百 KB，开销可忽略。
        /// </summary>
        private static bool IsUpToDate(string dir)
        {
            try
            {
                for (int i = 0; i < Map.GetLength(0); i++)
                {
                    string target = Path.Combine(dir, Map[i, 1]);
                    byte[] data = ReadResource(ResourcePrefix + Map[i, 0]);
                    if (data == null || data.Length == 0) return false;   // 资源缺失，交给下面报 miss
                    if (!IsSameBytes(target, data)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsSameBytes(string path, byte[] data)
        {
            try
            {
                if (!File.Exists(path)) return false;
                FileInfo fi = new FileInfo(path);
                if (fi.Length != data.Length) return false;
                byte[] cur = File.ReadAllBytes(path);
                if (cur.Length != data.Length) return false;
                for (int i = 0; i < data.Length; i++)
                {
                    if (cur[i] != data[i]) return false;
                }
                return true;
            }
            catch { return false; }
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

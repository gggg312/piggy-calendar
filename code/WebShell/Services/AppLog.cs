using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 统一运行日志（EXE 同目录）。v0.1.6.32 起改为「按天归档 + 当天累积」：
    /// - app_log.log = 当天日志，当天内每次启动只追加一行「本次运行」段头，历史不再被丢弃；
    /// - 跨天（启动时或运行中跨过午夜）把 app_log.log 改名为 app_log_yyyyMMdd.log（日期取文件最后写入日）；
    /// - 归档保留 7 天，更早的自动清理。
    /// 线程安全，单行清洗（控制字符转空格、2000 字符截断）。
    /// </summary>
    public static class AppLog
    {
        public const string FileName = "app_log.log";
        private const string ArchivePrefix = "app_log_";
        private const int KeepDays = 7;

        private static readonly object _lock = new object();
        private static string _path = "";
        private static DateTime _day = DateTime.Today;   // 当前 app_log.log 所属的日期

        public static string LogPath
        {
            get
            {
                lock (_lock)
                {
                    if (string.IsNullOrEmpty(_path))
                        _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
                    return _path;
                }
            }
        }

        /// <summary>启动时尽早调用：跨天则先归档昨天的日志，再追加本次运行段头。</summary>
        public static void Init()
        {
            lock (_lock)
            {
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    _path = Path.Combine(dir, FileName);
                    _day = DateTime.Today;
                    ArchiveIfNeeded(dir);
                    AppendLine(Header("本次运行", DateTime.Now));
                    CleanupArchives(dir);
                }
                catch { }
            }
        }

        /// <summary>追加一行日志（线程安全；FileShare.ReadWrite 防并发写丢行；跨天自动切分）。</summary>
        public static void Write(string msg)
        {
            lock (_lock)
            {
                try
                {
                    if (DateTime.Today != _day)
                    {
                        string dir = AppDomain.CurrentDomain.BaseDirectory;
                        try { ArchiveIfNeeded(dir); } catch { }
                        _day = DateTime.Today;
                        try { AppendLine(Header("跨天续写", DateTime.Now)); } catch { }
                        try { CleanupArchives(dir); } catch { }
                    }
                    string s = SanitizeForLog(msg);
                    if (s.Length > 2000) s = s.Substring(0, 2000) + "…[已截断]";
                    AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s);
                }
                catch { }
            }
        }

        private static string Header(string kind, DateTime when)
        {
            // 形如：===== 本次运行 2026-11-05 16:00:06 =====
            return "===== " + kind + " " + when.ToString("yyyy-MM-dd HH:mm:ss") + " =====";
        }

        private static void AppendLine(string line)
        {
            using (var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.Write(line + Environment.NewLine);
            }
        }

        /// <summary>若 app_log.log 的最后写入日不是今天，则按那天日期归档；当天文件不动（继续累积）。</summary>
        private static void ArchiveIfNeeded(string dir)
        {
            if (!File.Exists(_path)) return;
            DateTime last = File.GetLastWriteTime(_path);
            if (last.Date >= DateTime.Today) return;
            string arch = Path.Combine(dir, ArchivePrefix + last.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            try { if (File.Exists(arch)) File.Delete(arch); } catch { }
            /* v0.1.6.92：归档失败不该影响日志初始化（文件被别的进程占用时 Move 会抛） */
            try { File.Move(_path, arch); } catch { }
        }

        /// <summary>清理超过 KeepDays 天的归档日志（app_log_yyyyMMdd.log）。</summary>
        private static void CleanupArchives(string dir)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, ArchivePrefix + "*.log"); } catch { return; }
            foreach (var f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string ds = name.Substring(name.Length - 8);
                DateTime d;
                if (!DateTime.TryParseExact(ds, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                if ((DateTime.Today - d.Date).TotalDays > KeepDays)
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        private static string SanitizeForLog(string msg)
        {
            string s = msg ?? "";
            if (s.Length == 0) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            return sb.ToString();
        }
    }
}

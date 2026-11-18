using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// v1.0.7 运行状态标记（EXE 同目录「运行状态.running」）。
    ///
    /// 解决的问题：进程"无声消失"时——崩溃、被安全软件结束、注销/关机、任务管理器强杀——
    /// 都不会走 App.OnExit，所以日志里连一行退出记录都没有，事后完全无从判断是"崩了"还是
    /// "根本没在跑"。这个标记文件只在正常退出时被删除：
    ///   · 启动时先检查：标记还在 → 上一次没走完退出流程，把上次的 PID / 启动时间 / 存活时长写进日志；
    ///   · 启动完成后重新写入本次标记；
    ///   · 托盘退出 / 窗口关闭 / OnExit / SessionEnding / ProcessExit 任一路径正常退出都删掉它。
    /// 这样"下次启动时日志里是否出现『上次未正常退出』"本身就是判据，不必等用户复现。
    ///
    /// v1.0.8 修正一处会误导判断的缺陷：
    /// 旧版存活时长按「本次启动时间 − 上次启动时间」算，等于把"已经死了但没人启动"的那段时间
    /// 也算成活着的。实测 11-15 那次：日志显示"存活 20 小时 44 分"，而按最后一条心跳推，
    /// 实际只活了 12 小时 6 分，多算了 8 小时 38 分。
    /// 现在每次心跳都会刷新标记里的 lastalive 字段，启动时用「lastalive − start」算真实存活；
    /// 拿不到 lastalive（旧版标记）时明确标注为"存活上限"，不再当成事实。
    /// </summary>
    public static class RunMarker
    {
        private const string FileName = "运行状态.running";
        private const string KeyStart = "start=";
        private const string KeyAlive = "lastalive=";
        private const string KeyPid = "pid=";
        private const string KeyVer = "version=";
        private const string KeyExe = "exe=";
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        // 心跳重写要用 start/pid/version/exe，缓存起来免得每次都回读文件。
        // MarkRunning / TouchAlive 都在 UI 线程，但 ClearRunning 可能来自 ProcessExit 线程，加锁统一。
        private static readonly object Gate = new object();
        private static string _startText;
        private static string _pidText;
        private static string _verText;
        private static string _exeText;
        private static bool _aliveWriteErrorLogged;

        private static string MarkerPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>
        /// 启动时最先调用（AppLog.Init 之后、其它初始化之前）：若上次标记仍在，说明上次非正常退出。
        /// 会把上次记录写进日志并保留成 .prev 便于对照，返回 true 表示检测到上一轮异常结束。
        /// </summary>
        public static bool CheckPreviousRun()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return false;
                string content;
                try { content = File.ReadAllText(MarkerPath, Encoding.UTF8); }
                catch { content = "(读取失败)"; }

                string pid = Extract(content, KeyPid);
                DateTime start = ParseTime(content, KeyStart);
                DateTime alive = ParseTime(content, KeyAlive);

                string startText = DateTime.MinValue.Equals(start) ? "未知" : start.ToString(TimeFormat);
                string aliveText;
                bool exact = false;

                if (!DateTime.MinValue.Equals(alive) && !DateTime.MinValue.Equals(start) && alive >= start)
                {
                    // 有 lastalive：这就是"确认活到过的最后一刻"，死亡时间落在它与下一次心跳之间。
                    TimeSpan span = alive - start;
                    aliveText = "，最后活动=" + alive.ToString(TimeFormat) + "，存活 " + FormatSpan(span);
                    exact = true;
                }
                else
                {
                    // 旧版标记没有 lastalive，只能用"现在"反推，是上限不是事实。
                    if (!DateTime.MinValue.Equals(start))
                    {
                        TimeSpan span = DateTime.Now - start;
                        if (span.TotalSeconds > 0)
                        {
                            aliveText = "，无最后活动记录（标记由旧版写入），存活上限 " + FormatSpan(span);
                        }
                        else aliveText = "";
                    }
                    else aliveText = "";
                }

                string nowPid = Process.GetCurrentProcess().Id.ToString();
                string dirText = "";
                try
                {
                    string exe = Extract(content, KeyExe);
                    if (!string.IsNullOrEmpty(exe) && exe != "?") dirText = "，目录=" + exe;
                }
                catch { }

                // 上次记录里的 PID 恰好等于本次 PID：说明标记是上一次进程留下的（PID 被复用），照常按异常退出处理。
                AppLog.Write("[APP] ===== 上次运行未正常退出（退出流程未执行，无退出日志）=====");
                AppLog.Write("[APP] 上次记录：pid=" + pid + " 启动时间=" + startText + aliveText + dirText);
                if (exact)
                {
                    AppLog.Write("[APP] 最后活动时间即最后一次心跳/写日志的时刻；进程在它之后、下一次心跳之前被结束，"
                        + "真实死亡时间落在其后的一个心跳间隔内（当前 5 分钟）");
                }
                else
                {
                    AppLog.Write("[APP] 该存活时长是上限，不是实际值：真实的死亡时刻请对照上次日志文件的最后一行时间");
                }
                AppLog.Write("[APP] 本次进程 pid=" + nowPid + "；如上次为崩溃/被结束/注销关机，本条即为唯一线索");

                // 留一份上次记录，便于连续多次异常退出时逐轮对照
                try
                {
                    string prev = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName + ".prev");
                    File.Delete(prev);
                    File.Move(MarkerPath, prev);
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { AppLog.Write("[APP] 检查上次运行标记失败：" + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>启动流程走完后写入本次标记，并缓存各字段供心跳刷新使用。</summary>
        public static void MarkRunning()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                string ver = "";
                try { ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { }
                string start = DateTime.Now.ToString(TimeFormat);

                lock (Gate)
                {
                    _startText = start;
                    _pidText = p.Id.ToString();
                    _verText = ver;
                    _exeText = AppDomain.CurrentDomain.BaseDirectory ?? "";
                    WriteMarker(start, start);
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Write("[APP] 写入运行标记失败：" + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 心跳调用：只刷新标记里的最后活动时间，不动启动时间。
        /// 这是"进程确认活到此刻"的唯一持久证据——进程被强杀时来不及写任何东西，
        /// 但几分钟前的心跳已经在磁盘上留下了时间戳。
        /// </summary>
        public static void TouchAlive()
        {
            try
            {
                lock (Gate)
                {
                    if (string.IsNullOrEmpty(_startText)) return;   // 还没 MarkRunning，不该刷新
                    WriteMarker(_startText, DateTime.Now.ToString(TimeFormat));
                }
            }
            catch (Exception ex)
            {
                if (!_aliveWriteErrorLogged)
                {
                    _aliveWriteErrorLogged = true;   // 每 5 分钟一次，失败不重复刷日志
                    try { AppLog.Write("[HB] 刷新运行标记失败（后续同类错误不再重复记录）：" + ex.Message); } catch { }
                }
            }
        }

        /// <summary>正常退出路径调用：删除标记。必须在日志写完后再删，顺序反了会丢掉"正常退出"的证据。</summary>
        public static void ClearRunning()
        {
            try
            {
                lock (Gate)
                {
                    _startText = _pidText = _verText = _exeText = null;
                }
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                    AppLog.Write("[APP] 运行标记已清除（本次为正常退出）");
                }
            }
            catch (Exception ex)
            {
                try { AppLog.Write("[APP] 清除运行标记失败：" + ex.Message); } catch { }
            }
        }

        private static void WriteMarker(string start, string alive)
        {
            var sb = new StringBuilder();
            sb.AppendLine(KeyStart + start);
            sb.AppendLine(KeyAlive + alive);
            sb.AppendLine(KeyPid + (_pidText ?? "?"));
            sb.AppendLine(KeyVer + (_verText ?? ""));
            sb.AppendLine(KeyExe + (_exeText ?? ""));
            File.WriteAllText(MarkerPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static DateTime ParseTime(string content, string key)
        {
            try
            {
                foreach (string line in content.Split('\n'))
                {
                    string t = line.Trim();
                    if (!t.StartsWith(key)) continue;
                    string v = t.Substring(key.Length).Trim();
                    DateTime dt;
                    if (DateTime.TryParseExact(v, TimeFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out dt))
                        return dt;
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        private static string Extract(string content, string key)
        {
            try
            {
                foreach (string line in content.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith(key)) return t.Substring(key.Length).Trim();
                }
            }
            catch { }
            return "?";
        }

        private static string FormatSpan(TimeSpan s)
        {
            if (s.TotalDays >= 1)
                return ((int)s.TotalDays) + " 天 " + s.Hours + " 小时 " + s.Minutes + " 分";
            if (s.TotalHours >= 1)
                return s.Hours + " 小时 " + s.Minutes + " 分";
            return s.Minutes + " 分 " + s.Seconds + " 秒";
        }
    }
}

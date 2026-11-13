using System;
using Microsoft.Win32;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 开机自启服务：HKCU Run 键（当前用户，无需管理员权限）。M0-3 提供基础读写；M0-10 接入设置开关。
    /// v0.1.6.99：写入前比对，路径未变化不再重复写注册表；路径变化时记「旧 → 新」便于追溯。
    /// </summary>
    internal static class AutoStartService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PiggyCalendar";

        private static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        /// <summary>把注册表里的命令行规范化到可比较的形式：去引号、展开变量、统一分隔符与大小写。</summary>
        private static string Normalize(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2).Trim();
            try { s = Environment.ExpandEnvironmentVariables(s); } catch { }
            return s.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>读取当前是否已注册自启（与 AppConfig.AutoStartEnabled 口径一致）。</summary>
        public static bool IsRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[AUTOSTART] 读取失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>注册表里的自启路径是否就是当前运行的这个 EXE。</summary>
        public static bool PointsToThisExe()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    if (v == null) return false;
                    return Normalize(v.ToString()) == Normalize(ExePath);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[AUTOSTART] 读取失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>按开关状态注册/移除开机自启；同步 AppConfig。失败写日志并返回 false。</summary>
        public static bool ApplyAutoStart(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        AppLog.Write("[AUTOSTART] 无法打开 Run 键");
                        return false;
                    }
                    if (enabled)
                    {
                        object cur = key.GetValue(ValueName);
                        // 已经指向当前 EXE 就别再写一次：无意义的启动项写入会被安全软件盯上，
                        // 日志也会在每次启动刷一条看不到变化的信息
                        if (cur != null && Normalize(cur.ToString()) == Normalize(ExePath))
                            return true;

                        // 带引号包裹路径，防止路径含空格被拆分
                        key.SetValue(ValueName, "\"" + ExePath + "\"");
                        if (cur == null)
                            AppLog.Write("[AUTOSTART] 已注册开机自启：" + ExePath);
                        else
                            AppLog.Write("[AUTOSTART] 自启路径已更新：" + cur + " → " + ExePath);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                        {
                            key.DeleteValue(ValueName, false);
                            AppLog.Write("[AUTOSTART] 已移除开机自启");
                        }
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("[AUTOSTART] 写入失败：" + ex.Message);
                return false;
            }
        }
    }
}

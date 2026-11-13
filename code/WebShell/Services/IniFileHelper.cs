using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// 精简 INI 读写（kernel32 PrivateProfile 接口）。供 AppConfig 使用。
    /// </summary>
    internal class IniFileHelper
    {
        private readonly string _iniFilePath;

        [DllImport("kernel32", CharSet = CharSet.Auto)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        [DllImport("kernel32", CharSet = CharSet.Auto)]
        private static extern long GetPrivateProfileString(string section, string key, string strDefault, StringBuilder retVal, int size, string filePath);

        [DllImport("Kernel32.dll", CharSet = CharSet.Auto)]
        public static extern int GetPrivateProfileInt(string section, string key, int nDefault, string filePath);

        public IniFileHelper(string iniFilePath)
        {
            _iniFilePath = iniFilePath ?? throw new ArgumentNullException(nameof(iniFilePath));
        }

        public string GetString(string section, string key, string defaultValue)
        {
            var sb = new StringBuilder(1024);
            GetPrivateProfileString(section, key, defaultValue ?? "", sb, sb.Capacity, _iniFilePath);
            return sb.ToString();
        }

        public int GetInt(string section, string key, int defaultValue)
        {
            return GetPrivateProfileInt(section, key, defaultValue, _iniFilePath);
        }

        public bool GetBool(string section, string key, bool defaultValue)
        {
            string v = GetString(section, key, defaultValue ? "1" : "0").Trim();
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        public bool WriteString(string section, string key, string val)
        {
            return WritePrivateProfileString(section, key, val ?? "", _iniFilePath) != 0;
        }

        public bool WriteInt(string section, string key, int val)
        {
            return WriteString(section, key, val.ToString());
        }

        public bool WriteBool(string section, string key, bool val)
        {
            return WriteString(section, key, val ? "1" : "0");
        }
    }
}

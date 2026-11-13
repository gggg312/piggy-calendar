using System;
using System.IO;
using System.Text;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// v0.1.6.92：用户数据的原子落盘。
    /// 先写同目录下的临时文件，再整体替换目标文件；写到一半被打断（断电 / 强杀进程 / 磁盘满）
    /// 不会留下截断的 JSON —— 原来直接 File.WriteAllText 覆盖，中断后下次启动解析失败会让全部提醒丢失。
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>UTF-8（无 BOM）原子写入。目标目录不存在时自动创建。</summary>
        public static void WriteAllText(string path, string content)
        {
            WriteAllText(path, content, new UTF8Encoding(false));
        }

        public static void WriteAllText(string path, string content, Encoding encoding)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content ?? string.Empty, encoding ?? new UTF8Encoding(false));

            if (File.Exists(path))
            {
                try
                {
                    // NTFS 上 Replace 是原子替换，并保留目标文件的属性与 ACL
                    File.Replace(tmp, path, null, true);
                    return;
                }
                catch (Exception)
                {
                    // 个别文件系统 / 权限场景不支持 Replace，退回「删除 + 改名」；
                    // 这里不吞错，后面 File.Move 失败会真正抛给调用方记日志。
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
            File.Move(tmp, path);
        }

        /// <summary>二进制资源的原子写入（目前用于解包出来的 WebUI 副本）。</summary>
        public static void WriteAllBytes(string path, byte[] data)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data ?? new byte[0]);

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null, true);
                    return;
                }
                catch (Exception)
                {
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
            File.Move(tmp, path);
        }
    }
}

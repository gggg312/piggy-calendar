using System;
using System.IO;
using System.Reflection;

namespace PiggyCalendar.WebShell.Services
{
    /// <summary>
    /// WebView2Loader 引导器：把嵌入资源中对应位数的 WebView2Loader.dll 提取到 EXE 同目录。
    /// Costura 只合并托管程序集，原生 DLL 必须位于 EXE 同目录才能被 WebView2 初始化加载。
    /// 必须在 EnsureCoreWebView2Async 之前调用。
    /// v0.1.6.94：同时内置 x64 与 x86 两份 loader，按当前进程位数选择，单 EXE 兼容 32/64 位系统。
    /// </summary>
    internal static class WebView2LoaderBootstrap
    {
        private const string DllName = "WebView2Loader.dll";
        private const string ResourceX64 = "PiggyCalendar.WebShell.assets.WebView2Loader-x64.dll";
        private const string ResourceX86 = "PiggyCalendar.WebShell.assets.WebView2Loader-x86.dll";

        private static readonly object syncRoot = new object();
        private static bool ready;

        public static void EnsureWebView2LoaderReady()
        {
            if (ready) return;
            lock (syncRoot)
            {
                if (ready) return;
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string dllPath = Path.Combine(baseDir, DllName);
                    // 进程位数决定释放哪一份：64 位进程用 x64 loader，32 位（WOW64）用 x86 loader
                    string resource = IntPtr.Size == 8 ? ResourceX64 : ResourceX86;
                    byte[] data = ReadEmbeddedResource(resource);
                    if (data == null || data.Length == 0) return;

                    bool needWrite = !File.Exists(dllPath);
                    if (!needWrite)
                    {
                        try { needWrite = new FileInfo(dllPath).Length != data.Length; }
                        catch { needWrite = true; }
                    }
                    if (needWrite)
                    {
                        try { File.WriteAllBytes(dllPath, data); }
                        catch { return; }
                    }
                    ready = true;
                }
                catch { }
            }
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
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
    }
}

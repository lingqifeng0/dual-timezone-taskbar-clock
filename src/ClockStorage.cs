using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace NativeTaskbarClock
{
    internal static class ClockStorage
    {
        internal const string ModuleName = "ClockNativeV7.dll";
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder output, uint size, uint flags);
        internal static string Root {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualZoneTaskbarClock"); }
        }
        internal static string ConfigPath { get { return Path.Combine(Root, "config.json"); } }

        internal static void Initialize()
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
            string oldExecutable = Autostart.RegisteredClockExecutable();
            if (!string.IsNullOrEmpty(oldExecutable))
                candidates.Add(Path.Combine(Path.GetDirectoryName(oldExecutable), "config.json"));
            InitializeConfig(Root, candidates);
        }

        // Parameterized for isolated migration tests.
        internal static void InitializeConfig(string root, IEnumerable<string> candidates)
        {
            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, "config.json");
            if (File.Exists(destination)) return;
            foreach (string oldPath in candidates) {
                try {
                    if (!File.Exists(oldPath)) continue;
                    string text = File.ReadAllText(oldPath);
                    var fields = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    if (fields == null || !fields.ContainsKey("top_template") || !fields.ContainsKey("bottom_template")) continue;
                    ClockSettings old = ClockSettings.Load(oldPath);
                    old.Validate();
                    // Show the redesigned panel on the first launch, without modifying the old file.
                    old.FirstRun = true;
                    if (!File.Exists(destination)) old.Save(destination);
                    return;
                } catch (IOException) { if (File.Exists(destination)) return; }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (InvalidDataException) { }
            }
        }

        internal static string ExtractModule()
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ModuleName)) {
                if (input == null) throw new InvalidOperationException("程序内部缺少时钟模块，请重新下载完整的单文件版。");
                using (MemoryStream bytes = new MemoryStream()) { input.CopyTo(bytes); return ExtractModule(Root, bytes.ToArray()); }
            }
        }

        internal static string ExtractModule(string root, byte[] payload)
        {
            string hash = Hash(payload);
            string dir = Path.Combine(root, "modules", hash);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, ModuleName);
            if (File.Exists(target)) {
                VerifyFile(target, hash);
                return PhysicalPath(target);
            }
            string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (FileStream file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    file.Write(payload, 0, payload.Length);
                try { File.Move(temp, target); }
                catch (IOException) { if (!File.Exists(target)) throw; }
                VerifyFile(target, hash);
                return PhysicalPath(target);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // Explorer may run outside an MSIX file-redirection context. Pass the
        // physical path resolved by the open file handle to the other process.
        private static string PhysicalPath(string path)
        {
            using (FileStream stream = File.OpenRead(path)) {
                StringBuilder result = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(stream.SafeFileHandle, result, (uint)result.Capacity, 0);
                if (length == 0 || length >= result.Capacity)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法确定时钟模块的实际路径");
                string real = result.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + real.Substring(8);
                if (real.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return real.Substring(4);
                return real;
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        private static void VerifyFile(string path, string expected)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new InvalidDataException("时钟模块缓存损坏或被修改，无法加载。请删除该缓存目录后重试：" + Path.GetDirectoryName(path));
            }
        }
    }
}

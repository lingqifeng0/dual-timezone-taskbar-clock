using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class PackageTest
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, SetLastError=true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Main(string[] args)
    {
        if (args.Length != 1) throw new Exception("Supply the single executable path");
        string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string scratch = Path.GetFullPath(Path.Combine(root, "package-check-" + Guid.NewGuid().ToString("N")));
        Check(scratch.StartsWith(root, StringComparison.OrdinalIgnoreCase), "test directory containment");
        try {
            Directory.CreateDirectory(scratch);
            string copied = Path.Combine(scratch, "TimeZoneClock.exe");
            File.Copy(args[0], copied);
            Check(Directory.GetFiles(scratch).Length == 1, "only one executable in copied folder");
            Assembly app = Assembly.Load(File.ReadAllBytes(copied));
            byte[] module;
            using (Stream resource = app.GetManifestResourceStream("ClockNativeV7.dll")) {
                Check(resource != null, "embedded module exists");
                using (MemoryStream mem = new MemoryStream()) { resource.CopyTo(mem); module = mem.ToArray(); }
            }
            Check(module.Length > 10000 && module[0] == 'M' && module[1] == 'Z', "embedded module is a PE file");
            Type options = app.GetType("NativeTaskbarClock.ClockOptions", true);
            Check(Marshal.SizeOf(options) == 1296, "controller and native module ABI size");
            Type settingsType = app.GetType("NativeTaskbarClock.ClockSettings", true);
            object settings = Activator.CreateInstance(settingsType, true);
            settingsType.GetField("LocalLabel").SetValue(settings, "");
            settingsType.GetField("ChinaLabel").SetValue(settings, "CN");
            settingsType.GetField("TopTemplate").SetValue(settings, "%label% %time%");
            settingsType.GetField("BottomTemplate").SetValue(settings, "%label% %time%");
            Type controller = app.GetType("NativeTaskbarClock.Controller", true);
            MethodInfo buildOptions = controller.GetMethod("BuildOptions", BindingFlags.Static | BindingFlags.NonPublic);
            object actualOptions = buildOptions.Invoke(null, new[] { settings });
            Check((string)options.GetField("TopTemplate").GetValue(actualOptions) == "%time%" &&
                (string)options.GetField("BottomTemplate").GetValue(actualOptions) == "%label% %time%", "taskbar receives no blank label or implicit space");
            settingsType.GetField("TopIsChina").SetValue(settings, true);
            actualOptions = buildOptions.Invoke(null, new[] { settings });
            Check((string)options.GetField("TopTemplate").GetValue(actualOptions) == "%label% %time%" &&
                (string)options.GetField("BottomTemplate").GetValue(actualOptions) == "%time%", "taskbar normalizes the correct row when zones are reversed");
            Type storage = app.GetType("NativeTaskbarClock.ClockStorage", true);
            MethodInfo extract = storage.GetMethod("ExtractModule", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(byte[]) }, null);
            string modulePath = (string)extract.Invoke(null, new object[] { Path.Combine(scratch, "data"), module });
            using (SHA256 sha = SHA256.Create()) {
                string actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(modulePath)));
                string expected = BitConverter.ToString(sha.ComputeHash(module));
                Check(actual == expected, "extracted module matches embedded resource");
            }
            IntPtr library = LoadLibraryW(modulePath);
            Check(library != IntPtr.Zero, "load extracted module in test process");
            try { Check(GetProcAddress(library, "InstallClockHook") != IntPtr.Zero && GetProcAddress(library, "UninstallClockHook") != IntPtr.Zero, "module exports"); }
            finally { FreeLibrary(library); }
            using (Icon icon = Icon.ExtractAssociatedIcon(copied)) {
                Check(icon != null, "EXE has an icon");
                using (Bitmap bmp = icon.ToBitmap()) Check(bmp.Width >= 16 && bmp.Height >= 16, "icon renders");
            }
        } finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        Console.WriteLine("PASS: copied single EXE, icon, embedded native module and ABI");
    }
}

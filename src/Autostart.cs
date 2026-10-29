using System;
using System.IO;
using Microsoft.Win32;

namespace NativeTaskbarClock
{
    internal static class Autostart
    {
        internal const string ValueName = "TimeZoneClock.Native";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static string Command(string executable)
        {
            if (string.IsNullOrEmpty(executable) || executable.IndexOf('"') >= 0)
                throw new InvalidOperationException("程序路径无效，无法设置开机启动。");
            return "\"" + Path.GetFullPath(executable) + "\" --background";
        }

        internal static bool IsEnabled(string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false)) {
                string value = key == null ? null : key.GetValue(ValueName) as string;
                return Matches(value, executable) || ClockExecutable(value) != null;
            }
        }

        private static bool Matches(string value, string executable)
        {
            return string.Equals(value, Command(executable), StringComparison.OrdinalIgnoreCase);
        }

        private static string ClockExecutable(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '"') return null;
            int end = value.IndexOf('"', 1);
            if (end < 0 || !string.Equals(value.Substring(end + 1).Trim(), "--background", StringComparison.OrdinalIgnoreCase)) return null;
            string path = value.Substring(1, end - 1);
            if (!Path.IsPathRooted(path)) return null;
            string name = Path.GetFileName(path);
            if (!name.StartsWith("TimeZoneClock", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }

        internal static string RegisteredClockExecutable()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return key == null ? null : ClockExecutable(key.GetValue(ValueName) as string);
        }

        internal static void SetEnabled(string executable, bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
                if (key == null) throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
                SetForKey(key, executable, enabled);
            }
        }

        // Parameterized for isolated tests using a temporary registry key.
        internal static void SetForKey(RegistryKey key, string executable, bool enabled)
        {
            object raw = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string existing = raw as string;
            if (enabled) {
                if (raw != null && !Matches(existing, executable) && ClockExecutable(existing) == null)
                    throw new InvalidOperationException("已有另一版本的同名开机启动项。请先关闭或移除旧项，再启用这个版本。");
                key.SetValue(ValueName, Command(executable), RegistryValueKind.String);
            } else if (existing != null && (Matches(existing, executable) || ClockExecutable(existing) != null)) {
                key.DeleteValue(ValueName, false);
            }
        }
    }
}

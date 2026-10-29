using System;
using Microsoft.Win32;
using NativeTaskbarClock;

internal static class AutostartTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Main()
    {
        string path = @"Software\TimeZoneClock-Test-" + Guid.NewGuid().ToString("N");
        string executable = @"C:\test folder\TimeZoneClock.exe";
        try {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path)) {
                Autostart.SetForKey(key, executable, true);
                Check((string)key.GetValue(Autostart.ValueName) == "\"C:\\test folder\\TimeZoneClock.exe\" --background", "set command");
                Autostart.SetForKey(key, executable, false);
                Check(key.GetValue(Autostart.ValueName) == null, "remove own command");
                key.SetValue(Autostart.ValueName, "other application", RegistryValueKind.String);
                bool refused = false;
                try { Autostart.SetForKey(key, executable, true); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "reject conflicting command");
                Autostart.SetForKey(key, executable, false);
                Check((string)key.GetValue(Autostart.ValueName) == "other application", "preserve conflicting command");
            }
        } finally { Registry.CurrentUser.DeleteSubKey(path, false); }
        Console.WriteLine("startup registry tests passed");
    }
}

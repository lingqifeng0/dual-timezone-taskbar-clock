using System;
using System.IO;
using NativeTaskbarClock;

internal static class SettingsTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Main()
    {
        DateTime instant = new DateTime(2026, 10, 28, 15, 30, 0, DateTimeKind.Utc);
        ClockSettings settings = new ClockSettings();
        settings.LocalLabel = "US"; settings.ChinaLabel = "CN";
        settings.TopIsChina = true;
        settings.TopTemplate = "%label% [%date%] %time% %%";
        settings.BottomTemplate = "%label% %time%";
        settings.Validate();
        string[] rows = ClockFormatter.Preview(settings, instant);
        Require(rows[0] == "CN [2026/10/28] 23:30 %", "China format, date or percent template");
        Require(rows[1] == "US " + instant.ToLocalTime().ToString("HH:mm"), "system time or zone ordering");
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-settings-output.json");
        settings.FirstRun = false; settings.Save(path);
        ClockSettings reloaded = ClockSettings.Load(path);
        Require(reloaded.TopIsChina && reloaded.TopTemplate == settings.TopTemplate, "settings persistence");
        settings.LocalLabel = ""; settings.Save(path);
        Require(ClockSettings.Load(path).LocalLabel == "", "atomic replacement / empty label");
        bool rejected = false;
        try { settings.TimeFormat = "HH:mm:ss"; settings.Validate(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "seconds should not be presented as live");
        rejected = false;
        try { settings.TimeFormat = "HH:mm"; settings.TopTemplate = "%foo%"; settings.Validate(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "unknown template variable");
        Console.WriteLine("PASS: China time, dates, labels, order, preview, settings persistence and validation");
    }
}

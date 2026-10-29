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
        settings.TargetZoneId = "Tokyo Standard Time";
        settings.LocalZoneId = "Eastern Standard Time";
        rows = ClockFormatter.Preview(settings, instant);
        Require(rows[0] == "CN [2026/10/29] 00:30 %", "Tokyo timezone or date rollover");
        Require(rows[1] == "US 11:30", "independent first timezone");
        settings.TargetZoneId = "Eastern Standard Time";
        DateTime beforeDst = new DateTime(2026, 3, 8, 6, 59, 0, DateTimeKind.Utc);
        DateTime afterDst = new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc);
        Require(ClockFormatter.Preview(settings, beforeDst)[0].EndsWith("01:59 %"), "before DST");
        Require(ClockFormatter.Preview(settings, afterDst)[0].EndsWith("03:00 %"), "after DST");
        settings.TopTemplate = "%weekday% %offset%";
        settings.Validate();
        Require(ClockFormatter.Preview(settings, beforeDst)[0].EndsWith("UTC-05:00"), "UTC offset before DST");
        Require(ClockFormatter.Preview(settings, afterDst)[0].EndsWith("UTC-04:00"), "UTC offset after DST");
        settings.TopIsChina = false; settings.TargetZoneId = ""; settings.BottomTemplate = "%time%";
        Require(ClockFormatter.Preview(settings, instant)[1] == instant.ToLocalTime().ToString("HH:mm"), "second timezone can follow system");
        settings.TopIsChina = true; settings.TargetZoneId = "Eastern Standard Time"; settings.BottomTemplate = "%label% %time%";
        settings.TopTemplate = "%label% [%date%] %time% %%";
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-settings-output.json");
        settings.FirstRun = false; settings.Save(path);
        ClockSettings reloaded = ClockSettings.Load(path);
        Require(reloaded.TopIsChina && reloaded.TopTemplate == settings.TopTemplate && reloaded.TargetZoneId == "Eastern Standard Time" && reloaded.LocalZoneId == "Eastern Standard Time", "settings persistence");
        settings.LocalLabel = ""; settings.Save(path);
        Require(ClockSettings.Load(path).LocalLabel == "", "atomic replacement / empty label");
        settings.TopIsChina = false; settings.TopTemplate = "%label% %time%";
        Require(ClockFormatter.Preview(settings, instant)[0] == "11:30", "legacy blank label and its space do not appear in preview");
        Require(settings.TopTemplate == "%label% %time%", "preview does not mutate the original settings");
        settings.TimeFormat = "HH:mm"; settings.TopTemplate = "%time%:%second%"; settings.BottomTemplate = "%date%";
        settings.Validate();
        DateTime sampleSecond = new DateTime(2026, 10, 28, 15, 30, 5, DateTimeKind.Utc);
        Require(ClockFormatter.Preview(settings, sampleSecond)[0].EndsWith(":05"), "second module renders two digits");
        Require(settings.RequiresSecondRefresh(), "seconds module needs a faster timer");
        settings.TopTemplate = "%time%%second%";
        Require(settings.RequiresSecondRefresh() && ClockFormatter.Preview(settings, sampleSecond)[0].EndsWith("11:30:05"), "adjacent time and second modules render with colon");
        settings.TopTemplate = "%second%%time%";
        Require(settings.RequiresSecondRefresh() && ClockFormatter.Preview(settings, sampleSecond)[0] == "0511:30", "reordered modules do not add a space");
        settings.TopTemplate = "%%second%% %date%";
        Require(!settings.RequiresSecondRefresh(), "escaped second token remains literal text");
        settings.TopTemplate = "%time%"; settings.TimeFormat = "HH:mm:ss"; settings.Validate();
        Require(settings.RequiresSecondRefresh() && ClockFormatter.Preview(settings, sampleSecond)[0].EndsWith(":05"), "seconds in time format need faster timer");
        settings.TimeFormat = "HH:mm 's'";
        Require(!settings.RequiresSecondRefresh(), "quoted literal s does not need faster timer");
        bool rejected = false;
        try { settings.TimeFormat = "HH:mm"; settings.TopTemplate = "%foo%"; settings.Validate(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "unknown template variable");
        Console.WriteLine("PASS: target time zones, DST, dates, preview, settings persistence and validation");
    }
}

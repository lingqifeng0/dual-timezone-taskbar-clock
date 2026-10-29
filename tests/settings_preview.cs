using System;
using System.Windows.Forms;
using NativeTaskbarClock;

internal static class SettingsPreview
{
    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ClockSettings sample = new ClockSettings();
        sample.LocalLabel = "纽约"; sample.LocalZoneId = "Eastern Standard Time";
        sample.ChinaLabel = "北京"; sample.TargetZoneId = "China Standard Time";
        sample.TopTemplate = "%label% %time%%second%";
        sample.BottomTemplate = "%date% %label% %time%";
        using (SettingsForm form = new SettingsForm(sample, delegate { })) {
            Application.Run(form);
        }
    }
}

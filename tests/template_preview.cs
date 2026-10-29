using System;
using System.Windows.Forms;
using NativeTaskbarClock;

internal static class TemplatePreview
{
    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ClockSettings settings = new ClockSettings();
        settings.LocalLabel = ""; settings.LocalZoneId = "China Standard Time";
        settings.ChinaLabel = "纽约"; settings.TargetZoneId = "Eastern Standard Time";
        settings.TopTemplate = "%time%%second%";
        using (TemplateEditor form = new TemplateEditor(settings, true)) {
            form.ShowInTaskbar = true; form.StartPosition = FormStartPosition.CenterScreen;
            Application.Run(form);
        }
    }
}

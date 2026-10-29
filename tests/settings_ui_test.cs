using System;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NativeTaskbarClock;

internal static class SettingsUiTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        Control[] found = root.Controls.Find(name, true);
        Check(found.Length == 1, "missing " + name);
        return (T)found[0];
    }

    private static void Click(Button button)
    {
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(button, new object[] { EventArgs.Empty });
    }

    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles();
        ClockSettings input = new ClockSettings();
        input.StartOnLogin = true;
        input.OpenOnStart = false;
        ClockSettings saved = null;
        using (SettingsForm form = new SettingsForm(input, delegate(ClockSettings s) { saved = s; })) {
            Check(Find<CheckBox>(form, "StartOnLoginInput").Checked, "load startup option");
            Check(!Find<CheckBox>(form, "OpenOnStartInput").Checked, "load open option");
            Click(Find<Button>(form, "ResetButton"));
            Check(Find<CheckBox>(form, "StartOnLoginInput").Checked, "reset preserves startup option");
            Check(!Find<CheckBox>(form, "OpenOnStartInput").Checked, "reset preserves panel option");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(saved != null && saved.StartOnLogin && !saved.OpenOnStart, "submit independent options");
        string wire = new JavaScriptSerializer().Serialize(saved);
        ClockSettings roundtrip = new JavaScriptSerializer().Deserialize<ClockSettings>(wire);
        Check(roundtrip.StartOnLogin && !roundtrip.OpenOnStart, "startup option survives settings IPC serialization");
        Console.WriteLine("settings UI option tests passed");
    }
}

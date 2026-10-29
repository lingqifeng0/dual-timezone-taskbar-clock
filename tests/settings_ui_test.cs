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

    private static void ChooseZone(ComboBox box, string id)
    {
        for (int i = 0; i < box.Items.Count; ++i) {
            object item = box.Items[i];
            FieldInfo field = item.GetType().GetField("Id");
            if (field != null && (string)field.GetValue(item) == id) { box.SelectedIndex = i; return; }
        }
        throw new Exception("missing time zone: " + id);
    }

    private static string SelectedZone(ComboBox box)
    {
        object item = box.SelectedItem;
        return (string)item.GetType().GetField("Id").GetValue(item);
    }

    [STAThread] private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message); return 1; }
    }

    private static void Run()
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
            ChooseZone(Find<ComboBox>(form, "LocalZoneInput"), "Eastern Standard Time");
            ChooseZone(Find<ComboBox>(form, "TargetZoneInput"), "Tokyo Standard Time");
            Check(Find<TextBox>(form, "LocalLabelInput").Text == "美东", "first zone auto label");
            Check(Find<TextBox>(form, "TargetLabelInput").Text == "日本", "second zone auto label");
            Check(Find<Button>(form, "TopModulesButton") != null && Find<Button>(form, "BottomModulesButton") != null, "both rows have module editors");
            Check(form.Controls.Find("TopTemplateInput", true).Length == 0 && form.Controls.Find("BottomTemplateInput", true).Length == 0, "raw templates are not displayed");
            Check(form.Controls.Find("TopDateCheckBox", true).Length == 0 && form.Controls.Find("TopZoneInput", true).Length == 0, "duplicate date and order controls removed");
            Check(Find<Label>(form, "TopModuleSummary").Text.Contains("标签") && Find<Label>(form, "TopModuleSummary").Text.Contains("时间"), "module summary is readable");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(saved != null && saved.StartOnLogin && !saved.OpenOnStart && saved.LocalZoneId == "Eastern Standard Time" && saved.TargetZoneId == "Tokyo Standard Time", "submit independent options and both zones");
        string wire = new JavaScriptSerializer().Serialize(saved);
        ClockSettings roundtrip = new JavaScriptSerializer().Deserialize<ClockSettings>(wire);
        Check(roundtrip.StartOnLogin && !roundtrip.OpenOnStart && roundtrip.LocalZoneId == "Eastern Standard Time" && roundtrip.TargetZoneId == "Tokyo Standard Time", "startup and zones survive settings IPC serialization");
        ClockSettings reversed = new ClockSettings();
        reversed.LocalZoneId = ""; reversed.LocalLabel = "本地";
        reversed.TargetZoneId = "Tokyo Standard Time"; reversed.ChinaLabel = "东京";
        reversed.TopIsChina = true;
        reversed.TopTemplate = "%label% %time% %weekday%";
        reversed.BottomTemplate = "%%date%% %date% %label%";
        DateTime instant = new DateTime(2026, 10, 28, 15, 30, 0, DateTimeKind.Utc);
        string[] before = ClockFormatter.Preview(reversed, instant);
        ClockSettings savedReversed = null;
        using (SettingsForm form = new SettingsForm(reversed, delegate(ClockSettings s) { savedReversed = s; })) {
            Check(SelectedZone(Find<ComboBox>(form, "LocalZoneInput")) == "Tokyo Standard Time", "reversed setting maps Tokyo to upper row");
            Check(SelectedZone(Find<ComboBox>(form, "TargetZoneInput")) == "", "reversed setting maps system to lower row");
            Check(Find<TextBox>(form, "LocalLabelInput").Text == "东京" && Find<TextBox>(form, "TargetLabelInput").Text == "本地", "row labels follow time zones");
            Check(Find<Label>(form, "BottomModuleSummary").Text.Contains("%date%"), "literal percent text remains a text module");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        string[] after = ClockFormatter.Preview(savedReversed, instant);
        Check(savedReversed.TopIsChina && savedReversed.TopTemplate == reversed.TopTemplate && savedReversed.BottomTemplate == reversed.BottomTemplate, "reversed saved configuration preserved");
        Check(before[0] == after[0] && before[1] == after[1], "reversed configuration displays exactly the same after save");
        ClockSettings bottomSystem = new ClockSettings(); bottomSystem.TargetZoneId = "";
        bottomSystem.LocalZoneId = "Tokyo Standard Time"; bottomSystem.BottomTemplate = "%label% %time%";
        ClockSettings savedBottomSystem = null;
        using (SettingsForm form = new SettingsForm(bottomSystem, delegate(ClockSettings s) { savedBottomSystem = s; })) {
            Check(SelectedZone(Find<ComboBox>(form, "TargetZoneInput")) == "", "lower row can follow system");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(savedBottomSystem.TargetZoneId == "" && ClockFormatter.Preview(savedBottomSystem, instant)[1].EndsWith(instant.ToLocalTime().ToString("HH:mm")), "lower system timezone survives save");
        ClockSettings blankLabel = new ClockSettings(); blankLabel.ChinaLabel = ""; blankLabel.BottomTemplate = "%label% %time%%second%";
        ClockSettings savedBlankLabel = null;
        using (SettingsForm form = new SettingsForm(blankLabel, delegate(ClockSettings s) { savedBlankLabel = s; })) {
            Label summary = Find<Label>(form, "BottomModuleSummary");
            Check(summary.Text == "时间   ·   秒", "empty label is absent from summary");
            Find<TextBox>(form, "TargetLabelInput").Text = "中国";
            Check(summary.Text == "时间   ·   秒", "entering label text does not add an unchosen module");
            Find<TextBox>(form, "TargetLabelInput").Text = " ";
            Check(summary.Text == "时间   ·   秒", "whitespace-only label is hidden");
            Find<TextBox>(form, "TargetLabelInput").Text = "";
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(savedBlankLabel != null && savedBlankLabel.BottomTemplate == "%time%%second%", "saving removes the unused blank label and preserves seconds");
        ClockSettings cleaned = null;
        using (SettingsForm form = new SettingsForm(blankLabel, delegate(ClockSettings s) { cleaned = s; })) {
            using (TemplateEditor editor = new TemplateEditor(blankLabel, false)) {
                Check(editor.ResultTemplate == "%time%%second%", "bottom editor has time and seconds but no empty label or spacer");
                form.AcceptEditor(editor, false);
            }
            Check(Find<Label>(form, "BottomModuleSummary").Text == "时间   ·   秒", "confirmed editor summary has no phantom label");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(cleaned != null && cleaned.BottomTemplate == "%time%%second%" && cleaned.ChinaLabel == "" &&
            cleaned.TargetZoneId == blankLabel.TargetZoneId, "confirmed edit saves cleaned modules while preserving timezone");
        blankLabel.BottomTemplate = "%label% ";
        using (SettingsForm form = new SettingsForm(blankLabel, delegate(ClockSettings s) {})) {
            string summary = Find<Label>(form, "BottomModuleSummary").Text;
            Check(!summary.Contains("标签") && !summary.Contains("空格"), "empty label alone does not become a phantom module");
        }
        ClockSettings reversedLabel = new ClockSettings();
        reversedLabel.TopIsChina = true; reversedLabel.ChinaLabel = ""; reversedLabel.LocalLabel = "美东";
        reversedLabel.TopTemplate = "%label% %time%%second%"; reversedLabel.BottomTemplate = "%date%";
        ClockSettings savedLabel = null;
        using (SettingsForm form = new SettingsForm(reversedLabel, delegate(ClockSettings s) { savedLabel = s; })) {
            using (TemplateEditor editor = new TemplateEditor(reversedLabel, true)) {
                Check(editor.ResultTemplate == "%time%%second%", "reversed upper row removes its own blank label");
                Check(editor.AddLabel("北京"), "add label to upper row");
                form.AcceptEditor(editor, true);
            }
            Check(Find<TextBox>(form, "LocalLabelInput").Text == "北京", "new editor label is shown in the correct row");
            Check(Find<Label>(form, "TopModuleSummary").Text == "时间   ·   秒   ·   标签", "summary follows added label and seconds");
            Click(Find<Button>(form, "SaveApplyButton"));
        }
        Check(savedLabel != null && savedLabel.TopIsChina && savedLabel.ChinaLabel == "北京" && savedLabel.LocalLabel == "美东" &&
            savedLabel.TopTemplate == "%time%%second%%label%", "label and seconds persist with reversed zone mapping without automatic spaces");
        Console.WriteLine("PASS: simplified settings, preserved reversed order, templates and both system-zone choices");
    }
}

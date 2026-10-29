using System;
using System.Reflection;
using System.Windows.Forms;
using NativeTaskbarClock;

internal static class TemplateEditorTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Find<T>(Control root, string name) where T : Control {
        Control[] found = root.Controls.Find(name, true); Check(found.Length == 1, "missing " + name); return (T)found[0];
    }
    private static void Click(Button button) {
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
    }
    [STAThread] private static void Main() {
        Application.EnableVisualStyles();
        ClockSettings settings = new ClockSettings(); settings.TopTemplate = "%label% %time%";
        using (TemplateEditor editor = new TemplateEditor(settings, true)) {
            FlowLayoutPanel strip = Find<FlowLayoutPanel>(editor, "ModuleStrip");
            Check(strip.FlowDirection == FlowDirection.LeftToRight && !strip.WrapContents, "modules arranged in one horizontal row");
            Click(Find<Button>(editor, "AddWeekdayButton"));
            Check(editor.ResultTemplate == "%label% %time%%weekday%", "add weekday without a space");
            Check(strip.Controls.Count == 4 && editor.SelectedIndex == 3, "new horizontal module is selected without a phantom space");
            Click(Find<Button>(editor, "MoveEarlierButton"));
            Check(editor.ResultTemplate == "%label% %weekday%%time%", "move without scrambling spaces");
            Click(Find<Button>(editor, "RemoveModuleButton"));
            Check(editor.ResultTemplate == "%label% %time%", "remove module");
            Click(Find<Button>(editor, "AddOffsetButton"));
            Check(editor.ResultTemplate == "%label% %time%%offset%", "add UTC offset without a space");
            Check(!Find<Label>(editor, "ModulePreview").Text.StartsWith("预览不可用"), "preview valid");
            Click(Find<Button>(editor, "ModuleDoneButton"));
            Check(editor.DialogResult == DialogResult.OK, "confirm dialog");
        }
        settings.LocalLabel = ""; settings.TopTemplate = "%label% %time%";
        using (TemplateEditor editor = new TemplateEditor(settings, true)) {
            FlowLayoutPanel strip = Find<FlowLayoutPanel>(editor, "ModuleStrip");
            Check(editor.ResultTemplate == "%time%" && strip.Controls.Count == 1 && Find<Button>(editor, "ModuleChip0").Text == "时间", "blank label and separator do not appear");
            Check(settings.TopTemplate == "%label% %time%", "opening the editor does not mutate original settings");
            Click(Find<Button>(editor, "AddSecondButton"));
            Check(editor.ResultTemplate == "%time%%second%", "seconds added next to time");
            Check(Find<Button>(editor, "ModuleChip1").Text == "秒", "seconds module visible");
            Check(!Find<Label>(editor, "ModulePreview").Text.StartsWith("预览不可用"), "seconds preview is valid");
            Click(Find<Button>(editor, "MoveEarlierButton"));
            Check(editor.ResultTemplate == "%second%%time%", "seconds can move before time");
            Click(Find<Button>(editor, "MoveLaterButton"));
            Check(editor.ResultTemplate == "%time%%second%", "seconds can move after time again");
            Click(Find<Button>(editor, "RemoveModuleButton"));
            Check(editor.ResultTemplate == "%time%", "removing seconds leaves the time");
            Check(!editor.AddLabel(" ") && editor.ResultTemplate == "%time%", "blank input cannot create a label");
            Check(editor.AddLabel("北京") && editor.ResultTemplate == "%time%%label%" && editor.ResultLabel == "北京", "adding a real label updates text and module without a space");
            Click(Find<Button>(editor, "AddSpaceButton"));
            Check(editor.ResultTemplate == "%time%%label% " && Find<Button>(editor, "ModuleChip2").Text == "空格", "explicitly added space appears");
            Check(settings.LocalLabel == "" && settings.TopTemplate == "%label% %time%", "editing does not modify source settings before confirmation");
        }
        Console.WriteLine("PASS: module editor add, reorder, remove, preview and apply");
    }
}

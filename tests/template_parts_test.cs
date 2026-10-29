using System;
using NativeTaskbarClock;

internal static class TemplatePartsTest
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Main()
    {
        string original = "北京市：%time%  |  %date%  %%  %weekday%";
        TemplateParts untouched = TemplateParts.Parse(original);
        Check(untouched.Build() == original, "round trip preserves custom text, spaces and percent escapes");
        TemplateParts parts = TemplateParts.Parse("%label% %time% %date%");
        int selected = parts.Move(2, -1);
        Check(selected == 0 && parts.Build() == "%time% %label% %date%", "move modules across spaces");
        parts.Remove(2);
        Check(parts.Build() == "%time%  %date%", "delete a module without deleting chosen spaces");
        parts.AddToken("%offset%");
        Check(parts.Build() == "%time%  %date%%offset%", "adding a module inserts no automatic space");
        parts.AddText(" | 100%");
        Check(parts.Build().EndsWith(" | 100%%"), "custom percent escapes correctly");
        TemplateParts blank = TemplateParts.Parse("");
        blank.AddText("文字"); blank.AddToken("%weekday%");
        Check(blank.Build() == "文字%weekday%", "build from blank without automatic space");
        TemplateParts chosenSpace = TemplateParts.Parse("%time%");
        chosenSpace.AddText(" "); chosenSpace.AddToken("%date%");
        Check(chosenSpace.Build() == "%time% %date%" && chosenSpace.Count == 3, "space is present only when explicitly added");
        chosenSpace.Remove(2);
        Check(chosenSpace.Build() == "%time% ", "deleting a module preserves an explicitly added space");
        TemplateParts drag = TemplateParts.Parse("%label% %time% %date% %weekday%");
        Check(drag.MoveTo(6, 0) == 0 && drag.Build() == "%weekday% %label% %time% %date%", "drag from right to left");
        Check(drag.MoveTo(0, 6) == 6 && drag.Build() == "%label% %time% %date% %weekday%", "drag from left to right");
        Check(drag.MoveTo(4, 1) == 0 && drag.Build() == "%date% %label% %time% %weekday%", "drop onto separator");
        TemplateParts noLabel = TemplateParts.Parse("%label% %time%%second%");
        noLabel.RemoveBlankLabels();
        Check(noLabel.Build() == "%time%%second%", "blank label and its separator are removed");
        TemplateParts middleLabel = TemplateParts.Parse("%date% %label% %time%");
        middleLabel.RemoveBlankLabels();
        Check(middleLabel.Build() == "%date% %time%", "blank middle label leaves one separator");
        TemplateParts customSpaces = TemplateParts.Parse("%label%   %time% %%label%%");
        customSpaces.RemoveBlankLabels();
        Check(customSpaces.Build() == "  %time% %%label%%", "custom extra spaces and literal text survive");
        TemplateParts repeatedLabels = TemplateParts.Parse("%label% %label% %time%");
        repeatedLabels.RemoveBlankLabels();
        Check(repeatedLabels.Build() == "%time%", "all blank labels are removed");
        Console.WriteLine("PASS: template parts round trip, move, remove, add and percent escaping");
    }
}

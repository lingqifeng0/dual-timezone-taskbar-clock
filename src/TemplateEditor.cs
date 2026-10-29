using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NativeTaskbarClock
{
    internal sealed class TemplateEditor : Form
    {
        private static readonly Color Background = Color.FromArgb(18, 26, 39);
        private static readonly Color Surface = Color.FromArgb(27, 39, 56);
        private static readonly Color Input = Color.FromArgb(37, 52, 72);
        private static readonly Color TextColor = Color.FromArgb(233, 240, 248);
        private static readonly Color Muted = Color.FromArgb(158, 177, 198);
        private static readonly Color Accent = Color.FromArgb(71, 174, 229);
        private readonly TemplateParts parts;
        private readonly ClockSettings settings;
        private readonly bool upper;
        private readonly FlowLayoutPanel strip = new FlowLayoutPanel();
        private readonly List<Button> chips = new List<Button>();
        private readonly ToolTip hints = new ToolTip();
        private readonly Label preview = new Label();
        private readonly Timer previewTimer = new Timer();
        private int selectedIndex = -1, dragSource = -1, pendingDrop = -2;
        private Point dragStart;
        internal string ResultTemplate { get { return parts.Build(); } }
        internal string ResultLabel { get { return RowLabel; } }
        internal int SelectedIndex { get { return selectedIndex; } }

        private string RowLabel {
            get { return upper ? (settings.TopIsChina ? settings.ChinaLabel : settings.LocalLabel) :
                (settings.TopIsChina ? settings.LocalLabel : settings.ChinaLabel); }
            set {
                if (upper == settings.TopIsChina) settings.ChinaLabel = value;
                else settings.LocalLabel = value;
            }
        }

        [DllImport("dwmapi.dll", PreserveSig=true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal TemplateEditor(ClockSettings current, bool isUpper)
        {
            settings = current.Copy(); upper = isUpper;
            parts = TemplateParts.Parse(upper ? settings.TopTemplate : settings.BottomTemplate);
            if (string.IsNullOrWhiteSpace(RowLabel)) parts.RemoveBlankLabels();
            Text = (upper ? "上行" : "下行") + " · 模块编辑";
            Name = "TemplateEditorForm";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Background; ForeColor = TextColor; ClientSize = new Size(760, 450);
            Disposed += delegate { hints.Dispose(); previewTimer.Dispose(); };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            HandleCreated += delegate { try { int dark=1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { } };

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill; layout.Padding = new Padding(20, 12, 20, 10);
            layout.ColumnCount = 1; layout.RowCount = 9;
            int[] heights = { 56, 38, 28, 80, 42, 28, 65, 30, 42 };
            foreach (int height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);

            Panel heading = new Panel(); heading.Dock = DockStyle.Fill;
            Label title = new Label(); title.Text = (upper ? "上行" : "下行") + "内容";
            title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold); title.ForeColor = TextColor;
            title.AutoSize = true; title.Location = new Point(0, 0);
            Label intro = new Label(); intro.Text = "点击添加模块，拖动它们来调整顺序。";
            intro.ForeColor = Muted; intro.AutoSize = true; intro.Location = new Point(1, 30);
            heading.Controls.Add(title); heading.Controls.Add(intro); layout.Controls.Add(heading, 0, 0);

            FlowLayoutPanel add = new FlowLayoutPanel(); add.Dock = DockStyle.Fill; add.WrapContents = false;
            Button addLabel = Button("AddLabelButton", "＋ 标签", 77, false);
            addLabel.Click += delegate {
                string label = RowLabel;
                if (string.IsNullOrWhiteSpace(label)) label = TextPrompt.ShowLabel(this);
                if (!string.IsNullOrWhiteSpace(label)) AddLabel(label);
            };
            add.Controls.Add(addLabel);
            AddTokenButton(add, "AddTimeButton", "＋ 时间", "%time%");
            AddTokenButton(add, "AddSecondButton", "＋ 秒", "%second%");
            AddTokenButton(add, "AddDateButton", "＋ 日期", "%date%");
            AddTokenButton(add, "AddWeekdayButton", "＋ 星期", "%weekday%");
            AddTokenButton(add, "AddOffsetButton", "＋ 偏移", "%offset%");
            Button addText = Button("AddTextButton", "＋ 文字", 77, false);
            addText.Click += delegate { string text = TextPrompt.Show(this, "添加自定义文字", ""); if (!string.IsNullOrEmpty(text)) RefreshParts(parts.AddText(text)); };
            add.Controls.Add(addText);
            Button addSpace = Button("AddSpaceButton", "＋ 空格", 77, false);
            addSpace.Click += delegate { RefreshParts(parts.AddText(" ")); }; add.Controls.Add(addSpace);
            layout.Controls.Add(add, 0, 1);

            Label orderTitle = new Label(); orderTitle.Text = "模块顺序  ·  从左到右";
            orderTitle.Dock = DockStyle.Fill; orderTitle.ForeColor = TextColor; orderTitle.Font = new Font(Font, FontStyle.Bold);
            orderTitle.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(orderTitle, 0, 2);
            strip.Name = "ModuleStrip"; strip.Dock = DockStyle.Fill; strip.BackColor = Surface;
            strip.FlowDirection = FlowDirection.LeftToRight; strip.WrapContents = false; strip.AutoScroll = true;
            strip.Padding = new Padding(10, 8, 10, 8); strip.AllowDrop = true;
            strip.DragEnter += OnChipDragEnter;
            strip.DragDrop += delegate(object sender, DragEventArgs e) {
                if (!e.Data.GetDataPresent(typeof(int)) || parts.Count == 0) return;
                Point point = strip.PointToClient(new Point(e.X, e.Y));
                int target = parts.Count - 1;
                for (int i = 0; i < chips.Count; ++i)
                    if (point.X < chips[i].Left + chips[i].Width / 2) { target = i; break; }
                pendingDrop = parts.MoveTo((int)e.Data.GetData(typeof(int)), target);
            };
            layout.Controls.Add(strip, 0, 3);

            FlowLayoutPanel edit = new FlowLayoutPanel(); edit.Dock = DockStyle.Fill; edit.WrapContents = false; edit.Padding = new Padding(0, 4, 0, 0);
            Button up = Button("MoveEarlierButton", "← 左移", 82, false);
            Button down = Button("MoveLaterButton", "右移 →", 82, false);
            Button remove = Button("RemoveModuleButton", "删除", 76, false);
            Button editText = Button("EditTextButton", "编辑文字", 92, false);
            up.Click += delegate { RefreshParts(parts.Move(selectedIndex, -1)); };
            down.Click += delegate { RefreshParts(parts.Move(selectedIndex, 1)); };
            remove.Click += delegate { RefreshParts(parts.Remove(selectedIndex)); };
            editText.Click += delegate { EditSelectedText(); };
            edit.Controls.Add(up); edit.Controls.Add(down); edit.Controls.Add(remove); edit.Controls.Add(editText);
            layout.Controls.Add(edit, 0, 4);

            Label previewTitle = new Label(); previewTitle.Text = "这一行的预览"; previewTitle.Font = new Font(Font, FontStyle.Bold);
            previewTitle.Dock = DockStyle.Fill; previewTitle.TextAlign = ContentAlignment.MiddleLeft; previewTitle.ForeColor = TextColor;
            layout.Controls.Add(previewTitle, 0, 5);
            preview.Name = "ModulePreview"; preview.Dock = DockStyle.Fill; preview.BackColor = Surface;
            preview.ForeColor = TextColor; preview.Padding = new Padding(14, 0, 14, 0);
            preview.Font = new Font("Segoe UI", 11F); preview.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(preview, 0, 6);
            Label help = new Label(); help.Text = "从左到右对应显示顺序。拖动或选中后左右移动；空格和文字也可编辑。";
            help.Dock = DockStyle.Fill; help.ForeColor = Muted; help.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(help, 0, 7);

            FlowLayoutPanel footer = new FlowLayoutPanel(); footer.Dock = DockStyle.Fill;
            footer.FlowDirection = FlowDirection.RightToLeft; footer.WrapContents = false; footer.Padding = new Padding(0, 4, 0, 0);
            Button done = Button("ModuleDoneButton", "完成", 92, true);
            Button cancel = Button("ModuleCancelButton", "取消", 80, false);
            done.Click += delegate { Finish(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            footer.Controls.Add(done); footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 8);
            AcceptButton = done; CancelButton = cancel;
            RefreshParts(parts.Count > 0 ? 0 : -1);
            previewTimer.Interval = 1000;
            previewTimer.Tick += delegate { if (settings.RequiresSecondRefresh()) UpdatePreview(); };
            previewTimer.Start();
        }

        private static Button Button(string name, string text, int width, bool primary)
        {
            Button button = new Button(); button.Name = name; button.Text = text; button.Size = new Size(width, 30);
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false; button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? Accent : Input; button.ForeColor = primary ? Color.FromArgb(10, 27, 40) : TextColor;
            button.Cursor = Cursors.Hand; button.Margin = new Padding(0, 2, 5, 2);
            return button;
        }
        private void AddTokenButton(FlowLayoutPanel parent, string name, string text, string token)
        {
            Button button = Button(name, text, 77, false);
            button.Click += delegate { RefreshParts(parts.AddToken(token)); };
            parent.Controls.Add(button);
        }
        internal bool AddLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label) || label.Length > 31) return false;
            foreach (char c in label) if (char.IsControl(c)) return false;
            RowLabel = label;
            RefreshParts(parts.AddToken("%label%"));
            return true;
        }
        private void RefreshParts(int selection)
        {
            strip.SuspendLayout();
            try {
                while (strip.Controls.Count > 0) { Control old = strip.Controls[0]; strip.Controls.RemoveAt(0); old.Dispose(); }
                chips.Clear();
                for (int i = 0; i < parts.Count; ++i) {
                    int index = i;
                    TemplatePart part = parts[i];
                    string caption = part.Caption;
                    int width = Math.Min(190, Math.Max(60, TextRenderer.MeasureText(caption, Font).Width + 26));
                    Button chip = Button("ModuleChip" + i, caption, width, false);
                    chip.Height = 34; chip.Margin = new Padding(0, 3, 8, 3); chip.AllowDrop = true;
                    chip.FlatAppearance.BorderSize = 1; chip.FlatAppearance.BorderColor = Color.FromArgb(70, 93, 116);
                    hints.SetToolTip(chip, part.ToString());
                    chip.Click += delegate { SelectPart(index); };
                    chip.DoubleClick += delegate { SelectPart(index); EditSelectedText(); };
                    chip.MouseDown += delegate(object sender, MouseEventArgs e) {
                        if (e.Button != MouseButtons.Left) return;
                        SelectPart(index); dragSource = index; dragStart = e.Location;
                    };
                    chip.MouseMove += delegate(object sender, MouseEventArgs e) {
                        if (dragSource != index || e.Button != MouseButtons.Left) return;
                        int dx = Math.Abs(e.X - dragStart.X), dy = Math.Abs(e.Y - dragStart.Y);
                        if (dx < SystemInformation.DragSize.Width / 2 && dy < SystemInformation.DragSize.Height / 2) return;
                        dragSource = -1; pendingDrop = -2;
                        chip.DoDragDrop(index, DragDropEffects.Move);
                        if (pendingDrop != -2) { int result = pendingDrop; pendingDrop = -2; RefreshParts(result); }
                    };
                    chip.MouseUp += delegate { dragSource = -1; };
                    chip.DragEnter += OnChipDragEnter;
                    chip.DragDrop += delegate(object sender, DragEventArgs e) {
                        if (e.Data.GetDataPresent(typeof(int))) pendingDrop = parts.MoveTo((int)e.Data.GetData(typeof(int)), index);
                    };
                    chips.Add(chip); strip.Controls.Add(chip);
                }
                SelectPart(selection >= 0 && selection < parts.Count ? selection : -1);
            } finally { strip.ResumeLayout(true); }
            if (selectedIndex >= 0 && selectedIndex < chips.Count) {
                strip.PerformLayout();
                strip.ScrollControlIntoView(chips[selectedIndex]);
            }
            UpdatePreview();
        }
        private void UpdatePreview()
        {
            try {
                if (upper) settings.TopTemplate = parts.Build(); else settings.BottomTemplate = parts.Build();
                settings.Validate();
                string[] rows = ClockFormatter.Preview(settings, DateTime.UtcNow);
                preview.ForeColor = TextColor; preview.Text = rows[upper ? 0 : 1];
            } catch (Exception ex) { preview.ForeColor = Color.FromArgb(255, 177, 152); preview.Text = "预览不可用：" + ex.Message; }
        }
        private static void OnChipDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(int))) e.Effect = DragDropEffects.Move;
        }
        private void SelectPart(int index)
        {
            selectedIndex = index;
            for (int i = 0; i < chips.Count; ++i) {
                chips[i].BackColor = i == index ? Color.FromArgb(40, 87, 119) : Input;
                chips[i].FlatAppearance.BorderColor = i == index ? Accent : Color.FromArgb(70, 93, 116);
            }
            if (index >= 0 && index < chips.Count) strip.ScrollControlIntoView(chips[index]);
        }
        private void EditSelectedText()
        {
            int selected = selectedIndex;
            if (selected < 0 || parts[selected].IsToken) return;
            string current = parts[selected].Raw.Replace("%%", "%");
            string text = TextPrompt.Show(this, "编辑自定义文字", current);
            if (text == null) return;
            if (text.Length == 0) RefreshParts(parts.Remove(selected));
            else { parts.SetText(selected, text); RefreshParts(selected); }
        }
        private void Finish()
        {
            try {
                if (upper) settings.TopTemplate = parts.Build(); else settings.BottomTemplate = parts.Build();
                settings.Validate(); DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法应用模块", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private sealed class TextPrompt : Form
        {
            private readonly TextBox box = new TextBox();
            private TextPrompt(string title, string value, string instruction, int maxLength)
            {
                Text = title; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
                Font = new Font("Microsoft YaHei UI", 9F); BackColor = Background; ForeColor = TextColor; ClientSize = new Size(440, 135);
                Label note = new Label(); note.Text = instruction; note.ForeColor = Muted;
                note.SetBounds(16, 12, 408, 27); Controls.Add(note);
                box.Text = value; box.MaxLength = maxLength; box.BackColor = Input; box.ForeColor = TextColor; box.BorderStyle = BorderStyle.FixedSingle;
                box.SetBounds(18, 43, 404, 26); Controls.Add(box);
                Button ok = Button("TextOkButton", "确定", 80, true); ok.SetBounds(252, 88, 80, 30);
                Button cancel = Button("TextCancelButton", "取消", 80, false); cancel.SetBounds(342, 88, 80, 30);
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
                Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            }
            internal static string Show(IWin32Window parent, string title, string initial)
            {
                using (TextPrompt prompt = new TextPrompt(title, initial, "输入文字或分隔符，例如：北京、｜、空格", 95))
                    return prompt.ShowDialog(parent) == DialogResult.OK ? prompt.box.Text : null;
            }
            internal static string ShowLabel(IWin32Window parent)
            {
                using (TextPrompt prompt = new TextPrompt("添加标签", "", "输入这一行的标签，例如：北京、美东", 31))
                    return prompt.ShowDialog(parent) == DialogResult.OK ? prompt.box.Text : null;
            }
        }
    }
}

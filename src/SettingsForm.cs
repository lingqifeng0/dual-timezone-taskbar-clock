using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NativeTaskbarClock
{
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Background = Color.FromArgb(18, 26, 39);
        private static readonly Color Surface = Color.FromArgb(27, 39, 56);
        private static readonly Color Input = Color.FromArgb(37, 52, 72);
        private static readonly Color TextColor = Color.FromArgb(233, 240, 248);
        private static readonly Color Muted = Color.FromArgb(158, 177, 198);
        private static readonly Color Accent = Color.FromArgb(71, 174, 229);
        private readonly TextBox localLabel = new TextBox();
        private readonly TextBox targetLabel = new TextBox();
        private readonly ComboBox localZone = new DarkComboBox();
        private readonly ComboBox targetZone = new DarkComboBox();
        private readonly ComboBox timeFormat = new DarkComboBox();
        private readonly ComboBox dateFormat = new DarkComboBox();
        private string topTemplate = "", bottomTemplate = "";
        private readonly Label topSummary = new Label();
        private readonly Label bottomSummary = new Label();
        private readonly CheckBox openOnStart = new CheckBox();
        private readonly CheckBox startOnLogin = new CheckBox();
        private readonly Label preview = new Label();
        private readonly Timer previewTimer = new Timer();
        private readonly Action<ClockSettings> saveAndApply;
        private bool loading, inverseMapping;

        [DllImport("dwmapi.dll", PreserveSig=true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private sealed class ZoneChoice
        {
            public string Id, Text;
            public ZoneChoice(string id, string text) { Id = id; Text = text; }
            public override string ToString() { return Text; }
        }

        private sealed class DarkComboBox : ComboBox
        {
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (message.Msg != 0x000F || !IsHandleCreated || IsDisposed || Width < 30 || Height < 12) return;
                using (Graphics g = Graphics.FromHwnd(Handle)) {
                    int arrowWidth = Math.Max(SystemInformation.VerticalScrollBarWidth, (int)(18 * g.DpiX / 96F));
                    Rectangle area = new Rectangle(Width - arrowWidth - 2, 2, arrowWidth, Height - 4);
                    using (Brush b = new SolidBrush(Input)) g.FillRectangle(b, area);
                    int cx = area.Left + area.Width / 2, cy = area.Top + area.Height / 2;
                    int d = Math.Max(3, (int)(3 * g.DpiX / 96F));
                    Point[] arrow = { new Point(cx - d, cy - d / 2), new Point(cx + d, cy - d / 2), new Point(cx, cy + d) };
                    using (Brush b = new SolidBrush(Muted)) g.FillPolygon(b, arrow);
                }
            }
        }

        public SettingsForm(ClockSettings settings, Action<ClockSettings> apply)
        {
            saveAndApply = apply;
            Text = "双时区时钟 · 设置";
            Name = "ClockSettingsForm";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = true; ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(700, 615);
            BackColor = Background; ForeColor = TextColor;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            HandleCreated += delegate {
                try { int dark = 1; if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); } catch { }
            };

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill; layout.Padding = new Padding(22, 12, 22, 10);
            layout.ColumnCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = { 61, 39, 39, 36, 36, 38, 38, 44, 44, 28, 68, 30, 30, 46 };
            layout.RowCount = heights.Length;
            foreach (int height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);

            Panel heading = new Panel(); heading.Dock = DockStyle.Fill;
            Label title = new Label(); title.Text = "双时区时钟"; title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            title.ForeColor = TextColor; title.AutoSize = true; title.Location = new Point(0, 1);
            Label subtitle = new Label(); subtitle.Text = "为上、下两行分别设置时区和显示内容"; subtitle.ForeColor = Muted;
            subtitle.AutoSize = true; subtitle.Location = new Point(1, 34);
            heading.Controls.Add(title); heading.Controls.Add(subtitle);
            layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);

            localLabel.Name = "LocalLabelInput"; localLabel.MaxLength = 31;
            targetLabel.Name = "TargetLabelInput"; targetLabel.MaxLength = 31;
            localZone.Name = "LocalZoneInput"; localZone.DropDownStyle = ComboBoxStyle.DropDownList;
            targetZone.Name = "TargetZoneInput"; targetZone.DropDownStyle = ComboBoxStyle.DropDownList;
            localZone.Items.Add(new ZoneChoice("", "跟随系统时区"));
            targetZone.Items.Add(new ZoneChoice("", "跟随系统时区"));
            foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones()) {
                localZone.Items.Add(new ZoneChoice(zone.Id, zone.DisplayName));
                targetZone.Items.Add(new ZoneChoice(zone.Id, zone.DisplayName));
            }
            timeFormat.Name = "TimeFormatInput"; timeFormat.MaxLength = 63;
            timeFormat.Items.AddRange(new object[] { "HH:mm", "H:mm", "hh:mm tt", "h:mm tt", "HH:mm:ss", "h:mm:ss tt" });
            dateFormat.Name = "DateFormatInput"; dateFormat.MaxLength = 63;
            dateFormat.Items.AddRange(new object[] { "yyyy/MM/dd", "yyyy-MM-dd", "MM/dd", "M/d", "M/d ddd" });
            foreach (TextBox box in new[] { localLabel, targetLabel }) StyleText(box);
            foreach (ComboBox box in new[] { localZone, targetZone, timeFormat, dateFormat }) StyleCombo(box);
            AddRow(layout, 1, "上行时区", localZone);
            AddRow(layout, 2, "下行时区", targetZone);
            AddRow(layout, 3, "上行标签", localLabel);
            AddRow(layout, 4, "下行标签", targetLabel);
            AddRow(layout, 5, "时间格式", timeFormat);
            AddRow(layout, 6, "日期格式", dateFormat);
            AddTemplateRow(layout, 7, "上行内容", topSummary, true);
            AddTemplateRow(layout, 8, "下行内容", bottomSummary, false);

            Label previewTitle = new Label(); previewTitle.Text = "显示预览";
            previewTitle.Font = new Font(Font, FontStyle.Bold); previewTitle.ForeColor = TextColor;
            previewTitle.Dock = DockStyle.Fill; previewTitle.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(previewTitle, 0, 9); layout.SetColumnSpan(previewTitle, 2);
            preview.Name = "ClockPreview"; preview.Dock = DockStyle.Fill;
            preview.BackColor = Surface; preview.ForeColor = TextColor; preview.Padding = new Padding(15, 0, 15, 0);
            preview.Font = new Font("Segoe UI", 11F); preview.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(preview, 0, 10); layout.SetColumnSpan(preview, 2);

            StyleCheck(startOnLogin, "StartOnLoginInput", "登录 Windows 时自动启动");
            StyleCheck(openOnStart, "OpenOnStartInput", "手动启动时打开设置");
            FlowLayoutPanel startup = new FlowLayoutPanel(); startup.Dock = DockStyle.Fill; startup.WrapContents = false;
            startup.Controls.Add(startOnLogin); startup.Controls.Add(openOnStart);
            layout.Controls.Add(startup, 0, 11); layout.SetColumnSpan(startup, 2);

            Label note = new Label(); note.Text = "需要秒数时可添加“秒”模块；内容过长可能截断。";
            note.ForeColor = Muted; note.Dock = DockStyle.Fill; note.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(note, 0, 12); layout.SetColumnSpan(note, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel(); actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft; actions.WrapContents = false; actions.Padding = new Padding(0, 4, 0, 0);
            Button save = MakeButton("SaveApplyButton", "保存并应用", 112, true);
            Button cancel = MakeButton("CancelButton", "取消", 80, false);
            Button reset = MakeButton("ResetButton", "恢复默认", 96, false);
            save.Click += delegate { SaveClicked(); };
            cancel.Click += delegate { Close(); };
            reset.Click += delegate {
                ClockSettings defaults = new ClockSettings(); defaults.StartOnLogin = startOnLogin.Checked;
                defaults.OpenOnStart = openOnStart.Checked; LoadValues(defaults);
            };
            actions.Controls.Add(save); actions.Controls.Add(cancel); actions.Controls.Add(reset);
            layout.Controls.Add(actions, 0, 13); layout.SetColumnSpan(actions, 2);
            AcceptButton = save; CancelButton = cancel;

            localZone.SelectedIndexChanged += delegate {
                if (!loading && localZone.SelectedItem is ZoneChoice)
                    localLabel.Text = SuggestedLabel(((ZoneChoice)localZone.SelectedItem).Id);
                UpdatePreview();
            };
            targetZone.SelectedIndexChanged += delegate {
                if (!loading && targetZone.SelectedItem is ZoneChoice)
                    targetLabel.Text = SuggestedLabel(((ZoneChoice)targetZone.SelectedItem).Id);
                UpdatePreview();
            };
            localLabel.TextChanged += delegate { UpdateSummaries(); UpdatePreview(); };
            targetLabel.TextChanged += delegate { UpdateSummaries(); UpdatePreview(); };
            timeFormat.TextChanged += delegate { UpdatePreview(); };
            timeFormat.SelectedIndexChanged += delegate { UpdatePreview(); };
            dateFormat.TextChanged += delegate { UpdatePreview(); };
            dateFormat.SelectedIndexChanged += delegate { UpdatePreview(); };
            LoadValues(settings);
            previewTimer.Interval = 1000;
            previewTimer.Tick += delegate { if (Values().RequiresSecondRefresh()) UpdatePreview(); };
            previewTimer.Start();
            Disposed += delegate { previewTimer.Dispose(); };
        }

        private static void StyleText(TextBox box) { box.BackColor = Input; box.ForeColor = TextColor; box.BorderStyle = BorderStyle.FixedSingle; }
        private static void StyleCombo(ComboBox box) {
            box.BackColor = Input; box.ForeColor = TextColor; box.FlatStyle = FlatStyle.Flat;
            box.IntegralHeight = false; box.DropDownHeight = 280;
            if (box.DropDownStyle == ComboBoxStyle.DropDownList) {
                box.DrawMode = DrawMode.OwnerDrawFixed; box.ItemHeight = 22;
                box.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                    if (e.Index < 0) return;
                    ComboBox c = (ComboBox)sender;
                    Color bg = (e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(47, 94, 130) : Input;
                    using (Brush brush = new SolidBrush(bg)) e.Graphics.FillRectangle(brush, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), e.Font, e.Bounds, TextColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    e.DrawFocusRectangle();
                };
            }
        }
        private static void StyleCheck(CheckBox box, string name, string text) {
            box.Name = name; box.Text = text; box.ForeColor = TextColor; box.AutoSize = true;
            box.FlatStyle = FlatStyle.Flat; box.FlatAppearance.BorderColor = Muted;
            box.FlatAppearance.CheckedBackColor = Accent; box.Margin = new Padding(3, 4, 16, 3);
        }
        private static Button MakeButton(string name, string text, int width, bool primary) {
            Button button = new Button(); button.Name = name; button.Text = text; button.Size = new Size(width, 32);
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Accent : Input; button.ForeColor = primary ? Color.FromArgb(10, 27, 40) : TextColor;
            button.FlatAppearance.BorderSize = 0; button.Cursor = Cursors.Hand;
            return button;
        }
        private static void AddRow(TableLayoutPanel layout, int row, string title, Control control) {
            Label label = new Label(); label.Text = title; label.Dock = DockStyle.Fill;
            label.ForeColor = Muted; label.TextAlign = ContentAlignment.MiddleLeft;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right; control.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(label, 0, row); layout.Controls.Add(control, 1, row);
        }
        private void AddTemplateRow(TableLayoutPanel layout, int row, string title, Label summary, bool upper) {
            Label label = new Label(); label.Text = title; label.Dock = DockStyle.Fill;
            label.ForeColor = Muted; label.TextAlign = ContentAlignment.MiddleLeft;
            TableLayoutPanel field = new TableLayoutPanel(); field.Dock = DockStyle.Fill; field.ColumnCount = 2;
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); field.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            field.RowCount = 1; field.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); field.Margin = new Padding(0);
            summary.Name = upper ? "TopModuleSummary" : "BottomModuleSummary";
            summary.Dock = DockStyle.Fill; summary.Margin = new Padding(0, 4, 6, 4);
            summary.Padding = new Padding(10, 0, 10, 0); summary.BackColor = Surface;
            summary.ForeColor = TextColor; summary.TextAlign = ContentAlignment.MiddleLeft; summary.AutoEllipsis = true;
            Button modules = MakeButton(upper ? "TopModulesButton" : "BottomModulesButton", "编辑模块", 90, false);
            modules.Dock = DockStyle.Fill; modules.Margin = new Padding(0, 3, 0, 3);
            modules.Click += delegate {
                using (TemplateEditor editor = new TemplateEditor(Values(), upper)) {
                    if (editor.ShowDialog(this) == DialogResult.OK) AcceptEditor(editor, upper);
                }
            };
            field.Controls.Add(summary, 0, 0); field.Controls.Add(modules, 1, 0);
            layout.Controls.Add(label, 0, row); layout.Controls.Add(field, 1, row);
        }
        internal void AcceptEditor(TemplateEditor editor, bool upper) {
            if (upper) { topTemplate = editor.ResultTemplate; localLabel.Text = editor.ResultLabel; }
            else { bottomTemplate = editor.ResultTemplate; targetLabel.Text = editor.ResultLabel; }
            UpdateSummaries(); UpdatePreview();
        }
        private static string Summary(string template, string rowLabel) {
            TemplateParts parts = TemplateParts.Parse(template);
            List<string> captions = new List<string>();
            bool hiddenLabel = false;
            for (int i = 0; i < parts.Count; ++i) {
                if (parts[i].IsToken && parts[i].Raw == "%label%" && string.IsNullOrWhiteSpace(rowLabel)) {
                    hiddenLabel = true; continue;
                }
                if (!parts[i].IsWhitespace) captions.Add(parts[i].Caption);
            }
            if (captions.Count == 0)
                return hiddenLabel ? "没有可显示的内容" : parts.Count == 0 ? "还没有内容，请点击右侧添加" : "空格";
            return string.Join("   ·   ", captions.ToArray());
        }
        private void UpdateSummaries() { topSummary.Text = Summary(topTemplate, localLabel.Text); bottomSummary.Text = Summary(bottomTemplate, targetLabel.Text); }
        private static string SuggestedLabel(string id) {
            switch (id) {
                case "": return "本地";
                case "China Standard Time": return "中国";
                case "Tokyo Standard Time": return "日本";
                case "Korea Standard Time": return "韩国";
                case "GMT Standard Time": return "英国";
                case "W. Europe Standard Time": return "欧洲";
                case "Eastern Standard Time": return "美东";
                case "Central Standard Time": return "美中";
                case "Mountain Standard Time": return "美山";
                case "Pacific Standard Time": return "美西";
                case "UTC": return "UTC";
                default: return "时区";
            }
        }
        private void LoadValues(ClockSettings s) {
            loading = true;
            try {
                inverseMapping = s.TopIsChina;
                SelectZone(localZone, inverseMapping ? s.TargetZoneId : s.LocalZoneId);
                SelectZone(targetZone, inverseMapping ? s.LocalZoneId : s.TargetZoneId);
                localLabel.Text = inverseMapping ? s.ChinaLabel : s.LocalLabel;
                targetLabel.Text = inverseMapping ? s.LocalLabel : s.ChinaLabel;
                timeFormat.Text = s.TimeFormat; dateFormat.Text = s.DateFormat;
                topTemplate = TemplateParts.ForLabel(s.TopTemplate, localLabel.Text);
                bottomTemplate = TemplateParts.ForLabel(s.BottomTemplate, targetLabel.Text);
                openOnStart.Checked = s.OpenOnStart; startOnLogin.Checked = s.StartOnLogin;
            } finally { loading = false; }
            UpdateSummaries(); UpdatePreview();
        }
        private static void SelectZone(ComboBox box, string id) {
            int zoneIndex = -1;
            for (int i = 0; i < box.Items.Count; ++i)
                if (string.Equals(((ZoneChoice)box.Items[i]).Id, id, StringComparison.OrdinalIgnoreCase)) { zoneIndex = i; break; }
            if (zoneIndex < 0) zoneIndex = box.Items.Add(new ZoneChoice(id, id + "（未找到，请重新选择）"));
            box.SelectedIndex = zoneIndex;
        }
        private ClockSettings Values() {
            ClockSettings s = new ClockSettings();
            ZoneChoice top = localZone.SelectedItem as ZoneChoice, bottom = targetZone.SelectedItem as ZoneChoice;
            string topId = top == null ? "" : top.Id, bottomId = bottom == null ? "" : bottom.Id;
            s.LocalLabel = inverseMapping ? targetLabel.Text : localLabel.Text;
            s.ChinaLabel = inverseMapping ? localLabel.Text : targetLabel.Text;
            s.LocalZoneId = inverseMapping ? bottomId : topId;
            s.TargetZoneId = inverseMapping ? topId : bottomId;
            s.TopIsChina = inverseMapping; s.TimeFormat = timeFormat.Text; s.DateFormat = dateFormat.Text;
            s.TopTemplate = TemplateParts.ForLabel(topTemplate, localLabel.Text);
            s.BottomTemplate = TemplateParts.ForLabel(bottomTemplate, targetLabel.Text);
            s.OpenOnStart = openOnStart.Checked; s.FirstRun = false; s.StartOnLogin = startOnLogin.Checked;
            return s;
        }
        private void UpdatePreview() {
            if (loading) return;
            try {
                ClockSettings s = Values(); s.Validate(); string[] lines = ClockFormatter.Preview(s, DateTime.UtcNow);
                preview.ForeColor = TextColor; preview.Text = lines[0] + "\r\n" + lines[1];
            } catch (Exception ex) { preview.ForeColor = Color.FromArgb(255, 177, 152); preview.Text = "预览不可用：" + ex.Message; }
        }
        private void SaveClicked() {
            try { ClockSettings settings = Values(); settings.Validate(); saveAndApply(settings); Close(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace NativeTaskbarClock
{
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox localLabel = new TextBox();
        private readonly TextBox chinaLabel = new TextBox();
        private readonly ComboBox order = new ComboBox();
        private readonly ComboBox timeFormat = new ComboBox();
        private readonly ComboBox dateFormat = new ComboBox();
        private readonly TextBox topTemplate = new TextBox();
        private readonly TextBox bottomTemplate = new TextBox();
        private readonly CheckBox topDate = new CheckBox();
        private readonly CheckBox bottomDate = new CheckBox();
        private bool syncingDate;
        private readonly CheckBox openOnStart = new CheckBox();
        private readonly CheckBox startOnLogin = new CheckBox();
        private readonly Label preview = new Label();
        private readonly Action<ClockSettings> saveAndApply;

        public SettingsForm(ClockSettings settings, Action<ClockSettings> apply)
        {
            saveAndApply = apply;
            Text = "双时区时钟 · 格式设置";
            Name = "ClockSettingsForm";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(680, 659);
            BackColor = SystemColors.Window;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20, 14, 20, 12);
            layout.ColumnCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowCount = 15;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            for (int i = 0; i < 7; ++i) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);

            Label heading = new Label();
            heading.Text = "任务栏时钟设置";
            heading.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            heading.Dock = DockStyle.Fill;
            heading.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);

            localLabel.Name = "LocalLabelInput"; localLabel.MaxLength = 31;
            chinaLabel.Name = "ChinaLabelInput"; chinaLabel.MaxLength = 31;
            order.Name = "TopZoneInput"; order.DropDownStyle = ComboBoxStyle.DropDownList;
            order.Items.AddRange(new object[] { "美国（系统时间）在上", "中国时间在上" });
            timeFormat.Name = "TimeFormatInput"; timeFormat.MaxLength = 63;
            timeFormat.Items.AddRange(new object[] { "HH:mm", "H:mm", "hh:mm tt", "h:mm tt" });
            dateFormat.Name = "DateFormatInput"; dateFormat.MaxLength = 63;
            dateFormat.Items.AddRange(new object[] { "yyyy/MM/dd", "yyyy-MM-dd", "MM/dd", "M/d", "M/d ddd" });
            topTemplate.Name = "TopTemplateInput"; topTemplate.MaxLength = 95;
            bottomTemplate.Name = "BottomTemplateInput"; bottomTemplate.MaxLength = 95;

            AddRow(layout, 1, "美国时间标签", localLabel);
            AddRow(layout, 2, "中国时间标签", chinaLabel);
            AddRow(layout, 3, "时区顺序", order);
            AddRow(layout, 4, "时间格式", timeFormat);
            AddRow(layout, 5, "日期格式", dateFormat);
            AddRow(layout, 6, "上行内容", topTemplate);
            AddRow(layout, 7, "下行内容", bottomTemplate);

            FlowLayoutPanel dateChoices = new FlowLayoutPanel();
            dateChoices.Dock = DockStyle.Fill; dateChoices.WrapContents = false;
            topDate.Name = "TopDateCheckBox"; topDate.Text = "上行显示日期"; topDate.AutoSize = true;
            bottomDate.Name = "BottomDateCheckBox"; bottomDate.Text = "下行显示日期"; bottomDate.AutoSize = true;
            dateChoices.Controls.Add(topDate); dateChoices.Controls.Add(bottomDate);
            layout.Controls.Add(dateChoices, 1, 8);

            Label help = new Label();
            help.Name = "TemplateHelp";
            help.Dock = DockStyle.Fill;
            help.Text = "%label% 表示标签，%time% 表示时间，%date% 表示日期。\n例如：%label% %time% %date% ；输入 %% 可显示 %。";
            help.ForeColor = SystemColors.GrayText;
            help.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(help, 0, 9); layout.SetColumnSpan(help, 2);

            Label previewTitle = new Label(); previewTitle.Text = "显示预览"; previewTitle.Font = new Font(Font, FontStyle.Bold);
            previewTitle.Dock = DockStyle.Fill; previewTitle.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(previewTitle, 0, 10); layout.SetColumnSpan(previewTitle, 2);
            preview.Name = "ClockPreview";
            preview.Dock = DockStyle.Fill;
            preview.BackColor = Color.FromArgb(32, 39, 49);
            preview.ForeColor = Color.White;
            preview.Padding = new Padding(14, 0, 14, 0);
            preview.Font = new Font("Segoe UI", 11F);
            preview.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(preview, 0, 11); layout.SetColumnSpan(preview, 2);

            openOnStart.Name = "OpenOnStartInput";
            openOnStart.Text = "手动启动时打开设置";
            openOnStart.AutoSize = true;
            startOnLogin.Name = "StartOnLoginInput";
            startOnLogin.Text = "登录 Windows 时自动启动";
            startOnLogin.AutoSize = true;
            FlowLayoutPanel startupChoices = new FlowLayoutPanel();
            startupChoices.Dock = DockStyle.Fill; startupChoices.WrapContents = false;
            startupChoices.Controls.Add(startOnLogin); startupChoices.Controls.Add(openOnStart);
            layout.Controls.Add(startupChoices, 0, 12); layout.SetColumnSpan(startupChoices, 2);

            Label note = new Label();
            note.Text = "保存后任务栏可能在下一分钟刷新。长内容可能被截断；暂不支持秒。";
            note.ForeColor = SystemColors.GrayText; note.Dock = DockStyle.Fill;
            note.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(note, 0, 13); layout.SetColumnSpan(note, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill; actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false; actions.Padding = new Padding(0, 5, 0, 0);
            Button save = new Button(); save.Name = "SaveApplyButton"; save.Text = "保存并应用"; save.Size = new Size(110, 32);
            Button cancel = new Button(); cancel.Name = "CancelButton"; cancel.Text = "取消"; cancel.Size = new Size(80, 32);
            Button reset = new Button(); reset.Name = "ResetButton"; reset.Text = "恢复默认"; reset.Size = new Size(96, 32);
            save.Click += delegate { SaveClicked(); };
            cancel.Click += delegate { Close(); };
            reset.Click += delegate {
                ClockSettings defaults = new ClockSettings();
                defaults.StartOnLogin = startOnLogin.Checked;
                defaults.OpenOnStart = openOnStart.Checked;
                LoadValues(defaults);
            };
            actions.Controls.Add(save); actions.Controls.Add(cancel); actions.Controls.Add(reset);
            layout.Controls.Add(actions, 0, 14); layout.SetColumnSpan(actions, 2);
            AcceptButton = save; CancelButton = cancel;

            localLabel.TextChanged += delegate { UpdatePreview(); };
            chinaLabel.TextChanged += delegate { UpdatePreview(); };
            order.SelectedIndexChanged += delegate { UpdatePreview(); };
            timeFormat.TextChanged += delegate { UpdatePreview(); };
            timeFormat.TextUpdate += delegate { UpdatePreview(); };
            timeFormat.SelectedIndexChanged += delegate { UpdatePreview(); };
            timeFormat.Leave += delegate { UpdatePreview(); };
            dateFormat.TextChanged += delegate { UpdatePreview(); };
            dateFormat.TextUpdate += delegate { UpdatePreview(); };
            dateFormat.SelectedIndexChanged += delegate { UpdatePreview(); };
            dateFormat.Leave += delegate { UpdatePreview(); };
            topTemplate.TextChanged += delegate { SyncDateChoices(); UpdatePreview(); };
            bottomTemplate.TextChanged += delegate { SyncDateChoices(); UpdatePreview(); };
            topDate.CheckedChanged += delegate { SetDateToken(topTemplate, topDate.Checked); };
            bottomDate.CheckedChanged += delegate { SetDateToken(bottomTemplate, bottomDate.Checked); };
            LoadValues(settings);
        }

        private static void AddRow(TableLayoutPanel layout, int row, string title, Control control)
        {
            Label label = new Label(); label.Text = title; label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(label, 0, row); layout.Controls.Add(control, 1, row);
        }

        private void LoadValues(ClockSettings s)
        {
            localLabel.Text = s.LocalLabel; chinaLabel.Text = s.ChinaLabel;
            order.SelectedIndex = s.TopIsChina ? 1 : 0;
            timeFormat.Text = s.TimeFormat; dateFormat.Text = s.DateFormat;
            topTemplate.Text = s.TopTemplate; bottomTemplate.Text = s.BottomTemplate;
            openOnStart.Checked = s.OpenOnStart;
            startOnLogin.Checked = s.StartOnLogin;
            UpdatePreview();
        }

        private ClockSettings Values()
        {
            ClockSettings s = new ClockSettings();
            s.LocalLabel = localLabel.Text; s.ChinaLabel = chinaLabel.Text;
            s.TopIsChina = order.SelectedIndex == 1;
            s.TimeFormat = timeFormat.Text; s.DateFormat = dateFormat.Text;
            s.TopTemplate = topTemplate.Text; s.BottomTemplate = bottomTemplate.Text;
            s.OpenOnStart = openOnStart.Checked; s.FirstRun = false;
            s.StartOnLogin = startOnLogin.Checked;
            return s;
        }

        private void SyncDateChoices()
        {
            if (syncingDate) return;
            syncingDate = true;
            try {
                topDate.Checked = topTemplate.Text.IndexOf("%date%", StringComparison.Ordinal) >= 0;
                bottomDate.Checked = bottomTemplate.Text.IndexOf("%date%", StringComparison.Ordinal) >= 0;
            } finally { syncingDate = false; }
        }

        private void SetDateToken(TextBox box, bool enabled)
        {
            if (syncingDate) return;
            const string token = "%date%";
            string text = box.Text;
            int position = text.IndexOf(token, StringComparison.Ordinal);
            if (enabled && position < 0) box.Text = text.TrimEnd() + (text.Length == 0 ? "" : " ") + token;
            if (!enabled && position >= 0) {
                // Remove each token and one adjacent separating space.
                while ((position = text.IndexOf(token, StringComparison.Ordinal)) >= 0) {
                    int start = position > 0 && text[position-1] == ' ' ? position-1 : position;
                    text = text.Remove(start, position - start + token.Length);
                }
                box.Text = text;
            }
        }

        private void UpdatePreview()
        {
            try {
                ClockSettings s = Values(); s.Validate();
                string[] lines = ClockFormatter.Preview(s, DateTime.UtcNow);
                preview.ForeColor = Color.White;
                preview.Text = lines[0] + "\r\n" + lines[1];
            } catch (Exception ex) {
                preview.ForeColor = Color.FromArgb(255, 191, 154);
                preview.Text = "预览不可用：" + ex.Message;
            }
        }

        private void SaveClicked()
        {
            try {
                ClockSettings settings = Values(); settings.Validate();
                saveAndApply(settings);
                Close();
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

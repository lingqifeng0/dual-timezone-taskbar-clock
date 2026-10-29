using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace NativeTaskbarClock
{
    internal sealed class ClockSettings
    {
        public string LocalLabel = "美国";
        public string ChinaLabel = "中国";
        public bool TopIsChina = false;
        public string TimeFormat = "HH:mm";
        public string DateFormat = "yyyy/MM/dd";
        public string TopTemplate = "%label% %time%";
        public string BottomTemplate = "%label% %time%";
        public bool FirstRun = true;
        public bool OpenOnStart = false;
        // This reflects the Windows per-user startup entry, not config.json.
        public bool StartOnLogin = false;

        public static ClockSettings Load(string path)
        {
            ClockSettings settings = new ClockSettings();
            if (!File.Exists(path)) return settings;
            var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (values == null) throw new InvalidDataException("配置文件必须包含 JSON 对象。");
            object v;
            if (values.TryGetValue("local_label", out v)) settings.LocalLabel = Convert.ToString(v);
            if (values.TryGetValue("china_label", out v)) settings.ChinaLabel = Convert.ToString(v);
            if (values.TryGetValue("top_is_china", out v)) settings.TopIsChina = Convert.ToBoolean(v);
            if (values.TryGetValue("time_format", out v)) settings.TimeFormat = Convert.ToString(v);
            if (values.TryGetValue("date_format", out v)) settings.DateFormat = Convert.ToString(v);
            if (values.TryGetValue("top_template", out v)) settings.TopTemplate = Convert.ToString(v);
            if (values.TryGetValue("bottom_template", out v)) settings.BottomTemplate = Convert.ToString(v);
            else if (values.TryGetValue("show_china_date", out v) && Convert.ToBoolean(v)) settings.BottomTemplate += " %date%";
            if (values.TryGetValue("first_run", out v)) settings.FirstRun = Convert.ToBoolean(v);
            if (values.TryGetValue("open_on_start", out v)) settings.OpenOnStart = Convert.ToBoolean(v);
            return settings;
        }

        public void Save(string path)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            string body = "{\r\n" +
                "  \"local_label\": " + json.Serialize(LocalLabel) + ",\r\n" +
                "  \"china_label\": " + json.Serialize(ChinaLabel) + ",\r\n" +
                "  \"top_is_china\": " + json.Serialize(TopIsChina) + ",\r\n" +
                "  \"time_format\": " + json.Serialize(TimeFormat) + ",\r\n" +
                "  \"date_format\": " + json.Serialize(DateFormat) + ",\r\n" +
                "  \"top_template\": " + json.Serialize(TopTemplate) + ",\r\n" +
                "  \"bottom_template\": " + json.Serialize(BottomTemplate) + ",\r\n" +
                "  \"first_run\": " + json.Serialize(FirstRun) + ",\r\n" +
                "  \"open_on_start\": " + json.Serialize(OpenOnStart) + "\r\n}\r\n";
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(tmp, body, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            } finally {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        public void Validate()
        {
            ValidateField(LocalLabel, "美国时间标签", 31, true);
            ValidateField(ChinaLabel, "中国时间标签", 31, true);
            ValidateField(TimeFormat, "时间格式", 63, false);
            ValidateField(DateFormat, "日期格式", 63, false);
            ValidateField(TopTemplate, "上行内容", 95, true);
            ValidateField(BottomTemplate, "下行内容", 95, true);
            if (ContainsSeconds(TimeFormat))
                throw new InvalidDataException("原生任务栏按分钟刷新，目前不支持显示秒。请从时间格式中移除 s 或 ss。");
            ValidateTemplate(TopTemplate, "上行内容");
            ValidateTemplate(BottomTemplate, "下行内容");
            ClockFormatter.Preview(this, DateTime.UtcNow); // reject formats Windows cannot render
        }

        private static void ValidateField(string value, string field, int limit, bool emptyAllowed)
        {
            if (value == null || (!emptyAllowed && string.IsNullOrWhiteSpace(value)) || value.Length > limit)
                throw new InvalidDataException(field + "长度无效，最多 " + limit + " 个字符。");
            foreach (char c in value) if (char.IsControl(c)) throw new InvalidDataException(field + "不能包含换行或控制字符。");
        }

        private static bool ContainsSeconds(string pattern)
        {
            bool quoted = false;
            for (int i = 0; i < pattern.Length; ++i) {
                if (pattern[i] == '\'') {
                    if (i + 1 < pattern.Length && pattern[i+1] == '\'') { ++i; continue; }
                    quoted = !quoted;
                } else if (!quoted && pattern[i] == 's') return true;
            }
            return false;
        }

        private static void ValidateTemplate(string text, string name)
        {
            for (int i = 0; i < text.Length; ++i) {
                if (text[i] != '%') continue;
                string tail = text.Substring(i);
                if (tail.StartsWith("%%", StringComparison.Ordinal)) { ++i; continue; }
                if (tail.StartsWith("%label%", StringComparison.Ordinal)) { i += 6; continue; }
                if (tail.StartsWith("%time%", StringComparison.Ordinal) || tail.StartsWith("%date%", StringComparison.Ordinal)) { i += 5; continue; }
                throw new InvalidDataException(name + "包含未知的 % 变量。请使用 %label%、%time%、%date%，或用 %% 显示百分号。");
            }
        }
    }

    internal static class ClockFormatter
    {
        [StructLayout(LayoutKind.Sequential)] private struct SystemTime
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
            public static SystemTime FromDateTime(DateTime d) {
                SystemTime s = new SystemTime(); s.Year=(ushort)d.Year; s.Month=(ushort)d.Month;
                s.DayOfWeek=(ushort)d.DayOfWeek; s.Day=(ushort)d.Day; s.Hour=(ushort)d.Hour;
                s.Minute=(ushort)d.Minute; s.Second=(ushort)d.Second; s.Milliseconds=(ushort)d.Millisecond; return s;
            }
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern int GetTimeFormatEx(string locale, uint flags, ref SystemTime time, string format, StringBuilder output, int size);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern int GetDateFormatEx(string locale, uint flags, ref SystemTime time, string format, StringBuilder output, int size, string calendar);

        public static string[] Preview(ClockSettings settings, DateTime utc)
        {
            DateTime local = utc.ToLocalTime();
            DateTime china = utc.AddHours(8);
            return new string[] {
                Render(settings, settings.TopIsChina ? china : local, settings.TopIsChina ? settings.ChinaLabel : settings.LocalLabel, settings.TopTemplate),
                Render(settings, settings.TopIsChina ? local : china, settings.TopIsChina ? settings.LocalLabel : settings.ChinaLabel, settings.BottomTemplate)
            };
        }

        private static string Render(ClockSettings s, DateTime value, string label, string template)
        {
            SystemTime time = SystemTime.FromDateTime(value);
            StringBuilder t = new StringBuilder(128), d = new StringBuilder(128);
            if (GetTimeFormatEx(null, 0, ref time, s.TimeFormat, t, t.Capacity) == 0 ||
                GetDateFormatEx(null, 0, ref time, s.DateFormat, d, d.Capacity, null) == 0)
                throw new InvalidDataException("Windows 无法显示该时间或日期格式，请调整后再试。");
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < template.Length;) {
                string rest = template.Substring(i);
                if (rest.StartsWith("%label%", StringComparison.Ordinal)) { result.Append(label); i += 7; }
                else if (rest.StartsWith("%time%", StringComparison.Ordinal)) { result.Append(t); i += 6; }
                else if (rest.StartsWith("%date%", StringComparison.Ordinal)) { result.Append(d); i += 6; }
                else if (rest.StartsWith("%%", StringComparison.Ordinal)) { result.Append('%'); i += 2; }
                else { result.Append(template[i]); ++i; }
            }
            if (result.Length >= 512) throw new InvalidDataException("内容过长，请缩短行格式。");
            return result.ToString();
        }
    }
}

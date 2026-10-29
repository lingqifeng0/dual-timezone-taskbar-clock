using System;
using System.Collections.Generic;
using System.Text;

namespace NativeTaskbarClock
{
    internal sealed class TemplatePart
    {
        internal string Raw;
        internal bool IsToken;
        internal TemplatePart(string raw, bool isToken) { Raw = raw; IsToken = isToken; }
        internal bool IsWhitespace { get { return !IsToken && string.IsNullOrWhiteSpace(Raw); } }
        internal string Caption {
            get {
                if (IsToken) {
                    switch (Raw) {
                        case "%label%": return "标签";
                        case "%time%": return "时间";
                        case "%second%": return "秒";
                        case "%date%": return "日期";
                        case "%weekday%": return "星期";
                        case "%offset%": return "UTC 偏移";
                    }
                }
                if (IsWhitespace) return Raw.Length == 1 ? "空格" : "空格 × " + Raw.Length;
                string text = Raw.Replace("%%", "%");
                if (text.Length > 12) text = text.Substring(0, 12) + "…";
                return "文字：" + text;
            }
        }
        public override string ToString()
        {
            if (IsToken) {
                switch (Raw) {
                    case "%label%": return "标签   ·   该时区的名称";
                    case "%time%": return "时间   ·   例如 18:30";
                    case "%second%": return "秒   ·   例如 05";
                    case "%date%": return "日期   ·   例如 2026/10/29";
                    case "%weekday%": return "星期   ·   例如 星期四";
                    case "%offset%": return "时区偏移   ·   例如 UTC+08:00";
                }
            }
            if (IsWhitespace) return Raw.Length == 1 ? "空格" : "空格 × " + Raw.Length;
            string text = Raw.Replace("%%", "%");
            return "自定义文字   ·   「" + text + "」";
        }
    }

    internal sealed class TemplateParts
    {
        private static readonly string[] Tokens = { "%label%", "%time%", "%second%", "%date%", "%weekday%", "%offset%" };
        private readonly List<TemplatePart> parts = new List<TemplatePart>();
        internal int Count { get { return parts.Count; } }
        internal TemplatePart this[int index] { get { return parts[index]; } }

        internal static TemplateParts Parse(string template)
        {
            TemplateParts result = new TemplateParts();
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < template.Length;) {
                string token = null;
                if (template[i] == '%' && i + 1 < template.Length && template[i + 1] == '%') {
                    text.Append("%%"); i += 2; continue;
                }
                foreach (string candidate in Tokens)
                    if (template.Length - i >= candidate.Length && string.CompareOrdinal(template, i, candidate, 0, candidate.Length) == 0) { token = candidate; break; }
                if (token == null) { text.Append(template[i++]); continue; }
                if (text.Length > 0) { result.parts.Add(new TemplatePart(text.ToString(), false)); text.Length = 0; }
                result.parts.Add(new TemplatePart(token, true)); i += token.Length;
            }
            if (text.Length > 0) result.parts.Add(new TemplatePart(text.ToString(), false));
            return result;
        }

        internal string Build()
        {
            StringBuilder result = new StringBuilder();
            foreach (TemplatePart part in parts) result.Append(part.Raw);
            return result.ToString();
        }

        internal static string ForLabel(string template, string label)
        {
            if (!string.IsNullOrWhiteSpace(label)) return template;
            TemplateParts parts = Parse(template);
            parts.RemoveBlankLabels();
            return parts.Build();
        }

        // Legacy configurations can retain a label token after its text was
        // cleared. Drop it in the editor along with one adjacent separator;
        // keep deliberately repeated spaces and escaped literal text intact.
        internal void RemoveBlankLabels()
        {
            for (int i = parts.Count - 1; i >= 0; --i) {
                if (!parts[i].IsToken || parts[i].Raw != "%label%") continue;
                parts.RemoveAt(i);
                if (i < parts.Count && parts[i].IsWhitespace) {
                    if (parts[i].Raw.Length == 1) parts.RemoveAt(i);
                    else parts[i].Raw = parts[i].Raw.Substring(1);
                } else if (i > 0 && parts[i - 1].IsWhitespace) {
                    if (parts[i - 1].Raw.Length == 1) parts.RemoveAt(i - 1);
                    else parts[i - 1].Raw = parts[i - 1].Raw.Substring(0, parts[i - 1].Raw.Length - 1);
                }
            }
        }
        internal int AddToken(string token)
        {
            if (Array.IndexOf(Tokens, token) < 0) throw new ArgumentException("未知模块。");
            parts.Add(new TemplatePart(token, true)); return parts.Count - 1;
        }
        internal int AddText(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            foreach (char c in text) if (char.IsControl(c)) throw new ArgumentException("自定义文字不能包含换行或控制字符。");
            parts.Add(new TemplatePart(text.Replace("%", "%%"), false)); return parts.Count - 1;
        }
        internal void SetText(int index, string text)
        {
            if (parts[index].IsToken) throw new ArgumentException("只能修改文字模块。");
            foreach (char c in text) if (char.IsControl(c)) throw new ArgumentException("自定义文字不能包含换行或控制字符。");
            parts[index].Raw = text.Replace("%", "%%");
        }
        internal int Move(int index, int direction)
        {
            if (index < 0 || index >= parts.Count || (direction != -1 && direction != 1)) return index;
            int target = index + direction;
            if (!parts[index].IsWhitespace)
                while (target >= 0 && target < parts.Count && parts[target].IsWhitespace) target += direction;
            if (target < 0 || target >= parts.Count) return index;
            TemplatePart old = parts[index]; parts[index] = parts[target]; parts[target] = old;
            return target;
        }
        internal int MoveTo(int index, int target)
        {
            if (index < 0 || index >= parts.Count || target < 0 || target >= parts.Count || index == target) return index;
            int direction = index < target ? 1 : -1;
            if (!parts[index].IsWhitespace)
                while (target >= 0 && target < parts.Count && parts[target].IsWhitespace) target += direction;
            if (target < 0 || target >= parts.Count) return index;
            while (index != target) {
                int next = Move(index, direction);
                if (next == index || (direction > 0 && next > target) || (direction < 0 && next < target)) break;
                index = next;
            }
            return index;
        }
        internal int Remove(int index)
        {
            if (index < 0 || index >= parts.Count) return index;
            parts.RemoveAt(index);
            return parts.Count == 0 ? -1 : Math.Min(index, parts.Count - 1);
        }
    }
}

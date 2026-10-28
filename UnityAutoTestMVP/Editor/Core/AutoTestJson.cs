using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityAutoTestMVP
{
    /// <summary>
    /// 有序 JSON 对象。使用 List 而不是 Dictionary，保证报告字段顺序稳定、可读。
    /// </summary>
    internal sealed class JsonObject : List<KeyValuePair<string, object>>
    {
        public void Add(string key, object value)
        {
            Add(new KeyValuePair<string, object>(key, value));
        }

        public bool TryGet(string key, out object value)
        {
            for (int i = 0; i < Count; i++)
            {
                if (string.Equals(this[i].Key, key, StringComparison.Ordinal))
                {
                    value = this[i].Value;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// 无第三方依赖的 JSON 读写，仅服务于本工具的 TestReport 模型。
    /// 枚举以字符串输出，保证报告可读；读取时兼容大小写。
    /// </summary>
    internal static class AutoTestJson
    {
        private const int MaxDepth = 64;

        public static string SerializeReport(TestReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException("report");
            }

            StringBuilder sb = new StringBuilder(8192);
            WriteValue(sb, ReportToJson(report), 0);
            sb.Append('\n');
            return sb.ToString();
        }

        public static TestReport DeserializeReport(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new FormatException("报告 JSON 为空。");
            }

            JsonParser parser = new JsonParser(json);
            object root = parser.Parse();
            JsonObject rootObject = root as JsonObject;
            if (rootObject == null)
            {
                throw new FormatException("报告 JSON 的根节点不是对象。");
            }

            if (!rootObject.TryGet("taskId", out _) && !rootObject.TryGet("issues", out _))
            {
                throw new FormatException("报告 JSON 缺少 taskId 与 issues 字段，可能不是 Unity Auto Test MVP 报告。");
            }

            return ReadReport(rootObject);
        }

        // ------------------------------------------------------------------
        // 写入
        // ------------------------------------------------------------------

        private static JsonObject ReportToJson(TestReport report)
        {
            JsonObject root = new JsonObject();
            root.Add("schemaVersion", AutoTestConstants.ReportSchemaVersion);
            root.Add("taskId", report.TaskId ?? string.Empty);
            root.Add("toolVersion", report.ToolVersion ?? string.Empty);
            root.Add("unityVersion", report.UnityVersion ?? string.Empty);
            root.Add("projectName", report.ProjectName ?? string.Empty);
            root.Add("projectPath", report.ProjectPath ?? string.Empty);
            root.Add("scenePath", report.ScenePath ?? string.Empty);
            root.Add("testType", report.TestType.ToString());
            root.Add("overallStatus", report.OverallStatus.ToString());
            root.Add("startedAtUtc", FormatDateTime(report.StartedAtUtc));
            root.Add("finishedAtUtc", FormatDateTime(report.FinishedAtUtc));
            root.Add("durationSeconds", report.DurationSeconds);

            TestSummary summary = report.Summary ?? new TestSummary();
            JsonObject summaryObject = new JsonObject();
            summaryObject.Add("total", summary.Total);
            summaryObject.Add("passed", summary.Passed);
            summaryObject.Add("failed", summary.Failed);
            summaryObject.Add("warning", summary.Warning);
            summaryObject.Add("notRun", summary.NotRun);
            summaryObject.Add("cancelled", summary.Cancelled);
            root.Add("summary", summaryObject);

            List<object> notes = new List<object>();
            if (report.Notes != null)
            {
                for (int i = 0; i < report.Notes.Count; i++)
                {
                    notes.Add(report.Notes[i] ?? string.Empty);
                }
            }

            root.Add("notes", notes);

            List<object> issues = new List<object>();
            if (report.Issues != null)
            {
                for (int i = 0; i < report.Issues.Count; i++)
                {
                    TestIssue issue = report.Issues[i];
                    if (issue == null)
                    {
                        continue;
                    }

                    JsonObject issueObject = new JsonObject();
                    issueObject.Add("id", issue.Id ?? string.Empty);
                    issueObject.Add("category", issue.Category.ToString());
                    issueObject.Add("severity", issue.Severity.ToString());
                    issueObject.Add("status", issue.Status.ToString());
                    issueObject.Add("title", issue.Title ?? string.Empty);
                    issueObject.Add("description", issue.Description ?? string.Empty);
                    issueObject.Add("scenePath", issue.ScenePath ?? string.Empty);
                    issueObject.Add("gameObjectPath", issue.GameObjectPath ?? string.Empty);
                    issueObject.Add("message", issue.Message ?? string.Empty);
                    issueObject.Add("stackTrace", issue.StackTrace ?? string.Empty);
                    issueObject.Add("suggestion", issue.Suggestion ?? string.Empty);
                    issues.Add(issueObject);
                }
            }

            root.Add("issues", issues);
            return root;
        }

        private static string FormatDateTime(DateTime value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static void WriteValue(StringBuilder sb, object value, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            string text = value as string;
            if (text != null)
            {
                WriteString(sb, text);
                return;
            }

            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is int)
            {
                sb.Append(((int)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is long)
            {
                sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double)
            {
                sb.Append(((double)value).ToString("0.######", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float)
            {
                sb.Append(((float)value).ToString("0.######", CultureInfo.InvariantCulture));
                return;
            }

            JsonObject obj = value as JsonObject;
            if (obj != null)
            {
                WriteObject(sb, obj, indent);
                return;
            }

            List<object> list = value as List<object>;
            if (list != null)
            {
                WriteArray(sb, list, indent);
                return;
            }

            throw new ArgumentException("不支持的 JSON 值类型: " + value.GetType().FullName);
        }

        private static void WriteObject(StringBuilder sb, JsonObject obj, int indent)
        {
            if (obj.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{\n");
            for (int i = 0; i < obj.Count; i++)
            {
                AppendIndent(sb, indent + 1);
                WriteString(sb, obj[i].Key);
                sb.Append(": ");
                WriteValue(sb, obj[i].Value, indent + 1);
                if (i < obj.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            AppendIndent(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, List<object> list, int indent)
        {
            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append("[\n");
            for (int i = 0; i < list.Count; i++)
            {
                AppendIndent(sb, indent + 1);
                WriteValue(sb, list[i], indent + 1);
                if (i < list.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            AppendIndent(sb, indent);
            sb.Append(']');
        }

        private static void AppendIndent(StringBuilder sb, int indent)
        {
            sb.Append(' ', indent * 2);
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        // ------------------------------------------------------------------
        // 读取
        // ------------------------------------------------------------------

        private static TestReport ReadReport(JsonObject root)
        {
            TestReport report = new TestReport();
            report.TaskId = GetString(root, "taskId");
            report.ToolVersion = GetString(root, "toolVersion");
            report.UnityVersion = GetString(root, "unityVersion");
            report.ProjectName = GetString(root, "projectName");
            report.ProjectPath = GetString(root, "projectPath");
            report.ScenePath = GetString(root, "scenePath");
            report.TestType = GetEnum(root, "testType", TestType.ProjectScan);
            report.OverallStatus = GetEnum(root, "overallStatus", TestStatus.NotRun);
            report.StartedAtUtc = GetDateTime(root, "startedAtUtc", DateTime.UtcNow);
            report.FinishedAtUtc = GetDateTime(root, "finishedAtUtc", report.StartedAtUtc);
            report.DurationSeconds = GetDouble(root, "durationSeconds", 0.0);

            JsonObject summaryObject = GetObject(root, "summary");
            if (summaryObject != null)
            {
                report.Summary = new TestSummary();
                report.Summary.Total = GetInt(summaryObject, "total", 0);
                report.Summary.Passed = GetInt(summaryObject, "passed", 0);
                report.Summary.Failed = GetInt(summaryObject, "failed", 0);
                report.Summary.Warning = GetInt(summaryObject, "warning", 0);
                report.Summary.NotRun = GetInt(summaryObject, "notRun", 0);
                report.Summary.Cancelled = GetInt(summaryObject, "cancelled", 0);
            }

            List<object> notes = GetArray(root, "notes");
            if (notes != null)
            {
                for (int i = 0; i < notes.Count; i++)
                {
                    string note = notes[i] as string;
                    if (note != null)
                    {
                        report.Notes.Add(note);
                    }
                }
            }

            List<object> issues = GetArray(root, "issues");
            if (issues != null)
            {
                for (int i = 0; i < issues.Count; i++)
                {
                    JsonObject issueObject = issues[i] as JsonObject;
                    if (issueObject == null)
                    {
                        continue;
                    }

                    TestIssue issue = new TestIssue();
                    issue.Id = GetString(issueObject, "id");
                    issue.Category = GetEnum(issueObject, "category", IssueCategory.ProjectScan);
                    issue.Severity = GetEnum(issueObject, "severity", IssueSeverity.Info);
                    issue.Status = GetEnum(issueObject, "status", TestStatus.NotRun);
                    issue.Title = GetString(issueObject, "title");
                    issue.Description = GetString(issueObject, "description");
                    issue.ScenePath = GetString(issueObject, "scenePath");
                    issue.GameObjectPath = GetString(issueObject, "gameObjectPath");
                    issue.Message = GetString(issueObject, "message");
                    issue.StackTrace = GetString(issueObject, "stackTrace");
                    issue.Suggestion = GetString(issueObject, "suggestion");
                    report.Issues.Add(issue);
                }
            }

            if (report.Summary == null || (summaryObject == null))
            {
                report.RecalculateSummary();
            }

            return report;
        }

        private static string GetString(JsonObject obj, string key)
        {
            object value;
            if (!obj.TryGet(key, out value) || value == null)
            {
                return string.Empty;
            }

            string text = value as string;
            return text ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(JsonObject obj, string key, int fallback)
        {
            object value;
            if (!obj.TryGet(key, out value) || value == null)
            {
                return fallback;
            }

            if (value is double)
            {
                return (int)Math.Round((double)value);
            }

            string text = value as string;
            int parsed;
            if (text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static double GetDouble(JsonObject obj, string key, double fallback)
        {
            object value;
            if (!obj.TryGet(key, out value) || value == null)
            {
                return fallback;
            }

            if (value is double)
            {
                return (double)value;
            }

            string text = value as string;
            double parsed;
            if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static JsonObject GetObject(JsonObject obj, string key)
        {
            object value;
            if (!obj.TryGet(key, out value))
            {
                return null;
            }

            return value as JsonObject;
        }

        private static List<object> GetArray(JsonObject obj, string key)
        {
            object value;
            if (!obj.TryGet(key, out value))
            {
                return null;
            }

            return value as List<object>;
        }

        private static DateTime GetDateTime(JsonObject obj, string key, DateTime fallback)
        {
            string text = GetString(obj, key);
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToUniversalTime();
            }

            return fallback;
        }

        private static T GetEnum<T>(JsonObject obj, string key, T fallback) where T : struct, Enum
        {
            object value;
            if (!obj.TryGet(key, out value) || value == null)
            {
                return fallback;
            }

            string text = value as string;
            if (text != null)
            {
                T parsed;
                if (Enum.TryParse(text, true, out parsed))
                {
                    return parsed;
                }
            }

            if (value is double)
            {
                object boxed = Enum.ToObject(typeof(T), (int)Math.Round((double)value));
                if (boxed is T)
                {
                    return (T)boxed;
                }
            }

            return fallback;
        }

        /// <summary>递归下降 JSON 解析器。只接受标准 JSON，带位置信息的错误提示。</summary>
        private sealed class JsonParser
        {
            private readonly string _text;
            private int _index;
            private int _depth;

            public JsonParser(string text)
            {
                _text = text;
            }

            public object Parse()
            {
                _index = 0;
                _depth = 0;
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("JSON 内容为空");
                }

                object value = ParseValue();
                SkipWhitespace();
                if (!AtEnd)
                {
                    throw Error("JSON 结尾存在多余内容");
                }

                return value;
            }

            private bool AtEnd
            {
                get { return _index >= _text.Length; }
            }

            private object ParseValue()
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("JSON 意外结束");
                }

                char c = _text[_index];
                switch (c)
                {
                    case '{':
                        return ParseObject();
                    case '[':
                        return ParseArray();
                    case '"':
                        return ParseString();
                    case 't':
                        ExpectLiteral("true");
                        return true;
                    case 'f':
                        ExpectLiteral("false");
                        return false;
                    case 'n':
                        ExpectLiteral("null");
                        return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ParseNumber();
                        }

                        throw Error("无法识别的值起始字符 '" + c + "'");
                }
            }

            private JsonObject ParseObject()
            {
                EnterContainer();
                _index++; // {
                JsonObject obj = new JsonObject();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == '}')
                {
                    _index++;
                    _depth--;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("对象未闭合");
                    }

                    if (_text[_index] != '"')
                    {
                        throw Error("对象属性名必须是字符串");
                    }

                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != ':')
                    {
                        throw Error("对象属性名后缺少 ':'");
                    }

                    _index++;
                    object value = ParseValue();
                    obj.Add(key, value);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("对象未闭合");
                    }

                    char c = _text[_index];
                    if (c == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (c == '}')
                    {
                        _index++;
                        break;
                    }

                    throw Error("对象缺少 ',' 或 '}'");
                }

                _depth--;
                return obj;
            }

            private List<object> ParseArray()
            {
                EnterContainer();
                _index++; // [
                List<object> list = new List<object>();
                SkipWhitespace();
                if (!AtEnd && _text[_index] == ']')
                {
                    _index++;
                    _depth--;
                    return list;
                }

                while (true)
                {
                    object value = ParseValue();
                    list.Add(value);
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("数组未闭合");
                    }

                    char c = _text[_index];
                    if (c == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (c == ']')
                    {
                        _index++;
                        break;
                    }

                    throw Error("数组缺少 ',' 或 ']'");
                }

                _depth--;
                return list;
            }

            private string ParseString()
            {
                _index++; // opening quote
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("字符串未闭合");
                    }

                    char c = _text[_index++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("字符串转义序列不完整");
                    }

                    char escape = _text[_index++];
                    switch (escape)
                    {
                        case '"':
                            sb.Append('"');
                            break;
                        case '\\':
                            sb.Append('\\');
                            break;
                        case '/':
                            sb.Append('/');
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            sb.Append(ParseUnicodeEscape());
                            break;
                        default:
                            throw Error("不支持的转义字符 '\\" + escape + "'");
                    }
                }
            }

            private char ParseUnicodeEscape()
            {
                if (_index + 4 > _text.Length)
                {
                    throw Error("Unicode 转义序列不完整");
                }

                string hex = _text.Substring(_index, 4);
                int code;
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                {
                    throw Error("Unicode 转义序列无效: " + hex);
                }

                _index += 4;
                return (char)code;
            }

            private double ParseNumber()
            {
                int start = _index;
                while (!AtEnd)
                {
                    char c = _text[_index];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    {
                        _index++;
                        continue;
                    }

                    break;
                }

                string slice = _text.Substring(start, _index - start);
                double value;
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    throw Error("数字格式无效: " + slice);
                }

                return value;
            }

            private void ExpectLiteral(string literal)
            {
                if (_index + literal.Length > _text.Length ||
                    string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                {
                    throw Error("期望字面量 " + literal);
                }

                _index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    char c = _text[_index];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        _index++;
                        continue;
                    }

                    break;
                }
            }

            private void EnterContainer()
            {
                _depth++;
                if (_depth > MaxDepth)
                {
                    throw Error("JSON 嵌套层级超过 " + MaxDepth);
                }
            }

            private FormatException Error(string message)
            {
                return new FormatException(message + "（位置 " + _index + "）");
            }
        }
    }
}

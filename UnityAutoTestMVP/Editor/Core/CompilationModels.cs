using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityAutoTestMVP
{
    /// <summary>最近一次被监听程序集编译的总体状态。</summary>
    internal enum CompilationStatus
    {
        Unknown = 0,
        Succeeded = 1,
        SucceededWithWarnings = 2,
        Failed = 3
    }

    internal sealed class CompilationMessage
    {
        public bool IsError;
        public string AssemblyPath = string.Empty;
        public string File = string.Empty;
        public int Line;
        public int Column;
        public string Message = string.Empty;
    }

    internal sealed class CompilationSnapshot
    {
        public CompilationStatus Status = CompilationStatus.Unknown;
        public DateTime TimestampUtc = DateTime.MinValue;
        public int AssemblyCount;
        public bool Truncated;
        public List<CompilationMessage> Messages = new List<CompilationMessage>();

        public bool HasData
        {
            get { return Status != CompilationStatus.Unknown; }
        }

        public int ErrorCount
        {
            get { return Count(true); }
        }

        public int WarningCount
        {
            get { return Count(false); }
        }

        private int Count(bool errors)
        {
            int count = 0;
            for (int i = 0; i < Messages.Count; i++)
            {
                if (Messages[i] != null && Messages[i].IsError == errors)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// 编译消息的字符串编解码，用于存入 SessionState（SessionState 只支持字符串/整型等基础类型）。
    /// 字段使用 Base64 编码，避免分隔符、换行、中文导致的解析错误。
    /// </summary>
    internal static class CompilationMessageCodec
    {
        private const char RecordSeparator = '\n';
        private const char FieldSeparator = '|';
        private static readonly char[] FieldSeparatorArray = { FieldSeparator };

        public static string Encode(IList<CompilationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                CompilationMessage message = messages[i];
                if (message == null)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(RecordSeparator);
                }

                sb.Append(message.IsError ? 'E' : 'W');
                sb.Append(FieldSeparator);
                sb.Append(EncodeField(message.AssemblyPath));
                sb.Append(FieldSeparator);
                sb.Append(EncodeField(message.File));
                sb.Append(FieldSeparator);
                sb.Append(message.Line.ToString(CultureInfo.InvariantCulture));
                sb.Append(FieldSeparator);
                sb.Append(message.Column.ToString(CultureInfo.InvariantCulture));
                sb.Append(FieldSeparator);
                sb.Append(EncodeField(message.Message));
            }

            return sb.ToString();
        }

        public static List<CompilationMessage> Decode(string encoded)
        {
            List<CompilationMessage> result = new List<CompilationMessage>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            string[] records = encoded.Split(RecordSeparator);
            for (int i = 0; i < records.Length; i++)
            {
                string record = records[i];
                if (string.IsNullOrEmpty(record))
                {
                    continue;
                }

                string[] parts = record.Split(FieldSeparatorArray);
                if (parts.Length != 6)
                {
                    continue;
                }

                CompilationMessage message = new CompilationMessage();
                message.IsError = parts[0] == "E";
                message.AssemblyPath = DecodeField(parts[1]);
                message.File = DecodeField(parts[2]);
                message.Line = ParseInt(parts[3]);
                message.Column = ParseInt(parts[4]);
                message.Message = DecodeField(parts[5]);
                result.Add(message);
            }

            return result;
        }

        private static string EncodeField(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeField(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                // 数据由本类写入 SessionState，正常不会损坏；若被外部改写则按空字段处理，
                // 避免单条损坏记录导致整份报告无法读取。
                return string.Empty;
            }
        }

        private static int ParseInt(string value)
        {
            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return 0;
        }
    }
}

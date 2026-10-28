using System;

namespace UnityAutoTestMVP
{
    /// <summary>
    /// 工具级常量。所有 SessionState 键都集中在这里，避免拼写不一致。
    /// </summary>
    internal static class AutoTestConstants
    {
        public const string ToolName = "Unity Auto Test MVP";
        public const string ToolVersion = "1.0.0";
        public const string MenuPath = "Tools/Unity Auto Test MVP";
        public const string WindowTitle = "Unity Auto Test MVP";

        public const int ReportSchemaVersion = 1;

        public const string ReportsRelativeFolder = "Library/UnityAutoTestMVP/Reports";

        public const int MinSmokeDurationSeconds = 5;
        public const int MaxSmokeDurationSeconds = 120;
        public const int DefaultSmokeDurationSeconds = 10;

        /// <summary>单次冒烟测试最多缓冲的运行时日志条数，防止大量日志拖垮编辑器。</summary>
        public const int MaxRuntimeLogEntries = 2000;

        /// <summary>单份报告中最多保留的编译消息条数。</summary>
        public const int MaxCompilationMessages = 100;

        /// <summary>报告中单条堆栈的最大字符数，避免报告过大。</summary>
        public const int MaxStackCharacters = 6000;

        internal static class SessionKeys
        {
            private const string Root = "UnityAutoTestMVP.";

            public const string Phase = Root + "Smoke.Phase";
            public const string TaskId = Root + "Smoke.TaskId";
            public const string ScenePath = Root + "Smoke.ScenePath";
            public const string SceneName = Root + "Smoke.SceneName";
            public const string StartUtcTicks = Root + "Smoke.StartUtcTicks";
            public const string PlayStartUtcTicks = Root + "Smoke.PlayStartUtcTicks";
            public const string DurationSeconds = Root + "Smoke.DurationSeconds";
            public const string IncludeWarnings = Root + "Smoke.IncludeWarnings";
            public const string StopRequested = Root + "Smoke.StopRequested";
            public const string NaturalTimeout = Root + "Smoke.NaturalTimeout";
            public const string ReloadDuringRun = Root + "Smoke.ReloadDuringRun";

            public const string LastScanReportPath = Root + "History.LastScanReportPath";
            public const string LastSmokeReportPath = Root + "History.LastSmokeReportPath";

            public const string CompileStatus = Root + "Compile.Status";
            public const string CompileTimestampUtc = Root + "Compile.TimestampUtc";
            public const string CompileMessages = Root + "Compile.Messages";
            public const string CompileAssemblyCount = Root + "Compile.AssemblyCount";
            public const string CompileTruncated = Root + "Compile.Truncated";
        }
    }
}

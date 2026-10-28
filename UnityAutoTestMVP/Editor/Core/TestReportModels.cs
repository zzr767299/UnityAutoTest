using System;
using System.Collections.Generic;

namespace UnityAutoTestMVP
{
    /// <summary>检测任务类型。</summary>
    public enum TestType
    {
        ProjectScan = 0,
        SmokeTest = 1
    }

    /// <summary>单条检查项以及整份报告的状态。</summary>
    public enum TestStatus
    {
        Passed = 0,
        Failed = 1,
        Warning = 2,
        NotRun = 3,
        Cancelled = 4
    }

    public enum IssueSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Critical = 3
    }

    /// <summary>
    /// 问题类别。编译错误与运行时错误使用不同类别，报告中不会混在一起。
    /// </summary>
    public enum IssueCategory
    {
        ProjectScan = 0,
        SceneAsset = 1,
        SceneLoad = 2,
        MissingScript = 3,
        SceneScan = 4,
        RuntimeError = 5,
        RuntimeWarning = 6,
        CompilationError = 7,
        CompilationWarning = 8,
        CompilationState = 9,
        TestInfrastructure = 10
    }

    /// <summary>
    /// 一条结构化的检查记录（通过、失败、警告、未执行或已取消）。
    /// 不使用拼接字符串，便于后续扩展 AI 分析、测试用例等能力。
    /// </summary>
    [Serializable]
    public sealed class TestIssue
    {
        public string Id = string.Empty;
        public IssueCategory Category;
        public IssueSeverity Severity;
        public TestStatus Status;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string ScenePath = string.Empty;
        public string GameObjectPath = string.Empty;
        public string Message = string.Empty;
        public string StackTrace = string.Empty;
        public string Suggestion = string.Empty;
    }

    /// <summary>按状态统计的检查项数量。</summary>
    [Serializable]
    public sealed class TestSummary
    {
        public int Total;
        public int Passed;
        public int Failed;
        public int Warning;
        public int NotRun;
        public int Cancelled;

        public void Reset()
        {
            Total = 0;
            Passed = 0;
            Failed = 0;
            Warning = 0;
            NotRun = 0;
            Cancelled = 0;
        }
    }

    /// <summary>
    /// 一次静态扫描或一次冒烟测试的完整结构化结果。
    /// </summary>
    [Serializable]
    public sealed class TestReport
    {
        public string TaskId = string.Empty;
        public string ToolVersion = string.Empty;
        public string UnityVersion = string.Empty;
        public string ProjectName = string.Empty;
        public string ProjectPath = string.Empty;
        public string ScenePath = string.Empty;
        public TestType TestType;
        public TestStatus OverallStatus = TestStatus.NotRun;
        public DateTime StartedAtUtc = DateTime.UtcNow;
        public DateTime FinishedAtUtc = DateTime.UtcNow;
        public double DurationSeconds;
        public TestSummary Summary = new TestSummary();
        public List<TestIssue> Issues = new List<TestIssue>();
        public List<string> Notes = new List<string>();

        public static TestReport Create(
            TestType testType,
            string unityVersion,
            string projectName,
            string projectPath,
            string scenePath)
        {
            TestReport report = new TestReport();
            report.TaskId = Guid.NewGuid().ToString("N");
            report.ToolVersion = AutoTestConstants.ToolVersion;
            report.UnityVersion = unityVersion ?? string.Empty;
            report.ProjectName = projectName ?? string.Empty;
            report.ProjectPath = projectPath ?? string.Empty;
            report.ScenePath = scenePath ?? string.Empty;
            report.TestType = testType;
            report.StartedAtUtc = DateTime.UtcNow;
            report.FinishedAtUtc = report.StartedAtUtc;
            return report;
        }

        /// <summary>根据 Issues 重新统计各状态数量。</summary>
        public void RecalculateSummary()
        {
            if (Summary == null)
            {
                Summary = new TestSummary();
            }

            Summary.Reset();
            if (Issues == null)
            {
                return;
            }

            for (int i = 0; i < Issues.Count; i++)
            {
                TestIssue issue = Issues[i];
                if (issue == null)
                {
                    continue;
                }

                Summary.Total++;
                switch (issue.Status)
                {
                    case TestStatus.Passed:
                        Summary.Passed++;
                        break;
                    case TestStatus.Failed:
                        Summary.Failed++;
                        break;
                    case TestStatus.Warning:
                        Summary.Warning++;
                        break;
                    case TestStatus.Cancelled:
                        Summary.Cancelled++;
                        break;
                    default:
                        Summary.NotRun++;
                        break;
                }
            }
        }

        /// <summary>当调用方没有显式指定总体状态时，根据问题列表推导一个保守结果。</summary>
        public TestStatus DeriveOverallStatus()
        {
            RecalculateSummary();
            if (Summary.Failed > 0)
            {
                return TestStatus.Failed;
            }

            if (Summary.Warning > 0)
            {
                return TestStatus.Warning;
            }

            if (Summary.Passed > 0)
            {
                return TestStatus.Passed;
            }

            if (Summary.Cancelled > 0)
            {
                return TestStatus.Cancelled;
            }

            return TestStatus.NotRun;
        }

        /// <summary>结束报告：写入结束时间、时长并重新统计。</summary>
        public void Finish(TestStatus overallStatus)
        {
            FinishedAtUtc = DateTime.UtcNow;
            DurationSeconds = Math.Max(0.0, (FinishedAtUtc - StartedAtUtc).TotalSeconds);
            RecalculateSummary();
            OverallStatus = overallStatus;
        }

        /// <summary>为所有问题分配稳定、可读的编号。</summary>
        public void AssignIssueIds()
        {
            if (Issues == null)
            {
                return;
            }

            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i] != null)
                {
                    Issues[i].Id = "ISSUE-" + (i + 1).ToString("D4");
                }
            }
        }
    }

    /// <summary>内部使用：一次冒烟测试结束后的原始数据（不直接序列化）。</summary>
    internal enum SmokeEndReason
    {
        Timeout = 0,
        UserCancelled = 1,
        UserExitedPlayMode = 2,
        EditorQuitting = 3,
        NotEnteredPlayMode = 4
    }
}

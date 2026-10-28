using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAutoTestMVP
{
    /// <summary>
    /// 冒烟测试的对外入口：启动前检查、把运行结果转换成结构化报告。
    /// 具体的计时、日志捕获和 Play Mode 生命周期由 AutoTestSession 负责。
    /// </summary>
    internal static class SmokeTestRunner
    {
        /// <summary>返回 null 表示可以启动；否则返回不可启动的原因。</summary>
        public static string GetStartBlockReason()
        {
            if (AutoTestSession.IsRunning)
            {
                return "已有冒烟测试正在运行，请等待结束或先停止。";
            }

            if (EditorApplication.isCompiling)
            {
                return "Unity 正在编译脚本，请等待编译完成。";
            }

            if (EditorApplication.isUpdating)
            {
                return "Unity 正在导入/刷新资源，请稍后重试。";
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Unity 当前已处于 Play Mode 或正在切换 Play Mode。";
            }

            if (Application.isBatchMode)
            {
                return "批处理模式（batchmode）下不支持交互式 Play Mode 冒烟测试。";
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return "没有可用的活动场景。";
            }

            if (EditorSceneManager.IsPreviewScene(scene))
            {
                return "当前处于 Prefab 编辑模式（Prefab Stage），请先返回主场景。";
            }

            if (string.IsNullOrEmpty(scene.path))
            {
                return "当前活动场景尚未保存为资源（untitled scene），请先保存场景。";
            }

            if (!SceneFileExists(scene.path))
            {
                return "当前活动场景文件不存在：" + scene.path;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loadedScene = SceneManager.GetSceneAt(i);
                if (!loadedScene.IsValid() || !loadedScene.isLoaded || EditorSceneManager.IsPreviewScene(loadedScene))
                {
                    continue;
                }

                if (loadedScene.isDirty)
                {
                    return "场景 '" + loadedScene.name +
                           "' 有未保存的修改。Unity 在进入 Play Mode 时可能弹出保存对话框；请先保存或撤销修改（工具不会自动保存）。";
                }
            }

            CompilationSnapshot snapshot = CompilationMonitor.GetSnapshot();
            if (snapshot.Status == CompilationStatus.Failed)
            {
                string timeText = snapshot.TimestampUtc == DateTime.MinValue
                    ? "未知时间"
                    : snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return "最近一次监听到的编译失败（" + timeText + "），请先修复编译错误。";
            }

            return null;
        }

        public static bool TryStart(out string error)
        {
            error = string.Empty;

            if (AutoTestSession.IsRunning)
            {
                error = "已有冒烟测试正在运行，请等待结束或先停止。";
                return false;
            }

            Scene scene = SceneManager.GetActiveScene();
            string scenePath = scene.IsValid() ? scene.path : string.Empty;
            string sceneName = scene.IsValid() ? scene.name : string.Empty;

            string blockReason = GetStartBlockReason();
            if (blockReason != null)
            {
                error = blockReason;
                CreateBlockedReport(scenePath, blockReason, true);
                return false;
            }

            SceneAsset sceneAsset = null;
            try
            {
                sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            }
            catch (Exception ex)
            {
                error = "加载活动场景资源时发生异常：" + ex.GetType().Name + ": " + ex.Message;
                CreateBlockedReport(scenePath, error, true);
                return false;
            }

            if (sceneAsset == null)
            {
                error = "当前活动场景资源无法加载：" + scenePath;
                CreateBlockedReport(scenePath, error, true);
                return false;
            }

            if (!AutoTestSession.TryStart(
                    scenePath,
                    sceneName,
                    AutoTestSettings.SmokeDurationSeconds,
                    AutoTestSettings.IncludeWarnings,
                    out error))
            {
                CreateBlockedReport(scenePath, error, true);
                return false;
            }

            return true;
        }

        public static bool TryStop(out string message)
        {
            return AutoTestSession.RequestStop(out message);
        }

        /// <summary>生成“测试未执行/被阻断”的 NotRun 报告，并可选地立即保存。</summary>
        public static TestReport CreateBlockedReport(string scenePath, string reason, bool save)
        {
            TestReport report = TestReport.Create(
                TestType.SmokeTest,
                AutoTestEnvironment.UnityVersion,
                AutoTestEnvironment.ProjectName,
                AutoTestEnvironment.ProjectRoot,
                scenePath ?? string.Empty);

            TestIssue issue = new TestIssue();
            issue.Category = IssueCategory.TestInfrastructure;
            issue.Severity = IssueSeverity.Warning;
            issue.Status = TestStatus.NotRun;
            issue.Title = "冒烟测试未执行";
            issue.Description = reason ?? string.Empty;
            issue.ScenePath = scenePath ?? string.Empty;
            issue.Suggestion = "解决上述环境/项目问题后重新运行冒烟测试。";
            report.Issues.Add(issue);

            AppendSmokeScopeNotes(report);
            report.Notes.Add("本次没有进入 Play Mode，因此没有捕获任何运行时日志。");

            report.AssignIssueIds();
            report.Finish(TestStatus.NotRun);

            if (save)
            {
                AutoTestHistory.SaveCompletedReport(report);
            }

            Debug.LogWarning("[UnityAutoTestMVP] 冒烟测试未执行：" + reason);
            return report;
        }

        /// <summary>由 AutoTestSession 在退出 Play Mode 时调用（Domain Reload 之前）。</summary>
        public static void HandleRunCompleted(SmokeRunOutcome outcome)
        {
            if (outcome == null)
            {
                Debug.LogError("[UnityAutoTestMVP] 冒烟测试结束回调收到 null 结果，无法生成报告。");
                return;
            }

            TestReport report = TestReport.Create(
                TestType.SmokeTest,
                AutoTestEnvironment.UnityVersion,
                AutoTestEnvironment.ProjectName,
                AutoTestEnvironment.ProjectRoot,
                outcome.ScenePath);

            if (!string.IsNullOrEmpty(outcome.TaskId))
            {
                report.TaskId = outcome.TaskId;
            }

            report.StartedAtUtc = outcome.StartedUtc;
            report.FinishedAtUtc = outcome.EndedUtc;

            AppendLifecycleIssue(report, outcome);

            int errorCount = 0;
            int warningCount = 0;
            int ignoredWarningCount = 0;
            int ignoredLogCount = 0;

            List<RuntimeLogEntry> capturedLogs = outcome.Logs ?? new List<RuntimeLogEntry>();
            for (int i = 0; i < capturedLogs.Count; i++)
            {
                RuntimeLogEntry log = capturedLogs[i];
                if (log == null)
                {
                    continue;
                }

                bool isErrorType = log.Type == LogType.Error ||
                                   log.Type == LogType.Exception ||
                                   log.Type == LogType.Assert;
                bool isWarningType = log.Type == LogType.Warning;

                if (!isErrorType && !isWarningType)
                {
                    ignoredLogCount++;
                    continue;
                }

                if (isWarningType && !outcome.IncludeWarnings)
                {
                    ignoredWarningCount++;
                    continue;
                }

                if (isWarningType)
                {
                    warningCount++;
                }
                else
                {
                    errorCount++;
                }

                report.Issues.Add(CreateLogIssue(log, outcome.ScenePath, isWarningType));
            }

            if (outcome.EndReason == SmokeEndReason.Timeout && errorCount == 0 && warningCount == 0)
            {
                TestIssue pass = new TestIssue();
                pass.Category = IssueCategory.RuntimeError;
                pass.Severity = IssueSeverity.Info;
                pass.Status = TestStatus.Passed;
                pass.Title = "运行期间未捕获目标错误日志";
                pass.Description = "冒烟测试运行 " + outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                                   " 秒，未捕获 Error / Exception / Assert" +
                                   (outcome.IncludeWarnings ? " / Warning" : string.Empty) + " 日志。";
                pass.ScenePath = outcome.ScenePath;
                pass.Suggestion = "注意：没有报错不代表游戏所有功能均正确。";
                report.Issues.Add(pass);
            }

            TestStatus overall;
            if (outcome.EndReason == SmokeEndReason.UserCancelled ||
                outcome.EndReason == SmokeEndReason.UserExitedPlayMode ||
                outcome.EndReason == SmokeEndReason.EditorQuitting)
            {
                overall = TestStatus.Cancelled;
            }
            else if (outcome.EndReason == SmokeEndReason.NotEnteredPlayMode)
            {
                overall = TestStatus.NotRun;
            }
            else if (errorCount > 0)
            {
                overall = TestStatus.Failed;
            }
            else if (warningCount > 0)
            {
                overall = TestStatus.Warning;
            }
            else
            {
                overall = TestStatus.Passed;
            }

            AppendSmokeScopeNotes(report);

            report.Notes.Add("配置运行时长：" + outcome.ConfiguredDurationSeconds + " 秒；实际运行：" +
                             outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
            report.Notes.Add("本次共收到 " + capturedLogs.Count + " 条日志；纳入报告的错误：" + errorCount +
                             " 条，警告：" + warningCount + " 条，未纳入的普通 Log：" + ignoredLogCount + " 条。");

            if (!outcome.IncludeWarnings)
            {
                report.Notes.Add("配置未将 Warning 纳入报告；本次捕获到 " + ignoredWarningCount + " 条 Warning，仅统计数量。");
            }

            if (outcome.LogsTruncated)
            {
                report.Notes.Add("日志数量超过上限（" + AutoTestConstants.MaxRuntimeLogEntries + " 条），仅保留前 " +
                                 AutoTestConstants.MaxRuntimeLogEntries + " 条。");
            }

            if (outcome.ReloadDuringRun)
            {
                report.Notes.Add("测试期间发生了程序集重载（脚本编译），重载之前的日志可能丢失，结果可能不完整。");
            }

            if (!string.IsNullOrEmpty(outcome.Detail))
            {
                report.Notes.Add("结束说明：" + outcome.Detail);
            }

            if (overall == TestStatus.Cancelled)
            {
                report.Notes.Add("测试被取消，不能视为通过。");
            }

            report.AssignIssueIds();
            report.Finish(overall);

            // Finish 使用当前时间；这里改用真实测试结束时间，保证报告耗时准确。
            report.FinishedAtUtc = outcome.EndedUtc;
            report.DurationSeconds = Math.Max(0.0, (outcome.EndedUtc - report.StartedAtUtc).TotalSeconds);

            string saveError = AutoTestHistory.SaveCompletedReport(report);
            if (!string.IsNullOrEmpty(saveError))
            {
                Debug.LogError("[UnityAutoTestMVP] 冒烟测试结果未能保存为报告文件：" + saveError);
            }

            Debug.Log("[UnityAutoTestMVP] 冒烟测试结束：" + overall + "；错误 " + errorCount + " 条，警告 " +
                      warningCount + " 条，耗时 " + outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
        }

        private static void AppendLifecycleIssue(TestReport report, SmokeRunOutcome outcome)
        {
            TestIssue issue = new TestIssue();
            issue.ScenePath = outcome.ScenePath;
            issue.Category = IssueCategory.TestInfrastructure;

            switch (outcome.EndReason)
            {
                case SmokeEndReason.Timeout:
                    issue.Severity = IssueSeverity.Info;
                    issue.Status = TestStatus.Passed;
                    issue.Title = "已运行设定时长并自动退出 Play Mode";
                    issue.Description = "测试运行 " + outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                                        " 秒（配置 " + outcome.ConfiguredDurationSeconds + " 秒），随后自动退出 Play Mode。";
                    break;

                case SmokeEndReason.UserCancelled:
                    issue.Severity = IssueSeverity.Warning;
                    issue.Status = TestStatus.Cancelled;
                    issue.Title = "用户主动停止测试";
                    issue.Description = outcome.HadPlayStarted
                        ? "用户点击“停止测试”，测试在运行 " + outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                          " 秒后被取消。取消的测试不能视为通过。"
                        : "用户在进入 Play Mode 之前取消了测试。";
                    break;

                case SmokeEndReason.UserExitedPlayMode:
                    issue.Severity = IssueSeverity.Warning;
                    issue.Status = TestStatus.Cancelled;
                    issue.Title = "用户手动退出 Play Mode";
                    issue.Description = "检测到 Play Mode 被用户或其他操作结束，测试在运行 " +
                                        outcome.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                                        " 秒后被取消。取消的测试不能视为通过。";
                    break;

                case SmokeEndReason.EditorQuitting:
                    issue.Severity = IssueSeverity.Warning;
                    issue.Status = TestStatus.Cancelled;
                    issue.Title = "Unity Editor 关闭，测试取消";
                    issue.Description = "Unity Editor 在测试过程中关闭，测试被取消，结果可能不完整。";
                    break;

                default:
                    issue.Severity = IssueSeverity.Error;
                    issue.Status = TestStatus.NotRun;
                    issue.Title = "未能进入 Play Mode，测试被阻断";
                    issue.Description = string.IsNullOrEmpty(outcome.Detail)
                        ? "请求进入 Play Mode 后未能成功运行，测试未执行。"
                        : outcome.Detail;
                    issue.Suggestion = "检查 Unity Console 中的编译错误、脚本异常或弹窗，解决后重新运行。";
                    break;
            }

            if (!string.IsNullOrEmpty(outcome.Detail) && outcome.EndReason != SmokeEndReason.NotEnteredPlayMode)
            {
                issue.Message = outcome.Detail;
            }

            report.Issues.Add(issue);
        }

        private static TestIssue CreateLogIssue(RuntimeLogEntry log, string scenePath, bool isWarning)
        {
            TestIssue issue = new TestIssue();
            issue.Category = isWarning ? IssueCategory.RuntimeWarning : IssueCategory.RuntimeError;
            issue.Severity = isWarning ? IssueSeverity.Warning : IssueSeverity.Error;
            issue.Status = isWarning ? TestStatus.Warning : TestStatus.Failed;
            issue.Title = FormatLogTitle(log);
            issue.Description = "捕获时间：" + log.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                                "；日志类型：" + log.Type + "。";
            issue.ScenePath = scenePath ?? string.Empty;
            issue.Message = log.Message ?? string.Empty;
            issue.StackTrace = log.StackTrace ?? string.Empty;
            issue.Suggestion = isWarning
                ? "评估该警告是否会影响目标功能；警告不会单独导致冒烟测试失败，但会降低总体状态。"
                : "根据错误消息和堆栈定位问题并修复，然后重新运行冒烟测试。";
            return issue;
        }

        private static string FormatLogTitle(RuntimeLogEntry log)
        {
            string firstLine = FirstLine(log.Message);
            if (string.IsNullOrEmpty(firstLine))
            {
                firstLine = "（空日志消息）";
            }

            if (firstLine.Length > 120)
            {
                firstLine = firstLine.Substring(0, 120) + "...";
            }

            return "[" + log.Type + "] " + firstLine;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int newline = text.IndexOf('\n');
            return (newline >= 0 ? text.Substring(0, newline) : text).Trim();
        }

        private static void AppendSmokeScopeNotes(TestReport report)
        {
            report.Notes.Add("本报告来自基础冒烟测试：只验证当前场景能进入 Play Mode 并在设定时长内没有捕获目标错误日志，不代表游戏玩法、UI、关卡或业务逻辑已经正确。");
            report.Notes.Add("冒烟测试不会点击 UI、不会模拟输入，也不会自动探索关卡。");
            report.Notes.Add("日志捕获范围：从请求进入 Play Mode 开始（包含场景加载与 Awake/初始化阶段），到退出 Play Mode 时结束，使用 Application.logMessageReceivedThreaded；测试开始前的历史日志不计入本次结果。");
            report.Notes.Add("工具无法读取 Unity Console 中的历史日志；普通 Log 不纳入报告。");
            report.Notes.Add("Unity 可能对同一个问题产生多条日志，报告按日志条数统计，未自动去重。");
            report.Notes.Add("工具不会修改任何场景、GameObject 或项目设置。");
        }

        private static bool SceneFileExists(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return false;
            }

            // 包内场景（Packages/...）在磁盘上的实际位置由 Package Manager 管理，这里不强行检查。
            if (scenePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                return File.Exists(Path.Combine(AutoTestEnvironment.ProjectRoot, scenePath));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}

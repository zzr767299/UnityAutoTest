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
    /// 项目级静态扫描：发现场景、校验场景资源、逐场景检查缺失脚本，并汇总编译监听到的信息。
    /// 本类只返回 TestReport，不负责落盘；调用方应使用 AutoTestHistory.SaveCompletedReport 保存。
    /// </summary>
    internal static class ProjectScanner
    {
        public static TestReport Scan(bool scanAllScenesInAssets)
        {
            TestReport report = TestReport.Create(
                TestType.ProjectScan,
                AutoTestEnvironment.UnityVersion,
                AutoTestEnvironment.ProjectName,
                AutoTestEnvironment.ProjectRoot,
                string.Empty);

            bool cancelled = false;

            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.TestInfrastructure,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "项目扫描未执行",
                        "Unity 当前处于 Play Mode 或正在切换 Play Mode，无法安全地加载和扫描场景。",
                        "请先退出 Play Mode，再运行扫描。"));
                    report.Notes.Add("扫描未执行：Play Mode 中打开场景可能影响正在运行的游戏。");
                    report.AssignIssueIds();
                    report.Finish(TestStatus.NotRun);
                    return report;
                }

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.TestInfrastructure,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "项目扫描未执行",
                        "Unity 正在编译脚本或导入资源，此时加载场景可能导致结果不可靠。",
                        "请等待编译/导入完成后再运行扫描。"));
                    report.Notes.Add("扫描未执行：Unity 正在编译或导入资源。");
                    report.AssignIssueIds();
                    report.Finish(TestStatus.NotRun);
                    return report;
                }

                Scene currentScene = SceneManager.GetActiveScene();
                if (currentScene.IsValid() && EditorSceneManager.IsPreviewScene(currentScene))
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.TestInfrastructure,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "项目扫描未执行",
                        "Unity 当前处于 Prefab 编辑模式，工具不会在 Prefab Stage 打开时加载其他场景。",
                        "请先退出 Prefab Mode（返回主场景），再运行扫描。"));
                    report.Notes.Add("扫描未执行：当前处于 Prefab 编辑模式。");
                    report.AssignIssueIds();
                    report.Finish(TestStatus.NotRun);
                    return report;
                }

                Progress(0.02f, "正在查找场景...");

                string scopeNote;
                List<string> scenePaths = DiscoverScenes(scanAllScenesInAssets, report, out scopeNote);
                report.Notes.Add(scopeNote);

                if (scenePaths.Count == 0)
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.ProjectScan,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "未找到可扫描的场景",
                        "Build Settings 中没有启用的场景，且 Assets 目录下也没有发现 .unity 场景文件。",
                        "请在 Build Settings 中添加场景，或确认 Assets 目录中存在场景文件。"));
                    report.Notes.Add("没有执行任何场景扫描。");
                    report.AssignIssueIds();
                    report.Finish(TestStatus.NotRun);
                    return report;
                }

                report.Notes.Add("本次共发现 " + scenePaths.Count.ToString(CultureInfo.InvariantCulture) + " 个待扫描场景。");

                int scannedSceneCount = 0;
                int totalGameObjects = 0;
                int totalMissingScriptObjects = 0;
                int totalMissingScriptComponents = 0;

                for (int i = 0; i < scenePaths.Count; i++)
                {
                    string scenePath = scenePaths[i];
                    float progress = 0.05f + 0.85f * ((float)i / scenePaths.Count);
                    if (Progress(progress, "正在扫描场景 " + (i + 1) + "/" + scenePaths.Count + "：" + scenePath))
                    {
                        cancelled = true;
                    }

                    if (cancelled)
                    {
                        break;
                    }

                    SceneAsset sceneAsset = null;
                    string assetError = string.Empty;
                    try
                    {
                        sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
                    }
                    catch (Exception ex)
                    {
                        assetError = ex.GetType().Name + ": " + ex.Message;
                    }

                    if (sceneAsset == null)
                    {
                        report.Issues.Add(CreateIssue(
                            IssueCategory.SceneAsset,
                            IssueSeverity.Error,
                            TestStatus.Failed,
                            "场景资源无效",
                            "无法通过 AssetDatabase 加载该场景资源，路径可能失效或场景文件已损坏。",
                            "确认场景文件存在并能在 Project 窗口中正常打开，必要时重新添加场景到 Build Settings。",
                            scenePath,
                            string.Empty,
                            string.IsNullOrEmpty(assetError) ? "AssetDatabase.LoadAssetAtPath<SceneAsset> 返回 null。" : assetError,
                            string.Empty));

                        report.Issues.Add(CreateIssue(
                            IssueCategory.SceneLoad,
                            IssueSeverity.Warning,
                            TestStatus.NotRun,
                            "未加载场景",
                            "场景资源无效，未执行加载检查。",
                            string.Empty,
                            scenePath,
                            string.Empty,
                            string.Empty,
                            string.Empty));
                        report.Issues.Add(CreateIssue(
                            IssueCategory.MissingScript,
                            IssueSeverity.Warning,
                            TestStatus.NotRun,
                            "未执行丢失脚本检测",
                            "场景资源无效，未执行检测，不能视为通过。",
                            string.Empty,
                            scenePath,
                            string.Empty,
                            string.Empty,
                            string.Empty));
                        report.Issues.Add(CreateIssue(
                            IssueCategory.SceneScan,
                            IssueSeverity.Warning,
                            TestStatus.NotRun,
                            "场景扫描未执行",
                            "场景资源无效，扫描未执行，不能视为通过。",
                            string.Empty,
                            scenePath,
                            string.Empty,
                            string.Empty,
                            string.Empty));
                        continue;
                    }

                    report.Issues.Add(CreateIssue(
                        IssueCategory.SceneAsset,
                        IssueSeverity.Info,
                        TestStatus.Passed,
                        "场景资源有效",
                        "可以通过 AssetDatabase 正常加载场景资源。",
                        string.Empty,
                        scenePath,
                        string.Empty,
                        string.Empty,
                        string.Empty));

                    SceneScanOutcome outcome = SceneScanner.Scan(scenePath);
                    scannedSceneCount++;
                    totalGameObjects += outcome.GameObjectCount;
                    totalMissingScriptObjects += outcome.MissingScriptGameObjectCount;
                    totalMissingScriptComponents += outcome.MissingScriptComponentCount;

                    if (outcome.Issues != null)
                    {
                        report.Issues.AddRange(outcome.Issues);
                    }

                    if (outcome.Notes != null)
                    {
                        report.Notes.AddRange(outcome.Notes);
                    }

                    if (outcome.OpenedByTool && outcome.LoadSucceeded)
                    {
                        report.Notes.Add("场景 " + scenePath + " 由工具临时加载并已尝试关闭（未保存任何修改）。");
                    }
                }

                if (cancelled)
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.ProjectScan,
                        IssueSeverity.Warning,
                        TestStatus.Cancelled,
                        "扫描被用户取消",
                        "用户在扫描过程中取消了任务，已扫描 " + scannedSceneCount + " / " + scenePaths.Count + " 个场景，结果不完整。",
                        "重新运行扫描以获得完整结果。"));
                    report.Notes.Add("扫描被取消：结果只覆盖已完成的场景。");
                }

                AppendCompilationIssues(report);

                report.Notes.Add("已完成场景数：" + scannedSceneCount + " / " + scenePaths.Count +
                                 "；检查 GameObject 总数：" + totalGameObjects +
                                 "；缺失脚本 GameObject 数：" + totalMissingScriptObjects +
                                 "；缺失脚本组件数：" + totalMissingScriptComponents + "。");
                report.Notes.Add("扫描以 Additive 方式临时加载未打开的场景，并在结束后关闭；已经打开的场景不会被关闭或保存。");
                report.Notes.Add("缺失脚本检测覆盖场景中所有 GameObject（包括根节点、子节点和未激活对象），但不会单独打开 Prefab 资源文件。");
                report.Notes.Add("本工具只读取场景数据，不会删除缺失脚本、不会保存场景，也不会修改 Prefab、ScriptableObject 或项目设置。");
                report.Notes.Add("编译信息仅来自工具启动后监听到的程序集编译事件；Unity Console 中的历史编译错误无法读取。");
            }
            catch (Exception ex)
            {
                report.Issues.Add(CreateIssue(
                    IssueCategory.ProjectScan,
                    IssueSeverity.Critical,
                    TestStatus.Failed,
                    "项目扫描发生未预期异常",
                    "扫描过程中出现未预期异常，扫描可能不完整。",
                    "请查看错误消息与堆栈，并向工具维护者反馈。",
                    string.Empty,
                    string.Empty,
                    ex.Message,
                    ex.ToString()));
                report.Notes.Add("扫描因未预期异常提前终止，结果不完整。");
                Debug.LogException(ex);
            }
            finally
            {
                if (!Application.isBatchMode)
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            report.RecalculateSummary();

            TestStatus overall;
            if (cancelled)
            {
                overall = TestStatus.Cancelled;
            }
            else if (report.Summary.Failed > 0)
            {
                overall = TestStatus.Failed;
            }
            else if (report.Summary.Warning > 0)
            {
                overall = TestStatus.Warning;
            }
            else if (report.Summary.Passed > 0)
            {
                overall = TestStatus.Passed;
            }
            else
            {
                overall = TestStatus.NotRun;
            }

            report.AssignIssueIds();
            report.Finish(overall);
            return report;
        }

        private static List<string> DiscoverScenes(bool scanAllScenesInAssets, TestReport report, out string scopeNote)
        {
            List<string> buildScenes = new List<string>();
            int disabledCount = 0;

            EditorBuildSettingsScene[] buildSettingsScenes = EditorBuildSettings.scenes;
            if (buildSettingsScenes != null)
            {
                for (int i = 0; i < buildSettingsScenes.Length; i++)
                {
                    EditorBuildSettingsScene scene = buildSettingsScenes[i];
                    if (scene == null || string.IsNullOrEmpty(scene.path))
                    {
                        continue;
                    }

                    if (scene.enabled)
                    {
                        buildScenes.Add(scene.path);
                    }
                    else
                    {
                        disabledCount++;
                    }
                }
            }

            if (!scanAllScenesInAssets && buildScenes.Count > 0)
            {
                scopeNote = "扫描范围：Build Settings 中启用的 " + buildScenes.Count +
                            " 个场景（另有 " + disabledCount + " 个未启用场景被跳过）。";
                return SortAndDedupe(buildScenes);
            }

            List<string> allScenes = FindAllScenesInAssets();
            if (scanAllScenesInAssets)
            {
                scopeNote = "扫描范围：用户选择扫描 Assets 下全部场景，共发现 " + allScenes.Count + " 个。";
                if (buildScenes.Count > 0)
                {
                    scopeNote += "（Build Settings 中启用 " + buildScenes.Count + " 个，仅作为参考。）";
                }

                return allScenes;
            }

            scopeNote = "Build Settings 中没有启用的场景，已自动回退为扫描 Assets 下全部 .unity 场景，共 " + allScenes.Count + " 个。";
            if (buildScenes.Count == 0 && disabledCount > 0)
            {
                scopeNote += "（存在 " + disabledCount + " 个未启用场景。）";
            }

            return allScenes;
        }

        private static List<string> FindAllScenesInAssets()
        {
            List<string> scenes = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
            if (guids == null)
            {
                return scenes;
            }

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    scenes.Add(path);
                }
            }

            return SortAndDedupe(scenes);
        }

        private static List<string> SortAndDedupe(List<string> scenes)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            scenes.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < scenes.Count; i++)
            {
                string normalized = scenes[i].Replace('\\', '/').Trim();
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static void AppendCompilationIssues(TestReport report)
        {
            CompilationSnapshot snapshot = CompilationMonitor.GetSnapshot();
            if (!snapshot.HasData)
            {
                report.Issues.Add(CreateIssue(
                    IssueCategory.CompilationState,
                    IssueSeverity.Info,
                    TestStatus.NotRun,
                    "未监听到编译结果",
                    "工具只记录自身启动之后发生的程序集编译事件，无法读取 Unity Console 中的历史编译错误，因此不能判断项目当前是否能通过编译。",
                    "如需编译检查，请在 Unity 中触发一次重新编译（例如修改并保存任意脚本），然后再运行扫描。"));
                report.Notes.Add("编译状态未知：自工具启动以来未监听到编译完成事件。");
                return;
            }

            string timeText = snapshot.TimestampUtc == DateTime.MinValue
                ? "未知时间"
                : snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            string summary = "监听时间：" + timeText + "；参与编译的程序集数量：" + snapshot.AssemblyCount +
                             "；错误：" + snapshot.ErrorCount + "；警告：" + snapshot.WarningCount + "。";

            switch (snapshot.Status)
            {
                case CompilationStatus.Succeeded:
                    report.Issues.Add(CreateIssue(
                        IssueCategory.CompilationState,
                        IssueSeverity.Info,
                        TestStatus.Passed,
                        "最近一次编译通过（工具监听期间）",
                        "工具监听到的程序集编译已完成，没有错误或警告。" + summary,
                        string.Empty));
                    break;
                case CompilationStatus.SucceededWithWarnings:
                    report.Issues.Add(CreateIssue(
                        IssueCategory.CompilationState,
                        IssueSeverity.Warning,
                        TestStatus.Warning,
                        "最近一次编译存在警告",
                        "编译已完成，但存在警告。" + summary,
                        "检查下方编译警告；警告不会阻止运行，但可能预示问题。"));
                    break;
                default:
                    report.Issues.Add(CreateIssue(
                        IssueCategory.CompilationState,
                        IssueSeverity.Error,
                        TestStatus.Failed,
                        "最近一次编译失败",
                        "工具监听到程序集编译错误。编译错误与运行时错误是不同类别，本项不代表场景运行结果。" + summary,
                        "先修复编译错误，再重新运行扫描或冒烟测试。"));
                    break;
            }

            if (snapshot.Truncated)
            {
                report.Notes.Add("编译消息超过上限，仅保留前 " + AutoTestConstants.MaxCompilationMessages + " 条。");
            }

            for (int i = 0; i < snapshot.Messages.Count; i++)
            {
                CompilationMessage message = snapshot.Messages[i];
                if (message == null)
                {
                    continue;
                }

                string location = FormatLocation(message.File, message.Line, message.Column);
                string assembly = string.IsNullOrEmpty(message.AssemblyPath)
                    ? "（未知程序集）"
                    : message.AssemblyPath;
                string firstLine = FirstLine(message.Message);

                if (message.IsError)
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.CompilationError,
                        IssueSeverity.Error,
                        TestStatus.Failed,
                        "编译错误: " + firstLine,
                        "程序集：" + assembly + "；位置：" + location + "。",
                        "根据位置修复编译错误后重新编译。",
                        string.Empty,
                        string.Empty,
                        message.Message,
                        string.Empty));
                }
                else
                {
                    report.Issues.Add(CreateIssue(
                        IssueCategory.CompilationWarning,
                        IssueSeverity.Warning,
                        TestStatus.Warning,
                        "编译警告: " + firstLine,
                        "程序集：" + assembly + "；位置：" + location + "。",
                        "评估该警告是否影响项目；警告不会被计为测试失败，但会降低总体状态。",
                        string.Empty,
                        string.Empty,
                        message.Message,
                        string.Empty));
                }
            }
        }

        private static string FormatLocation(string file, int line, int column)
        {
            if (string.IsNullOrEmpty(file))
            {
                return "（未知位置）";
            }

            if (line <= 0)
            {
                return file;
            }

            return file + "(" + line + "," + column + ")";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "（空消息）";
            }

            int newline = text.IndexOf('\n');
            string first = newline >= 0 ? text.Substring(0, newline) : text;
            first = first.Trim();
            if (first.Length > 120)
            {
                first = first.Substring(0, 120) + "...";
            }

            return first;
        }

        /// <summary>返回 true 表示用户取消了任务。</summary>
        private static bool Progress(float progress, string info)
        {
            if (Application.isBatchMode)
            {
                return false;
            }

            return EditorUtility.DisplayCancelableProgressBar(
                AutoTestConstants.ToolName + " - 扫描项目",
                info,
                Mathf.Clamp01(progress));
        }

        private static TestIssue CreateIssue(
            IssueCategory category,
            IssueSeverity severity,
            TestStatus status,
            string title,
            string description,
            string suggestion)
        {
            return CreateIssue(category, severity, status, title, description, suggestion, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        private static TestIssue CreateIssue(
            IssueCategory category,
            IssueSeverity severity,
            TestStatus status,
            string title,
            string description,
            string suggestion,
            string scenePath,
            string gameObjectPath,
            string message,
            string stackTrace)
        {
            TestIssue issue = new TestIssue();
            issue.Category = category;
            issue.Severity = severity;
            issue.Status = status;
            issue.Title = title ?? string.Empty;
            issue.Description = description ?? string.Empty;
            issue.Suggestion = suggestion ?? string.Empty;
            issue.ScenePath = scenePath ?? string.Empty;
            issue.GameObjectPath = gameObjectPath ?? string.Empty;
            issue.Message = message ?? string.Empty;
            issue.StackTrace = stackTrace ?? string.Empty;
            return issue;
        }
    }
}

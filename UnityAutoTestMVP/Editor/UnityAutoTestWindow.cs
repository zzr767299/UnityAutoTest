using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAutoTestMVP
{
    /// <summary>
    /// Unity Auto Test MVP 主面板：项目信息、扫描/冒烟测试操作、统计与检查明细。
    /// 只做 UI 与调用编排，具体逻辑分布在 Core/* 中。
    /// </summary>
    public sealed class UnityAutoTestWindow : EditorWindow
    {
        private const double RepaintIntervalSeconds = 0.25;
        private const double BlockReasonRefreshSeconds = 0.5;
        private const int MaxRenderedIssues = 100;

        private int _durationSeconds = AutoTestConstants.DefaultSmokeDurationSeconds;
        private bool _includeWarnings = true;
        private bool _scanAllScenes;
        private bool _isScanning;
        private TestType _selectedReportType = TestType.ProjectScan;
        private bool _showNotes;
        private Vector2 _mainScroll;
        private readonly HashSet<string> _expandedIssues = new HashSet<string>();

        private string _lastActionMessage = string.Empty;
        private MessageType _lastActionType = MessageType.Info;

        private double _nextRepaintTime;
        private double _nextBlockReasonRefreshTime;
        private string _cachedBlockReason;

        private static GUIStyle _titleStyle;

        [MenuItem(AutoTestConstants.MenuPath)]
        public static void Open()
        {
            UnityAutoTestWindow window = GetWindow<UnityAutoTestWindow>();
            window.titleContent = new GUIContent(AutoTestConstants.WindowTitle);
            window.minSize = new Vector2(580f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            _durationSeconds = AutoTestSettings.SmokeDurationSeconds;
            _includeWarnings = AutoTestSettings.IncludeWarnings;
            _scanAllScenes = AutoTestSettings.ScanAllScenesInAssets;

            AutoTestSession.StateChanged += OnStateChanged;
            AutoTestHistory.ReportSaved += OnReportSaved;
            EditorApplication.update += OnEditorTick;
        }

        private void OnDisable()
        {
            AutoTestSession.StateChanged -= OnStateChanged;
            AutoTestHistory.ReportSaved -= OnReportSaved;
            EditorApplication.update -= OnEditorTick;
        }

        private void OnStateChanged()
        {
            InvalidateBlockReason();
            Repaint();
        }

        private void OnReportSaved(TestReport report)
        {
            if (report != null)
            {
                _selectedReportType = report.TestType;
            }

            InvalidateBlockReason();
            Repaint();
        }

        private void OnEditorTick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextRepaintTime)
            {
                return;
            }

            _nextRepaintTime = now + RepaintIntervalSeconds;
            Repaint();
        }

        private void InvalidateBlockReason()
        {
            _nextBlockReasonRefreshTime = 0;
        }

        private void OnGUI()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            DrawHeader();
            DrawProjectInfo();
            DrawSettings();
            DrawActions();
            DrawRunningStatus();
            DrawActionMessage();
            DrawReportSection();
            DrawFooter();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(EditorStyles.boldLabel);
                _titleStyle.fontSize = 16;
                _titleStyle.margin = new RectOffset(0, 0, 2, 2);
            }

            EditorGUILayout.LabelField("Unity Auto Test MVP", _titleStyle);
            EditorGUILayout.LabelField(
                "项目静态扫描（场景 / 丢失脚本）+ 运行时基础冒烟测试。全部在本地 Editor 中执行，不上传任何数据。",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();
        }

        private void DrawProjectInfo()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("项目信息", EditorStyles.boldLabel);

            DrawInfoRow("Unity 版本", Application.unityVersion, false);
            DrawInfoRow("项目名称", AutoTestEnvironment.ProjectName, false);
            DrawInfoRow("活动场景", GetActiveSceneDisplay(), true);

            string compileState;
            if (EditorApplication.isCompiling)
            {
                compileState = "是（正在编译脚本）";
            }
            else if (EditorApplication.isUpdating)
            {
                compileState = "否（正在导入/刷新资源）";
            }
            else
            {
                compileState = "否";
            }

            DrawInfoRow("正在编译", compileState, false);

            CompilationSnapshot snapshot = CompilationMonitor.GetSnapshot();
            if (snapshot.HasData)
            {
                string timeText = snapshot.TimestampUtc == DateTime.MinValue
                    ? "未知时间"
                    : snapshot.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
                DrawInfoRow(
                    "最近编译",
                    CompilationStatusLabel(snapshot.Status) + "（" + timeText + "，错误 " + snapshot.ErrorCount +
                    " / 警告 " + snapshot.WarningCount + "）",
                    false);
            }
            else
            {
                DrawInfoRow("最近编译", "未监听到（工具启动后尚无编译完成事件）", false);
            }

            DrawLatestReportRow("最近静态扫描", TestType.ProjectScan);
            DrawLatestReportRow("最近冒烟测试", TestType.SmokeTest);

            if (!string.IsNullOrEmpty(AutoTestHistory.LastSaveError))
            {
                EditorGUILayout.HelpBox(AutoTestHistory.LastSaveError, MessageType.Error);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLatestReportRow(string label, TestType type)
        {
            TestReport report;
            if (!AutoTestHistory.TryGetLatest(type, out report))
            {
                DrawInfoRow(label, "（尚未运行）", false);
                return;
            }

            string text = report.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                          "  " + StatusLabel(report.OverallStatus) +
                          "  （失败 " + report.Summary.Failed + " / 警告 " + report.Summary.Warning + "）";
            DrawInfoRow(label, text, false);
        }

        private void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("测试设置", EditorStyles.boldLabel);

            int duration = EditorGUILayout.IntSlider(
                "冒烟测试时长（秒）",
                _durationSeconds,
                AutoTestConstants.MinSmokeDurationSeconds,
                AutoTestConstants.MaxSmokeDurationSeconds);
            if (duration != _durationSeconds)
            {
                _durationSeconds = duration;
                AutoTestSettings.SmokeDurationSeconds = duration;
            }

            bool includeWarnings = EditorGUILayout.Toggle("将 Warning 日志纳入报告", _includeWarnings);
            if (includeWarnings != _includeWarnings)
            {
                _includeWarnings = includeWarnings;
                AutoTestSettings.IncludeWarnings = includeWarnings;
            }

            bool scanAll = EditorGUILayout.Toggle("扫描 Assets 下全部场景", _scanAllScenes);
            if (scanAll != _scanAllScenes)
            {
                _scanAllScenes = scanAll;
                AutoTestSettings.ScanAllScenesInAssets = scanAll;
            }

            if (!_scanAllScenes)
            {
                EditorGUILayout.LabelField(
                    "默认扫描 Build Settings 中启用的场景；若没有启用场景，自动回退为扫描 Assets 下全部 .unity 场景。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);

            string scanBlock = GetScanBlockReason();
            string smokeBlock = GetSmokeBlockReason();
            bool hasSelectedReport = HasReport(_selectedReportType);

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(scanBlock != null))
            {
                if (GUILayout.Button("扫描项目", GUILayout.Height(30)))
                {
                    RunScan();
                }
            }

            using (new EditorGUI.DisabledScope(smokeBlock != null))
            {
                if (GUILayout.Button("运行冒烟测试", GUILayout.Height(30)))
                {
                    RunSmokeTest();
                }
            }

            using (new EditorGUI.DisabledScope(!AutoTestSession.IsRunning))
            {
                if (GUILayout.Button("停止测试", GUILayout.Height(30)))
                {
                    StopSmokeTest();
                }
            }

            using (new EditorGUI.DisabledScope(!hasSelectedReport))
            {
                if (GUILayout.Button("导出测试报告", GUILayout.Height(30)))
                {
                    ExportSelectedReport();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (scanBlock != null)
            {
                EditorGUILayout.HelpBox("当前无法扫描项目：" + scanBlock, MessageType.Info);
            }

            if (smokeBlock != null && !string.Equals(smokeBlock, scanBlock, StringComparison.Ordinal))
            {
                EditorGUILayout.HelpBox("当前无法运行冒烟测试：" + smokeBlock, MessageType.Info);
            }

            EditorGUILayout.LabelField(
                "“导出测试报告”会同时导出 JSON 与 Markdown 到 Library/UnityAutoTestMVP/Reports/。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawRunningStatus()
        {
            SmokePhase phase = AutoTestSession.Phase;
            if (phase == SmokePhase.Idle)
            {
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (phase == SmokePhase.Starting)
            {
                EditorGUILayout.LabelField("正在进入 Play Mode...", EditorStyles.boldLabel);
                DrawInfoRow("目标场景", AutoTestSession.CurrentScenePath, true);
                DrawInfoRow("已等待", AutoTestSession.WallClockSeconds.ToString("0.0") + " 秒", false);
                EditorGUILayout.LabelField(
                    "Unity 完成脚本重载和场景加载后开始计时。点击“停止测试”可取消。",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("冒烟测试运行中", EditorStyles.boldLabel);
                DrawInfoRow("目标场景", AutoTestSession.CurrentScenePath, true);

                double elapsed = AutoTestSession.ElapsedSeconds;
                int duration = AutoTestSession.ConfiguredDurationSeconds;
                Rect rect = GUILayoutUtility.GetRect(18f, 18f);
                EditorGUI.ProgressBar(
                    rect,
                    AutoTestSession.Progress01,
                    elapsed.ToString("0.0") + " / " + duration + " 秒");

                EditorGUILayout.LabelField(
                    "剩余约 " + AutoTestSession.RemainingSeconds.ToString("0.0") + " 秒；结束时自动退出 Play Mode。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActionMessage()
        {
            if (string.IsNullOrEmpty(_lastActionMessage))
            {
                return;
            }

            EditorGUILayout.HelpBox(_lastActionMessage, _lastActionType);
        }

        private void DrawReportSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("测试结果", EditorStyles.boldLabel);

            _selectedReportType = (TestType)GUILayout.Toolbar(
                (int)_selectedReportType,
                new[] { "静态扫描报告", "冒烟测试报告" });

            TestReport report;
            if (!AutoTestHistory.TryGetLatest(_selectedReportType, out report))
            {
                EditorGUILayout.HelpBox(
                    _selectedReportType == TestType.ProjectScan
                        ? "尚未运行静态扫描。点击“扫描项目”开始。"
                        : "尚未运行冒烟测试。点击“运行冒烟测试”开始。",
                    MessageType.Info);
                return;
            }

            DrawReportHeader(report);
            DrawSummary(report.Summary);
            DrawIssues(report);
            DrawNotes(report);
        }

        private void DrawReportHeader(TestReport report)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            DrawStatusSwatch(report.OverallStatus);
            EditorGUILayout.LabelField(
                (report.TestType == TestType.SmokeTest ? "基础冒烟测试" : "项目静态扫描") +
                "：" + StatusLabel(report.OverallStatus),
                EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            DrawInfoRow("开始时间", report.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), false);
            DrawInfoRow("耗时", report.DurationSeconds.ToString("0.00") + " 秒", false);
            if (!string.IsNullOrEmpty(report.ScenePath))
            {
                DrawInfoRow("目标场景", report.ScenePath, true);
            }

            DrawInfoRow("任务 ID", report.TaskId, true);

            string reportPath = AutoTestHistory.GetLatestReportPath(report.TestType);
            if (!string.IsNullOrEmpty(reportPath))
            {
                DrawInfoRow("JSON 报告", reportPath, true);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSummary(TestSummary summary)
        {
            EditorGUILayout.BeginHorizontal();
            DrawStat("检查项", summary.Total, new Color(0.62f, 0.62f, 0.62f));
            DrawStat("通过", summary.Passed, new Color(0.40f, 0.85f, 0.45f));
            DrawStat("失败", summary.Failed, new Color(0.95f, 0.38f, 0.38f));
            DrawStat("警告", summary.Warning, new Color(0.95f, 0.78f, 0.28f));
            DrawStat("未执行", summary.NotRun, new Color(0.65f, 0.65f, 0.65f));
            DrawStat("已取消", summary.Cancelled, new Color(0.95f, 0.62f, 0.25f));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawStat(string label, int value, Color color)
        {
            EditorGUILayout.BeginVertical(GUILayout.MinWidth(62f));
            Color old = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(value.ToString(), EditorStyles.boldLabel);
            GUI.color = old;
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawIssues(TestReport report)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("检查明细", EditorStyles.boldLabel);

            if (report.Issues == null || report.Issues.Count == 0)
            {
                EditorGUILayout.HelpBox("本次没有记录任何检查项。", MessageType.Info);
                return;
            }

            int renderCount = Math.Min(report.Issues.Count, MaxRenderedIssues);
            for (int i = 0; i < renderCount; i++)
            {
                DrawIssue(report, report.Issues[i], i);
            }

            if (report.Issues.Count > renderCount)
            {
                EditorGUILayout.HelpBox(
                    "共 " + report.Issues.Count + " 条检查项，界面仅显示前 " + renderCount +
                    " 条。完整内容请导出报告或查看 JSON 文件。",
                    MessageType.Info);
            }
        }

        private void DrawIssue(TestReport report, TestIssue issue, int index)
        {
            if (issue == null)
            {
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            DrawStatusSwatch(issue.Status);
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(issue.Title) ? "（未命名检查项）" : issue.Title,
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(StatusLabel(issue.Status), EditorStyles.miniLabel, GUILayout.Width(92f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                CategoryLabel(issue.Category) + " · " + SeverityLabel(issue.Severity),
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(issue.ScenePath))
            {
                EditorGUILayout.LabelField("场景：" + issue.ScenePath, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(issue.GameObjectPath))
            {
                EditorGUILayout.LabelField("对象：" + issue.GameObjectPath, EditorStyles.miniLabel);
            }

            string key = (report.TaskId ?? string.Empty) + "|" + (issue.Id ?? index.ToString());
            bool expanded = _expandedIssues.Contains(key);
            bool newExpanded = EditorGUILayout.Foldout(expanded, "详细信息", true);
            if (newExpanded != expanded)
            {
                if (newExpanded)
                {
                    _expandedIssues.Add(key);
                }
                else
                {
                    _expandedIssues.Remove(key);
                }
            }

            if (newExpanded)
            {
                if (!string.IsNullOrEmpty(issue.Description))
                {
                    EditorGUILayout.LabelField(issue.Description, EditorStyles.wordWrappedLabel);
                }

                if (!string.IsNullOrEmpty(issue.Suggestion))
                {
                    EditorGUILayout.LabelField("建议：" + issue.Suggestion, EditorStyles.wordWrappedLabel);
                }

                if (!string.IsNullOrEmpty(issue.Message))
                {
                    EditorGUILayout.LabelField("错误消息", EditorStyles.miniBoldLabel);
                    DrawReadOnlyText(issue.Message, 90f);
                }

                if (!string.IsNullOrEmpty(issue.StackTrace))
                {
                    EditorGUILayout.LabelField("堆栈信息", EditorStyles.miniBoldLabel);
                    DrawReadOnlyText(issue.StackTrace, 120f);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawReadOnlyText(string text, float height)
        {
            string display = text.Length > 4000 ? text.Substring(0, 4000) + "\n...（已截断，完整内容见报告）" : text;
            EditorGUILayout.SelectableLabel(display, EditorStyles.textArea, GUILayout.Height(height));
        }

        private void DrawNotes(TestReport report)
        {
            EditorGUILayout.Space();
            _showNotes = EditorGUILayout.Foldout(_showNotes, "说明与限制", true);
            if (!_showNotes)
            {
                return;
            }

            if (report.Notes == null || report.Notes.Count == 0)
            {
                EditorGUILayout.LabelField("无附加说明。", EditorStyles.miniLabel);
                return;
            }

            for (int i = 0; i < report.Notes.Count; i++)
            {
                string note = report.Notes[i];
                if (!string.IsNullOrEmpty(note))
                {
                    EditorGUILayout.LabelField("• " + note, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AutoTestConstants.ToolName + " v" + AutoTestConstants.ToolVersion,
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("打开报告目录", EditorStyles.miniButton, GUILayout.Width(110f)))
            {
                OpenReportsFolder();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusSwatch(TestStatus status)
        {
            Color old = GUI.color;
            GUI.color = StatusColor(status);
            GUILayout.Label("■", GUILayout.Width(14f));
            GUI.color = old;
        }

        private static void DrawInfoRow(string label, string value, bool selectable)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(90f));
            if (selectable)
            {
                EditorGUILayout.SelectableLabel(
                    string.IsNullOrEmpty(value) ? "（无）" : value,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(value) ? "（无）" : value,
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndHorizontal();
        }

        private string GetActiveSceneDisplay()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return "（无活动场景）";
            }

            if (string.IsNullOrEmpty(scene.path))
            {
                return scene.name + "（未保存的 untitled 场景）";
            }

            return scene.path;
        }

        private string GetScanBlockReason()
        {
            if (_isScanning)
            {
                return "项目扫描正在进行。";
            }

            if (AutoTestSession.IsRunning)
            {
                return "冒烟测试正在运行。";
            }

            if (EditorApplication.isCompiling)
            {
                return "Unity 正在编译脚本。";
            }

            if (EditorApplication.isUpdating)
            {
                return "Unity 正在导入/刷新资源。";
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Play Mode 中不能安全地加载和扫描场景。";
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() && EditorSceneManager.IsPreviewScene(activeScene))
            {
                return "当前处于 Prefab 编辑模式，请先退出 Prefab Mode。";
            }

            return null;
        }

        private string GetSmokeBlockReason()
        {
            if (_isScanning)
            {
                return "项目扫描正在进行。";
            }

            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextBlockReasonRefreshTime)
            {
                _cachedBlockReason = SmokeTestRunner.GetStartBlockReason();
                _nextBlockReasonRefreshTime = now + BlockReasonRefreshSeconds;
            }

            return _cachedBlockReason;
        }

        private void RunScan()
        {
            _isScanning = true;
            InvalidateBlockReason();
            _lastActionMessage = "正在扫描项目，请稍候...";
            _lastActionType = MessageType.Info;
            Repaint();

            try
            {
                TestReport report = ProjectScanner.Scan(_scanAllScenes);
                string saveError = AutoTestHistory.SaveCompletedReport(report);
                _selectedReportType = TestType.ProjectScan;

                if (!string.IsNullOrEmpty(saveError))
                {
                    _lastActionMessage = "扫描完成，但报告保存失败：" + saveError;
                    _lastActionType = MessageType.Error;
                }
                else
                {
                    _lastActionMessage = "扫描完成：" + StatusLabel(report.OverallStatus) +
                                         "；检查项 " + report.Summary.Total +
                                         "，失败 " + report.Summary.Failed +
                                         "，警告 " + report.Summary.Warning +
                                         "，未执行 " + report.Summary.NotRun + "。";
                    _lastActionType = ToMessageType(report.OverallStatus);
                }
            }
            catch (Exception ex)
            {
                _lastActionMessage = "扫描失败：" + ex.GetType().Name + ": " + ex.Message;
                _lastActionType = MessageType.Error;
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("扫描失败", _lastActionMessage, "确定");
            }
            finally
            {
                _isScanning = false;
                InvalidateBlockReason();
                Repaint();
            }
        }

        private void RunSmokeTest()
        {
            string error;
            if (SmokeTestRunner.TryStart(out error))
            {
                _lastActionMessage = "已请求进入 Play Mode，测试运行 " + _durationSeconds +
                                     " 秒后自动退出。期间可点击“停止测试”取消。";
                _lastActionType = MessageType.Info;
            }
            else
            {
                _lastActionMessage = "无法启动冒烟测试：" + error;
                _lastActionType = MessageType.Warning;
            }

            InvalidateBlockReason();
            _selectedReportType = TestType.SmokeTest;
            Repaint();
        }

        private void StopSmokeTest()
        {
            string message;
            if (SmokeTestRunner.TryStop(out message))
            {
                _lastActionMessage = message;
                _lastActionType = MessageType.Info;
            }
            else
            {
                _lastActionMessage = message;
                _lastActionType = MessageType.Warning;
            }

            InvalidateBlockReason();
            Repaint();
        }

        private void ExportSelectedReport()
        {
            TestReport report;
            if (!AutoTestHistory.TryGetLatest(_selectedReportType, out report))
            {
                _lastActionMessage = "当前没有可导出的报告。";
                _lastActionType = MessageType.Warning;
                return;
            }

            ReportExportResult result = ReportExporter.ExportAll(report, AutoTestEnvironment.ProjectRoot);
            if (result.Success)
            {
                _lastActionMessage = "报告已导出：\n" + result.JsonPath + "\n" + result.MarkdownPath;
                _lastActionType = MessageType.Info;
                EditorUtility.DisplayDialog(
                    "导出成功",
                    "JSON:\n" + result.JsonPath + "\n\nMarkdown:\n" + result.MarkdownPath,
                    "确定");
            }
            else
            {
                _lastActionMessage = "报告导出失败：" + result.Error;
                _lastActionType = MessageType.Error;
                Debug.LogError("[UnityAutoTestMVP] 报告导出失败：" + result.Error);
                EditorUtility.DisplayDialog("导出失败", result.Error, "确定");
            }
        }

        private void OpenReportsFolder()
        {
            string directory = AutoTestEnvironment.ReportsDirectory;
            try
            {
                Directory.CreateDirectory(directory);
                EditorUtility.RevealInFinder(directory);
            }
            catch (Exception ex)
            {
                _lastActionMessage = "无法打开报告目录：" + ex.GetType().Name + ": " + ex.Message;
                _lastActionType = MessageType.Error;
                Debug.LogException(ex);
            }
        }

        private bool HasReport(TestType type)
        {
            TestReport report;
            return AutoTestHistory.TryGetLatest(type, out report);
        }

        private static MessageType ToMessageType(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Failed:
                    return MessageType.Error;
                case TestStatus.Warning:
                case TestStatus.Cancelled:
                    return MessageType.Warning;
                default:
                    return MessageType.Info;
            }
        }

        private static Color StatusColor(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:
                    return new Color(0.40f, 0.85f, 0.45f);
                case TestStatus.Failed:
                    return new Color(0.95f, 0.38f, 0.38f);
                case TestStatus.Warning:
                    return new Color(0.95f, 0.78f, 0.28f);
                case TestStatus.Cancelled:
                    return new Color(0.95f, 0.62f, 0.25f);
                default:
                    return new Color(0.65f, 0.65f, 0.65f);
            }
        }

        private static string StatusLabel(TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:
                    return "通过";
                case TestStatus.Failed:
                    return "失败";
                case TestStatus.Warning:
                    return "警告";
                case TestStatus.Cancelled:
                    return "已取消";
                default:
                    return "未执行";
            }
        }

        private static string SeverityLabel(IssueSeverity severity)
        {
            switch (severity)
            {
                case IssueSeverity.Critical:
                    return "严重";
                case IssueSeverity.Error:
                    return "错误";
                case IssueSeverity.Warning:
                    return "警告";
                default:
                    return "信息";
            }
        }

        private static string CategoryLabel(IssueCategory category)
        {
            switch (category)
            {
                case IssueCategory.SceneAsset:
                    return "场景资源";
                case IssueCategory.SceneLoad:
                    return "场景加载";
                case IssueCategory.MissingScript:
                    return "丢失脚本";
                case IssueCategory.SceneScan:
                    return "场景扫描";
                case IssueCategory.RuntimeError:
                    return "运行时错误";
                case IssueCategory.RuntimeWarning:
                    return "运行时警告";
                case IssueCategory.CompilationError:
                    return "编译错误";
                case IssueCategory.CompilationWarning:
                    return "编译警告";
                case IssueCategory.CompilationState:
                    return "编译状态";
                case IssueCategory.TestInfrastructure:
                    return "测试环境";
                default:
                    return "项目扫描";
            }
        }

        private static string CompilationStatusLabel(CompilationStatus status)
        {
            switch (status)
            {
                case CompilationStatus.Succeeded:
                    return "通过";
                case CompilationStatus.SucceededWithWarnings:
                    return "有警告";
                case CompilationStatus.Failed:
                    return "失败";
                default:
                    return "未知";
            }
        }
    }
}

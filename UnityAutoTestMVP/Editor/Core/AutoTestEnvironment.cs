using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAutoTestMVP
{
    /// <summary>Unity 环境信息读取（项目根目录、名称、版本）。</summary>
    internal static class AutoTestEnvironment
    {
        public static string UnityVersion
        {
            get { return Application.unityVersion; }
        }

        public static string ProjectRoot
        {
            get
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath))
                {
                    return string.Empty;
                }

                DirectoryInfo parent = Directory.GetParent(dataPath);
                return parent != null ? parent.FullName : dataPath;
            }
        }

        public static string ProjectName
        {
            get
            {
                string productName = Application.productName;
                if (!string.IsNullOrEmpty(productName))
                {
                    return productName;
                }

                string root = ProjectRoot;
                return string.IsNullOrEmpty(root) ? "UnknownProject" : Path.GetFileName(root);
            }
        }

        public static string ReportsDirectory
        {
            get { return ReportExporter.GetReportsDirectory(ProjectRoot); }
        }
    }

    /// <summary>面板设置，保存在 EditorPrefs 中，跨编辑器会话保留。</summary>
    internal static class AutoTestSettings
    {
        private const string DurationKey = "UnityAutoTestMVP.SmokeDurationSeconds";
        private const string IncludeWarningsKey = "UnityAutoTestMVP.IncludeWarnings";
        private const string ScanAllScenesKey = "UnityAutoTestMVP.ScanAllScenes";

        public static int SmokeDurationSeconds
        {
            get
            {
                int value = EditorPrefs.GetInt(DurationKey, AutoTestConstants.DefaultSmokeDurationSeconds);
                return Mathf.Clamp(value, AutoTestConstants.MinSmokeDurationSeconds, AutoTestConstants.MaxSmokeDurationSeconds);
            }
            set
            {
                int clamped = Mathf.Clamp(value, AutoTestConstants.MinSmokeDurationSeconds, AutoTestConstants.MaxSmokeDurationSeconds);
                EditorPrefs.SetInt(DurationKey, clamped);
            }
        }

        public static bool IncludeWarnings
        {
            get { return EditorPrefs.GetBool(IncludeWarningsKey, true); }
            set { EditorPrefs.SetBool(IncludeWarningsKey, value); }
        }

        public static bool ScanAllScenesInAssets
        {
            get { return EditorPrefs.GetBool(ScanAllScenesKey, false); }
            set { EditorPrefs.SetBool(ScanAllScenesKey, value); }
        }
    }

    /// <summary>
    /// 最近一次扫描 / 冒烟测试报告的缓存与持久化入口。
    /// 报告路径保存在 SessionState 中，因此 Domain Reload 之后仍能重新加载。
    /// </summary>
    internal static class AutoTestHistory
    {
        private static TestReport _lastScanReport;
        private static TestReport _lastSmokeReport;
        private static string _lastSaveError = string.Empty;

        /// <summary>报告保存完成后触发，供 Editor 窗口刷新。回调异常会被记录，不影响测试流程。</summary>
        public static event Action<TestReport> ReportSaved;

        public static string LastSaveError
        {
            get { return _lastSaveError; }
        }

        public static bool TryGetLatest(TestType type, out TestReport report)
        {
            report = type == TestType.SmokeTest ? _lastSmokeReport : _lastScanReport;
            if (report != null)
            {
                return true;
            }

            string key = type == TestType.SmokeTest
                ? AutoTestConstants.SessionKeys.LastSmokeReportPath
                : AutoTestConstants.SessionKeys.LastScanReportPath;
            string path = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            TestReport loaded;
            string error;
            if (!ReportExporter.TryReadReport(path, out loaded, out error))
            {
                Debug.LogWarning("[UnityAutoTestMVP] " + error);
                return false;
            }

            loaded.TestType = type;
            if (type == TestType.SmokeTest)
            {
                _lastSmokeReport = loaded;
            }
            else
            {
                _lastScanReport = loaded;
            }

            report = loaded;
            return true;
        }

        public static string GetLatestReportPath(TestType type)
        {
            string key = type == TestType.SmokeTest
                ? AutoTestConstants.SessionKeys.LastSmokeReportPath
                : AutoTestConstants.SessionKeys.LastScanReportPath;
            return SessionState.GetString(key, string.Empty);
        }

        /// <summary>
        /// 自动保存 JSON 报告并更新“最近报告”缓存。失败时返回错误文本（同时写入 Console），不抛出。
        /// </summary>
        public static string SaveCompletedReport(TestReport report)
        {
            if (report == null)
            {
                _lastSaveError = "报告对象为空，未保存。";
                return _lastSaveError;
            }

            string path = null;
            string error = null;
            try
            {
                path = ReportExporter.WriteReport(report, AutoTestEnvironment.ProjectRoot, ReportFormat.Json);
            }
            catch (Exception ex)
            {
                error = "报告自动保存失败：" + ex.GetType().Name + ": " + ex.Message;
                Debug.LogError("[UnityAutoTestMVP] " + error + "\n" + ex);
            }

            SetLatest(report, path);
            _lastSaveError = error ?? string.Empty;

            Action<TestReport> handler = ReportSaved;
            if (handler != null)
            {
                try
                {
                    handler(report);
                }
                catch (Exception ex)
                {
                    // UI 回调失败不应影响报告保存或 Play Mode 退出流程。
                    Debug.LogException(ex);
                }
            }

            return _lastSaveError;
        }

        private static void SetLatest(TestReport report, string path)
        {
            if (report.TestType == TestType.SmokeTest)
            {
                _lastSmokeReport = report;
                SessionState.SetString(AutoTestConstants.SessionKeys.LastSmokeReportPath, path ?? string.Empty);
            }
            else
            {
                _lastScanReport = report;
                SessionState.SetString(AutoTestConstants.SessionKeys.LastScanReportPath, path ?? string.Empty);
            }
        }
    }
}

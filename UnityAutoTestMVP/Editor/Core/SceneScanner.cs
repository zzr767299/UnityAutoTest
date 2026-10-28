using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UnityAutoTestMVP
{
    /// <summary>单个场景的扫描结果（内部使用，最终合并进 TestReport）。</summary>
    internal sealed class SceneScanOutcome
    {
        public string ScenePath = string.Empty;
        public bool WasAlreadyOpen;
        public bool OpenedByTool;
        public bool LoadSucceeded;
        public bool ScanCompleted;
        public string LoadError = string.Empty;
        public string ScanError = string.Empty;
        public int GameObjectCount;
        public int MissingScriptGameObjectCount;
        public int MissingScriptComponentCount;
        public double DurationSeconds;
        public List<TestIssue> Issues = new List<TestIssue>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// 单场景扫描。安全性约束：
    /// - 已打开的场景原地扫描，不关闭、不保存；
    /// - 未打开的场景以 Additive 方式临时加载，并在 finally 中关闭；
    /// - 只读取数据，不删除缺失脚本、不保存场景、不修改任何资源。
    /// </summary>
    internal static class SceneScanner
    {
        public static SceneScanOutcome Scan(string scenePath)
        {
            SceneScanOutcome outcome = new SceneScanOutcome();
            outcome.ScenePath = scenePath ?? string.Empty;

            if (string.IsNullOrEmpty(outcome.ScenePath))
            {
                outcome.LoadError = "场景路径为空。";
                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneLoad,
                    IssueSeverity.Error,
                    TestStatus.Failed,
                    "场景路径为空",
                    "无法扫描路径为空的场景。",
                    "调用方应确保传入有效的 Assets 相对路径。",
                    outcome.ScenePath,
                    string.Empty,
                    outcome.LoadError,
                    string.Empty));
                return outcome;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Scene scene = default(Scene);
            bool activeSceneChanged = false;
            Scene activeBefore = SceneManager.GetActiveScene();
            string fullPath = string.Empty;

            try
            {
                try
                {
                    fullPath = Path.Combine(AutoTestEnvironment.ProjectRoot, outcome.ScenePath);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException("场景路径包含非法字符: " + outcome.ScenePath, ex);
                }

                if (string.IsNullOrEmpty(fullPath) || (!IsPackageScenePath(outcome.ScenePath) && !File.Exists(fullPath)))
                {
                    throw new FileNotFoundException("场景文件不存在: " + outcome.ScenePath, fullPath);
                }

                if (TryFindLoadedScene(outcome.ScenePath, out scene))
                {
                    outcome.WasAlreadyOpen = true;
                    if (!scene.isLoaded)
                    {
                        throw new InvalidOperationException("场景已在编辑器中，但尚未完成加载。");
                    }
                }
                else
                {
                    scene = EditorSceneManager.OpenScene(outcome.ScenePath, OpenSceneMode.Additive);
                    outcome.OpenedByTool = true;
                    activeSceneChanged = true;

                    if (!scene.IsValid())
                    {
                        throw new InvalidOperationException("EditorSceneManager.OpenScene 返回了无效场景。");
                    }

                    if (!scene.isLoaded)
                    {
                        throw new InvalidOperationException("场景以 Additive 方式打开后仍处于未加载状态。");
                    }
                }

                outcome.LoadSucceeded = true;
                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneLoad,
                    IssueSeverity.Info,
                    TestStatus.Passed,
                    "场景加载成功",
                    outcome.WasAlreadyOpen
                        ? "场景已在编辑器中打开，就地扫描（不会关闭或保存）。"
                        : "已使用 Additive 方式临时加载场景，扫描结束后关闭。",
                    string.Empty,
                    outcome.ScenePath,
                    string.Empty,
                    string.Empty,
                    string.Empty));

                try
                {
                    ScanHierarchy(scene, outcome);
                    outcome.ScanCompleted = true;
                }
                catch (Exception scanEx)
                {
                    outcome.ScanError = DescribeException(scanEx);
                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.SceneScan,
                        IssueSeverity.Error,
                        TestStatus.Failed,
                        "场景扫描失败",
                        "扫描场景内容时发生异常，未能完成全部检查。",
                        "查看错误消息与堆栈定位问题；该场景的结果不可靠。",
                        outcome.ScenePath,
                        string.Empty,
                        scanEx.Message,
                        scanEx.ToString()));
                    Debug.LogException(scanEx);
                }

                if (outcome.ScanCompleted)
                {
                    if (outcome.MissingScriptComponentCount == 0)
                    {
                        outcome.Issues.Add(CreateIssue(
                            IssueCategory.MissingScript,
                            IssueSeverity.Info,
                            TestStatus.Passed,
                            "未发现丢失脚本",
                            "已检查 " + outcome.GameObjectCount + " 个 GameObject（含未激活对象），未发现 Missing Script 组件。",
                            string.Empty,
                            outcome.ScenePath,
                            string.Empty,
                            string.Empty,
                            string.Empty));
                    }

                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.SceneScan,
                        IssueSeverity.Info,
                        TestStatus.Passed,
                        "场景扫描完成",
                        "共检查 " + outcome.GameObjectCount + " 个 GameObject；缺失脚本的 GameObject 数量：" +
                        outcome.MissingScriptGameObjectCount + "。",
                        string.Empty,
                        outcome.ScenePath,
                        string.Empty,
                        string.Empty,
                        string.Empty));
                }
                else
                {
                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.MissingScript,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "未完成丢失脚本检测",
                        "场景内容扫描未能正常完成，无法可靠判断是否存在缺失脚本。",
                        string.Empty,
                        outcome.ScenePath,
                        string.Empty,
                        string.Empty,
                        string.Empty));

                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.SceneScan,
                        IssueSeverity.Error,
                        TestStatus.NotRun,
                        "场景扫描未完成",
                        "该场景的检查结果不完整，不能视为通过。",
                        string.Empty,
                        outcome.ScenePath,
                        string.Empty,
                        string.Empty,
                        string.Empty));
                }
            }
            catch (Exception ex)
            {
                outcome.LoadError = DescribeException(ex);
                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneLoad,
                    IssueSeverity.Error,
                    TestStatus.Failed,
                    "场景无法打开",
                    "工具无法加载该场景，因此无法完成场景内检查。",
                    "确认场景文件未损坏、没有被其他工具锁定，且项目没有阻止加载的编译错误。",
                    outcome.ScenePath,
                    string.Empty,
                    ex.Message,
                    ex.ToString()));

                outcome.Issues.Add(CreateIssue(
                    IssueCategory.MissingScript,
                    IssueSeverity.Warning,
                    TestStatus.NotRun,
                    "未执行丢失脚本检测",
                    "场景未能加载，未执行检测，不能视为通过。",
                    string.Empty,
                    outcome.ScenePath,
                    string.Empty,
                    string.Empty,
                    string.Empty));

                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneScan,
                    IssueSeverity.Warning,
                    TestStatus.NotRun,
                    "场景扫描未执行",
                    "场景未能加载，扫描未执行，不能视为通过。",
                    string.Empty,
                    outcome.ScenePath,
                    string.Empty,
                    string.Empty,
                    string.Empty));

                Debug.LogWarning("[UnityAutoTestMVP] 场景加载失败: " + outcome.ScenePath + " -> " + outcome.LoadError);
            }
            finally
            {
                // 极少数情况下 OpenScene 可能已经加载场景但随后抛出异常，这里再做一次兜底查找，
                // 确保临时加载的场景不会被遗留在 Hierarchy 中。
                if (outcome.OpenedByTool && !scene.IsValid())
                {
                    Scene recovered;
                    if (TryFindLoadedScene(outcome.ScenePath, out recovered))
                    {
                        scene = recovered;
                    }
                }

                if (outcome.OpenedByTool && scene.IsValid() && scene.isLoaded)
                {
                    try
                    {
                        if (scene.isDirty)
                        {
                            outcome.Notes.Add("扫描期间临时场景被标记为 dirty；工具不会保存它，关闭时只丢弃扫描期间产生的临时状态。");
                        }

                        bool closed = EditorSceneManager.CloseScene(scene, true);
                        if (!closed)
                        {
                            outcome.Issues.Add(CreateIssue(
                                IssueCategory.SceneScan,
                                IssueSeverity.Warning,
                                TestStatus.Failed,
                                "临时场景未能关闭",
                                "工具尝试关闭扫描用临时场景，但 Unity 返回失败。该场景可能仍留在 Hierarchy 中。",
                                "请手动检查 Hierarchy 并关闭该临时场景；工具没有保存任何修改。",
                                outcome.ScenePath,
                                string.Empty,
                                string.Empty,
                                string.Empty));
                        }
                    }
                    catch (Exception closeEx)
                    {
                        outcome.Issues.Add(CreateIssue(
                            IssueCategory.SceneScan,
                            IssueSeverity.Error,
                            TestStatus.Failed,
                            "临时场景关闭时发生异常",
                            "工具无法安全清理临时加载的场景，该场景可能仍留在 Hierarchy 中。",
                            "请手动检查 Hierarchy；工具没有保存任何修改。",
                            outcome.ScenePath,
                            string.Empty,
                            closeEx.Message,
                            closeEx.ToString()));
                        Debug.LogException(closeEx);
                    }
                }

                if (activeSceneChanged)
                {
                    RestoreActiveScene(activeBefore, outcome);
                }

                stopwatch.Stop();
                outcome.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            }

            return outcome;
        }

        private static void ScanHierarchy(Scene scene, SceneScanOutcome outcome)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            if (roots == null)
            {
                throw new InvalidOperationException("scene.GetRootGameObjects() 返回 null。");
            }

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }

                ScanTransform(root.transform, string.Empty, outcome);
            }
        }

        private static void ScanTransform(Transform transform, string parentPath, SceneScanOutcome outcome)
        {
            if (transform == null)
            {
                return;
            }

            string hierarchyPath = string.IsNullOrEmpty(parentPath)
                ? transform.name
                : parentPath + "/" + transform.name;

            outcome.GameObjectCount++;

            try
            {
                GameObject gameObject = transform.gameObject;
                int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                if (missingCount > 0)
                {
                    outcome.MissingScriptGameObjectCount++;
                    outcome.MissingScriptComponentCount += missingCount;

                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.MissingScript,
                        IssueSeverity.Error,
                        TestStatus.Failed,
                        "丢失脚本: " + gameObject.name + "（" + missingCount + " 个组件）",
                        "该 GameObject 上存在引用了不存在脚本的组件。常见原因是脚本被删除、重命名、移动，或脚本所在的程序集缺失。相关逻辑在运行时可能不会执行。",
                        "在 Inspector 中移除丢失的组件，或恢复被删除/改名的脚本及其程序集。工具不会自动删除或修复组件。",
                        outcome.ScenePath,
                        hierarchyPath,
                        "GameObject 上存在 " + missingCount + " 个缺失脚本（Missing Script）组件。",
                        string.Empty));
                }
            }
            catch (Exception ex)
            {
                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneScan,
                    IssueSeverity.Warning,
                    TestStatus.NotRun,
                    "无法检查 GameObject",
                    "检查该对象是否包含缺失脚本时发生异常，该项结果未知，不能视为通过。",
                    string.Empty,
                    outcome.ScenePath,
                    hierarchyPath,
                    ex.Message,
                    ex.ToString()));
                Debug.LogWarning(
                    "[UnityAutoTestMVP] 无法检查 GameObject '" + hierarchyPath + "'（场景 " + outcome.ScenePath + "）：" + ex.Message);
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child;
                try
                {
                    child = transform.GetChild(i);
                }
                catch (Exception ex)
                {
                    outcome.Issues.Add(CreateIssue(
                        IssueCategory.SceneScan,
                        IssueSeverity.Warning,
                        TestStatus.NotRun,
                        "无法访问子节点",
                        "遍历该对象的子节点时发生异常，部分子对象未被检查。",
                        string.Empty,
                        outcome.ScenePath,
                        hierarchyPath,
                        ex.Message,
                        ex.ToString()));
                    continue;
                }

                ScanTransform(child, hierarchyPath, outcome);
            }
        }

        private static bool TryFindLoadedScene(string scenePath, out Scene scene)
        {
            string normalized = NormalizePath(scenePath);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (!candidate.IsValid() || !candidate.isLoaded)
                {
                    continue;
                }

                if (string.Equals(NormalizePath(candidate.path), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    scene = candidate;
                    return true;
                }
            }

            scene = default(Scene);
            return false;
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').Trim();
        }

        private static bool IsPackageScenePath(string scenePath)
        {
            return !string.IsNullOrEmpty(scenePath) &&
                   scenePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        private static void RestoreActiveScene(Scene activeBefore, SceneScanOutcome outcome)
        {
            if (!activeBefore.IsValid() || !activeBefore.isLoaded)
            {
                return;
            }

            try
            {
                Scene current = SceneManager.GetActiveScene();
                if (current != activeBefore)
                {
                    if (!SceneManager.SetActiveScene(activeBefore))
                    {
                        outcome.Issues.Add(CreateIssue(
                            IssueCategory.SceneScan,
                            IssueSeverity.Warning,
                            TestStatus.Warning,
                            "未能恢复原活动场景",
                            "扫描结束后尝试恢复扫描前的活动场景失败。场景内容没有被修改。",
                            "请手动在 Hierarchy 中选择原来的活动场景。",
                            outcome.ScenePath,
                            string.Empty,
                            string.Empty,
                            string.Empty));
                    }
                }
            }
            catch (Exception ex)
            {
                outcome.Issues.Add(CreateIssue(
                    IssueCategory.SceneScan,
                    IssueSeverity.Warning,
                    TestStatus.Warning,
                    "恢复活动场景时发生异常",
                    "扫描结束后尝试恢复扫描前的活动场景失败。场景内容没有被修改。",
                    "请手动在 Hierarchy 中选择原来的活动场景。",
                    outcome.ScenePath,
                    string.Empty,
                    ex.Message,
                    ex.ToString()));
                Debug.LogWarning("[UnityAutoTestMVP] 恢复活动场景失败：" + ex.Message);
            }
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

        private static string DescribeException(Exception ex)
        {
            if (ex == null)
            {
                return "未知异常。";
            }

            if (ex.InnerException != null)
            {
                return ex.GetType().Name + ": " + ex.Message + " -> " + DescribeException(ex.InnerException);
            }

            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}

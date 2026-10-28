using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityAutoTestMVP
{
    internal enum SmokePhase
    {
        Idle = 0,
        Starting = 1,
        Running = 2
    }

    internal sealed class RuntimeLogEntry
    {
        public DateTime TimestampUtc;
        public LogType Type;
        public string Message = string.Empty;
        public string StackTrace = string.Empty;
    }

    /// <summary>一次冒烟测试结束后移交给出报告构建器的数据。</summary>
    internal sealed class SmokeRunOutcome
    {
        public string TaskId = string.Empty;
        public string ScenePath = string.Empty;
        public string SceneName = string.Empty;
        public DateTime StartedUtc;
        public DateTime PlayStartedUtc;
        public DateTime EndedUtc;
        public int ConfiguredDurationSeconds;
        public double ElapsedSeconds;
        public bool IncludeWarnings;
        public SmokeEndReason EndReason;
        public bool LogsTruncated;
        public bool ReloadDuringRun;
        public bool HadPlayStarted;
        public string Detail = string.Empty;
        public List<RuntimeLogEntry> Logs = new List<RuntimeLogEntry>();
    }

    /// <summary>
    /// 跨 Play Mode / Domain Reload 的冒烟测试状态机。
    ///
    /// 设计要点：
    /// - 所有跨 Domain Reload 的状态写入 SessionState（普通 static 字段不可靠）；
    /// - [InitializeOnLoad] 静态构造函数在每次 Domain Reload 后重新订阅事件；
    /// - 静态 _hooked / _logSubscribed 标志防止关闭 Domain Reload 时重复订阅；
    /// - 日志只缓冲在内存中，并在 ExitingPlayMode（Domain Reload 之前）汇总成报告；
    /// - 用户手动退出 Play Mode、编辑器关闭、无法进入 Play Mode 都会安全地结束任务。
    /// </summary>
    [InitializeOnLoad]
    internal static class AutoTestSession
    {
        private const double EnterPlayModeGraceSeconds = 20.0;
        private const double ExitPlayModeGraceSeconds = 15.0;

        private static readonly object LogLock = new object();
        private static readonly List<RuntimeLogEntry> Logs = new List<RuntimeLogEntry>();
        private static volatile bool _capturing;
        private static bool _logsTruncated;
        private static bool _hooked;
        private static bool _logSubscribed;
        private static bool _exitRequested;
        private static DateTime _exitRequestedUtc = DateTime.MinValue;

        public static event Action StateChanged;

        static AutoTestSession()
        {
            HookEditorEvents();
            ResumeLogCaptureIfNeeded();
        }

        public static SmokePhase Phase
        {
            get
            {
                string raw = SessionState.GetString(AutoTestConstants.SessionKeys.Phase, SmokePhase.Idle.ToString());
                SmokePhase phase;
                return Enum.TryParse(raw, true, out phase) ? phase : SmokePhase.Idle;
            }
        }

        public static bool IsRunning
        {
            get { return Phase != SmokePhase.Idle; }
        }

        public static string CurrentTaskId
        {
            get { return SessionState.GetString(AutoTestConstants.SessionKeys.TaskId, string.Empty); }
        }

        public static string CurrentScenePath
        {
            get { return SessionState.GetString(AutoTestConstants.SessionKeys.ScenePath, string.Empty); }
        }

        public static string CurrentSceneName
        {
            get { return SessionState.GetString(AutoTestConstants.SessionKeys.SceneName, string.Empty); }
        }

        public static int ConfiguredDurationSeconds
        {
            get
            {
                return Mathf.Clamp(
                    SessionState.GetInt(
                        AutoTestConstants.SessionKeys.DurationSeconds,
                        AutoTestConstants.DefaultSmokeDurationSeconds),
                    AutoTestConstants.MinSmokeDurationSeconds,
                    AutoTestConstants.MaxSmokeDurationSeconds);
            }
        }

        public static bool IncludeWarnings
        {
            get { return SessionState.GetBool(AutoTestConstants.SessionKeys.IncludeWarnings, true); }
        }

        public static DateTime StartTimeUtc
        {
            get { return GetUtcDate(AutoTestConstants.SessionKeys.StartUtcTicks); }
        }

        public static double ElapsedSeconds
        {
            get
            {
                if (Phase != SmokePhase.Running)
                {
                    return 0.0;
                }

                DateTime playStart = GetUtcDate(AutoTestConstants.SessionKeys.PlayStartUtcTicks);
                if (playStart == DateTime.MinValue)
                {
                    return 0.0;
                }

                return Math.Max(0.0, (DateTime.UtcNow - playStart).TotalSeconds);
            }
        }

        public static double RemainingSeconds
        {
            get { return Math.Max(0.0, ConfiguredDurationSeconds - ElapsedSeconds); }
        }

        public static float Progress01
        {
            get
            {
                int duration = ConfiguredDurationSeconds;
                if (duration <= 0)
                {
                    return 0f;
                }

                return Mathf.Clamp01((float)(ElapsedSeconds / duration));
            }
        }

        /// <summary>从用户点击“运行”开始计算的总时长，用于进入 Play Mode 阶段的计时显示。</summary>
        public static double WallClockSeconds
        {
            get
            {
                DateTime start = StartTimeUtc;
                if (start == DateTime.MinValue)
                {
                    return 0.0;
                }

                return Math.Max(0.0, (DateTime.UtcNow - start).TotalSeconds);
            }
        }

        /// <summary>
        /// 启动冒烟测试。调用方必须已经完成场景、编译状态、未保存修改等检查；
        /// 本方法只负责状态初始化并请求进入 Play Mode。
        /// </summary>
        public static bool TryStart(
            string scenePath,
            string sceneName,
            int durationSeconds,
            bool includeWarnings,
            out string error)
        {
            error = string.Empty;

            if (IsRunning)
            {
                error = "已有冒烟测试正在运行，请等待结束或先停止。";
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                error = "Unity 当前已处于 Play Mode 或正在切换 Play Mode。";
                return false;
            }

            if (EditorApplication.isCompiling)
            {
                error = "Unity 正在编译脚本，请等待编译完成后再试。";
                return false;
            }

            if (EditorApplication.isUpdating)
            {
                error = "Unity 正在导入/刷新资源，请稍后重试。";
                return false;
            }

            if (Application.isBatchMode)
            {
                error = "批处理模式（batchmode）下不支持交互式 Play Mode 冒烟测试。";
                return false;
            }

            if (string.IsNullOrEmpty(scenePath))
            {
                error = "当前活动场景没有资源路径（可能是尚未保存的新场景），无法测试。";
                return false;
            }

            int duration = Mathf.Clamp(
                durationSeconds,
                AutoTestConstants.MinSmokeDurationSeconds,
                AutoTestConstants.MaxSmokeDurationSeconds);

            ClearLogBuffer();

            SessionState.SetString(AutoTestConstants.SessionKeys.Phase, SmokePhase.Starting.ToString());
            SessionState.SetString(AutoTestConstants.SessionKeys.TaskId, Guid.NewGuid().ToString("N"));
            SessionState.SetString(AutoTestConstants.SessionKeys.ScenePath, scenePath);
            SessionState.SetString(AutoTestConstants.SessionKeys.SceneName, sceneName ?? string.Empty);
            SetUtcDate(AutoTestConstants.SessionKeys.StartUtcTicks, DateTime.UtcNow);
            SetUtcDate(AutoTestConstants.SessionKeys.PlayStartUtcTicks, DateTime.MinValue);
            SessionState.SetInt(AutoTestConstants.SessionKeys.DurationSeconds, duration);
            SessionState.SetBool(AutoTestConstants.SessionKeys.IncludeWarnings, includeWarnings);
            SessionState.SetBool(AutoTestConstants.SessionKeys.StopRequested, false);
            SessionState.SetBool(AutoTestConstants.SessionKeys.NaturalTimeout, false);
            SessionState.SetBool(AutoTestConstants.SessionKeys.ReloadDuringRun, false);
            _exitRequested = false;
            _exitRequestedUtc = DateTime.MinValue;

            // 从请求进入 Play Mode 开始捕获日志：
            // - 关闭 Domain Reload 时，这里订阅的回调会持续生效；
            // - 开启 Domain Reload 时，新 Domain 的 [InitializeOnLoad] 会重新订阅（见 ResumeLogCaptureIfNeeded）。
            // 这样可以捕获场景 Awake/初始化阶段、EnteredPlayMode 之前的运行时错误。
            _capturing = true;
            EnsureLogSubscription();

            RaiseStateChanged();

            try
            {
                EditorApplication.isPlaying = true;
            }
            catch (Exception ex)
            {
                ResetRunState();
                _capturing = false;
                EndLogSubscription();
                error = "请求进入 Play Mode 失败：" + ex.GetType().Name + ": " + ex.Message;
                Debug.LogError("[UnityAutoTestMVP] " + error + "\n" + ex);
                RaiseStateChanged();
                return false;
            }

            return true;
        }

        /// <summary>用户主动停止。返回 false 表示当前没有可停止的任务。</summary>
        public static bool RequestStop(out string message)
        {
            SmokePhase phase = Phase;
            if (phase == SmokePhase.Idle)
            {
                message = "当前没有正在运行的冒烟测试。";
                return false;
            }

            SessionState.SetBool(AutoTestConstants.SessionKeys.StopRequested, true);
            SessionState.SetBool(AutoTestConstants.SessionKeys.NaturalTimeout, false);

            if (phase == SmokePhase.Starting)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    FinalizeRun(SmokeEndReason.UserCancelled, "在进入 Play Mode 之前被用户取消。");
                    message = "已取消冒烟测试（尚未进入 Play Mode）。";
                    return true;
                }

                message = "已请求停止：Unity 正在进入 Play Mode，进入后将立即退出。";
                return true;
            }

            if (EditorApplication.isPlaying)
            {
                RequestExitPlayMode(false);
                message = "已请求停止冒烟测试，正在退出 Play Mode...";
                return true;
            }

            FinalizeRun(SmokeEndReason.UserCancelled, "Play Mode 已结束，按用户取消处理。");
            message = "已取消冒烟测试。";
            return true;
        }

        private static void HookEditorEvents()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void ResumeLogCaptureIfNeeded()
        {
            // 进入 Play Mode 的 Domain Reload 会清空静态日志缓冲和订阅。
            // 只要测试仍在进行（Starting 或 Running），就在新 Domain 中重新开始捕获，
            // 避免漏掉 EnteredPlayMode 之前（场景加载/初始化）产生的运行时错误。
            if (Phase != SmokePhase.Idle)
            {
                _capturing = true;
                EnsureLogSubscription();
            }
        }

        private static void OnEditorUpdate()
        {
            SmokePhase phase = Phase;

            if (phase == SmokePhase.Running)
            {
                if (EditorApplication.isPlaying)
                {
                    if (_exitRequested && _exitRequestedUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - _exitRequestedUtc).TotalSeconds > ExitPlayModeGraceSeconds)
                    {
                        bool natural = SessionState.GetBool(AutoTestConstants.SessionKeys.NaturalTimeout, false);
                        FinalizeRun(
                            natural ? SmokeEndReason.Timeout : SmokeEndReason.UserCancelled,
                            "请求退出 Play Mode 后 " + ExitPlayModeGraceSeconds.ToString("0") +
                            " 秒仍未退出，已终止测试记录；游戏可能仍在运行，请手动退出 Play Mode。");

                        try
                        {
                            EditorApplication.isPlaying = false;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }

                        return;
                    }

                    if (!_exitRequested && ElapsedSeconds >= ConfiguredDurationSeconds)
                    {
                        RequestExitPlayMode(true);
                    }

                    return;
                }

                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                    !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    FinalizeRun(
                        SmokeEndReason.UserExitedPlayMode,
                        "检测到 EditorApplication.isPlaying 已为 false（可能是用户手动退出 Play Mode 或 Play Mode 异常结束）。");
                }

                return;
            }

            if (phase == SmokePhase.Starting)
            {
                if (EditorApplication.isPlaying)
                {
                    // 兜底：如果 EnteredPlayMode 事件因 Domain Reload 时序问题没有触发。
                    BeginRunning();
                    return;
                }

                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                    EditorApplication.isUpdating)
                {
                    return;
                }

                DateTime start = StartTimeUtc;
                if (start != DateTime.MinValue && (DateTime.UtcNow - start).TotalSeconds > EnterPlayModeGraceSeconds)
                {
                    FinalizeRun(
                        SmokeEndReason.NotEnteredPlayMode,
                        "请求进入 Play Mode 后 " + EnterPlayModeGraceSeconds.ToString("0") +
                        " 秒内未能进入，测试被阻断（常见原因：编译错误、脚本异常或编辑器弹窗）。");
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (Phase == SmokePhase.Starting)
                    {
                        BeginRunning();
                    }
                    else if (Phase == SmokePhase.Running)
                    {
                        // 兜底：Phase 在 Domain Reload 前已是 Running 时，确保新 Domain 订阅了日志。
                        _capturing = true;
                        EnsureLogSubscription();
                    }
                    break;

                case PlayModeStateChange.ExitingPlayMode:
                    if (Phase == SmokePhase.Running || Phase == SmokePhase.Starting)
                    {
                        FinalizeRun(DetermineEndReason(), string.Empty);
                    }

                    break;

                case PlayModeStateChange.EnteredEditMode:
                    if (Phase != SmokePhase.Idle)
                    {
                        FinalizeRun(
                            SmokeEndReason.UserExitedPlayMode,
                            "已回到 Edit Mode，但仍存在未结束的测试状态，按取消处理。");
                    }

                    break;
            }
        }

        private static void BeginRunning()
        {
            if (Phase != SmokePhase.Starting)
            {
                return;
            }

            SessionState.SetString(AutoTestConstants.SessionKeys.Phase, SmokePhase.Running.ToString());
            SetUtcDate(AutoTestConstants.SessionKeys.PlayStartUtcTicks, DateTime.UtcNow);
            _exitRequested = false;
            _exitRequestedUtc = DateTime.MinValue;
            // 不在这里清空日志：进入 Play Mode 期间（EnteredPlayMode 之前）的错误也需要保留在报告中。
            _capturing = true;
            EnsureLogSubscription();
            RaiseStateChanged();

            // 用户在进入 Play Mode 的过程中点击了停止。
            if (SessionState.GetBool(AutoTestConstants.SessionKeys.StopRequested, false))
            {
                RequestExitPlayMode(false);
            }
        }

        private static SmokeEndReason DetermineEndReason()
        {
            bool stopRequested = SessionState.GetBool(AutoTestConstants.SessionKeys.StopRequested, false);
            bool naturalTimeout = SessionState.GetBool(AutoTestConstants.SessionKeys.NaturalTimeout, false);
            bool playStarted = GetUtcDate(AutoTestConstants.SessionKeys.PlayStartUtcTicks) != DateTime.MinValue;

            if (stopRequested)
            {
                return SmokeEndReason.UserCancelled;
            }

            if (!playStarted)
            {
                return SmokeEndReason.NotEnteredPlayMode;
            }

            return naturalTimeout ? SmokeEndReason.Timeout : SmokeEndReason.UserExitedPlayMode;
        }

        private static void RequestExitPlayMode(bool natural)
        {
            SessionState.SetBool(AutoTestConstants.SessionKeys.StopRequested, !natural);
            SessionState.SetBool(AutoTestConstants.SessionKeys.NaturalTimeout, natural);
            _exitRequested = true;
            _exitRequestedUtc = DateTime.UtcNow;

            try
            {
                EditorApplication.isPlaying = false;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                FinalizeRun(
                    natural ? SmokeEndReason.Timeout : SmokeEndReason.UserCancelled,
                    "请求退出 Play Mode 失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void FinalizeRun(SmokeEndReason reason, string detail)
        {
            // 先切换到 Idle，防止 ExitingPlayMode / EnteredEditMode 等事件重复生成报告。
            if (Phase == SmokePhase.Idle)
            {
                return;
            }

            string taskId = CurrentTaskId;
            string scenePath = CurrentScenePath;
            string sceneName = CurrentSceneName;
            DateTime startedUtc = StartTimeUtc;
            DateTime playStartedUtc = GetUtcDate(AutoTestConstants.SessionKeys.PlayStartUtcTicks);
            DateTime endedUtc = DateTime.UtcNow;
            int duration = ConfiguredDurationSeconds;
            bool includeWarnings = IncludeWarnings;
            bool reloadDuringRun = SessionState.GetBool(AutoTestConstants.SessionKeys.ReloadDuringRun, false);
            bool hadPlayStarted = playStartedUtc != DateTime.MinValue;

            if (reason == SmokeEndReason.Timeout && !hadPlayStarted)
            {
                reason = SmokeEndReason.NotEnteredPlayMode;
            }

            _capturing = false;
            _exitRequested = false;
            _exitRequestedUtc = DateTime.MinValue;

            bool truncated;
            List<RuntimeLogEntry> logs = SnapshotLogs(out truncated);
            EndLogSubscription();

            double elapsed = hadPlayStarted
                ? Math.Max(0.0, (endedUtc - playStartedUtc).TotalSeconds)
                : 0.0;

            SessionState.SetString(AutoTestConstants.SessionKeys.Phase, SmokePhase.Idle.ToString());
            ResetRunState();
            RaiseStateChanged();

            SmokeRunOutcome outcome = new SmokeRunOutcome();
            outcome.TaskId = taskId;
            outcome.ScenePath = scenePath;
            outcome.SceneName = sceneName;
            outcome.StartedUtc = startedUtc == DateTime.MinValue ? endedUtc : startedUtc;
            outcome.PlayStartedUtc = playStartedUtc;
            outcome.EndedUtc = endedUtc;
            outcome.ConfiguredDurationSeconds = duration;
            outcome.ElapsedSeconds = elapsed;
            outcome.IncludeWarnings = includeWarnings;
            outcome.EndReason = reason;
            outcome.LogsTruncated = truncated;
            outcome.ReloadDuringRun = reloadDuringRun;
            outcome.HadPlayStarted = hadPlayStarted;
            outcome.Detail = detail ?? string.Empty;
            outcome.Logs = logs;

            try
            {
                SmokeTestRunner.HandleRunCompleted(outcome);
            }
            catch (Exception ex)
            {
                // 报告构建/保存失败必须记录，但不能让编辑器卡在退出 Play Mode 的流程中。
                Debug.LogError("[UnityAutoTestMVP] 生成冒烟测试报告时发生异常：" + ex.GetType().Name + ": " + ex.Message);
                Debug.LogException(ex);
            }
        }

        private static void OnEditorQuitting()
        {
            if (Phase != SmokePhase.Idle)
            {
                FinalizeRun(SmokeEndReason.EditorQuitting, "Unity Editor 正在关闭，测试被取消。");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (Phase == SmokePhase.Running)
            {
                SessionState.SetBool(AutoTestConstants.SessionKeys.ReloadDuringRun, true);
            }
        }

        private static void ResetRunState()
        {
            SessionState.SetString(AutoTestConstants.SessionKeys.Phase, SmokePhase.Idle.ToString());
            SessionState.EraseString(AutoTestConstants.SessionKeys.TaskId);
            SessionState.EraseString(AutoTestConstants.SessionKeys.ScenePath);
            SessionState.EraseString(AutoTestConstants.SessionKeys.SceneName);
            SessionState.EraseString(AutoTestConstants.SessionKeys.StartUtcTicks);
            SessionState.EraseString(AutoTestConstants.SessionKeys.PlayStartUtcTicks);
            SessionState.EraseInt(AutoTestConstants.SessionKeys.DurationSeconds);
            SessionState.EraseBool(AutoTestConstants.SessionKeys.IncludeWarnings);
            SessionState.EraseBool(AutoTestConstants.SessionKeys.StopRequested);
            SessionState.EraseBool(AutoTestConstants.SessionKeys.NaturalTimeout);
            SessionState.EraseBool(AutoTestConstants.SessionKeys.ReloadDuringRun);
            ClearLogBuffer();
        }

        private static void EnsureLogSubscription()
        {
            if (_logSubscribed)
            {
                return;
            }

            _logSubscribed = true;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        private static void EndLogSubscription()
        {
            if (!_logSubscribed)
            {
                return;
            }

            _logSubscribed = false;
            Application.logMessageReceivedThreaded -= OnLogMessage;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (!_capturing)
            {
                return;
            }

            lock (LogLock)
            {
                if (Logs.Count >= AutoTestConstants.MaxRuntimeLogEntries)
                {
                    _logsTruncated = true;
                    return;
                }

                RuntimeLogEntry entry = new RuntimeLogEntry();
                entry.TimestampUtc = DateTime.UtcNow;
                entry.Type = type;
                entry.Message = condition ?? string.Empty;
                entry.StackTrace = stackTrace ?? string.Empty;
                Logs.Add(entry);
            }
        }

        private static void ClearLogBuffer()
        {
            lock (LogLock)
            {
                Logs.Clear();
                _logsTruncated = false;
            }
        }

        private static List<RuntimeLogEntry> SnapshotLogs(out bool truncated)
        {
            lock (LogLock)
            {
                truncated = _logsTruncated;
                return new List<RuntimeLogEntry>(Logs);
            }
        }

        private static DateTime GetUtcDate(string key)
        {
            string raw = SessionState.GetString(key, string.Empty);
            long ticks;
            if (!string.IsNullOrEmpty(raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) &&
                ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }

            return DateTime.MinValue;
        }

        private static void SetUtcDate(string key, DateTime value)
        {
            long ticks = value == DateTime.MinValue ? 0 : value.ToUniversalTime().Ticks;
            SessionState.SetString(key, ticks.ToString(CultureInfo.InvariantCulture));
        }

        private static void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                // UI 回调异常不应破坏状态机。
                Debug.LogException(ex);
            }
        }
    }
}

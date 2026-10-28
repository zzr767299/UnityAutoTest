using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityAutoTestMVP
{
    /// <summary>
    /// 监听工具加载之后发生的程序集编译。
    /// 结果写入 SessionState，因此编译触发的 Domain Reload 不会导致结果丢失。
    /// 注意：无法读取 Unity Console 中的历史编译错误，未监听到编译时状态为 Unknown。
    /// </summary>
    [InitializeOnLoad]
    internal static class CompilationMonitor
    {
        private const int MaxMessageLength = 2000;

        private static bool _subscribed;
        private static readonly List<CompilationMessage> PendingMessages = new List<CompilationMessage>();
        private static int _pendingAssemblyCount;
        private static bool _pendingTruncated;

        private static CompilationSnapshot _cachedSnapshot;
        private static bool _cacheValid;

        static CompilationMonitor()
        {
            EnsureSubscribed();
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationStarted(object context)
        {
            PendingMessages.Clear();
            _pendingAssemblyCount = 0;
            _pendingTruncated = false;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _pendingAssemblyCount++;
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            for (int i = 0; i < messages.Length; i++)
            {
                CompilerMessage message = messages[i];
                bool isError;
                if (message.type == CompilerMessageType.Error)
                {
                    isError = true;
                }
                else if (message.type == CompilerMessageType.Warning)
                {
                    isError = false;
                }
                else
                {
                    continue;
                }

                if (PendingMessages.Count >= AutoTestConstants.MaxCompilationMessages)
                {
                    _pendingTruncated = true;
                    break;
                }

                CompilationMessage entry = new CompilationMessage();
                entry.IsError = isError;
                entry.AssemblyPath = assemblyPath ?? string.Empty;
                entry.File = message.file ?? string.Empty;
                entry.Line = message.line;
                entry.Column = message.column;
                entry.Message = Truncate(message.message ?? string.Empty, MaxMessageLength);
                PendingMessages.Add(entry);
            }
        }

        private static void OnCompilationFinished(object context)
        {
            bool hasError = false;
            bool hasWarning = false;
            for (int i = 0; i < PendingMessages.Count; i++)
            {
                if (PendingMessages[i].IsError)
                {
                    hasError = true;
                }
                else
                {
                    hasWarning = true;
                }
            }

            CompilationStatus status;
            if (hasError)
            {
                status = CompilationStatus.Failed;
            }
            else if (hasWarning)
            {
                status = CompilationStatus.SucceededWithWarnings;
            }
            else
            {
                status = CompilationStatus.Succeeded;
            }

            SessionState.SetString(AutoTestConstants.SessionKeys.CompileStatus, status.ToString());
            SessionState.SetString(
                AutoTestConstants.SessionKeys.CompileTimestampUtc,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            SessionState.SetInt(AutoTestConstants.SessionKeys.CompileAssemblyCount, _pendingAssemblyCount);
            SessionState.SetBool(AutoTestConstants.SessionKeys.CompileTruncated, _pendingTruncated);
            SessionState.SetString(
                AutoTestConstants.SessionKeys.CompileMessages,
                CompilationMessageCodec.Encode(PendingMessages));

            _cacheValid = false;
            PendingMessages.Clear();
        }

        /// <summary>读取最近一次监听到的编译结果（带缓存，避免每帧重复解码）。</summary>
        public static CompilationSnapshot GetSnapshot()
        {
            if (_cacheValid && _cachedSnapshot != null)
            {
                return _cachedSnapshot;
            }

            CompilationSnapshot snapshot = new CompilationSnapshot();

            string statusText = SessionState.GetString(
                AutoTestConstants.SessionKeys.CompileStatus,
                CompilationStatus.Unknown.ToString());
            CompilationStatus status;
            if (!Enum.TryParse(statusText, true, out status))
            {
                status = CompilationStatus.Unknown;
            }

            snapshot.Status = status;
            snapshot.AssemblyCount = SessionState.GetInt(AutoTestConstants.SessionKeys.CompileAssemblyCount, 0);
            snapshot.Truncated = SessionState.GetBool(AutoTestConstants.SessionKeys.CompileTruncated, false);

            string timestamp = SessionState.GetString(AutoTestConstants.SessionKeys.CompileTimestampUtc, string.Empty);
            DateTime parsed;
            if (!string.IsNullOrEmpty(timestamp) &&
                DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                snapshot.TimestampUtc = parsed.ToUniversalTime();
            }

            snapshot.Messages = CompilationMessageCodec.Decode(
                SessionState.GetString(AutoTestConstants.SessionKeys.CompileMessages, string.Empty));

            _cachedSnapshot = snapshot;
            _cacheValid = true;
            return snapshot;
        }

        /// <summary>
        /// 供测试/诊断使用：直接记录一批编译消息。不会修改 Unity 的实际编译状态。
        /// </summary>
        internal static void CommitMessagesForDiagnostics(
            CompilationStatus status,
            IList<CompilationMessage> messages,
            int assemblyCount,
            bool truncated)
        {
            SessionState.SetString(AutoTestConstants.SessionKeys.CompileStatus, status.ToString());
            SessionState.SetString(
                AutoTestConstants.SessionKeys.CompileTimestampUtc,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            SessionState.SetInt(AutoTestConstants.SessionKeys.CompileAssemblyCount, assemblyCount);
            SessionState.SetBool(AutoTestConstants.SessionKeys.CompileTruncated, truncated);
            SessionState.SetString(
                AutoTestConstants.SessionKeys.CompileMessages,
                CompilationMessageCodec.Encode(messages));
            _cacheValid = false;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength) + "...(truncated)";
        }
    }
}

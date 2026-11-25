using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    // Append-only journal: a missing finish event also diagnoses a crashed/closed game.
    // Input text is kept separately from progress. Never record headers or credentials.
    internal sealed class TranslationDiagnostics
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, JObject> _remaining = new Dictionary<string, JObject>();
        private readonly Dictionary<string, JObject> _results = new Dictionary<string, JObject>();
        private readonly string[] _secrets;
        private readonly DateTime _started = DateTime.UtcNow;
        private readonly string _taskId;
        private readonly string _domain;
        private readonly int _planned;
        private JObject _configuration;
        private ServerJobState _serverStatus;
        internal string FilePath { get; private set; }
        internal string InputPath { get; }
        internal string ReportPath { get; }
        internal string WriteError { get; private set; }
        internal int Requests { get; private set; }
        internal long InputTokens { get; private set; }
        internal long OutputTokens { get; private set; }
        internal int UsageResponses { get; private set; }

        internal TranslationDiagnostics(string domain, IEnumerable<TranslationWorkItem> items,
            IEnumerable<string> secrets = null, string rootOverride = null)
        {
            _secrets = (secrets ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany(s => new[] { s, s.Trim() })
                .OrderByDescending(s => s.Length).Distinct().ToArray();
            var directory = TranslationFoundation.TranslationLogPath.ForProject(rootOverride ?? Paths.PluginPath, "ConfigurationManager.zh-cn");
            _taskId = _started.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _domain = DomainLabel(domain);
            FilePath = Path.Combine(directory, "翻译过程-" + _taskId + ".jsonl");
            InputPath = Path.Combine(directory, "翻译输入-" + _taskId + ".jsonl");
            ReportPath = Path.Combine(directory, "翻译总览-" + _taskId + ".txt");
            var planned = (items ?? Enumerable.Empty<TranslationWorkItem>()).ToList();
            foreach (var item in planned)
                _remaining[Key(item)] = Row(item, "not_started", null);
            _planned = _remaining.Count;
            Write("overview", new JObject { ["task"] = _domain, ["status"] = "已开始", ["planned"] = _planned,
                ["report"] = Path.GetFileName(ReportPath), ["input"] = Path.GetFileName(InputPath) });
            Write("start", new JObject { ["domain"] = domain, ["items"] = new JArray(_remaining.Values),
                ["note"] = "无 finish 事件表示游戏关闭、崩溃或任务被中断；token 只统计供应商实际返回的 usage，不是费用估算。" });
            Write("input", new JObject { ["items"] = new JArray(planned.Select(item =>
                new JObject { ["id"] = item.Id, ["plugin"] = item.PluginName ?? item.PluginGuid, ["source"] = item.Source })) });
            WriteReport(null, null);
        }

        private static string Key(TranslationWorkItem item) { return item.PluginGuid + "\n" + item.Id; }
        private static JObject Row(TranslationWorkItem item, string state, string reason)
        {
            return new JObject { ["plugin"] = item.PluginName ?? item.PluginGuid, ["pluginGuid"] = item.PluginGuid,
                ["id"] = item.Id, ["setting"] = item.EntryKey ?? item.EnumMember ?? item.Kind,
                ["field"] = item.Field ?? item.Kind, ["state"] = state, ["reason"] = reason };
        }

        internal void Observe(TranslationActivity activity)
        {
            lock (_sync)
            {
                if (activity.RequestStarted) Requests++;
                if (activity.RequestStarted && !string.IsNullOrEmpty(activity.SystemPrompt))
                    Write("input", new JObject { ["requestCount"] = Requests, ["prompt"] = activity.SystemPrompt });
                if (activity.InputTokens.HasValue || activity.OutputTokens.HasValue) UsageResponses++;
                InputTokens += activity.InputTokens ?? 0;
                OutputTokens += activity.OutputTokens ?? 0;
                var rows = new JArray();
                foreach (var item in activity.Items ?? new List<TranslationWorkItem>())
                {
                    var row = Row(item, "in_progress", activity.Phase);
                    var key = Key(item);
                    if (!_results.ContainsKey(key)) _remaining[key] = row;
                    rows.Add(row);
                }
                Write("activity", new JObject { ["phase"] = activity.Phase, ["requestCount"] = Requests,
                    ["inputCharacters"] = activity.InputCharacters, ["inputTokens"] = activity.InputTokens,
                    ["outputTokens"] = activity.OutputTokens, ["items"] = rows });
            }
        }

        internal void Result(IEnumerable<TranslationWorkItem> items, string state, string reason = null)
        {
            lock (_sync)
            {
                var rows = new JArray();
                foreach (var item in items)
                {
                    var row = Row(item, state, reason);
                    var key = Key(item);
                    _remaining.Remove(key);
                    _results[key] = row;
                    rows.Add(row);
                }
                Write("result", new JObject { ["items"] = rows });
            }
        }

        internal void Write(string kind, JObject data)
        {
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if (kind == "configuration") _configuration = (JObject)data.DeepClone();
                    var record = new JObject { ["timeUtc"] = DateTime.UtcNow.ToString("O"), ["taskId"] = _taskId,
                        ["event"] = kind, ["data"] = data };
                    // Redact string values before JSON escaping (keys can contain punctuation).
                    foreach (var value in record.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String && v.Value != null))
                    {
                        value.Value = Redact((string)value);
                    }
                    var textRecord = record.ToString(Formatting.None) + "\n";
                    File.AppendAllText(kind == "input" ? InputPath : FilePath, textRecord, new UTF8Encoding(false));
                    if (kind == "finish" && !string.IsNullOrWhiteSpace((string)data["error"]) ||
                        kind == "result" && (data["items"] as JArray ?? new JArray()).Any(row => (string)row["state"] == "failed"))
                        lock (OperationSync)
                            File.AppendAllText(DailyLog(Path.GetDirectoryName(FilePath), true), textRecord, new UTF8Encoding(false));
                }
                catch (Exception ex) { WriteError = "诊断日志写入失败：" + ex.GetType().Name; }
            }
        }

        internal static void Operation(string action, string detail, bool error = false)
        {
            try
            {
                var root = TranslationFoundation.TranslationLogPath.ForProject(Paths.PluginPath, "ConfigurationManager.zh-cn");
                lock (OperationSync)
                {
                    Directory.CreateDirectory(root);
                    var row = new JObject { ["timeUtc"] = DateTime.UtcNow.ToString("O"), ["action"] = action, ["detail"] = detail };
                    File.AppendAllText(DailyLog(root, error), row.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }
        private static readonly object OperationSync = new object();
        internal static string DailyLog(string directory, bool error)
        {
            return Path.Combine(directory, (error ? "错误汇总-" : "操作记录-") +
                DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        internal void Finish(string state, string error = null)
        {
            lock (_sync)
            {
                Write("finish", new JObject { ["state"] = state, ["error"] = error,
                    ["elapsedSeconds"] = (DateTime.UtcNow - _started).TotalSeconds, ["requests"] = Requests,
                    ["inputTokensReported"] = InputTokens, ["outputTokensReported"] = OutputTokens,
                    ["responsesWithUsage"] = UsageResponses, ["unfinished"] = new JArray(_remaining.Values),
                    ["planned"] = _serverStatus?.Total ?? _planned,
                    ["saved"] = _serverStatus?.Saved ?? _results.Values.Count(row => (string)row["state"] == "saved"),
                    ["failed"] = _serverStatus?.Rejected ?? _results.Values.Count(row => (string)row["state"] == "failed") });
                WriteReport(state, error);
            }
        }

        internal void ServerProgress(ServerJobState job)
        {
            lock (_sync)
            {
                _serverStatus = job;
                var snapshot = job.Diagnostics;
                if (snapshot == null) return;
                Requests = (int?)snapshot["requests"] ?? 0;
                InputTokens = (long?)snapshot["inputTokensReported"] ?? 0;
                OutputTokens = (long?)snapshot["outputTokensReported"] ?? 0;
                UsageResponses = (int?)snapshot["responsesWithUsage"] ?? 0;
                Write("server-progress", (JObject)snapshot.DeepClone());
            }
        }

        private void WriteReport(string state, string error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                var report = new StringBuilder();
                var saved = _serverStatus?.Saved ?? _results.Values.Count(row => (string)row["state"] == "saved");
                var failed = _results.Values.Where(row => (string)row["state"] == "failed").ToArray();
                var failedCount = _serverStatus?.Rejected ?? failed.Length;
                var planned = _serverStatus?.Total ?? _planned;
                report.AppendLine("翻译总览 · " + _domain);
                report.AppendLine("开始时间  " + _started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                report.AppendLine("任务编号  " + _taskId);
                report.AppendLine("状态      " + StateLabel(state));
                report.AppendLine();
                if (state == null)
                    report.AppendLine("待处理 " + planned + " 条。任务结束后会写入结果。若游戏中途退出，请查看同编号的翻译过程。");
                else
                {
                    var waiting = _serverStatus != null ? Math.Max(0, planned - _serverStatus.Completed) : _remaining.Count;
                    var savedLabel = _serverStatus == null ? "已保存" : _serverStatus.Contribution ? "已送审" : "服务端已保存";
                    report.AppendLine("计划 " + planned + " 条 · " + savedLabel + " " + saved + " 条 · 失败 " +
                        failedCount + " 条 · 未完成 " + waiting + " 条");
                    var retained = _results.Values.Count(row => (string)row["state"] == "saved" && (string)row["reason"] == "保留原文");
                    if (retained > 0) report.AppendLine("其中保留原文 " + retained + " 条");
                    if (planned > 0)
                        report.AppendLine(savedLabel + "比例  " + (100.0 * saved / planned).ToString("0.00", CultureInfo.InvariantCulture) + "%");
                    if (_serverStatus == null && waiting > 0)
                        report.AppendLine("未完成中有 " + _remaining.Values.Count(row => (string)row["state"] == "in_progress") +
                            " 条处理到一半，其余尚未开始。");
                    report.AppendLine("用时 " + (DateTime.UtcNow - _started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                        " 秒 · API 请求 " + Requests + " 次");
                    report.AppendLine(UsageResponses == 0 ? "供应商未报告 Token 用量。" :
                        "供应商报告用量（" + UsageResponses + " 次响应） · 输入 " + InputTokens + " · 输出 " + OutputTokens);
                    if (!string.IsNullOrWhiteSpace(error)) report.AppendLine("任务错误  " + Compact(error));
                    if (_configuration != null)
                        report.AppendLine("供应商  " + Compact((string)_configuration["provider"]) +
                            " · 模型  " + Compact((string)_configuration["model"]) +
                            " · 批量上限  " + Compact((string)_configuration["batchSize"]));
                    AppendBreakdown(report, failed);
                }
                report.AppendLine().AppendLine("详细记录");
                report.AppendLine("任务进度和逐条结果  " + Path.GetFileName(FilePath));
                report.AppendLine("原文和系统提示词    " + Path.GetFileName(InputPath));
                report.AppendLine("每日错误汇总        按本地日期查看「错误汇总」JSONL，用任务编号筛选。");
                var temporary = ReportPath + ".tmp";
                try
                {
                    File.WriteAllText(temporary, report.ToString(), new UTF8Encoding(true));
                    if (File.Exists(ReportPath)) File.Replace(temporary, ReportPath, null);
                    else File.Move(temporary, ReportPath);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception ex) { WriteError = "翻译总览写入失败：" + ex.GetType().Name; }
        }

        private void AppendBreakdown(StringBuilder report, JObject[] failed)
        {
            var plugins = _results.Values.Concat(_remaining.Values)
                .GroupBy(row => (string)row["pluginGuid"] ?? string.Empty)
                .OrderByDescending(group => group.Count(row => (string)row["state"] == "failed"))
                .ThenBy(group => (string)group.First()["plugin"], StringComparer.Ordinal);
            if (_results.Count + _remaining.Count > 0)
            {
                report.AppendLine().AppendLine("按插件统计");
                foreach (var plugin in plugins)
                    report.AppendLine(Compact((string)plugin.First()["plugin"]) + " · 已保存 " +
                        plugin.Count(row => (string)row["state"] == "saved") + " · 失败 " +
                        plugin.Count(row => (string)row["state"] == "failed") + " · 未完成 " +
                        plugin.Count(row => (string)row["state"] == "in_progress" || (string)row["state"] == "not_started"));
            }
            if (failed.Length == 0) return;
            var causes = failed.GroupBy(row => FailureCategory((string)row["reason"]))
                .OrderByDescending(group => group.Count()).ToArray();
            report.AppendLine().AppendLine("失败原因");
            foreach (var group in causes)
                report.AppendLine(group.Key + " · " + group.Count() + " 条");
            report.AppendLine().AppendLine("失败条目摘录");
            var examples = causes.SelectMany(group => group.Take(5)).Take(20).ToArray();
            foreach (var row in examples)
                report.AppendLine("• " + Compact((string)row["plugin"]) + " / " +
                    Compact((string)row["setting"]) + " / " + Compact((string)row["field"]) +
                    " · " + Compact((string)row["reason"]));
            if (failed.Length > examples.Length)
                report.AppendLine("另有 " + (failed.Length - examples.Length) + " 条，见同编号的翻译过程。");
        }

        private string Redact(string text)
        {
            foreach (var secret in _secrets) text = text.Replace(secret, "[已隐藏]");
            return text;
        }

        private string Compact(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "—";
            var value = string.Join(" ", Redact(text).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return value.Length > 220 ? value.Substring(0, 220) + "…" : value;
        }

        private static string FailureCategory(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "其他失败";
            if (reason.StartsWith("译文仍是英文原文", StringComparison.Ordinal)) return "返回值与原文相同";
            if (reason.StartsWith("插件标题未生成中文", StringComparison.Ordinal)) return "插件标题未生成中文";
            if (reason.StartsWith("受保护的占位符", StringComparison.Ordinal)) return "受保护标记不一致";
            if (reason.StartsWith("富文本标签", StringComparison.Ordinal)) return "富文本标签变化";
            if (reason.StartsWith("换行数量", StringComparison.Ordinal)) return "换行数量变化";
            if (reason.StartsWith("AI 接口响应外层 JSON", StringComparison.Ordinal)) return "接口响应 JSON 无法解析";
            if (reason.StartsWith("AI 译文 JSON 语法错误", StringComparison.Ordinal)) return "译文 JSON 语法错误或截断";
            if (reason.StartsWith("AI 译文 JSON 有重复 ID", StringComparison.Ordinal)) return "译文 ID 重复";
            if (reason.StartsWith("AI 译文 JSON 缺少 ID", StringComparison.Ordinal)) return "译文 ID 缺失";
            if (reason.StartsWith("AI 译文 JSON 的值不是字符串", StringComparison.Ordinal)) return "译文值不是字符串";
            if (reason.StartsWith("AI 译文 JSON 顶层", StringComparison.Ordinal) ||
                reason.StartsWith("AI 接口响应缺少文本", StringComparison.Ordinal) ||
                reason.StartsWith("AI 未返回文本", StringComparison.Ordinal)) return "接口译文格式不符";
            if (reason.StartsWith("接口遗漏", StringComparison.Ordinal) || reason.StartsWith("接口未返回", StringComparison.Ordinal))
                return "接口遗漏译文";
            if (reason.Contains("预算") || reason.Contains("上限")) return "超出任务预算或接口上限";
            return "其他失败";
        }

        private static string DomainLabel(string domain)
        {
            if (domain == "F12 AI") return "F12 API 翻译";
            if (domain == "F12 机翻") return "F12 机器翻译";
            return (domain ?? "").Replace("服务端 ai", "服务端 AI").Replace("服务端 machine", "服务端机器");
        }

        private static string StateLabel(string state)
        {
            switch (state)
            {
                case null: return "进行中";
                case "completed": return "完成";
                case "partial": return "部分完成";
                case "cancelled": return "已取消";
                case "preflight_failed": return "开始前检查失败";
                case "client_cancelled_or_disconnected": return "客户端取消或断开";
                case "client_failed_or_disconnected": return "客户端失败或断开";
                default: return "失败";
            }
        }
    }
}

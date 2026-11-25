using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigurationManagerZhCn
{
    internal sealed class TranslationInventory
    {
        internal bool HasFiles;
        internal string Error;
        internal int Total, Translated, Manual, Ai, Machine, Stale, Overlap;
        internal readonly HashSet<string> ValidIds = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> StoredAiIds = new HashSet<string>(StringComparer.Ordinal);
        internal readonly HashSet<string> StoredManualIds = new HashSet<string>(StringComparer.Ordinal);
        internal string Label => Error != null ? "文件异常" : !HasFiles ? "无 JSONC" :
            Stale > 0 ? "待核对 " + Stale : Translated == Total && Total > 0 ? "已汉化" : "部分汉化 " + Translated + "/" + Total;
        internal string Details => Error ?? "有效译文 " + Translated + "/" + Total + "；人工 " + Manual + "；AI " + Ai + "；机翻 " + Machine +
            "；两层重复 " + Overlap + "；过期 " + Stale + "。状态按原文基准核对，与译文层开关无关。";
    }

    internal sealed class TranslationBatchTarget
    {
        internal string Guid, Name;
        internal IList<TranslationWorkItem> Items;
    }

    internal sealed class TranslationBatchPlan
    {
        internal readonly List<TranslationBatchTarget> Targets = new List<TranslationBatchTarget>();
        internal readonly List<string> Errors = new List<string>();
        internal int SelectedPlugins, ScopedItems, SkippedItems, AiOverwrites, ManualOverlaps;
        internal int TotalItems => Targets.Sum(target => target.Items.Count);

        internal static TranslationBatchPlan Create(IEnumerable<LocalizationSnapshot> snapshots, ISet<string> selected,
            TranslationScope scope, bool ai, string profile, bool force, bool onlyMissing,
            IDictionary<string, TranslationInventory> inventories = null)
        {
            var plan = new TranslationBatchPlan();
            foreach (var snapshot in snapshots.Where(snapshot => selected.Contains(snapshot.Guid)))
            {
                plan.SelectedPlugins++;
                TranslationInventory inventory;
                if (inventories == null || !inventories.TryGetValue(snapshot.Guid, out inventory))
                    inventory = JsoncLocalization.GetInventory(snapshot);
                if (inventory.Error != null) { plan.Errors.Add(snapshot.PluginName + "：" + inventory.Error); continue; }
                var scoped = snapshot.CreateWorkItems(scope);
                plan.ScopedItems += scoped.Count;
                var items = scoped.Where(item => !onlyMissing || !inventory.ValidIds.Contains(item.Id)).ToList();
                if (ai) items = JsoncLocalization.PendingAiItems(items, profile, force).ToList();
                plan.SkippedItems += scoped.Count - items.Count;
                plan.AiOverwrites += ai ? items.Count(item => inventory.StoredAiIds.Contains(item.Id)) : 0;
                plan.ManualOverlaps += items.Count(item => inventory.StoredManualIds.Contains(item.Id));
                if (items.Count > 0) plan.Targets.Add(new TranslationBatchTarget { Guid = snapshot.Guid, Name = snapshot.PluginName, Items = items });
            }
            return plan;
        }
    }

    internal sealed class TranslationBatchResult
    {
        internal int CompletedPlugins, SavedItems;
        internal readonly string Channel = "volcano.workbench-batch.v1";
        internal bool Cancelled;
        internal readonly List<string> Failures = new List<string>();
    }

    internal static class TranslationBatchRunner
    {
        internal static async Task<TranslationBatchResult> RunAsync(TranslationBatchPlan plan,
            Func<TranslationBatchTarget, Action<int, int>, CancellationToken, Task<TranslationResult>> translate,
            Action<TranslationBatchTarget, TranslationResult> save, Action<string, int, int> progress, CancellationToken token,
            int checkpointSize = 0, TranslationDiagnostics diagnostics = null, int checkpointCharacters = 0,
            Func<IList<TranslationWorkItem>, CancellationToken, Task<TranslationResult>> translateTitles = null)
        {
            var report = new TranslationBatchResult();
            if (plan.Errors.Count > 0)
            {
                report.Failures.AddRange(plan.Errors);
                diagnostics?.Finish("preflight_failed", string.Join("; ", plan.Errors.ToArray()));
                return report;
            }
            var processed = 0;
            var stopRequested = false;
            var savedTitles = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (translateTitles != null)
            {
                var titles = plan.Targets.SelectMany(target => target.Items).Where(item => item.Kind == "Plugin").ToList();
                if (titles.Count > 0)
                {
                    TranslationResult result;
                    try
                    {
                        progress?.Invoke("插件标题", 0, plan.TotalItems);
                        result = await translateTitles(titles, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        report.Cancelled = true;
                        diagnostics?.Finish("cancelled");
                        return report;
                    }
                    catch (Exception ex)
                    {
                        result = new TranslationResult();
                        foreach (var title in titles) result.Failures[title.PluginGuid] = ex.Message;
                    }
                    foreach (var title in titles)
                    {
                        if (token.IsCancellationRequested) { report.Cancelled = true; break; }
                        savedTitles[title.PluginGuid] = SaveTitle(title, result, save, report, diagnostics);
                        processed++;
                    }
                    stopRequested = !string.IsNullOrEmpty(result.StopReason);
                    progress?.Invoke("插件标题", processed, plan.TotalItems);
                }
            }
            foreach (var target in plan.Targets)
            {
                if (report.Cancelled || token.IsCancellationRequested) { report.Cancelled = true; break; }
                var titleProcessed = savedTitles.TryGetValue(target.Guid, out var titleSaved);
                var items = titleProcessed ? target.Items.Where(item => item.Kind != "Plugin").ToList() : target.Items;
                var targetSaved = titleSaved ? 1 : 0;
                var targetFailed = titleProcessed && !titleSaved;
                if (items.Count == 0)
                {
                    if (!targetFailed) report.CompletedPlugins++;
                    continue;
                }
                if (stopRequested) continue;
                var chunkSize = checkpointSize > 0 ? Math.Max(1, checkpointSize) : items.Count;
                var chunkOffset = 0;
                foreach (var chunkItems in TranslationClients.BatchByInputSize(items, chunkSize,
                    checkpointCharacters > 0 ? checkpointCharacters : int.MaxValue))
                {
                    var chunk = new TranslationBatchTarget { Guid = target.Guid, Name = target.Name, Items = chunkItems };
                    var offset = processed;
                    try
                    {
                        progress?.Invoke(target.Name, offset, plan.TotalItems);
                        var result = await translate(chunk, (done, total) =>
                            progress?.Invoke(target.Name, offset + Math.Min(done, chunkItems.Count), plan.TotalItems), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        var accepted = chunkItems.Where(item => result.Values.ContainsKey(item.Id)).ToList();
                        if (accepted.Count > 0)
                        {
                            var acceptedTarget = new TranslationBatchTarget { Guid = target.Guid, Name = target.Name, Items = accepted };
                            save(acceptedTarget, result);
                            if (diagnostics != null)
                                foreach (var group in accepted.GroupBy(item => string.Equals(item.Source?.Trim(),
                                    result.Values[item.Id]?.Trim(), StringComparison.OrdinalIgnoreCase)))
                                    diagnostics.Result(group, "saved", group.Key ? "保留原文" : null);
                            targetSaved += accepted.Count;
                            report.SavedItems += accepted.Count;
                        }

                        var missing = chunkItems.Where(item => !result.Values.ContainsKey(item.Id)).ToList();
                        if (missing.Count > 0)
                        {
                            foreach (var item in missing)
                            {
                                string reason;
                                diagnostics?.Result(new[] { item }, "failed",
                                    result.Failures.TryGetValue(item.Id, out reason) ? reason : "接口未返回译文");
                            }
                            targetFailed = true;
                            var details = missing.Take(3).Select(item =>
                            {
                                string failure;
                                return result.Failures.TryGetValue(item.Id, out failure) ? item.Id + "：" + failure : item.Id + "：接口未返回译文";
                            });
                            report.Failures.Add(target.Name + "：本检查点有 " + missing.Count + " 条未保存；" + string.Join("；", details.ToArray()) +
                                (missing.Count > 3 ? "；另有 " + (missing.Count - 3) + " 条未展开" : string.Empty));
                        }
                        if (!string.IsNullOrEmpty(result.StopReason)) { stopRequested = true; break; }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        report.Cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        diagnostics?.Result(chunkItems, "failed", ex.Message);
                        targetFailed = true;
                        report.Failures.Add(target.Name + "：" + ex.Message);
                        processed += items.Count - chunkOffset;
                        progress?.Invoke(target.Name, processed, plan.TotalItems);
                        break;
                    }
                    processed += chunkItems.Count;
                    chunkOffset += chunkItems.Count;
                    progress?.Invoke(target.Name, processed, plan.TotalItems);
                }
                if (report.Cancelled || stopRequested) break;
                if (!targetFailed && targetSaved == target.Items.Count) report.CompletedPlugins++;
            }
            diagnostics?.Finish(report.Cancelled ? "cancelled" : report.Failures.Count > 0 ? "partial" : "completed");
            return report;
        }

        private static bool SaveTitle(TranslationWorkItem title, TranslationResult result,
            Action<TranslationBatchTarget, TranslationResult> save, TranslationBatchResult report, TranslationDiagnostics diagnostics)
        {
            string reason;
            if (result.Values.TryGetValue(title.PluginGuid, out var value))
            {
                try
                {
                    var titleResult = new TranslationResult();
                    titleResult.Values[title.Id] = value;
                    save(new TranslationBatchTarget { Guid = title.PluginGuid, Name = title.PluginName, Items = new[] { title } }, titleResult);
                    diagnostics?.Result(new[] { title }, "saved", value == title.Source ? "保留原文" : null);
                    report.SavedItems++;
                    return true;
                }
                catch (Exception ex) { reason = ex.Message; }
            }
            else if (!result.Failures.TryGetValue(title.PluginGuid, out reason))
                reason = "接口未返回标题译文";
            diagnostics?.Result(new[] { title }, "failed", reason);
            report.Failures.Add((title.PluginName ?? title.PluginGuid) + " · 插件标题：" + reason);
            return false;
        }
    }
}

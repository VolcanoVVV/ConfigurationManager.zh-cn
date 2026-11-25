using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;

internal static partial class Program
{
    private static void CheckLocaleMainThreadAndRestoration()
    {
        MainThreadDispatcher.Initialize();
        var thread = Environment.CurrentManagedThreadId;
        var manager = EFT.LocalizationManager._instance;
        manager._locales["ch"]["restore"] = "旧译文";
        manager._locales["ch"]["delete"] = "应删除";
        var task = Task.Run(() => ClientLocaleRefresher.ApplyAsync(
            new Dictionary<string, string> { ["restore"] = "原文" }, new[] { "delete" }, "ch"));
        PumpUntil(task);
        Check(manager.MergeThread == thread && manager._onLocaleUpdatedEvent.LastThread == thread,
            "network-worker locale merge and UI notification execute on the initialized main thread");
        Check(manager._locales["ch"]["restore"] == "原文" && !manager._locales["ch"].ContainsKey("delete"),
            "locale refresh restores original values and removes server tombstones");
        var previousNotifications = manager._onLocaleUpdatedEvent.Count;
        manager.Culture = "en";
        manager._locales["en"]["restore"] = "English";
        task = Task.Run(() => ClientLocaleRefresher.ApplyAsync(new Dictionary<string, string> { ["restore"] = "新的中文" }, new string[0], "ch"));
        PumpUntil(task);
        Check(manager._locales["en"]["restore"] == "English" && manager._locales["ch"]["restore"] == "新的中文"
            && manager._onLocaleUpdatedEvent.Count == previousNotifications,
            "pull updates Chinese without corrupting the player's active English locale");
        manager.Culture = "ch";
        task = Task.Run(() => ClientLocaleRefresher.ApplyAsync(new Dictionary<string, string>(), new[] { "restore" }, "ch"));
        PumpUntil(task);
        Check(!manager._locales["ch"].ContainsKey("restore"), "removal-only payload is applied");
        MainThreadDispatcher.Shutdown();
        Reject(() => MainThreadDispatcher.InvokeAsync(() => 1).GetAwaiter().GetResult(), "shutdown dispatcher rejects new work");
    }

    private static void PumpUntil(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.ElapsedMilliseconds < 5000)
        {
            MainThreadDispatcher.Pump();
            Thread.Yield();
        }
        Check(task.IsCompleted, "main-thread work completes within timeout");
        task.GetAwaiter().GetResult();
    }
}

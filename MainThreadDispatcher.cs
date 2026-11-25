using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigurationManagerZhCn
{
    internal static class MainThreadDispatcher
    {
        private sealed class Work
        {
            internal Action Run;
            internal Action Cancel;
        }
        private static readonly object Sync = new object();
        private static readonly Queue<Work> Pending = new Queue<Work>();
        private static int _threadId;

        internal static void Initialize()
        {
            lock (Sync) _threadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal static Task<T> InvokeAsync<T>(Func<T> action)
        {
            lock (Sync)
            {
                if (_threadId == 0) return Task.FromException<T>(new InvalidOperationException("游戏主线程调度器尚未就绪或已关闭。"));
                if (Thread.CurrentThread.ManagedThreadId == _threadId)
                {
                    try { return Task.FromResult(action()); }
                    catch (Exception ex) { return Task.FromException<T>(ex); }
                }
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending.Enqueue(new Work
                {
                    Run = () => { try { completion.TrySetResult(action()); } catch (Exception ex) { completion.TrySetException(ex); } },
                    Cancel = () => completion.TrySetCanceled()
                });
                return completion.Task;
            }
        }

        internal static void Pump()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId) return;
            Work[] batch;
            lock (Sync) { batch = Pending.ToArray(); Pending.Clear(); }
            foreach (var work in batch) work.Run();
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                _threadId = 0;
                while (Pending.Count > 0) Pending.Dequeue().Cancel();
            }
        }
    }
}

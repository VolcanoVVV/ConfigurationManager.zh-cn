namespace ConfigurationManagerZhCn;

internal sealed class BrowserTabs
{
    private const long StartupGrace = 30_000;
    private const long NavigationGrace = 10_000;
    private const long CloseGrace = 2_000;
    private const long TabTimeout = 90_000;

    private readonly object sync = new();
    private readonly Dictionary<Guid, (bool Open, long Expires)> tabs = new();
    private long awaitingTabUntil = Environment.TickCount64 + StartupGrace;
    private long closeGraceUntil;
    private bool stopping;

    internal bool ExpectTab()
    {
        lock (sync)
        {
            if (stopping) return false;
            awaitingTabUntil = Math.Max(awaitingTabUntil, Environment.TickCount64 + NavigationGrace);
            return true;
        }
    }

    internal bool Update(Guid id, bool open)
    {
        lock (sync)
        {
            if (stopping) return false;
            // Ignore delayed renewals from a document that already sent pagehide.
            if (tabs.TryGetValue(id, out var previous) && !previous.Open) return true;
            var now = Environment.TickCount64;
            tabs[id] = (open, now + TabTimeout);
            if (open) awaitingTabUntil = 0;
            else if (!HasOpenTab(now)) closeGraceUntil = now + CloseGrace;
            return true;
        }
    }

    internal async Task StopWhenIdleAsync(IHostApplicationLifetime lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.ApplicationStopping))
            {
                if (!BeginStoppingIfIdle()) continue;
                lifetime.StopApplication();
                break;
            }
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
    }

    private bool BeginStoppingIfIdle()
    {
        lock (sync)
        {
            var now = Environment.TickCount64;
            foreach (var pair in tabs.Where(pair => pair.Value.Expires <= now).ToArray())
                tabs.Remove(pair.Key);
            if (now < awaitingTabUntil || now < closeGraceUntil || HasOpenTab(now)) return false;
            stopping = true;
            return true;
        }
    }

    private bool HasOpenTab(long now) => tabs.Values.Any(tab => tab.Open && tab.Expires > now);
}

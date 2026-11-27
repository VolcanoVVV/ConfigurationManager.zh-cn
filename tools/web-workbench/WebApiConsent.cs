using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn;

internal sealed class WebApiConsent
{
    private readonly Dictionary<Guid, (string Scope, long ReadyAt, bool Accepted)> _entries = new();

    internal IResult Change(JObject body)
    {
        var scope = (string)body["scope"];
        if (scope is not ("workbench" or "cfg")) throw new InputError("无效的 API 使用范围。");
        lock (_entries)
        {
            switch ((string)body["action"])
            {
                case "open":
                    var now = Environment.TickCount64;
                    foreach (var stale in _entries.Where(entry => !entry.Value.Accepted && now - entry.Value.ReadyAt > 300_000).ToArray())
                        _entries.Remove(stale.Key);
                    if (_entries.Count >= 256) throw new InputError("API 确认窗口过多，请关闭不用的工作台标签页。");
                    var token = Guid.NewGuid();
                    _entries.Add(token, (scope, now + 5_000, false));
                    return Results.Json(new { consent = token });
                case "accept":
                    if (!Guid.TryParse((string)body["consent"], out var pending) || !_entries.TryGetValue(pending, out var entry) ||
                        entry.Scope != scope || Environment.TickCount64 < entry.ReadyAt)
                        throw new InputError("请完成五秒倒计时并同意 API 使用声明。");
                    _entries[pending] = (scope, entry.ReadyAt, true);
                    return Results.NoContent();
                case "leave":
                    if (Guid.TryParse((string)body["consent"], out var leaving) && _entries.TryGetValue(leaving, out var old) && old.Scope == scope)
                        _entries.Remove(leaving);
                    return Results.NoContent();
                default:
                    throw new InputError("无效的 API 确认操作。");
            }
        }
    }

    internal bool Allows(JObject body, string scope = null)
    {
        lock (_entries)
            return Guid.TryParse((string)body["consent"], out var token) && _entries.TryGetValue(token, out var entry) &&
                   entry.Accepted && (scope == null || scope == entry.Scope);
    }
}
